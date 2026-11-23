using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal sealed partial class WordWatermark : WatermarkSource
    {
        const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string StrictWord = "http://purl.oclc.org/ooxml/wordprocessingml/main";
        const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string StrictRel = "http://purl.oclc.org/ooxml/officeDocument/relationships";
        const string WordType = "application/vnd.openxmlformats-officedocument.wordprocessingml.";
        const string HeaderType = WordType + "header+xml";
        readonly string path;
        readonly FileStream sourceLock;
        readonly Uri mainUri;
        readonly string w, r;

        static InvalidDataException InvalidWord()
        {
            return new InvalidDataException("Le document Word est incomplet ou présente une structure incohérente.");
        }

        internal WordWatermark(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            this.path = path;
            sourceLock = File.OpenRead(path);
            try
            {
                CheckContainer(sourceLock, token);
                using (var pkg = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    foreach (var rel in pkg.GetRelationships())
                    {
                        if (rel.RelationshipType.Contains("/digital-signature/")) throw Signed();
                        if (rel.RelationshipType != OfficeRel + "/officeDocument" && rel.RelationshipType != StrictRel + "/officeDocument") continue;
                        if (mainUri != null || rel.TargetMode != TargetMode.Internal) throw InvalidWord();
                        mainUri = PackUriHelper.ResolvePartUri(rel.SourceUri, rel.TargetUri);
                    }
                    foreach (var part in pkg.GetParts())
                    {
                        token.ThrowIfCancellationRequested();
                        if (part.ContentType.IndexOf("digital-signature", StringComparison.OrdinalIgnoreCase) >= 0) throw Signed();
                    }
                    if (mainUri == null || !pkg.PartExists(mainUri)) throw InvalidWord();
                    var main = pkg.GetPart(mainUri);
                    if (main.ContentType != WordType + "document.main+xml") throw InvalidWord();
                    var doc = ReadXml(main);
                    w = doc.DocumentElement.NamespaceURI;
                    if ((w != WordNs && w != StrictWord) || doc.DocumentElement.LocalName != "document") throw InvalidWord();
                    r = w == StrictWord ? StrictRel : OfficeRel;
                    var sections = Sections(doc);
                    var size = PageSizes(sections)[0];
                    Width = size.Width / 15.0;
                    Height = size.Height / 15.0;
                }
                Extension = ".docx";
                Notice = "Simulation du placement";
            }
            catch { sourceLock.Dispose(); throw; }
        }

        public override void Dispose() { sourceLock.Dispose(); }

        static NotSupportedException Signed()
        {
            return new NotSupportedException("Ce document Word porte une signature numérique. L’ajout d’un filigrane invaliderait sa signature.");
        }

        static void CheckContainer(Stream stream, CancellationToken token)
        {
            var magic = new byte[8];
            int n = stream.Read(magic, 0, magic.Length);
            if ((n >= 4 && magic[0] == 0xd0 && magic[1] == 0xcf && magic[2] == 0x11 && magic[3] == 0xe0)
                || (n == 8 && magic[0] == 'P' && magic[1] == 'K' && (magic[6] & 1) != 0))
                throw new NotSupportedException("Ce document Word est chiffré ou utilise l’ancien format binaire. Enregistrez une copie .docx non chiffrée pour ajouter un filigrane.");
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                long bytes = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)) throw Signed();
                    if (entry.Length > 1024L * 1024 * 1024 - bytes || zip.Entries.Count > 65536)
                        throw new NotSupportedException("Ce document Word dépasse la limite de 1 Gio décompressé ou de 65 536 parties.");
                    bytes += entry.Length;
                }
            }
        }

        static XmlDocument ReadXml(PackagePart part)
        {
            var xml = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 }))
                xml.Load(reader);
            return xml;
        }

        static void SaveXml(PackagePart part, XmlDocument xml)
        {
            using (var stream = part.GetStream(FileMode.Create, FileAccess.Write)) xml.Save(stream);
        }

        List<XmlElement> Sections(XmlDocument doc)
        {
            var body = doc.DocumentElement["body", w];
            if (body == null) throw InvalidWord();
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("w", w);
            var sections = body.SelectNodes(".//w:p/w:pPr/w:sectPr[not(ancestor::w:txbxContent or ancestor::w:del or ancestor::w:pPrChange)]", ns).Cast<XmlElement>().ToList();
            var last = body["sectPr", w];
            if (last == null) last = (XmlElement)body.AppendChild(doc.CreateElement("w", "sectPr", w));
            sections.Add(last);
            return sections;
        }

        sealed class PageSize
        {
            internal int Width, Height;
            internal string Key { get { return Width + "x" + Height; } }
        }

        PageSize[] PageSizes(List<XmlElement> sections)
        {
            var sizes = new PageSize[sections.Count];
            var letter = new PageSize { Width = 12240, Height = 15840 };
            for (int i = sizes.Length - 1; i >= 0; i--)
            {
                var pg = sections[i]["pgSz", w];
                var type = sections[i]["type", w];
                if (pg == null && i + 1 < sizes.Length && type != null && type.GetAttribute("val", w) == "continuous") sizes[i] = sizes[i + 1];
                else if (pg == null) sizes[i] = letter;
                else
                {
                    bool landscape = pg.GetAttribute("orient", w) == "landscape";
                    sizes[i] = new PageSize { Width = Twips(pg.GetAttribute("w", w), landscape ? letter.Height : letter.Width),
                        Height = Twips(pg.GetAttribute("h", w), landscape ? letter.Width : letter.Height) };
                }
            }
            return sizes;
        }

        static int Twips(string value, int fallback)
        {
            if (value == "") return fallback;
            // Strict OOXML also permits an explicit measurement unit.
            double factor = 1;
            string suffix = value.Length >= 2 ? value.Substring(value.Length - 2) : "";
            switch (suffix)
            {
                case "in": factor = 1440; break;
                case "cm": factor = 1440 / 2.54; break;
                case "mm": factor = 1440 / 25.4; break;
                case "pt": factor = 20; break;
                case "pc": case "pi": factor = 240; break;
            }
            if (factor != 1) value = value.Substring(0, value.Length - 2);
            double amount;
            if (!Double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
                || amount * factor < 1 || amount * factor > Int32.MaxValue / 635) throw InvalidWord();
            return (int)Math.Round(amount * factor);
        }

        internal override void WriteCopy(string temporary, WatermarkOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }
            using (var original = Package.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var copy = Package.Open(temporary, FileMode.Open, FileAccess.ReadWrite))
            {
                var main = copy.GetPart(mainUri);
                var doc = ReadXml(main);
                var sections = Sections(doc);
                var sizes = PageSizes(sections);
                var state = new HeaderWriter(this, original, copy, main, options, token);
                state.MarkScannedPages(doc, sections, sizes);
                for (int i = 0; i < sections.Count; i++) state.MarkSection(sections[i], sizes[i]);
                SaveXml(main, doc);
            }
            token.ThrowIfCancellationRequested();
        }
    }
}
