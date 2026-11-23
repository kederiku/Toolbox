using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        const string MarkWord = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string MarkRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string MarkWp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        const string MarkDrawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string MarkPic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        const string MarkType = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

        static Uri MarkUri(string path) { return new Uri(path, UriKind.Relative); }

        static PackagePart MarkPart(Package pkg, string path, string type, string xml)
        {
            var part = pkg.CreatePart(MarkUri(path), MarkType + type + "+xml");
            using (var writer = new StreamWriter(part.GetStream())) writer.Write(xml);
            return part;
        }

        static void MarkFixture(string path, bool signed)
        {
            string ns = " xmlns:w='" + MarkWord + "' xmlns:r='" + MarkRel + "' xmlns:wp='" + MarkWp
                + "' xmlns:a='" + MarkDrawing + "' xmlns:pic='" + MarkPic + "'";
            string first = "<w:sectPr><w:headerReference w:type='default' r:id='ordinary'/>"
                + "<w:headerReference w:type='first' r:id='cover'/><w:pgSz w:w='12240' w:h='15840'/><w:titlePg/></w:sectPr>";
            string landscape = "<w:sectPr><w:pgSz w:w='15840' w:h='12240' w:orient='landscape'/><w:titlePg/></w:sectPr>";
            string scan = "<w:drawing><wp:inline><wp:extent cx='7772400' cy='10058400'/><wp:docPr id='1' name='Scan'/>"
                + "<a:graphic><a:graphicData uri='" + MarkPic + "'><pic:pic><pic:nvPicPr><pic:cNvPr id='0' name='Scan'/>"
                + "<pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed='scan'/><a:stretch><a:fillRect/></a:stretch>"
                + "</pic:blipFill><pic:spPr><a:xfrm><a:off x='0' y='0'/><a:ext cx='7772400' cy='10058400'/></a:xfrm>"
                + "<a:prstGeom prst='rect'><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";
            string vml = "<w:pict><v:shape xmlns:v='urn:schemas-microsoft-com:vml' id='legacyScan' style='position:absolute; width:612pt; height:27.94cm; z-index:270000003'>"
                + "<v:imagedata r:id='scan'/></v:shape></w:pict>";
            string small = vml.Replace("legacyScan", "thumbnail").Replace("612pt", "48pt").Replace("27.94cm", "1in");
            string alternate = "<mc:AlternateContent xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006'"
                + " xmlns:w14='http://schemas.microsoft.com/office/word/2010/wordml'><mc:Choice Requires='w14'>" + scan
                + "</mc:Choice><mc:Fallback>" + vml.Replace("legacyScan", "fallbackScan") + "</mc:Fallback></mc:AlternateContent>";
            using (var pkg = Package.Open(path, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var main = MarkPart(pkg, "/book/main.xml", "document.main", "<w:document" + ns + "><w:body>"
                    + "<w:p><w:r>" + vml + "</w:r></w:p><w:p><w:r>" + small + "</w:r></w:p>"
                    + "<w:p><w:pPr>" + first + "</w:pPr><w:r>" + alternate + "</w:r></w:p>"
                    + "<w:p><w:pPr>" + landscape + "</w:pPr><w:r><w:t>Texte modifiable</w:t></w:r></w:p>"
                    + "<w:p/>" + landscape + "</w:body></w:document>");
                pkg.CreateRelationship(main.Uri, TargetMode.Internal, MarkRel + "/officeDocument");
                var header = MarkPart(pkg, "/heads/odd.xml", "header", "<w:hdr" + ns + "><w:p><w:hyperlink r:id='site'><w:r><w:t>Site</w:t></w:r></w:hyperlink></w:p></w:hdr>");
                header.CreateRelationship(new Uri("https://example.org/"), TargetMode.External, MarkRel + "/hyperlink", "site");
                main.CreateRelationship(header.Uri, TargetMode.Internal, MarkRel + "/header", "ordinary");
                var cover = MarkPart(pkg, "/heads/cover.xml", "header", "<w:hdr" + ns + "><w:p><w:r><w:t>Couverture</w:t></w:r></w:p></w:hdr>");
                main.CreateRelationship(cover.Uri, TargetMode.Internal, MarkRel + "/header", "cover");
                var settings = MarkPart(pkg, "/book/settings.xml", "settings", "<w:settings" + ns + "><w:evenAndOddHeaders/></w:settings>");
                main.CreateRelationship(settings.Uri, TargetMode.Internal, MarkRel + "/settings");
                var photo = pkg.CreatePart(MarkUri("/img/scan.png"), "image/png");
                using (var s = photo.GetStream())
                {
                    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
                    s.Write(png, 0, png.Length);
                }
                main.CreateRelationship(photo.Uri, TargetMode.Internal, MarkRel + "/image", "scan");
                if (signed)
                    pkg.CreateRelationship(MarkUri("/_xmlsignatures/origin.sigs"), TargetMode.Internal,
                        "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin");
            }
        }

        static XmlDocument MarkXml(PackagePart part)
        {
            var xml = new XmlDocument();
            using (var s = part.GetStream()) xml.Load(s);
            return xml;
        }

        static PackagePart MarkTarget(Package pkg, PackagePart from, string id)
        {
            return pkg.GetPart(PackUriHelper.ResolvePartUri(from.Uri, from.GetRelationship(id).TargetUri));
        }

        static void CheckWordPixels(BitmapSource source, bool repeated)
        {
            var small = new TransformedBitmap(source, new ScaleTransform(160.0 / source.PixelWidth, 200.0 / source.PixelHeight));
            var bitmap = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            int ink = 0, outerInk = 0;
            for (int y = 0; y < bitmap.PixelHeight; y++)
                for (int x = 0; x < bitmap.PixelWidth; x++)
                    if (pixels[y * stride + x * 4 + 3] > 8)
                    {
                        ink++;
                        if (y < 55 || y > 145) outerInk++;
                    }
            Check(ink > 0 && pixels[3] == 0 && (repeated ? outerInk > 0 : outerInk == 0),
                "Le PNG rouvert contient des lettres visibles à l’emplacement choisi et un fond transparent");
        }

        static void CheckWordCopy(string path, bool repeated)
        {
            using (var pkg = Package.Open(path, FileMode.Open, FileAccess.Read))
            {
                var main = pkg.GetPart(MarkUri("/book/main.xml"));
                var doc = MarkXml(main);
                Check(doc.InnerText.Contains("Texte modifiable"), "Word conserve le texte éditable");
                Check(MarkTarget(pkg, main, "scan").ContentType == "image/png"
                    && doc.GetElementsByTagName("inline", MarkWp).Count == 1, "Word conserve le scan pleine page");
                var paragraphs = doc.GetElementsByTagName("p", MarkWord);
                foreach (int p in new[] { 0, 2 })
                {
                    var marks = ((XmlElement)paragraphs[p]).GetElementsByTagName("anchor", MarkWp);
                    Check(marks.Count == 1 && marks[0].ParentNode.ParentNode.ParentNode == paragraphs[p],
                        p == 0 ? "Le scan Word VML porte un filigrane dans le corps" : "Les deux représentations du scan partagent un seul filigrane hors des branches de compatibilité");
                    var mark = (XmlElement)marks[0];
                    Check(mark.GetAttribute("behindDoc") == "0" && UInt32.Parse(mark.GetAttribute("relativeHeight")) > 270000003,
                        "Le filigrane du corps passe devant le scan VML");
                }
                Check(((XmlElement)paragraphs[1]).GetElementsByTagName("anchor", MarkWp).Count == 0, "Une miniature VML ne double pas le filigrane d’en-tête");
                var effective = new Dictionary<string, string>();
                var sections = doc.GetElementsByTagName("sectPr", MarkWord);
                Check(sections.Count == 3, "Word conserve les trois sections");
                for (int i = 0; i < sections.Count; i++)
                {
                    var refs = ((XmlElement)sections[i]).GetElementsByTagName("headerReference", MarkWord);
                    if (i == 2) Check(refs.Count == 0, "Une section de même taille reste liée à la précédente");
                    foreach (XmlElement r in refs) effective[r.GetAttribute("type", MarkWord)] = r.GetAttribute("id", MarkRel);
                    foreach (string kind in new[] { "default", "first", "even" })
                    {
                        Check(effective.ContainsKey(kind), "Word couvre les pages de type " + kind);
                        var header = MarkTarget(pkg, main, effective[kind]);
                        var xml = MarkXml(header);
                        if (kind == "default") Check(xml.InnerText.Contains("Site") && header.GetRelationship("site").TargetMode == TargetMode.External,
                            "Word préserve le lien de l’en-tête hérité");
                        if (kind == "first") Check(xml.InnerText.Contains("Couverture"), "Word préserve la première page différente");
                        var anchors = xml.GetElementsByTagName("anchor", MarkWp);
                        Check(anchors.Count == 1 && ((XmlElement)anchors[0]).GetAttribute("behindDoc") == "0",
                            "Un seul filigrane Word au premier plan");
                        var extent = (XmlElement)xml.GetElementsByTagName("extent", MarkWp)[0];
                        Check(extent.GetAttribute("cx") == (i == 0 ? "7772400" : "10058400")
                            && extent.GetAttribute("cy") == (i == 0 ? "10058400" : "7772400"), "Le dessin suit les dimensions de la section");
                        foreach (string axis in new[] { "positionH", "positionV" })
                            Check(((XmlElement)xml.GetElementsByTagName(axis, MarkWp)[0]).GetAttribute("relativeFrom") == "page",
                                "Le dessin est positionné sur la feuille");
                        var blip = (XmlElement)xml.GetElementsByTagName("blip", MarkDrawing)[0];
                        using (var stream = MarkTarget(pkg, header, blip.GetAttribute("embed", MarkRel)).GetStream())
                        {
                            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                            Check((frame.PixelWidth < frame.PixelHeight) == (i == 0), "Le PNG du motif suit l’orientation de la section");
                            if (kind == "default" && i == 0) CheckWordPixels(frame, repeated);
                        }
                    }
                }
            }
        }

        internal static void RunWordWatermarkTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "Atelier.WordMark." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string input = Path.Combine(dir, "original.docx");
                MarkFixture(input, false);
                byte[] original = File.ReadAllBytes(input);
                using (var source = new WordWatermark(input, CancellationToken.None))
                {
                    Check(source.Preview == null && Math.Abs(source.Width / source.Height - 8.5 / 11) < 0.001,
                        "La simulation Word prend les proportions de la première section");
                    foreach (bool repeat in new[] { false, true })
                    {
                        string output = Path.Combine(dir, repeat ? "répété.docx" : "unique.docx");
                        source.WriteCopy(output, new WatermarkOptions("Réservé à Léa", repeat, WatermarkPosition.Center, 0.08, 0.5), CancellationToken.None);
                        CheckWordCopy(output, repeat);
                    }
                    string cancelled = Path.Combine(dir, "annulé.docx");
                    bool stopped = false;
                    try { source.WriteCopy(cancelled, new WatermarkOptions("Nom", false, WatermarkPosition.Center, 0.2, 0.5), new CancellationToken(true)); }
                    catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && !File.Exists(cancelled), "Une opération Word déjà annulée ne crée aucune copie");
                }
                Check(File.ReadAllBytes(input).SequenceEqual(original), "Word préserve les octets de l’original");
                string signed = Path.Combine(dir, "signé.docx");
                MarkFixture(signed, true);
                string encrypted = Path.Combine(dir, "chiffré.docx");
                File.WriteAllBytes(encrypted, new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 });
                foreach (string path in new[] { signed, encrypted })
                {
                    bool refused = false;
                    try { using (new WordWatermark(path, CancellationToken.None)) { } }
                    catch (NotSupportedException e) { refused = e.Message.Contains(path == signed ? "sign" : "chiffr"); }
                    Check(refused, "Word explique le refus du document " + Path.GetFileName(path));
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
