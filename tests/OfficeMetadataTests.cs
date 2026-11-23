using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        const string TestOfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        const string TestPackage = "http://schemas.openxmlformats.org/package/2006/";
        const string TestOffice = "application/vnd.openxmlformats-officedocument.";
        const string TestOfficeNs = "http://schemas.openxmlformats.org/officeDocument/2006/";
        const string TestWord = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string TestExcel = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string TestPowerPoint = "http://schemas.openxmlformats.org/presentationml/2006/main";

        static byte[] OfficeTestPhoto()
        {
            var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[] { 19, 41, 101 }, 3);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                byte[] jpeg = stream.ToArray();
                var comment = Encoding.ASCII.GetBytes("PRIVATE-PHOTO");
                return jpeg.Take(2).Concat(new byte[] { 255, 254, 0, (byte)(comment.Length + 2) })
                    .Concat(comment).Concat(jpeg.Skip(2)).ToArray();
            }
        }

        static void OfficeBytes(ZipArchive archive, string path, byte[] data)
        {
            using (var stream = archive.CreateEntry(path).Open()) stream.Write(data, 0, data.Length);
        }

        static void OfficeXml(ZipArchive archive, string path, string xml)
        {
            OfficeBytes(archive, path, Encoding.UTF8.GetBytes(xml));
        }

        static string OfficeTestRel(string id, string type, string target)
        {
            return "<Relationship Id='" + id + "' Type='" + type + "' Target='" + target + "'/>";
        }

        static string OfficeTestOverride(string path, string type)
        {
            return "<Override PartName='/" + path + "' ContentType='" + type + "'/>";
        }

        static string OfficeTestRels(string children)
        {
            return "<Relationships xmlns='" + TestPackage + "relationships'>" + children + "</Relationships>";
        }

        static void OfficeFixture(string path, string format, string defect)
        {
            string root, main, family, name, mainXml, child, childType, childXml;
            if (format == ".docx")
            {
                root = "word"; main = "document.xml"; family = "wordprocessingml"; name = "document";
                mainXml = "<w:document xmlns:w='" + TestWord + "'><w:body><w:p><w:r><w:t>Texte visible</w:t></w:r></w:p></w:body></w:document>";
                child = "comments.xml"; childType = "comments";
                childXml = "<w:comments xmlns:w='" + TestWord + "'><w:comment w:id='0' w:author='Relectrice'><w:p><w:r><w:t>Commentaire éditorial</w:t></w:r></w:p></w:comment></w:comments>";
            }
            else if (format == ".xlsx")
            {
                root = "xl"; main = "workbook.xml"; family = "spreadsheetml"; name = "sheet";
                mainXml = "<workbook xmlns='" + TestExcel + "' xmlns:r='" + TestOfficeRel.TrimEnd('/') + "'><sheets><sheet name='Calcul' sheetId='1' r:id='child'/></sheets></workbook>";
                child = "worksheets/sheet1.xml"; childType = "worksheet";
                childXml = "<worksheet xmlns='" + TestExcel + "'><sheetData><row r='1'><c r='A1'><f>SUM(2,3)</f><v>5</v></c></row></sheetData></worksheet>";
            }
            else
            {
                root = "ppt"; main = "presentation.xml"; family = "presentationml"; name = "presentation";
                mainXml = "<p:presentation xmlns:p='" + TestPowerPoint + "' xmlns:r='" + TestOfficeRel.TrimEnd('/') + "'><p:sldIdLst><p:sldId id='256' r:id='child'/></p:sldIdLst></p:presentation>";
                child = "slides/slide1.xml"; childType = "slide";
                childXml = "<p:sld xmlns:p='" + TestPowerPoint + "'><p:cSld name='Présentation visible'><p:spTree/></p:cSld></p:sld>";
            }
            string bodyPath = root + "/" + main;
            string childPath = root + "/" + child;
            string properties = "information/private.xml";
            string custom = "information/fields.xml";
            string app = "information/application.xml";
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                string overrides = OfficeTestOverride(bodyPath, TestOffice + family + "." + name + ".main+xml")
                    + OfficeTestOverride(childPath, TestOffice + family + "." + childType + "+xml")
                    + OfficeTestOverride(properties, "application/vnd.openxmlformats-package.core-properties+xml")
                    + OfficeTestOverride(custom, TestOffice + "custom-properties+xml");
                string rels = OfficeTestRel("main", TestOfficeRel + "officeDocument", bodyPath)
                    + OfficeTestRel("core", TestPackage + "relationships/metadata/core-properties", properties)
                    + OfficeTestRel("app", TestOfficeRel + "extended-properties", app)
                    + OfficeTestRel("custom", TestOfficeRel + "custom-properties", custom)
                    + OfficeTestRel("thumbnail", TestPackage + "relationships/metadata/thumbnail", "preview/cover.art");
                string mainRels = OfficeTestRel("child", TestOfficeRel + childType, child)
                    + "<Relationship Id='web' Type='" + TestOfficeRel + "hyperlink' Target='https://example.org/page' TargetMode='External'/>";
                string printerRels = "";
                if (defect == "imprimante")
                {
                    string profile = root + "/printerSettings/printer1.bin";
                    overrides += OfficeTestOverride(profile, TestOffice + family + ".printerSettings");
                    printerRels = OfficeTestRel("driver123", TestOfficeRel + "printerSettings", "/" + profile);
                    if (format == ".docx")
                        mainXml = mainXml.Replace("</w:body>", "<w:sectPr><w:printerSettings xmlns:r='" + TestOfficeRel.TrimEnd('/') + "' r:id='driver123'/></w:sectPr></w:body>");
                    if (format == ".xlsx")
                        childXml = childXml.Replace("</worksheet>", "<pageSetup xmlns:r='" + TestOfficeRel.TrimEnd('/') + "' orientation='landscape' r:id='driver123'/></worksheet>");
                    else mainRels += printerRels;
                    OfficeBytes(archive, profile, Encoding.ASCII.GetBytes("PRIVATE-PRINTER"));
                }
                if (defect == "joint")
                {
                    mainRels += OfficeTestRel("extra", TestOfficeRel + "package", "../object.xml");
                    OfficeXml(archive, "object.xml", "<payload/>");
                }
                if (defect == "signature")
                {
                    rels += OfficeTestRel("signed", TestPackage + "relationships/digital-signature/origin", "signature.xml");
                    OfficeXml(archive, "signature.xml", "<signature/>");
                }
                if (defect == "absent") mainRels += OfficeTestRel("lost", TestOfficeRel + "image", "../lost.jpg");
                if (defect == "référence")
                    mainXml = mainXml.Replace("<w:r>", "<w:r><w:drawing><a:blip xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main' xmlns:r='" + TestOfficeRel.TrimEnd('/') + "' r:embed='absente'/></w:drawing>");
                if (defect == "externe")
                    mainXml = mainXml.Replace("<w:r>", "<w:r><w:pict><v:imagedata xmlns:v='urn:schemas-microsoft-com:vml' src='file:///C:/Images/source.jpg'/></w:pict>");
                if (defect == "image") OfficeBytes(archive, "pictures/fake.art", new byte[] { 1, 2, 3 });
                if (defect == "opaque") OfficeBytes(archive, "records/random.blob", new byte[] { 80, 75, 3, 4, 1, 2 });
                OfficeXml(archive, "[Content_Types].xml", "<Types xmlns='" + TestPackage + "content-types'>"
                    + "<Default Extension='xml' ContentType='application/xml'/>"
                    + "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>"
                    + "<Default Extension='art' ContentType='image/jpeg'/>"
                    + "<Default Extension='blob' ContentType='application/octet-stream'/>" + overrides + "</Types>");
                OfficeXml(archive, "_rels/.rels", OfficeTestRels(rels));
                OfficeXml(archive, root + "/_rels/" + main + ".rels", OfficeTestRels(mainRels));
                string childRels = childPath.Substring(0, childPath.LastIndexOf('/') + 1) + "_rels/" + Path.GetFileName(child) + ".rels";
                OfficeXml(archive, childRels, OfficeTestRels(OfficeTestRel("pic", TestOfficeRel + "image", "/pictures/shared.blob") + (format == ".xlsx" ? printerRels : "")));
                OfficeXml(archive, bodyPath, mainXml);
                OfficeXml(archive, childPath, childXml);
                OfficeXml(archive, properties, "<cp:coreProperties xmlns:cp='" + TestPackage + "metadata/core-properties' xmlns:dc='http://purl.org/dc/elements/1.1/'>"
                    + "<dc:creator>PRIVATE-AUTHOR</dc:creator><cp:lastModifiedBy>PRIVATE-EDITOR</cp:lastModifiedBy>"
                    + "<dc:title>PRIVATE-TITLE</dc:title><cp:keywords/></cp:coreProperties>");
                OfficeXml(archive, app, "<Properties xmlns='" + TestOfficeNs + "extended-properties'><Company>PRIVATE-COMPANY</Company></Properties>");
                OfficeXml(archive, custom, "<Properties xmlns='" + TestOfficeNs + "custom-properties'><property fmtid='{D5CDD505-2E9C-101B-9397-08002B2CF9AE}' pid='2' name='PRIVATE-FIELD'><v:lpwstr xmlns:v='" + TestOfficeNs + "docPropsVTypes'>PRIVATE-VALUE</v:lpwstr></property></Properties>");
                byte[] photo = OfficeTestPhoto();
                OfficeBytes(archive, "pictures/shared.blob", photo);
                OfficeBytes(archive, "preview/cover.art", photo);
                if (defect == "doublon") OfficeXml(archive, bodyPath.ToUpperInvariant(), mainXml);
            }
        }

        static byte[] OfficeTestRead(ZipArchiveEntry entry)
        {
            using (var input = entry.Open())
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        static void CheckWorkbookSharing(string dir, string baseline)
        {
            const string absNs = "http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac";
            const string strict = "http://purl.oclc.org/ooxml/spreadsheetml/main";
            foreach (bool reserved in new[] { false, true })
            {
                string path = Path.Combine(dir, "partage-" + reserved + ".xlsx");
                string clean = Path.Combine(dir, "partage-nettoye-" + reserved + ".xlsx");
                File.Copy(baseline, path);
                using (var file = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
                using (var package = new ZipArchive(file, ZipArchiveMode.Update))
                {
                    var entry = package.GetEntry("xl/workbook.xml");
                    string original = Encoding.UTF8.GetString(OfficeTestRead(entry));
                    entry.Delete();
                    string sharing = "<fileSharing userName='PRIVATE-EXCEL-AUTHOR' readOnlyRecommended='1'" + (reserved ? " reservationPassword='AD09'" : "") + "/>";
                    string paths = "<mc:AlternateContent xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' xmlns:ac='" + absNs
                        + "'><mc:Choice Requires='ac'><ac:absPath url='file:///Users/PRIVATE-EXCEL-PATH/'/></mc:Choice></mc:AlternateContent>";
                    string ext = "<extLst><ext uri='editorial'><other xmlns='urn:editorial'><absPath url='chemin éditorial'/><fileSharing userName='valeur éditoriale'/></other></ext></extLst>";
                    string cache = "<pivotCaches><pivotCache cacheId='1' r:id='pivotCache'/></pivotCaches>";
                    string edited = original.Replace("<sheets>", sharing + paths + "<sheets>").Replace("</workbook>", cache + ext + "</workbook>");
                    OfficeXml(package, "xl/workbook.xml", reserved ? edited.Replace(TestExcel, strict) : edited);
                    entry = package.GetEntry("[Content_Types].xml");
                    string types = Encoding.UTF8.GetString(OfficeTestRead(entry)); entry.Delete();
                    OfficeXml(package, "[Content_Types].xml", types.Replace("</Types>", OfficeTestOverride("xl/pivotCache/source.xml", TestOffice + "spreadsheetml.pivotCacheDefinition+xml")
                        + OfficeTestOverride("xl/pivotTables/table.xml", TestOffice + "spreadsheetml.pivotTable+xml") + "</Types>"));
                    entry = package.GetEntry("xl/_rels/workbook.xml.rels");
                    string rels = Encoding.UTF8.GetString(OfficeTestRead(entry)); entry.Delete();
                    OfficeXml(package, "xl/_rels/workbook.xml.rels", rels.Replace("</Relationships>", OfficeTestRel("pivotCache", TestOfficeRel + "pivotCacheDefinition", "pivotCache/source.xml") + "</Relationships>"));
                    OfficeXml(package, "xl/pivotCache/source.xml", "<pivotCacheDefinition xmlns='" + (reserved ? strict : TestExcel)
                        + "' refreshedBy='PRIVATE-CACHE-AUTHOR' recordCount='1'><cacheSource type='worksheet'><worksheetSource ref='A1:A2' sheet='Calcul'/></cacheSource>"
                        + "<cacheFields count='1'><cacheField name='Résultat'><sharedItems count='1'><n v='5'/></sharedItems></cacheField></cacheFields></pivotCacheDefinition>");
                    entry = package.GetEntry("xl/worksheets/_rels/sheet1.xml.rels");
                    rels = Encoding.UTF8.GetString(OfficeTestRead(entry)); entry.Delete();
                    OfficeXml(package, "xl/worksheets/_rels/sheet1.xml.rels", rels.Replace("</Relationships>", OfficeTestRel("table", TestOfficeRel + "pivotTable", "../pivotTables/table.xml") + "</Relationships>"));
                    OfficeXml(package, "xl/pivotTables/table.xml", "<pivotTableDefinition xmlns='" + (reserved ? strict : TestExcel) + "' name='Synthèse' cacheId='1' dataCaption='Valeurs'>"
                        + "<location ref='G1:G2' firstHeaderRow='0' firstDataRow='0' firstDataCol='0'/><pivotFields count='1'><pivotField dataField='1'/></pivotFields>"
                        + "<dataFields count='1'><dataField name='Somme' fld='0' subtotal='sum'/></dataFields></pivotTableDefinition>");
                    OfficeXml(package, "xl/pivotTables/_rels/table.xml.rels", OfficeTestRels(OfficeTestRel("cache", TestOfficeRel + "pivotCacheDefinition", "../pivotCache/source.xml")));
                }
                var result = new MetadataResult();
                OfficeMetadata.Clean(path, clean, ".xlsx", result, CancellationToken.None);
                Check(result.DocumentProperties && result.EmbeddedPhotos == 0, "Signaler les propriétés propres au classeur Excel");
                string summary = result.Summary();
                foreach (string label in new[] { "Chemin d’origine", "Utilisateur du partage", "Dernier utilisateur du tableau croisé dynamique" })
                    Check(summary.Contains(label), "Bilan Excel du partage et du tableau croisé : " + label);
                Check(!summary.Contains("PRIVATE-") && !summary.Contains("Dernier éditeur"), "Bilan Excel limité aux champs du classeur");
                using (var package = new ZipArchive(File.OpenRead(clean)))
                using (var source = new ZipArchive(File.OpenRead(path)))
                {
                    string body = Encoding.UTF8.GetString(OfficeTestRead(package.GetEntry("xl/workbook.xml")));
                    var workbook = new XmlDocument(); workbook.LoadXml(body);
                    var sharing = (XmlElement)workbook.GetElementsByTagName("fileSharing", reserved ? strict : TestExcel)[0];
                    Check(!body.Contains("PRIVATE-EXCEL") && workbook.GetElementsByTagName("absPath", absNs).Count == 0, "Chemin de travail et éditeur personnels supprimés du classeur");
                    Check(sharing.GetAttribute("readOnlyRecommended") == "1" && (reserved
                        ? sharing.GetAttribute("reservationPassword") == "AD09" && sharing.GetAttribute("userName").Length > 0
                        : !sharing.HasAttribute("userName")), "Nom requis et protection du partage conservés");
                    Check(body.Contains("chemin éditorial") && body.Contains("valeur éditoriale"), "Éléments homonymes d’un autre espace de noms conservés");
                    string definition = Encoding.UTF8.GetString(OfficeTestRead(package.GetEntry("xl/pivotCache/source.xml")));
                    var cache = new XmlDocument(); cache.LoadXml(definition);
                    Check(!cache.DocumentElement.HasAttribute("refreshedBy") && cache.DocumentElement.GetAttribute("recordCount") == "1"
                        && definition.Contains("Résultat") && definition.Contains("A1:A2") && body.Contains("pivotCache"), "Auteur du cache retiré, source et valeurs du tableau croisé conservées");
                    foreach (string name in new[] { "xl/pivotTables/table.xml", "xl/pivotTables/_rels/table.xml.rels", "xl/worksheets/_rels/sheet1.xml.rels" })
                        Check(OfficeTestRead(source.GetEntry(name)).SequenceEqual(OfficeTestRead(package.GetEntry(name))), "Tableau croisé et liens vers son cache conservés");
                    Check(Encoding.UTF8.GetString(OfficeTestRead(package.GetEntry("xl/worksheets/sheet1.xml"))).Contains("SUM(2,3)")
                        && Encoding.UTF8.GetString(OfficeTestRead(package.GetEntry("xl/_rels/workbook.xml.rels"))).Contains("https://example.org/page"), "Formule et lien du classeur conservés");
                }
                var next = new MetadataResult();
                OfficeMetadata.Clean(clean, Path.Combine(dir, "relecture-" + reserved + ".xlsx"), ".xlsx", next, CancellationToken.None);
                Check(!next.DocumentProperties && next.EmbeddedPhotos == 0, "Propriétés du classeur stables après relecture");
            }
        }

        static void CheckOfficeMetadata()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AtelierPC-Office-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using (PhotoMetadata.BeginBatch())
            try
            {
                foreach (string ext in new[] { ".docx", ".xlsx", ".pptx" })
                {
                    string source = Path.Combine(dir, "source" + ext);
                    string clean = Path.Combine(dir, "clean" + ext);
                    OfficeFixture(source, ext, "");
                    var result = new MetadataResult();
                    OfficeMetadata.Clean(source, clean, ext, result, CancellationToken.None);
                    Check(result.DocumentProperties && !result.PhotoProperties && result.EmbeddedPhotos == 2, "Propriétés et deux photos intégrées comptées pour " + ext);
                    string summary = result.Summary();
                    foreach (string label in new[] { "Auteur", "Dernier éditeur", "Titre", "Entreprise", "Propriétés personnalisées", "Commentaire" })
                        Check(summary.Contains(label), "Champ supprimé du document ou de sa photo dans le bilan " + ext + " : " + label);
                    Check(!summary.Contains("PRIVATE-") && !summary.Contains("Mots-clés") && !summary.Contains("Profil d’imprimante"), "Bilan Office limité aux champs présents, sans valeur privée");
                    using (var original = new ZipArchive(File.OpenRead(source)))
                    using (var output = new ZipArchive(File.OpenRead(clean)))
                    {
                        Check(output.Entries.Count == original.Entries.Count, "Les parties Office restent disponibles " + ext);
                        foreach (var entry in original.Entries)
                        {
                            byte[] bytes = OfficeTestRead(output.GetEntry(entry.FullName));
                            Check(!Encoding.UTF8.GetString(bytes).Contains("PRIVATE-"), "Suppression des marqueurs dans " + entry.FullName);
                            if (entry.FullName.StartsWith("pictures/") || entry.FullName.StartsWith("preview/"))
                            {
                                using (var content = new MemoryStream(bytes))
                                    Check(BitmapDecoder.Create(content, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0].PixelWidth == 1, "La photo reste décodable");
                            }
                            else if (!entry.FullName.StartsWith("information/"))
                                Check(OfficeTestRead(entry).SequenceEqual(bytes), "Texte, commentaires, formules, diapositives et relations conservés dans " + entry.FullName);
                        }
                    }
                    var again = new MetadataResult();
                    OfficeMetadata.Clean(clean, Path.Combine(dir, "second" + ext), ext, again, CancellationToken.None);
                    Check(!again.DocumentProperties && again.EmbeddedPhotos == 0, "Deuxième passage Office sans modification " + ext);
                    if (ext == ".xlsx") CheckWorkbookSharing(dir, clean);
                    string print = Path.Combine(dir, "print" + ext);
                    string printClean = Path.Combine(dir, "print-clean" + ext);
                    OfficeFixture(print, ext, "imprimante");
                    var printer = new MetadataResult();
                    OfficeMetadata.Clean(print, printClean, ext, printer, CancellationToken.None);
                    Check(printer.Summary().Contains("Profil d’imprimante"), "Profil d’imprimante identifié dans le bilan " + ext);
                    using (var output = new ZipArchive(File.OpenRead(printClean)))
                    {
                        Check(!output.Entries.Any(e => e.FullName.EndsWith(".bin")), "Profil d’imprimante privé retiré " + ext);
                        string xml = String.Join(" ", output.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))
                            .Select(e => Encoding.UTF8.GetString(OfficeTestRead(e))));
                        Check(!xml.Contains("printerSettings") && !xml.Contains("driver123"), "Références au profil d’imprimante retirées " + ext);
                        if (ext == ".xlsx") Check(xml.Contains("landscape") && xml.Contains("SUM(2,3)"), "Mise en page et formule conservées");
                    }
                }
                foreach (string defect in new[] { "joint", "signature", "absent", "image", "opaque", "doublon", "chiffré", "format", "référence", "externe" })
                {
                    string source = Path.Combine(dir, defect + ".docx");
                    if (defect == "chiffré") File.WriteAllBytes(source, new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 });
                    else OfficeFixture(source, ".docx", defect);
                    string message = "";
                    try { OfficeMetadata.Clean(source, Path.Combine(dir, defect + ".tmp"), defect == "format" ? ".xlsx" : ".docx", new MetadataResult(), CancellationToken.None); }
                    catch (InvalidDataException e) { message = e.Message; }
                    catch (NotSupportedException e) { message = e.Message; }
                    Check(message.Length > 10, "Explication pour un paquet " + defect);
                    if (defect == "chiffré" || defect == "signature" || defect == "joint")
                        Check(message.IndexOf(defect == "signature" ? "sign" : defect, StringComparison.OrdinalIgnoreCase) >= 0, "Motif du refus pour " + defect);
                }
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
