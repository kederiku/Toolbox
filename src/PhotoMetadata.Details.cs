using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal static partial class PhotoMetadata
    {
        // Only inspect optional tags when their directory fits inside this TIFF payload.
        // Private directories could be malformed even in images the cleaner can safely copy.
        static int OptionalDirectory(byte[] data, int start, int length, bool littleEndian, uint offset)
        {
            if (offset < 8 || offset > length - 6) return -1;
            int directory = start + (int)offset;
            return 12 * U16(data, directory, littleEndian) <= length - (int)offset - 6 ? directory : -1;
        }

        static bool HasGpsCoordinates(byte[] data, int start, int length, bool littleEndian, uint offset)
        {
            int directory = OptionalDirectory(data, start, length, littleEndian, offset);
            if (directory < 0) return false;
            int count = U16(data, directory, littleEndian);
            for (int i = 0; i < count; i++)
            {
                int entry = directory + 2 + i * 12, tag = U16(data, entry, littleEndian);
                if ((tag == 2 || tag == 4 || tag == 20 || tag == 22) &&
                    U16(data, entry + 2, littleEndian) == 5 && U32(data, entry + 4, littleEndian) == 3 &&
                    U32(data, entry + 8, littleEndian) <= length - 24)
                    return true;
            }
            return false;
        }

        static string ExifTextLabel(int tag, bool subIfd)
        {
            if (subIfd)
            {
                switch (tag)
                {
                    case 0x9003: return "Date de prise de vue";
                    case 0x9004: return "Date de numérisation";
                    case 0xa431: case 0xa435: return "Numéro de série";
                    case 0xa434: return "Modèle d’objectif";
                    default: return null;
                }
            }
            switch (tag)
            {
                case 0x010e: return "Description";
                case 0x010f: return "Marque d’appareil";
                case 0x0110: return "Modèle d’appareil";
                case 0x0131: return "Logiciel";
                case 0x0132: return "Date de modification";
                case 0x013b: case 0x9c9d: return "Auteur";
                case 0x8298: return "Droits d’auteur";
                case 0x9c9b: return "Titre";
                case 0x9c9c: return "Commentaire";
                default: return null;
            }
        }

        static bool HasExifText(byte[] data, int start, int length, int entry, bool littleEndian, int tag)
        {
            bool wide = tag >= 0x9c9b && tag <= 0x9c9d; // XP tags hold UTF-16LE bytes in either TIFF byte order.
            uint size = U32(data, entry + 4, littleEndian);
            if (U16(data, entry + 2, littleEndian) != (wide ? 1 : 2) || size == 0 || size > length)
                return false;
            int position = entry + 8;
            if (size > 4)
            {
                uint offset = U32(data, position, littleEndian);
                if (offset > length - size) return false;
                position = start + (int)offset;
            }
            // Bound optional inspection if broken tags repeatedly point at oversized blank strings.
            int end = position + (int)System.Math.Min(size, 65536u);
            for (int i = position; i < end - (wide ? 1 : 0); i += wide ? 2 : 1)
            {
                char value = (char)(wide ? U16(data, i, true) : data[i]);
                if (value == 0) break;
                if (!char.IsWhiteSpace(value)) return true;
            }
            return false;
        }

        static void DescribeExifDirectory(byte[] data, int start, int length, bool littleEndian, int offset, bool subIfd, MetadataResult report)
        {
            int directory = start + offset, count = U16(data, directory, littleEndian);
            bool inspectedGps = false;
            for (int i = 0; i < count; i++)
            {
                int entry = directory + 2 + i * 12, tag = U16(data, entry, littleEndian);
                string label = ExifTextLabel(tag, subIfd);
                if (label != null)
                {
                    report.Removed(HasExifText(data, start, length, entry, littleEndian, tag) ? label : "Champ EXIF");
                    continue;
                }
                if (subIfd)
                {
                    switch (tag)
                    {
                        case 0xa001: case 0xa005: break; // Preserved colour space and interoperability pointer.
                        case 0x9286: report.Removed("Commentaire"); break;
                        default: report.Removed("Autres données EXIF"); break;
                    }
                    continue;
                }
                switch (tag)
                {
                    case 0x8769: break; // The nested EXIF directory is inspected separately.
                    case 0x0112:
                        if (U16(data, entry + 8, littleEndian) == 1) report.Removed("Orientation par défaut");
                        break;
                    case 0x8825:
                        if (inspectedGps) break; // A TIFF directory has at most one GPS pointer.
                        inspectedGps = true;
                        bool coordinates = U16(data, entry + 2, littleEndian) == 4 &&
                            U32(data, entry + 4, littleEndian) == 1 &&
                            HasGpsCoordinates(data, start, length, littleEndian, U32(data, entry + 8, littleEndian));
                        report.Removed(coordinates ? "Coordonnées GPS" : "Répertoire GPS");
                        break;
                    default: report.Removed("Autres données EXIF"); break;
                }
            }
            if (subIfd) return;
            uint next = U32(data, directory + 2 + 12 * count, littleEndian);
            int thumbnail = OptionalDirectory(data, start, length, littleEndian, next);
            if (thumbnail < 0) return;
            int thumbnailCount = U16(data, thumbnail, littleEndian);
            for (int i = 0; i < thumbnailCount; i++)
            {
                int tag = U16(data, thumbnail + 2 + i * 12, littleEndian);
                if (tag == 0x0201 || tag == 0x0111) { report.Removed("Vignette"); return; }
            }
            report.Removed("Répertoire EXIF secondaire");
        }

        internal static void DescribeXmp(byte[] data, int position, int length, MetadataResult report)
        {
            if (report == null) return;
            using (var stream = new MemoryStream(data, position, length))
                DescribeXmp(stream, report);
        }

        internal static void DescribeXmp(Stream stream, MetadataResult report, CancellationToken token = default(CancellationToken))
        {
            if (report == null) return;
            token.ThrowIfCancellationRequested();
            report.Removed("Métadonnées XMP");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(stream, settings))
                {
                    // One pending depth per known category, even for deeply nested RDF containers.
                    var pending = new Dictionary<string, int>();
                    var buffer = new char[512];
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Element:
                                string label = XmpLabel(reader.NamespaceURI, reader.LocalName);
                                if (!reader.IsEmptyElement && label != null && !pending.ContainsKey(label))
                                    pending.Add(label, reader.Depth);
                                bool resourceValue = false;
                                while (reader.MoveToNextAttribute())
                                {
                                    string attribute = XmpLabel(reader.NamespaceURI, reader.LocalName);
                                    bool resource = reader.NamespaceURI == "http://www.w3.org/1999/02/22-rdf-syntax-ns#" &&
                                        (reader.LocalName == "resource" || reader.LocalName == "value");
                                    if ((attribute != null || resource) && XmpHasValue(reader, buffer, token))
                                    {
                                        if (attribute != null) report.Removed(attribute);
                                        if (resource) resourceValue = true;
                                    }
                                }
                                reader.MoveToElement();
                                if (resourceValue)
                                {
                                    if (label != null) report.Removed(label);
                                    ReportXmpValues(pending, report);
                                }
                                break;
                            case XmlNodeType.CDATA:
                            case XmlNodeType.Text:
                                if (pending.Count != 0 && XmpHasValue(reader, buffer, token))
                                    ReportXmpValues(pending, report);
                                break;
                            case XmlNodeType.EndElement:
                                string closing = XmpLabel(reader.NamespaceURI, reader.LocalName);
                                int depth;
                                if (closing != null && pending.TryGetValue(closing, out depth) && depth == reader.Depth)
                                    pending.Remove(closing);
                                break;
                        }
                    }
                }
            }
            catch (XmlException) { /* An unreadable packet is still removed as XMP. */ }
        }

        static bool XmpHasValue(XmlReader reader, char[] buffer, CancellationToken token)
        {
            int count;
            while ((count = reader.ReadValueChunk(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                for (int i = 0; i < count; i++)
                    if (!char.IsWhiteSpace(buffer[i])) return true;
            }
            return false;
        }

        static void ReportXmpValues(Dictionary<string, int> pending, MetadataResult report)
        {
            foreach (string label in pending.Keys) report.Removed(label);
            pending.Clear();
        }

        static string XmpLabel(string ns, string name)
        {
            switch (ns)
            {
                case "http://purl.org/dc/elements/1.1/":
                    switch (name)
                    {
                        case "creator": return "Auteur";
                        case "rights": return "Droits d’auteur";
                        case "title": return "Titre";
                        case "description": return "Description";
                        case "subject": return "Mots-clés";
                    }
                    break;
                case "http://ns.adobe.com/tiff/1.0/":
                    switch (name)
                    {
                        case "Make": return "Marque d’appareil";
                        case "Model": return "Modèle d’appareil";
                        case "Artist": return "Auteur";
                        case "Software": return "Logiciel";
                        case "DateTime": return "Date de modification";
                    }
                    break;
                case "http://ns.adobe.com/exif/1.0/":
                    switch (name)
                    {
                        case "GPSLatitude": case "GPSLongitude": case "GPSDestLatitude": case "GPSDestLongitude":
                            return "Coordonnées GPS";
                        case "DateTimeOriginal": return "Date de prise de vue";
                        case "DateTimeDigitized": return "Date de numérisation";
                        case "UserComment": return "Commentaire";
                    }
                    break;
                case "http://ns.adobe.com/xap/1.0/":
                    switch (name)
                    {
                        case "CreateDate": return "Date de création";
                        case "ModifyDate": return "Date de modification";
                        case "MetadataDate": return "Date des métadonnées";
                        case "CreatorTool": return "Logiciel";
                        case "Thumbnails": return "Vignette";
                    }
                    break;
            }
            return null;
        }

        static void DescribePhotoshop(byte[] data, int position, int length, MetadataResult report)
        {
            if (report == null) return;
            int end = position + length;
            report.Removed("Bloc Photoshop");
            while (position <= end - 12 && Starts(data, position, end - position, "8BIM"))
            {
                int id = U16(data, position + 4, false);
                int nameLength = 1 + data[position + 6]; // Pascal string padded to even length.
                position += 6 + nameLength + (nameLength & 1);
                if (position > end - 4) return;
                uint size = U32(data, position, false);
                position += 4;
                if (size > end - position) return;
                if (id == 1028) DescribeIptc(data, position, (int)size, report);
                else if (id == 1033 || id == 1036) report.Removed("Vignette");
                else if (id == 1060) DescribeXmp(data, position, (int)size, report);
                position += (int)size + (int)(size & 1);
            }
        }

        static void DescribeIptc(byte[] data, int position, int length, MetadataResult report)
        {
            report.Removed("Métadonnées IPTC");
            int end = position + length;
            while (position <= end - 5 && data[position] == 0x1c)
            {
                int record = data[position + 1], field = data[position + 2], size = U16(data, position + 3, false);
                position += 5;
                if ((size & 0x8000) != 0 || size > end - position) return;
                if (record == 2 && size != 0)
                {
                    switch (field)
                    {
                        case 25: report.Removed("Mots-clés"); break;
                        case 55: report.Removed("Date de création"); break;
                        case 80: report.Removed("Auteur"); break;
                        case 116: report.Removed("Droits d’auteur"); break;
                        case 120: report.Removed("Description"); break;
                    }
                }
                position += size;
            }
        }
    }
}
