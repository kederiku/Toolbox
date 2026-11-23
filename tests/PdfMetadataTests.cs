using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void PdfText(Stream file, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            file.Write(bytes, 0, bytes.Length);
        }

        static void PdfStream(Stream file, string dictionary, byte[] data)
        {
            PdfText(file, "<< " + dictionary + " /Length " + data.Length + " >>\nstream\n");
            file.Write(data, 0, data.Length);
            PdfText(file, "\nendstream");
        }

        static void MakeMetadataPdf(string path, string extra, string filter)
        {
            using (var file = File.Create(path))
            {
                PdfText(file, "%PDF-1.7\n");
                var offsets = new List<long>();
                Action<int> start = i => { offsets.Add(file.Position); PdfText(file, i + " 0 obj\n"); };
                Action end = () => PdfText(file, "\nendobj\n");
                start(1); PdfText(file, "<< /Type /Catalog /Pages 2 0 R " + extra + " >>"); end();
                start(2); PdfText(file, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>"); end();
                start(3); PdfText(file, "<< /Type /Page /Parent 2 0 R /PTEX.FileName (C:/PRIVE_PTEX_DOSSIER/source.pdf) /PTEX.InfoDict 14 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 4 0 R >> /XObject << /Photo 6 0 R >> >> /Thumb 13 0 R /Contents 5 0 R /Annots [7 0 R] >>"); end();
                start(4); PdfText(file, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); end();
                start(5); PdfStream(file, "", Encoding.ASCII.GetBytes("BT /F1 12 Tf 50 320 Td (Texte selectionnable) Tj ET\nq 30 0 0 20 50 250 cm /Photo Do Q\n")); end();
                var photo = MetadataTestJpeg("PRIVE_PHOTO_PDF");
                if (filter.Contains("/ASCIIHexDecode")) photo = Encoding.ASCII.GetBytes(BitConverter.ToString(photo).Replace("-", "") + ">");
                start(6); PdfStream(file, "/Type /XObject /Subtype /Image /PTEX.FileName 15 0 R /Width 3 /Height 2 /BitsPerComponent 8 /ColorSpace [ /ICCBased 12 0 R ] /Metadata 8 0 R /Filter " + filter, photo); end();
                start(7); PdfText(file, "<< /Type /Annot /Subtype /Link /Contents 15 0 R /Rect [0 0 20 20] /A << /S /URI /URI (https://example.org/exemple) >> >>"); end();
                start(8); PdfStream(file, "/Type /Metadata /Subtype /XML", Encoding.ASCII.GetBytes("<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title>PRIVE_XMP_IMBRIQUE</dc:title></rdf:Description></rdf:RDF>")); end();
                start(9); PdfText(file, "<< /Author (PRIVE_REVISION_ANTERIEURE) >>"); end();
                start(10); PdfText(file, "<< /FT /Sig /T (Signature) /V 11 0 R >>"); end();
                start(11); PdfText(file, "<< /Type /Sig /ByteRange [0 5 12 7] /Contents <00> >>"); end();
                var profile = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, @"spool\drivers\color\sRGB Color Space Profile.icm"));
                Encoding.ASCII.GetBytes("PRIVEICC").CopyTo(profile, 48);
                start(12); PdfStream(file, "/N 3 /Alternate /DeviceRGB", profile); end();
                start(13); PdfStream(file, "/Width 3 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", MetadataTestJpeg("PRIVE_VIGNETTE_PDF")); end();
                start(14); PdfText(file, "<< /Author (PRIVE_PTEX_AUTEUR) /Subject (PRIVE_PTEX_SUJET) >>"); end();
                start(15); PdfText(file, "(Note editoriale partagee)"); end();
                var previous = file.Position;
                int size = offsets.Count + 1;
                PdfText(file, "xref\n0 " + size + "\n0000000000 65535 f \n");
                foreach (long offset in offsets) PdfText(file, offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                PdfText(file, "trailer\n<< /Size " + size + " /Root 1 0 R /Info 9 0 R >>\nstartxref\n" + previous + "\n%%EOF\n");
                long lastInfo = file.Position;
                PdfText(file, "9 0 obj\n<< /Author (PRIVE_PROPRIETES_ACTUELLES) /Keywords (PRIVE_MOTS) /PRIVE_CHAMP (PRIVE_VALEUR) >>\nendobj\n");
                long lastXref = file.Position;
                PdfText(file, "xref\n9 1\n" + lastInfo.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                PdfText(file, "trailer\n<< /Size " + size + " /Root 1 0 R /Info 9 0 R /Prev " + previous + " >>\nstartxref\n" + lastXref + "\n%%EOF\n");
            }
        }

        static void CheckPdfMetadata()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-pdf-test-" + Guid.NewGuid().ToString("N")));
            try
            {
                string source = Path.Combine(dir.FullName, "révisions.pdf");
                string output = Path.Combine(dir.FullName, "copie.pdf");
                CheckGeoreferencedPdf(dir.FullName);
                CheckPdfInfoSummary(dir.FullName);
                MakeMetadataPdf(source, "", "/DCTDecode");
                var result = new MetadataResult();
                PdfMetadata.Clean(source, output, result, CancellationToken.None);
                Check(result.DocumentProperties && !result.PhotoProperties && result.EmbeddedPhotos == 2, "PDF : propriétés, JPEG intégrée et vignette sans Subtype comptées");
                string summary = result.Summary();
                foreach (string label in new[] { "Auteur", "Titre", "Objet", "Mots-clés", "Propriétés personnalisées", "Métadonnées XMP", "Chemin d’origine", "Informations du profil ICC", "Commentaire" })
                    Check(summary.Contains(label), "PDF : champ supprimé du document ou de sa photo dans le bilan : " + label);
                Check(!summary.Contains("PRIVE_") && !summary.Contains("Date de modification"), "PDF : uniquement les types de champs présents, sans contenu privé");
                string inspected = Path.Combine(dir.FullName, "structure.pdf");
                using (var engine = new PdfMetadataEngine())
                    engine.Run(CancellationToken.None, false, "--stream-data=uncompress", "--object-streams=disable", output, inspected);
                var clean = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(inspected));
                foreach (string marker in new[] { "PRIVE_REVISION_ANTERIEURE", "PRIVE_PROPRIETES_ACTUELLES", "PRIVE_PHOTO_PDF", "PRIVE_VIGNETTE_PDF", "PRIVE_XMP_IMBRIQUE", "PRIVEICC", "PRIVE_PTEX_DOSSIER", "PRIVE_PTEX_AUTEUR" })
                    Check(!clean.Contains(marker) && !Encoding.ASCII.GetString(File.ReadAllBytes(output)).Contains(marker), "PDF : marqueur supprimé " + marker);
                Check(clean.Contains("https://example.org/exemple") && clean.Contains("/MediaBox [ 0 0 300 400 ]"), "PDF : lien et géométrie conservés");
                Check(clean.Contains("Texte selectionnable"), "PDF : conservation du contenu textuel sélectionnable");
                Check(!clean.Contains("/PTEX.FileName") && !clean.Contains("/PTEX.InfoDict") && clean.Contains("Note editoriale partagee"), "PDF : champs pdfTeX retirés et annotation partagée conservée");
                var verify = new MetadataResult();
                PdfMetadata.Clean(output, Path.Combine(dir.FullName, "relu.pdf"), verify, CancellationToken.None);
                Check(!verify.Changed, "PDF : deuxième lecture sans propriété privée");
                Check(result.PreviousRevisions, "PDF : suppression des révisions antérieures signalée");
                // Une mise à jour incrémentale sans propriété active doit compter uniquement dans l’historique.
                string historyOnly = Path.Combine(dir.FullName, "historique-seul.pdf");
                File.Copy(output, historyOnly);
                var active = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(output));
                string previous = Regex.Match(active, @"startxref\s+(\d+)").Groups[1].Value;
                string size = Regex.Match(active, @"/Size (\d+)").Groups[1].Value;
                string root = Regex.Match(active, @"/Root (\d+ \d+ R)").Groups[1].Value;
                using (var revision = new FileStream(historyOnly, FileMode.Append))
                {
                    long next = revision.Position;
                    PdfText(revision, "xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size " + size + " /Root " + root + " /Prev " + previous + " >>\nstartxref\n" + next + "\n%%EOF\n");
                }
                var history = new MetadataResult();
                PdfMetadata.Clean(historyOnly, Path.Combine(dir.FullName, "sans-historique.pdf"), history, CancellationToken.None);
                Check(history.PreviousRevisions && !history.DocumentProperties && history.EmbeddedPhotos == 0, "PDF : bilan de l’historique seul");
                Check(!history.Summary().Contains("Auteur") && !history.Summary().Contains("Métadonnées XMP"), "PDF : aucun champ déduit des anciennes révisions");
                MakeMetadataPdf(source, "", "[ /ASCIIHexDecode /DCTDecode ]");
                var wrapped = new MetadataResult();
                PdfMetadata.Clean(source, Path.Combine(dir.FullName, "jpeg-enveloppe.pdf"), wrapped, CancellationToken.None);
                Check(wrapped.EmbeddedPhotos == 2, "PDF : nettoyage d’un JPEG protégé par un filtre de transport");
                using (var engine = new PdfMetadataEngine())
                {
                    string encrypted = Path.Combine(dir.FullName, "chiffre.pdf");
                    engine.Run(CancellationToken.None, false, "--encrypt", "lecture", "proprietaire", "256", "--", source, encrypted);
                    bool rejected = false;
                    try { PdfMetadata.Clean(encrypted, Path.Combine(dir.FullName, "refus.pdf"), new MetadataResult(), CancellationToken.None); }
                    catch (InvalidDataException e) { rejected = e.Message.Contains("chiffr"); }
                    Check(rejected, "PDF : chiffrement expliqué");
                }
                foreach (var example in new[] {
                    new[] { "/AcroForm << /Fields [10 0 R] >>", "/DCTDecode", "sign" },
                    new[] { "/Names << /EmbeddedFiles << /Names [] >> >>", "/DCTDecode", "jointe" },
                    new[] { "", "/JPXDecode", "image" }
                })
                {
                    MakeMetadataPdf(source, example[0], example[1]);
                    var refused = false;
                    try { PdfMetadata.Clean(source, Path.Combine(dir.FullName, "refus.pdf"), new MetadataResult(), CancellationToken.None); }
                    catch (InvalidDataException e) { refused = e.Message.IndexOf(example[2], StringComparison.OrdinalIgnoreCase) >= 0; }
                    Check(refused, "PDF : explication du refus (" + example[2] + ")");
                }
            }
            finally { dir.Delete(true); }
        }

        static void CheckGeoreferencedPdf(string directory)
        {
            foreach (string kind in new[] { "ISO", "OGC", "plan" })
            {
                string source = Path.Combine(directory, kind + ".pdf");
                string target = Path.Combine(directory, kind + "-copie.pdf");
                bool legacy = kind == "OGC", map = kind != "plan";
                var bodies = new[] {
                    "<< /Type /Catalog /Pages 2 0 R >>",
                    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                    "<< /Type /Page /Parent 2 0 R /Resources << >> /MediaBox [0 0 100 100] " + (legacy ? "/LGIDict 4 0 R" : "/VP [4 0 R]") + " >>",
                    legacy ? "<< /Type /LGIDict /Version (2.1) /Registration [[0 0 18 43] [100 100 19 44]] /Projection << /ProjectionType (GEOGRAPHIC) >> >>"
                        : "<< /Type /Viewport /BBox [0 0 100 100] /Measure 5 0 R >>",
                    map ? "<< /Type /Measure /Subtype 6 0 R /GPTS [43 18 44 18 44 19 43 19] /LPTS [0 0 0 1 1 1 1 0] /GCS << /Type /GEOGCS /EPSG 4326 >> >>"
                        : "<< /Type /Measure /Subtype /RL /R (1 cm = 1 m) >>",
                    "/GEO"
                };
                WritePdfObjects(source, bodies, "");
                bool refused = false;
                try { PdfMetadata.Clean(source, target, new MetadataResult(), CancellationToken.None); }
                catch (InvalidDataException e) { refused = e.Message.Contains("géoréférenc") && e.Message.Contains("coordonnées"); }
                Check(map ? refused && !File.Exists(target) : !refused && File.Exists(target),
                    map ? "PDF : refus d’un document géoréférencé " + kind : "PDF : mesures de plan sans coordonnées géographiques conservées");
            }
        }

        static void CheckPdfInfoSummary(string directory)
        {
            foreach (bool author in new[] { true, false })
            {
                string path = Path.Combine(directory, "info-" + author + ".pdf");
                WritePdfObjects(path, new[] {
                    "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [4 0 R] >>",
                    author ? "<< /Author (PRIVE_AUTEUR) >>" : "<< /Title (PRIVE_TITRE) /Author () >>",
                    "<< /Type /Page /Parent 2 0 R /Resources << >> /MediaBox [0 0 100 100] >>"
                }, "/Info 3 0 R");
                var result = new MetadataResult();
                PdfMetadata.Clean(path, Path.Combine(directory, "nettoye-info-" + author + ".pdf"), result, CancellationToken.None);
                string summary = result.Summary();
                Check(result.DocumentProperties && summary.Contains("Auteur") == author && summary.Contains("Titre") != author
                    && !summary.Contains("PRIVE_"), "PDF : distinction entre auteur seul et titre seul dans le dictionnaire Info");
            }
        }

        static void WritePdfObjects(string path, string[] bodies, string trailerFields)
        {
            using (var stream = File.Create(path))
            {
                PdfText(stream, "%PDF-1.7\n");
                var offsets = new List<long>();
                for (int i = 0; i < bodies.Length; i++)
                {
                    offsets.Add(stream.Position);
                    PdfText(stream, (i + 1) + " 0 obj\n" + bodies[i] + "\nendobj\n");
                }
                long xref = stream.Position;
                int count = bodies.Length + 1;
                PdfText(stream, "xref\n0 " + count + "\n0000000000 65535 f \n");
                foreach (long offset in offsets) PdfText(stream, offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                PdfText(stream, "trailer\n<< /Size " + count + " /Root 1 0 R " + trailerFields + " >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }
    }
}
