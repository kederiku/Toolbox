using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal static partial class PhotoMetadata
    {
        const string Invalid = "L’image est illisible ou sa structure ne correspond pas au format choisi.";
        const string Unsupported = "Cette variante de l’image contient des données de rendu ou une signature dont la conservation ne peut pas être vérifiée.";
        // Reuse the bundled decoder for the images and the verification pass of one document.
        [ThreadStatic]
        static Batch activeBatch;
        sealed class Batch : IDisposable
        {
            readonly Batch previous;
            internal ImageEngine Engine;
            internal Batch()
            {
                previous = activeBatch;
                activeBatch = this;
            }

            public void Dispose()
            {
                activeBatch = previous;
                if (Engine != null)
                    Engine.Dispose();
            }
        }

        internal static IDisposable BeginBatch()
        {
            return activeBatch == null ? new Batch() : null;
        }

        internal static byte[] Clean(byte[] data, string extension, CancellationToken token, out bool changed, MetadataResult result = null)
        {
            token.ThrowIfCancellationRequested();
            Require(data != null && data.Length >= 12 && extension != null);
            using (BeginBatch())
            {
                byte[] cleaned;
                var display = new ExifDisplay { Report = result };
                int frames = 1;
                string coder;
                switch (extension.ToLowerInvariant())
                {
                    case ".jpg":
                    case ".jpeg":
                        coder = "JPEG";
                        cleaned = Jpeg(data, token, display);
                        break;
                    case ".png":
                        coder = "PNG";
                        cleaned = Png(data, token, display);
                        break;
                    case ".webp":
                        coder = "WEBP";
                        cleaned = Webp(data, token, display, out frames);
                        break;
                    default:
                        throw new InvalidDataException(Invalid);
                }

                Decode(cleaned, coder, frames, token);
                changed = !data.SequenceEqual(cleaned);
                return changed ? cleaned : data;
            }
        }

        static void Decode(byte[] data, string coder, int frames, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (activeBatch.Engine == null)
                activeBatch.Engine = new ImageEngine();
            var engine = activeBatch.Engine;
            string path = Path.Combine(engine.DirectoryPath, "controle-image");
            try
            {
                File.WriteAllBytes(path, data);
                // The pixel signature forces every scanline of every frame to be decoded.
                string output = engine.Run("identify -format \"%#\\n\" " + coder + ":controle-image", Invalid, token);
                var hashes = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Require(hashes.Length == frames && hashes.All(s => s.Length == 64));
            }
            finally
            {
                File.Delete(path);
            }
        }

        static void Require(bool value)
        {
            if (!value)
                throw new InvalidDataException(Invalid);
        }

        static string Text(byte[] data, int position, int length)
        {
            return Encoding.ASCII.GetString(data, position, length);
        }

        static byte[] Ascii(string s)
        {
            return Encoding.ASCII.GetBytes(s);
        }

        static byte[] Slice(byte[] data, int position, int length)
        {
            var b = new byte[length];
            Buffer.BlockCopy(data, position, b, 0, length);
            return b;
        }

        static bool Starts(byte[] data, int position, int length, string s)
        {
            return length >= s.Length && Text(data, position, s.Length) == s;
        }

        static int U16(byte[] data, int position, bool littleEndian)
        {
            return littleEndian ? data[position] | data[position + 1] << 8 : data[position] << 8 | data[position + 1];
        }

        static uint U32(byte[] data, int position, bool littleEndian)
        {
            return littleEndian ? (uint)(U16(data, position, true) | U16(data, position + 2, true) << 16) : (uint)(U16(data, position, false) << 16 | U16(data, position + 2, false));
        }

        static int Size(byte[] data, int position, bool littleEndian)
        {
            uint value = U32(data, position, littleEndian);
            Require(value <= int.MaxValue);
            return (int)value;
        }

        static void Write32(Stream output, uint length, bool littleEndian)
        {
            for (int i = 0; i < 4; i++)
                output.WriteByte((byte)(length >> ((littleEndian ? i : 3 - i) * 8)));
        }

        static void Put(Stream output, byte[] b)
        {
            output.Write(b, 0, b.Length);
        }

        static void SetOrientation(ref int value, int next)
        {
            Require(next >= 1 && next <= 8 && (value == 0 || value == next));
            value = next;
        }

        sealed class ExifDisplay
        {
            internal MetadataResult Report;
            internal void Removed(string label)
            {
                if (Report != null) Report.Removed(label);
            }

            internal int Orientation, ColorSpace = -1;
            internal string Interop;
            internal bool Needed
            {
                get
                {
                    return Orientation > 1 || ColorSpace >= 0 || Interop != null;
                }
            }
        }

        static int TiffEntry(byte[] data, int position, int length, bool littleEndian, int offset, int tag)
        {
            Require(offset >= 8 && offset <= length - 2);
            int count = U16(data, position + offset, littleEndian), found = -1;
            Require(length - offset >= 6 + 12 * count);
            for (int i = 0; i < count; i++)
            {
                int entryOffset = position + offset + 2 + i * 12;
                if (U16(data, entryOffset, littleEndian) == tag)
                {
                    Require(found < 0);
                    found = entryOffset;
                }
            }

            return found;
        }

        static int TiffNumber(byte[] data, int entryOffset, bool littleEndian, int type)
        {
            Require(U16(data, entryOffset + 2, littleEndian) == type && U32(data, entryOffset + 4, littleEndian) == 1);
            return type == 3 ? U16(data, entryOffset + 8, littleEndian) : Size(data, entryOffset + 8, littleEndian);
        }

        static void ReadExif(byte[] data, int position, int length, ExifDisplay display)
        {
            if (Starts(data, position, length, "Exif\0\0"))
            {
                position += 6;
                length -= 6;
            }

            Require(length >= 8);
            bool littleEndian = Text(data, position, 2) == "II";
            Require((littleEndian || Text(data, position, 2) == "MM") && U16(data, position + 2, littleEndian) == 42);
            int offset = Size(data, position + 4, littleEndian);
            int entryOffset = TiffEntry(data, position, length, littleEndian, offset, 274);
            if (display.Report != null)
                DescribeExifDirectory(data, position, length, littleEndian, offset, false, display.Report);
            if (entryOffset >= 0)
                SetOrientation(ref display.Orientation, TiffNumber(data, entryOffset, littleEndian, 3));
            entryOffset = TiffEntry(data, position, length, littleEndian, offset, 34665);
            if (entryOffset < 0)
                return;
            offset = TiffNumber(data, entryOffset, littleEndian, 4);
            entryOffset = TiffEntry(data, position, length, littleEndian, offset, 40961);
            if (display.Report != null)
                DescribeExifDirectory(data, position, length, littleEndian, offset, true, display.Report);
            if (entryOffset >= 0)
            {
                int colorSpace = TiffNumber(data, entryOffset, littleEndian, 3);
                Require(display.ColorSpace < 0 || display.ColorSpace == colorSpace);
                display.ColorSpace = colorSpace;
            }

            entryOffset = TiffEntry(data, position, length, littleEndian, offset, 40965);
            if (entryOffset < 0)
                return;
            offset = TiffNumber(data, entryOffset, littleEndian, 4);
            entryOffset = TiffEntry(data, position, length, littleEndian, offset, 1);
            if (entryOffset < 0)
                return;
            Require(U16(data, entryOffset + 2, littleEndian) == 2 && U32(data, entryOffset + 4, littleEndian) == 4 && data[entryOffset + 11] == 0);
            string index = Text(data, entryOffset + 8, 3);
            if (index != "R98" && index != "R03" && index != "THM")
                throw new InvalidDataException(Unsupported);
            Require(display.Interop == null || display.Interop == index);
            display.Interop = index;
        }

        static void WriteTiffNumber(BinaryWriter writer, ushort tag, ushort type, int number)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(1);
            writer.Write(number);
        }

        static byte[] Exif(ExifDisplay display)
        {
            bool color = display.ColorSpace >= 0, index = display.Interop != null, orientation = display.Orientation > 1;
            int rootCount = (orientation ? 1 : 0) + (color || index ? 1 : 0), subCount = (color ? 1 : 0) + (index ? 1 : 0);
            int subOffset = 14 + 12 * rootCount;
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data))
            {
                writer.Write(Ascii("II"));
                writer.Write((ushort)42);
                writer.Write(8);
                writer.Write((ushort)rootCount);
                if (orientation)
                    WriteTiffNumber(writer, 274, 3, display.Orientation);
                if (subCount > 0)
                    WriteTiffNumber(writer, 34665, 4, subOffset);
                writer.Write(0);
                if (subCount > 0)
                {
                    writer.Write((ushort)subCount);
                    if (color)
                        WriteTiffNumber(writer, 40961, 3, display.ColorSpace);
                    if (index)
                        WriteTiffNumber(writer, 40965, 4, subOffset + 6 + 12 * subCount);
                    writer.Write(0);
                }

                if (index)
                {
                    writer.Write((ushort)1);
                    writer.Write((ushort)1);
                    writer.Write((ushort)2);
                    writer.Write(4);
                    writer.Write(Ascii(display.Interop));
                    writer.Write((byte)0);
                    writer.Write(0);
                }

                return data.ToArray();
            }
        }

        static void ReadXmp(byte[] data, int position, int length, ExifDisplay display)
        {
            DescribeXmp(data, position, length, display.Report);
            string xml = Encoding.UTF8.GetString(data, position, length).TrimEnd('\0');
            if (xml.IndexOf("hdr-gain-map", StringComparison.OrdinalIgnoreCase) >= 0 || xml.IndexOf("GainMap", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException(Unsupported);
            if (!xml.Contains("Orientation"))
                return;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            try
            {
                var doc = new XmlDocument
                {
                    XmlResolver = null
                };
                using (var r = XmlReader.Create(new StringReader(xml), settings))
                    doc.Load(r);
                foreach (XmlNode node in doc.SelectNodes("//*[local-name()='Orientation'] | //@*[local-name()='Orientation']"))
                {
                    if (node.NamespaceURI != "http://ns.adobe.com/tiff/1.0/")
                        continue;
                    int value;
                    Require(int.TryParse(node.InnerText, out value));
                    SetOrientation(ref display.Orientation, value);
                }
            }
            catch (XmlException e)
            {
                throw new InvalidDataException(Invalid, e);
            }
        }

        sealed class JpegPart
        {
            internal int Marker, Start, End, Payload;
            internal JpegPart(int marker, int start, int end, int payload)
            {
                Marker = marker;
                Start = start;
                End = end;
                Payload = payload;
            }
        }

        static void JpegSegment(Stream output, int marker, byte[] payload)
        {
            int length = payload.Length + 2;
            output.WriteByte(255);
            output.WriteByte((byte)marker);
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)length);
            Put(output, payload);
        }

        static byte[] Jpeg(byte[] data, CancellationToken token, ExifDisplay display)
        {
            Require(data[0] == 255 && data[1] == 216);
            var parts = new List<JpegPart>();
            var profile = new SortedDictionary<int, byte[]>();
            int position = 2, profileParts = 0;
            bool sawScan = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Require(position < data.Length && data[position] == 255);
                int start = position;
                while (position < data.Length && data[position] == 255)
                    position++;
                Require(position < data.Length);
                int marker = data[position++];
                if (marker == 217)
                {
                    Require(sawScan);
                    if (position < data.Length) display.Removed("Données après l’image");
                    for (int i = position; i < data.Length - 1; i++)
                        if (data[i] == 255 && data[i + 1] == 216)
                            throw new InvalidDataException(Unsupported);
                    break;
                }

                Require(position <= data.Length - 2);
                int segmentLength = U16(data, position, false);
                Require(segmentLength >= 2 && segmentLength <= data.Length - position);
                int payloadOffset = position + 2, payloadLength = segmentLength - 2;
                position += segmentLength;
                var part = new JpegPart(marker, start, position, payloadOffset);
                parts.Add(part);
                if (marker == 225 && Starts(data, payloadOffset, payloadLength, "Exif\0\0"))
                    ReadExif(data, payloadOffset, payloadLength, display);
                else if (marker == 225 && Starts(data, payloadOffset, payloadLength, "http://ns.adobe.com/xap/1.0/\0"))
                    ReadXmp(data, payloadOffset + 29, payloadLength - 29, display);
                else if (marker == 226 && Starts(data, payloadOffset, payloadLength, "ICC_PROFILE\0"))
                {
                    Require(payloadLength >= 14);
                    int sequence = data[payloadOffset + 12], totalParts = data[payloadOffset + 13];
                    Require(sequence > 0 && sequence <= totalParts && (profileParts == 0 || profileParts == totalParts) && !profile.ContainsKey(sequence));
                    profileParts = totalParts;
                    profile.Add(sequence, Slice(data, payloadOffset + 14, payloadLength - 14));
                }
                else if ((marker == 226 && Starts(data, payloadOffset, payloadLength, "MPF\0")) || marker == 235 || (marker == 225 && Starts(data, payloadOffset, payloadLength, "http://ns.adobe.com/xmp/extension/\0")))
                    throw new InvalidDataException(Unsupported);
                else if (marker == 218)
                {
                    Require(payloadLength >= 4 && payloadLength == 4 + 2 * data[payloadOffset]);
                    sawScan = true;
                    int scan = position;
                    while (position < data.Length)
                    {
                        int nextMarker = Array.IndexOf(data, (byte)255, position);
                        Require(nextMarker >= 0);
                        position = nextMarker + 1;
                        while (position < data.Length && data[position] == 255)
                            position++;
                        Require(position < data.Length);
                        if (data[position] != 0 && (data[position] < 208 || data[position] > 215))
                        {
                            position = nextMarker;
                            break;
                        }

                        position++;
                    }

                    Require(position < data.Length);
                    parts.Add(new JpegPart(-1, scan, position, scan));
                }
                else if (marker < 224 && marker != 192 && marker != 193 && marker != 194 && marker != 196 && marker != 219 && marker != 221)
                    throw new InvalidDataException(Unsupported);
                else if (marker > 239 && marker != 254)
                    throw new InvalidDataException(Unsupported);
                else if (marker == 254)
                    display.Removed("Commentaire");
                else if (marker == 237 && Starts(data, payloadOffset, payloadLength, "Photoshop 3.0\0"))
                    DescribePhotoshop(data, payloadOffset + 14, payloadLength - 14, display.Report);
                else if (marker == 224 && Starts(data, payloadOffset, payloadLength, "JFXX\0"))
                    display.Removed("Vignette");
                else if (marker >= 224 && marker <= 239 &&
                    !(marker == 224 && Starts(data, payloadOffset, payloadLength, "JFIF\0")) &&
                    !(marker == 238 && Starts(data, payloadOffset, payloadLength, "Adobe")))
                    display.Removed("Bloc annexe JPEG");
            }

            Require(profile.Count == profileParts);
            byte[] profileBytes = profileParts == 0 ? null : CleanIcc(profile.Values.SelectMany(s => s).ToArray(), display.Report);
            using (var output = new MemoryStream(data.Length))
            {
                output.WriteByte(255);
                output.WriteByte(216);
                bool wroteExif = false;
                foreach (var part in parts)
                {
                    int marker = part.Marker, payloadOffset = part.Payload, payloadLength = part.End - payloadOffset;
                    if (marker == 224 && Starts(data, payloadOffset, payloadLength, "JFIF\0"))
                    {
                        Require(payloadLength >= 14 && payloadLength >= 14 + 3 * data[payloadOffset + 12] * data[payloadOffset + 13]);
                        if (data[payloadOffset + 12] != 0 && data[payloadOffset + 13] != 0)
                            display.Removed("Vignette");
                        if (payloadLength > 14 + 3 * data[payloadOffset + 12] * data[payloadOffset + 13])
                            display.Removed("Données annexes JPEG");
                        var jfif = Slice(data, payloadOffset, 14);
                        jfif[12] = jfif[13] = 0;
                        JpegSegment(output, marker, jfif);
                    }
                    else if (marker == 238 && Starts(data, payloadOffset, payloadLength, "Adobe"))
                    {
                        Require(payloadLength >= 12);
                        if (payloadLength > 12) display.Removed("Données annexes JPEG");
                        JpegSegment(output, marker, Slice(data, payloadOffset, 12));
                    }
                    else if (marker == 225 && display.Needed && !wroteExif)
                    {
                        JpegSegment(output, 225, Ascii("Exif\0\0").Concat(Exif(display)).ToArray());
                        wroteExif = true;
                    }
                    else if (marker == 226 && profileBytes != null && Starts(data, payloadOffset, payloadLength, "ICC_PROFILE\0"))
                    {
                        const int maximum = 65519;
                        int count = (profileBytes.Length + maximum - 1) / maximum;
                        Require(count <= 255);
                        for (int i = 0; i < count; i++)
                        {
                            byte[] header = Ascii("ICC_PROFILE\0").Concat(new byte[] { (byte)(i + 1), (byte)count }).ToArray();
                            int profileOffset = i * maximum;
                            JpegSegment(output, 226, header.Concat(Slice(profileBytes, profileOffset, Math.Min(maximum, profileBytes.Length - profileOffset))).ToArray());
                        }

                        profileBytes = null;
                    }
                    else if (marker < 224)
                        output.Write(data, part.Start, part.End - part.Start);
                }

                output.WriteByte(255);
                output.WriteByte(217);
                return output.ToArray();
            }
        }
    }
}
