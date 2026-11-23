using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace AtelierPC
{
    internal static partial class PhotoMetadata
    {
        static readonly byte[] PngMagic = { 137, 80, 78, 71, 13, 10, 26, 10 };
        static readonly uint[] CrcTable = BuildCrcTable();
        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int j = 0; j < 8; j++)
                    value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
                table[i] = value;
            }

            return table;
        }

        static uint Crc(byte[] data, int position, int length, uint crc)
        {
            for (int end = position + length; position < end; position++)
                crc = CrcTable[(crc ^ data[position]) & 255] ^ (crc >> 8);
            return crc;
        }

        static void PngChunk(Stream output, string tag, byte[] data, int position, int length)
        {
            byte[] name = Ascii(tag);
            Write32(output, (uint)length, false);
            Put(output, name);
            output.Write(data, position, length);
            Write32(output, ~Crc(data, position, length, Crc(name, 0, 4, uint.MaxValue)), false);
        }

        static void PngChunk(Stream output, string tag, byte[] data)
        {
            PngChunk(output, tag, data, 0, data.Length);
        }

        static uint Adler(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        static byte[] Inflate(byte[] data, int position, int length)
        {
            Require(length >= 6 && (data[position] & 15) == 8 && (data[position + 1] & 32) == 0 && U16(data, position, false) % 31 == 0);
            using (var compressed = new MemoryStream(data, position + 2, length - 6))
            using (var inflater = new DeflateStream(compressed, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Require(output.Length + read <= MaxProfile);
                    output.Write(buffer, 0, read);
                }

                var result = output.ToArray();
                Require(Adler(result) == U32(data, position + length - 4, false));
                return result;
            }
        }

        static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9c);
                using (var z = new DeflateStream(output, CompressionMode.Compress, true))
                    Put(z, data);
                Write32(output, Adler(data), false);
                return output.ToArray();
            }
        }

        static int Zero(byte[] data, int position, int end)
        {
            int at = Array.IndexOf(data, (byte)0, position, end - position);
            Require(at >= position);
            return at;
        }

        static bool PngTextHeader(string tag, byte[] data, ref int position, int end)
        {
            if (tag == "zTXt")
            {
                Require(position < end && data[position++] == 0);
                return true;
            }
            if (tag == "iTXt")
            {
                Require(position <= end - 2 && data[position] <= 1 && data[position + 1] == 0);
                bool compressed = data[position] == 1;
                position = Zero(data, position + 2, end) + 1;
                position = Zero(data, position, end) + 1;
                return compressed;
            }
            return false;
        }

        static bool HasPngText(string tag, byte[] data, int position, int end)
        {
            try
            {
                bool compressed = PngTextHeader(tag, data, ref position, end);
                if (compressed)
                {
                    if (end - position < 6 || (data[position] & 15) != 8 || U16(data, position, false) % 31 != 0)
                        return false;
                    position += 2; // zlib header and trailing Adler-32 surround the deflate stream.
                    end -= 4;
                }
                using (var bytes = new MemoryStream(data, position, end - position))
                using (var inflater = compressed ? new DeflateStream(bytes, CompressionMode.Decompress) : null)
                using (var reader = new StreamReader((Stream)inflater ?? bytes, tag == "iTXt" ? Encoding.UTF8 : Encoding.GetEncoding(28591), false))
                {
                    var buffer = new char[512];
                    int read;
                    for (int total = 0; total < 65536 && (read = reader.Read(buffer, 0, buffer.Length)) > 0; total += read)
                        for (int i = 0; i < read; i++)
                            if (buffer[i] != 0 && !char.IsWhiteSpace(buffer[i])) return true;
                }
            }
            catch (InvalidDataException) { /* Malformed optional text can still be dropped as a block. */ }
            return false;
        }

        static void PngText(string tag, byte[] data, int position, int length, ExifDisplay display)
        {
            int end = position + length, keywordEnd = Zero(data, position, end);
            string key = Text(data, position, keywordEnd - position);
            position = keywordEnd + 1;
            bool legacyExif = key.Equals("Raw profile type exif", StringComparison.OrdinalIgnoreCase) || key.Equals("Raw profile type APP1", StringComparison.OrdinalIgnoreCase);
            if (!legacyExif && key != "XML:com.adobe.xmp" && key != "Raw profile type xmp")
            {
                if (display.Report == null) return;
                string label;
                switch (key.ToLowerInvariant())
                {
                    case "author": label = "Auteur"; break;
                    case "comment": label = "Commentaire"; break;
                    case "creation time": label = "Date de création"; break;
                    case "copyright": label = "Droits d’auteur"; break;
                    case "description": label = "Description"; break;
                    case "software": label = "Logiciel"; break;
                    case "title": label = "Titre"; break;
                    default: label = null; break;
                }
                display.Removed(label != null && HasPngText(tag, data, position, end) ? label : "Texte PNG");
                return;
            }
            bool compressed = PngTextHeader(tag, data, ref position, end);
            var text = compressed ? Inflate(data, position, end - position) : Slice(data, position, end - position);
            if (key == "XML:com.adobe.xmp")
            {
                ReadXmp(text, 0, text.Length, display);
                return;
            }

            // ImageMagick's textual "Raw profile" serialization: name, size, then hex lines.
            string[] lines = Encoding.ASCII.GetString(text).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int size;
            Require(lines.Length >= 3);
            Require(int.TryParse(lines[1].Trim(), out size));
            string hex = string.Concat(lines.Skip(2)).Trim();
            Require(size >= 0 && size <= MaxProfile && hex.Length == 2 * size);
            var bytes = new byte[size];
            try
            {
                for (int i = 0; i < size; i++)
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            catch (FormatException e)
            {
                throw new InvalidDataException(Invalid, e);
            }

            if (legacyExif)
            {
                ReadExif(bytes, 0, size, display);
                display.Removed("Bloc texte EXIF");
            }
            else
                ReadXmp(bytes, 0, size, display);
        }

        // APNG fdAT streams have the PNG image encoding. Validate each subframe with its own IHDR.
        static void CheckPngFrame(MemoryStream frame, CancellationToken token)
        {
            if (frame == null)
                return;
            PngChunk(frame, "IEND", new byte[0]);
            Decode(frame.ToArray(), "PNG", 1, token);
        }

        static byte[] Png(byte[] data, CancellationToken token, ExifDisplay display)
        {
            Require(data.Take(8).SequenceEqual(PngMagic));
            using (var output = new MemoryStream(data.Length))
            {
                Put(output, PngMagic);
                byte[] header = null, palette = null, transparency = null;
                int position = 8, exifPosition = 0, frames = 0, expectedFrames = 0, sequence = 0;
                bool sawImage = false, afterData = false, frameHasData = false;
                MemoryStream frame = null;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        Require(position <= data.Length - 12);
                        int payloadLength = Size(data, position, false), payloadOffset = position + 8;
                        Require(payloadLength <= data.Length - position - 12);
                        string tag = Text(data, position + 4, 4);
                        Require(~Crc(data, position + 4, payloadLength + 4, uint.MaxValue) == U32(data, payloadOffset + payloadLength, false));
                        Require(header != null || tag == "IHDR");
                        if (tag != "IDAT" && sawImage)
                            afterData = true;
                        bool retain = true;
                        switch (tag)
                        {
                            case "IHDR":
                                Require(header == null && payloadLength == 13 && Size(data, payloadOffset, false) > 0 && Size(data, payloadOffset + 4, false) > 0);
                                header = Slice(data, payloadOffset, payloadLength);
                                exifPosition = 33;
                                break;
                            case "IDAT":
                                Require(!afterData && frame == null);
                                sawImage = true;
                                frameHasData = true;
                                break;
                            case "IEND":
                                Require(payloadLength == 0 && sawImage && frames == expectedFrames && frameHasData);
                                CheckPngFrame(frame, token);
                                if (position < data.Length - 12) display.Removed("Données après l’image");
                                break;
                            case "PLTE":
                                Require(payloadLength >= 3 && payloadLength <= 768 && payloadLength % 3 == 0);
                                palette = Slice(data, payloadOffset, payloadLength);
                                break;
                            case "tRNS":
                                Require(payloadLength > 0 && payloadLength <= (header[9] == 3 ? 256 : header[9] == 2 ? 6 : 2));
                                transparency = Slice(data, payloadOffset, payloadLength);
                                break;
                            case "acTL":
                                Require(!sawImage && expectedFrames == 0 && payloadLength == 8);
                                expectedFrames = Size(data, payloadOffset, false);
                                Require(expectedFrames > 0);
                                break;
                            case "fcTL":
                                Require(expectedFrames > frames && payloadLength == 26 && Size(data, payloadOffset, false) == sequence++ && (frames == 0 || frameHasData) && data[payloadOffset + 24] <= 2 && data[payloadOffset + 25] <= 1);
                                CheckPngFrame(frame, token);
                                if (frame != null)
                                    frame.Dispose();
                                frames++;
                                frameHasData = false;
                                int frameWidth = Size(data, payloadOffset + 4, false), frameHeight = Size(data, payloadOffset + 8, false), x = Size(data, payloadOffset + 12, false), y = Size(data, payloadOffset + 16, false);
                                int canvasWidth = Size(header, 0, false), canvasHeight = Size(header, 4, false);
                                Require(frameWidth > 0 && frameHeight > 0 && x <= canvasWidth - frameWidth && y <= canvasHeight - frameHeight);
                                if (!sawImage)
                                {
                                    Require(frameWidth == canvasWidth && frameHeight == canvasHeight);
                                    break;
                                }

                                frame = new MemoryStream();
                                var frameHeader = (byte[])header.Clone();
                                Buffer.BlockCopy(data, payloadOffset + 4, frameHeader, 0, 8);
                                Put(frame, PngMagic);
                                PngChunk(frame, "IHDR", frameHeader);
                                if (palette != null)
                                    PngChunk(frame, "PLTE", palette);
                                if (transparency != null)
                                    PngChunk(frame, "tRNS", transparency);
                                break;
                            case "fdAT":
                                Require(frame != null && payloadLength >= 4 && Size(data, payloadOffset, false) == sequence++);
                                PngChunk(frame, "IDAT", data, payloadOffset + 4, payloadLength - 4);
                                frameHasData = true;
                                break;
                            case "eXIf":
                                ReadExif(data, payloadOffset, payloadLength, display);
                                retain = false;
                                break;
                            case "zTXt":
                            case "iTXt":
                            case "tEXt":
                                PngText(tag, data, payloadOffset, payloadLength, display);
                                retain = false;
                                break;
                            case "iCCP":
                                int nameEnd = Zero(data, payloadOffset, payloadOffset + payloadLength);
                                Require(nameEnd > payloadOffset && nameEnd <= payloadOffset + 79 && nameEnd < payloadOffset + payloadLength - 1 && data[nameEnd + 1] == 0);
                                if (Text(data, payloadOffset, nameEnd - payloadOffset) != "ICC")
                                    display.Removed("Nom du profil ICC");
                                byte[] profile = CleanIcc(Inflate(data, nameEnd + 2, payloadOffset + payloadLength - nameEnd - 2), display.Report);
                                PngChunk(output, "iCCP", Ascii("ICC\0\0").Concat(Deflate(profile)).ToArray());
                                retain = false;
                                break;
                            case "sRGB":
                                Require(payloadLength == 1);
                                break;
                            case "pHYs":
                                Require(payloadLength == 9);
                                break;
                            case "gAMA":
                            case "cICP":
                            case "cLLi":
                            case "mDCv":
                            case "cHRM":
                                Require(payloadLength == (tag == "mDCv" ? 24 : tag == "cHRM" ? 32 : tag == "cLLi" ? 8 : 4));
                                break;
                            case "sBIT":
                                Require(payloadLength == (header[9] == 6 ? 4 : header[9] == 4 ? 2 : header[9] == 0 ? 1 : 3));
                                break;
                            case "bKGD":
                                Require(payloadLength == (header[9] == 3 ? 1 : header[9] == 0 || header[9] == 4 ? 2 : 6));
                                break;
                            case "hIST":
                                Require(palette != null && payloadLength == 2 * palette.Length / 3);
                                break;
                            case "sTER":
                                Require(payloadLength == 1 && data[payloadOffset] <= 1);
                                break;
                            case "dSIG":
                            case "caBX":
                            case "gmAP":
                                throw new InvalidDataException(Unsupported);
                            default:
                                if ((data[position + 4] & 32) == 0)
                                    throw new InvalidDataException(Unsupported);
                                display.Removed(tag == "tIME" ? "Date de modification" : "Bloc annexe PNG");
                                retain = false;
                                break;
                        }

                        if (retain)
                            output.Write(data, position, payloadLength + 12);
                        if (tag == "IEND")
                            break;
                        position = payloadOffset + payloadLength + 4;
                    }

                    if (!display.Needed)
                        return output.ToArray();
                    using (var oriented = new MemoryStream())
                    {
                        var bytes = output.ToArray();
                        oriented.Write(bytes, 0, exifPosition);
                        PngChunk(oriented, "eXIf", Exif(display));
                        oriented.Write(bytes, exifPosition, bytes.Length - exifPosition);
                        return oriented.ToArray();
                    }
                }
                finally
                {
                    if (frame != null)
                        frame.Dispose();
                }
            }
        }
    }
}
