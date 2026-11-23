using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Text;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal static partial class OfficeMetadata
    {
        const string PackageNs = "http://schemas.openxmlformats.org/package/2006/";
        const string OfficeNs = "http://schemas.openxmlformats.org/officeDocument/2006/";
        const string StrictNs = "http://purl.oclc.org/ooxml/officeDocument/";
        const string OfficeType = "application/vnd.openxmlformats-officedocument.";
        const string RelsType = "application/vnd.openxmlformats-package.relationships+xml";
        const string Manifest = "[Content_Types].xml";

        sealed class Part
        {
            internal ZipArchiveEntry Entry;
            internal Uri Uri;
            internal string Type;
            internal bool Image;
            internal bool Properties;
            internal HashSet<string> RelationshipIds;
            internal HashSet<string> PrinterIds;

            internal void AddPrinterId(string id)
            {
                if (PrinterIds == null) PrinterIds = new HashSet<string>(StringComparer.Ordinal);
                PrinterIds.Add(id);
            }
        }

        static InvalidDataException InvalidPackage()
        {
            return new InvalidDataException("La structure du document Office est incomplète ou incohérente. La copie ne peut pas être vérifiée.");
        }

        static NotSupportedException Opaque()
        {
            return new NotSupportedException("Le document contient une pièce jointe, un objet ou un flux incorporé dont les métadonnées ne peuvent pas être vérifiées.");
        }

        static NotSupportedException UnsupportedImage()
        {
            return new NotSupportedException("Le document contient une image ou une vignette impossible à vérifier. Les photos JPEG, PNG et WebP sont prises en charge.");
        }

        static NotSupportedException Signed()
        {
            return new NotSupportedException("Ce document Office porte une signature numérique. Le nettoyage invaliderait cette signature.");
        }

        internal static void Clean(string input, string output, string extension, MetadataResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var file = File.OpenRead(input))
            {
                var signature = new byte[8];
                int length = file.Read(signature, 0, signature.Length);
                if (length >= 4 && signature[0] == 0xd0 && signature[1] == 0xcf && signature[2] == 0x11 && signature[3] == 0xe0)
                    throw new NotSupportedException("Le fichier utilise un conteneur Office chiffré ou un ancien format binaire. Son contenu ne peut pas être vérifié.");
                if (length < 4 || signature[0] != 'P' || signature[1] != 'K' || signature[2] != 3 || signature[3] != 4) throw InvalidPackage();
                if (length >= 8 && (signature[6] & 1) != 0)
                    throw new NotSupportedException("Le paquet Office est chiffré. Son contenu ne peut pas être vérifié.");
                file.Position = 0;
                try
                {
                    using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                        Rewrite(archive, output, extension.ToLowerInvariant(), result, token);
                }
                catch (XmlException) { throw new InvalidDataException("Une partie XML du document Office est illisible ou utilise une déclaration non prise en charge."); }
                catch (UriFormatException) { throw InvalidPackage(); }
                catch (ArgumentException) { throw InvalidPackage(); }
            }
        }

        static void Rewrite(ZipArchive archive, string output, string extension, MetadataResult result, CancellationToken token)
        {
            var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry manifest = null;
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Length < 0 || entry.Length > 1024L * 1024 * 1024 - expanded || parts.Count > 65535)
                    throw new NotSupportedException("Le document Office dépasse la limite de 1 Gio décompressé ou de 65 536 parties.");
                expanded += entry.Length;
                string name = entry.FullName;
                if (name.EndsWith("/", StringComparison.Ordinal) && entry.Length == 0) continue;
                if (name == Manifest)
                {
                    if (manifest != null) throw InvalidPackage();
                    manifest = entry;
                    continue;
                }
                string key = "/" + name;
                var uri = PackUriHelper.CreatePartUri(new Uri(key, UriKind.Relative));
                if (key != uri.OriginalString || parts.ContainsKey(key) || name.Equals(Manifest, StringComparison.OrdinalIgnoreCase)) throw InvalidPackage();
                string lower = key.ToLowerInvariant();
                if (lower.StartsWith("/_xmlsignatures/", StringComparison.Ordinal)) throw Signed();
                if (lower.Contains("/embeddings/") || lower.Contains("/activex/")) throw Opaque();
                parts.Add(key, new Part { Entry = entry, Uri = uri });
            }
            if (manifest == null || !parts.ContainsKey("/_rels/.rels")) throw InvalidPackage();
            ReadTypes(manifest, parts, token);
            Part main = null;
            bool hasPrinter = false;
            foreach (var part in parts.Values)
            {
                token.ThrowIfCancellationRequested();
                if (PrinterType(part.Type)) hasPrinter = true;
                bool rels = PackUriHelper.IsRelationshipPartUri(part.Uri);
                if (rels != (part.Type == RelsType)) throw InvalidPackage();
                if (rels) ReadRelationships(part, parts, ref main, token);
            }
            if (main == null || main.Type != MainType(extension))
                throw new InvalidDataException("Le document Office ne correspond pas au format indiqué par son extension.");

            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var clean = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var target = clean.CreateEntry(Manifest).Open())
                {
                    if (hasPrinter) FilterXml(manifest, target, XmlPart.Manifest, null, result, token);
                    else Copy(manifest, target, token);
                }
                foreach (var part in parts.Values)
                {
                    token.ThrowIfCancellationRequested();
                    if (PrinterType(part.Type))
                    {
                        result.Removed("Profil d’imprimante");
                        result.DocumentProperties = true;
                        continue;
                    }
                    using (var target = clean.CreateEntry(part.Entry.FullName).Open())
                    {
                        if (part.Type == RelsType && part.PrinterIds != null)
                            FilterXml(part.Entry, target, XmlPart.Relationships, part.PrinterIds, result, token);
                        else if (part.Type == RelsType) Copy(part.Entry, target, token);
                        else WritePart(part, target, part == main ? extension : null, result, token);
                    }
                }
            }
        }

        static string MainType(string extension)
        {
            switch (extension)
            {
                case ".docx": return OfficeType + "wordprocessingml.document.main+xml";
                case ".xlsx": return OfficeType + "spreadsheetml.sheet.main+xml";
                case ".pptx": return OfficeType + "presentationml.presentation.main+xml";
                default: throw InvalidPackage();
            }
        }

        static XmlReader Reader(Stream stream)
        {
            return XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024
            });
        }

        static void ReadIndex(ZipArchiveEntry entry, string ns, string root, Action<XmlReader> visit, CancellationToken token)
        {
            if (entry.Length > 16L * 1024 * 1024) throw new NotSupportedException("Un index Office dépasse la limite de vérification de 16 Mio.");
            using (var stream = entry.Open())
            using (var reader = Reader(stream))
            {
                reader.MoveToContent();
                if (reader.NamespaceURI != ns || reader.LocalName != root) throw InvalidPackage();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Depth != 1 || reader.NamespaceURI != ns) throw InvalidPackage();
                    visit(reader);
                }
            }
        }

        static void ReadTypes(ZipArchiveEntry manifest, Dictionary<string, Part> parts, CancellationToken token)
        {
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadIndex(manifest, PackageNs + "content-types", "Types", delegate(XmlReader reader)
            {
                string type = reader.GetAttribute("ContentType");
                if (String.IsNullOrWhiteSpace(type)) throw InvalidPackage();
                type = type.ToLowerInvariant();
                if (type.Contains("digital-signature")) throw Signed();
                if (reader.LocalName == "Default")
                {
                    string extension = reader.GetAttribute("Extension");
                    if (String.IsNullOrEmpty(extension) || defaults.ContainsKey(extension)) throw InvalidPackage();
                    defaults.Add(extension, type);
                }
                else if (reader.LocalName == "Override")
                {
                    string name = reader.GetAttribute("PartName");
                    Part part;
                    if (name == null || !parts.TryGetValue(name, out part) || part.Type != null) throw InvalidPackage();
                    part.Type = type;
                }
                else throw InvalidPackage();
            }, token);
            foreach (var part in parts.Values)
            {
                string extension = Path.GetExtension(part.Entry.FullName).TrimStart('.');
                if (part.Type == null && !defaults.TryGetValue(extension, out part.Type)) throw InvalidPackage();
                if (part.Type.Contains("oleobject") || part.Type.Contains("activex") || part.Type.Contains("vbaproject")) throw Opaque();
                part.Properties = part.Type == "application/vnd.openxmlformats-package.core-properties+xml"
                    || part.Type == OfficeType + "extended-properties+xml" || part.Type == OfficeType + "custom-properties+xml";
            }
        }

        static void ReadRelationships(Part relationships, Dictionary<string, Part> parts, ref Part main, CancellationToken token)
        {
            var owner = PackUriHelper.GetSourcePartUriFromRelationshipPartUri(relationships.Uri);
            bool package = owner.OriginalString == "/";
            Part ownerPart = null;
            if (!package && (!parts.TryGetValue(owner.OriginalString, out ownerPart) || PrinterType(ownerPart.Type))) throw InvalidPackage();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            Part foundMain = main;
            ReadIndex(relationships.Entry, PackageNs + "relationships", "Relationships", delegate(XmlReader reader)
            {
                string id = reader.GetAttribute("Id"), type = reader.GetAttribute("Type"), target = reader.GetAttribute("Target");
                if (reader.LocalName != "Relationship" || String.IsNullOrEmpty(id) || !ids.Add(id) ||
                    String.IsNullOrEmpty(type) || String.IsNullOrEmpty(target)) throw InvalidPackage();
                if (type.IndexOf("digital-signature", StringComparison.OrdinalIgnoreCase) >= 0) throw Signed();
                string role = type.Substring(type.LastIndexOf('/') + 1).ToLowerInvariant();
                if (role == "oleobject" || role == "package" || role == "afchunk" || role == "control" ||
                    role == "attachment" || role == "vbaproject" || role == "video" || role == "audio" || role == "media") throw Opaque();
                bool image = role == "image" || role == "thumbnail";
                bool properties = role == "core-properties" || role == "extended-properties" || role == "extendedproperties" ||
                    role == "custom-properties" || role == "customproperties";
                bool document = type == OfficeNs + "relationships/officeDocument" || type == StrictNs + "relationships/officeDocument";
                bool printer = type == OfficeNs + "relationships/printerSettings" || type == StrictNs + "relationships/printerSettings";
                string mode = reader.GetAttribute("TargetMode");
                if (mode == "External")
                {
                    if (image) throw new NotSupportedException("Une image liée à une ressource externe ne peut pas être vérifiée hors ligne.");
                    if (properties || document || printer) throw InvalidPackage();
                    return;
                }
                if (mode != null && mode != "Internal") throw InvalidPackage();
                int fragment = target.IndexOf('#');
                if (fragment >= 0) target = target.Substring(0, fragment);
                Uri targetUri = target.Length == 0 ? owner : PackUriHelper.ResolvePartUri(owner, new Uri(target, UriKind.Relative));
                Part destination;
                if (!parts.TryGetValue(targetUri.OriginalString, out destination) || destination.Type == RelsType) throw InvalidPackage();
                if (printer != PrinterType(destination.Type)) throw InvalidPackage();
                if (printer)
                {
                    if (ownerPart == null) throw InvalidPackage();
                    relationships.AddPrinterId(id);
                    ownerPart.AddPrinterId(id);
                }
                if (image) destination.Image = true;
                if (properties) destination.Properties = true;
                if (document)
                {
                    if (!package || foundMain != null) throw InvalidPackage();
                    foundMain = destination;
                }
            }, token);
            if (ownerPart != null) ownerPart.RelationshipIds = ids;
            main = foundMain;
        }

        static string PhotoSignature(ZipArchiveEntry entry)
        {
            var header = new byte[12];
            int n = 0;
            using (var stream = entry.Open())
                while (n < header.Length)
                {
                    int read = stream.Read(header, n, header.Length - n);
                    if (read == 0) break;
                    n += read;
                }
            if (n >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255) return ".jpg";
            if (n >= 8 && header[0] == 137 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G') return ".png";
            if (n >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP") return ".webp";
            return null;
        }

        static string ImageExtension(string type)
        {
            switch (type)
            {
                case "image/jpg": case "image/jpeg": case "image/pjpeg": case ".jpg": case ".jpeg": case ".jpe": case ".jfif": return ".jpg";
                case "image/png": case "image/x-png": case ".png": return ".png";
                case "image/webp": case ".webp": return ".webp";
                default: return null;
            }
        }

        static void WritePart(Part part, Stream output, string mainFormat, MetadataResult result, CancellationToken token)
        {
            string signature = PhotoSignature(part.Entry), fromType = ImageExtension(part.Type);
            string ext = Path.GetExtension(part.Entry.FullName).ToLowerInvariant();
            string fromName = ImageExtension(ext);
            bool imageType = part.Type.StartsWith("image/", StringComparison.Ordinal);
            if (part.Image || imageType || fromName != null || signature != null)
            {
                if (signature == null || (imageType && fromType != signature) || (fromName != null && fromName != signature) ||
                    part.Properties || mainFormat != null) throw UnsupportedImage();
                if (part.Entry.Length > 256L * 1024 * 1024) throw new NotSupportedException("Une photo intégrée dépasse la limite de vérification de 256 Mio.");
                using (var bytes = new MemoryStream())
                {
                    Copy(part.Entry, bytes, token);
                    bool changed;
                    byte[] cleaned = PhotoMetadata.Clean(bytes.ToArray(), signature, token, out changed, result);
                    output.Write(cleaned, 0, cleaned.Length);
                    if (changed) result.EmbeddedPhotos++;
                }
                return;
            }
            bool xml = part.Type == "application/xml" || part.Type == "text/xml" || part.Type.EndsWith("+xml", StringComparison.Ordinal)
                || part.Type == OfficeType + "vmldrawing" || part.Properties || ext == ".xml";
            if (!xml) throw Opaque();
            XmlPart kind = mainFormat == ".xlsx" ? XmlPart.Workbook : XmlPart.Document;
            bool privateExcelProperties = false;
            using (var stream = part.Entry.Open())
            using (var reader = Reader(stream))
            {
                reader.MoveToContent();
                string root = reader.LocalName, ns = reader.NamespaceURI;
                if (root == "pivotCacheDefinition" && FamilyNamespace(ns, "spreadsheetml")) kind = XmlPart.PivotCache;
                bool properties = (root == "coreProperties" && ns == PackageNs + "metadata/core-properties")
                    || (root == "Properties" && (ns == OfficeNs + "extended-properties" || ns == OfficeNs + "custom-properties"
                        || ns == StrictNs + "extendedProperties" || ns == StrictNs + "customProperties"));
                if (part.Properties && !properties) throw InvalidPackage();
                if (mainFormat != null) CheckMainRoot(mainFormat, root, ns);
                if (properties)
                {
                    WriteProperties(part.Entry, output, root, ns, result, token);
                    return;
                }
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        InspectElement(reader, part.RelationshipIds);
                        if (!privateExcelProperties && WorkbookEdit(reader, kind) != ExcelEdit.None) privateExcelProperties = true;
                    }
                } while (reader.Read());
            }
            if (privateExcelProperties) result.DocumentProperties = true;
            if (privateExcelProperties || part.PrinterIds != null) FilterXml(part.Entry, output, kind, part.PrinterIds, result, token);
            else Copy(part.Entry, output, token);
        }

        static void CheckMainRoot(string format, string root, string ns)
        {
            string family, expected;
            if (format == ".docx") { family = "wordprocessingml"; expected = "document"; }
            else if (format == ".xlsx") { family = "spreadsheetml"; expected = "workbook"; }
            else { family = "presentationml"; expected = "presentation"; }
            if (root != expected || !FamilyNamespace(ns, family)) throw InvalidPackage();
        }

        static void InspectElement(XmlReader reader, HashSet<string> ids)
        {
            string ns = reader.NamespaceURI, element = reader.LocalName;
            if (ns == "http://www.w3.org/2000/09/xmldsig#" && element == "Signature") throw Signed();
            if (ns == "http://www.w3.org/2000/svg" && element == "svg") throw UnsupportedImage();
            if (element == "binData" || element == "binaryData" || element == "OLEObject" || element == "oleObject" ||
                element == "altChunk" || element == "EncryptedData") throw Opaque();
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    if (IsRelationshipNamespace(reader.NamespaceURI) || (reader.NamespaceURI == "urn:schemas-microsoft-com:office:office" && reader.LocalName == "relid"))
                        if (ids == null || !ids.Contains(reader.Value)) throw InvalidPackage();
                    if (ns == "urn:schemas-microsoft-com:vml" && reader.LocalName == "src" && !String.IsNullOrWhiteSpace(reader.Value))
                        throw new NotSupportedException("L’adresse directe d’une image VML ne permet pas de vérifier sa source intégrée hors ligne.");
                    if ((reader.LocalName == "src" || reader.LocalName == "href") &&
                        reader.Value.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)) throw Opaque();
                }
                reader.MoveToElement();
            }
        }

        static void WriteProperties(ZipArchiveEntry entry, Stream output, string root, string ns, MetadataResult result, CancellationToken token)
        {
            bool changed = false;
            string field = null;
            using (var stream = entry.Open())
            using (var reader = Reader(stream))
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                    {
                        while (reader.MoveToNextAttribute())
                            if (reader.NamespaceURI != "http://www.w3.org/2000/xmlns/") changed = true;
                        reader.MoveToElement();
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1) field = null;
                    else if (reader.NodeType != XmlNodeType.Whitespace && reader.NodeType != XmlNodeType.XmlDeclaration &&
                        reader.NodeType != XmlNodeType.EndElement)
                    {
                        changed = true;
                        if (reader.Depth == 1 && reader.NodeType == XmlNodeType.Element)
                        {
                            field = PropertyLabel(ns, reader.NamespaceURI, reader.LocalName);
                            if (field == "Propriétés personnalisées" && !String.IsNullOrWhiteSpace(reader.GetAttribute("name")))
                                result.Removed(field);
                            if (reader.IsEmptyElement) field = null;
                        }
                        else if (field != null && (reader.NodeType == XmlNodeType.Text || reader.NodeType == XmlNodeType.CDATA)
                            && !String.IsNullOrWhiteSpace(reader.Value))
                        {
                            result.Removed(field);
                            field = null;
                        }
                    }
                }
            if (!changed) { Copy(entry, output, token); return; }
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            {
                writer.WriteStartElement(root, ns);
                writer.WriteEndElement();
            }
            result.DocumentProperties = true;
        }

        static void Copy(ZipArchiveEntry entry, Stream output, CancellationToken token)
        {
            byte[] buffer = new byte[65536];
            long total = 0;
            using (var input = entry.Open())
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    total += n;
                    if (total > entry.Length) throw InvalidPackage();
                    output.Write(buffer, 0, n);
                }
            }
            if (total != entry.Length) throw InvalidPackage();
        }
    }
}
