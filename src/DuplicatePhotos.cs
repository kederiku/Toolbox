using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal sealed class DuplicatePhotos : IDisposable
    {
        const string Error = "Impossible de vérifier les pixels de cette photo.";
        ImageEngine engine;
        DuplicateFile lastExamined;
        DuplicateFile staged;
        bool singleImage;

        internal static string Identify(Stream input)
        {
            var bytes = new byte[32];
            int length = 0, read;
            while (length < bytes.Length && (read = input.Read(bytes, length, bytes.Length - length)) > 0) length += read;
            string header = Encoding.GetEncoding(28591).GetString(bytes, 0, length);
            if (header.StartsWith("\u0089PNG\r\n\u001a\n", StringComparison.Ordinal)) return "PNG";
            if (header.StartsWith("\u00ff\u00d8\u00ff", StringComparison.Ordinal)) return "JPEG";
            if (header.StartsWith("BM", StringComparison.Ordinal)) return "BMP";
            if (header.StartsWith("GIF87a", StringComparison.Ordinal) || header.StartsWith("GIF89a", StringComparison.Ordinal)) return "GIF";
            if (header.StartsWith("II*\0", StringComparison.Ordinal) || header.StartsWith("MM\0*", StringComparison.Ordinal) ||
                header.StartsWith("II+\0", StringComparison.Ordinal) || header.StartsWith("MM\0+", StringComparison.Ordinal)) return "TIFF";
            if (header.StartsWith("RIFF", StringComparison.Ordinal) && length >= 12 && header.Substring(8, 4) == "WEBP") return "WEBP";
            if (header.StartsWith("qoif", StringComparison.Ordinal)) return "QOI";
            if (header.StartsWith("%PDF-", StringComparison.Ordinal)) return "PDF";
            if (header.StartsWith("8BPS", StringComparison.Ordinal)) return "PSD";
            if (header.StartsWith("\u00ff\u000a", StringComparison.Ordinal) ||
                header.StartsWith("\0\0\0\u000cJXL \r\n\u0087\n", StringComparison.Ordinal)) return "JXL";
            if (length >= 12 && header.Substring(4, 4) == "ftyp")
            {
                switch (header.Substring(8, 4))
                {
                    case "avif": case "avis": return "AVIF";
                    case "heic": case "heix": case "hevc": case "hevx": case "mif1": case "msf1": return "HEIF";
                }
            }
            return null;
        }

        internal static bool CanDecode(string format)
        {
            return format == "PNG" || format == "JPEG" || format == "BMP" || format == "GIF" ||
                format == "TIFF" || format == "WEBP" || format == "QOI" || format == "AVIF";
        }

        string CopyInput(DuplicateFile file, CancellationToken token)
        {
            if (engine == null) engine = new ImageEngine();
            string path = Path.Combine(engine.DirectoryPath, "photo.bin");
            staged = null;
            File.Delete(path);
            ImageEngine.CopyFile(file.FullPath, path, token);
            staged = file;
            return path;
        }

        internal string Read(DuplicateFile file, CancellationToken token)
        {
            lastExamined = file;
            singleImage = false;
            string path = CopyInput(file, token);
            string source = file.Format + ":photo.bin";
            try
            {
                // Optional identify properties can be absent. The full pixel decode below still rejects decoder warnings.
                string info = engine.Run("identify -quiet -ping -format \"%w|%h|%[orientation]|%z|%[colorspace]|%[gamma]|%[profiles]|%[exif:ColorSpace]|%[exif:InteroperabilityIndex]|%W|%H|%X|%Y|%[tiff:photometric]\\n\" " + source, Error, token);
                var lines = info.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 1) return null;
                if (lines.Length == 0) throw new InvalidDataException(Error);
                var data = lines[0].Split('|');
                int width, height, depth;
                if (data.Length != 14 || !int.TryParse(data[0], out width) || !int.TryParse(data[1], out height) ||
                    !int.TryParse(data[3], out depth) || width <= 0 || height <= 0) throw new InvalidDataException(Error);
                bool swap = data[2] == "LeftTop" || data[2] == "RightTop" || data[2] == "RightBottom" || data[2] == "LeftBottom";
                bool commonColor = true;
                using (var stream = File.OpenRead(path))
                {
                    if (file.Format == "AVIF")
                    {
                        var avif = DuplicateAvif.Inspect(stream, token);
                        if (avif == DuplicateAvif.ImageKind.Unsupported) return null;
                        commonColor = avif == DuplicateAvif.ImageKind.Standard8Bit;
                    }
                    if (file.Format == "PNG")
                    {
                        var header = ReadPngHeader(stream, false, token);
                        if (header.Animated) return null;
                        commonColor = header.CommonColor;
                    }
                    if (file.Format == "JPEG" && !JpegIsStill(stream, token)) return null;
                    if (file.Format == "BMP") commonColor = BmpFitsEightBits(stream);
                }
                file.Width = swap ? height : width;
                file.Height = swap ? width : height;
                singleImage = true;
                // The embedded Q8 build cannot distinguish low-order bits of deeper source samples.
                bool icc = data[6].Split(',').Any(p => p.Trim().Equals("icc", StringComparison.OrdinalIgnoreCase) ||
                    p.Trim().Equals("icm", StringComparison.OrdinalIgnoreCase));
                if (!commonColor || depth < 1 || depth > 8 || data[13] == "palette" || (data[4] != "sRGB" && data[4] != "Gray") ||
                    (!icc && ((data[7] != "" && data[7] != "1") || (data[8] != "" && data[8] != "R98"))) ||
                    data[0] != data[9] || data[1] != data[10] || data[11] != "+0" || data[12] != "+0") return null;
                double gamma;
                if (!double.TryParse(data[5], NumberStyles.Float, CultureInfo.InvariantCulture, out gamma) ||
                    Math.Abs(gamma - 0.45455) > 0.00001) return null;
                // The PNG encoder recognizes standard sRGB profiles even when their binary serialization differs.
                // A one-pixel swatch retains a nonstandard ICC profile; these pixels only match within that profile.
                string finish = icc ? " -crop 1x1+0+0 +repage +profile \"!icc,*\" -define png:compression-level=9 PNG:profil.png" : " -exit";
                string digest = engine.Run(source + " -auto-orient -colorspace sRGB -alpha set -background black -alpha background -type TrueColorAlpha -print \"%#\"" + finish, Error, token).Trim();
                if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException(Error);
                string profile = "";
                if (icc)
                {
                    using (var swatch = File.OpenRead(Path.Combine(engine.DirectoryPath, "profil.png")))
                        profile = ReadPngHeader(swatch, true, token).Profile;
                    if (profile == null) return null;
                }
                return file.Width + "x" + file.Height + "/" + profile + "/" + digest;
            }
            catch { staged = null; File.Delete(path); throw; }
        }

        internal void ReadVisual(DuplicateFile file, CancellationToken token)
        {
            if (!CanDecode(file.Format)) return;
            if (!Object.ReferenceEquals(lastExamined, file)) Read(file, token);
            if (!singleImage || Math.Min(file.Width, file.Height) < SimilarPhotos.Side) return;
            // Scan fingerprints the exact pixels and the reduced image under the same read lock.
            string path = Object.ReferenceEquals(staged, file) ? Path.Combine(engine.DirectoryPath, "photo.bin") : CopyInput(file, token);
            try
            {
                string side = SimilarPhotos.Side.ToString(CultureInfo.InvariantCulture);
                engine.Run(file.Format + ":photo.bin -auto-orient +repage -colorspace sRGB -filter Box -resize " +
                    side + "x" + side + "! -strip -depth 8 PNG:empreinte.png", Error, token);
                using (var input = File.OpenRead(Path.Combine(engine.DirectoryPath, "empreinte.png")))
                {
                    var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
                    if (bitmap.PixelWidth != SimilarPhotos.Side || bitmap.PixelHeight != SimilarPhotos.Side)
                        throw new InvalidDataException(Error);
                    var sample = new byte[SimilarPhotos.Side * SimilarPhotos.Side * 4];
                    bitmap.CopyPixels(sample, SimilarPhotos.Side * 4, 0);
                    file.VisualSample = sample;
                }
            }
            finally { staged = null; File.Delete(path); }
        }

        // APNG, HDR signaling and non-sRGB primaries may all be hidden by an 8-bit PNG decoder.
        sealed class PngHeader
        {
            internal bool CommonColor = true, Animated;
            internal string Profile;
        }

        static PngHeader ReadPngHeader(Stream input, bool captureProfile, CancellationToken token)
        {
            var header = new PngHeader();
            using (var reader = new BinaryReader(input, Encoding.ASCII, true))
            {
                input.Position = 8;
                while (input.Length - input.Position >= 12)
                {
                    token.ThrowIfCancellationRequested();
                    uint size = BigEndian(reader.ReadBytes(4), 0);
                    string type = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if (size > input.Length - input.Position - 4) throw new InvalidDataException(Error);
                    if (type == "acTL") { header.Animated = true; return header; }
                    if (type == "IDAT") return header;
                    long next = input.Position + size + 4;
                    if (captureProfile && type == "sRGB") header.Profile = "";
                    if (captureProfile && type == "iCCP" && size <= 16 * 1024 * 1024)
                        using (var profile = new MemoryStream(reader.ReadBytes((int)size)))
                            header.Profile = DuplicateScanner.Hash(profile, token);
                    if (type == "cICP" || type == "mDCV" || type == "cLLI") header.CommonColor = false;
                    if (type == "cHRM")
                    {
                        if (size != 32) throw new InvalidDataException(Error);
                        byte[] chroma = reader.ReadBytes(32);
                        uint[] srgb = { 31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000 };
                        for (int i = 0; i < srgb.Length; i++)
                            if (BigEndian(chroma, i * 4) != srgb[i]) header.CommonColor = false;
                    }
                    input.Position = next;
                }
                throw new InvalidDataException(Error);
            }
        }

        static uint BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        // ImageMagick Q8 reports 8 bits even for a BMP with 10:10:10 source channel masks.
        static bool BmpFitsEightBits(Stream input)
        {
            using (var r = new BinaryReader(input, Encoding.ASCII, true))
            {
                input.Position = 10;
                uint pixels = r.ReadUInt32(), header = r.ReadUInt32();
                if (header == 12)
                {
                    input.Position = 24;
                    ushort coreBits = r.ReadUInt16();
                    return coreBits == 1 || coreBits == 4 || coreBits == 8 || coreBits == 24;
                }
                if (header != 40 && header != 52 && header != 56 && header != 108 && header != 124) return false;
                input.Position = 28;
                int bits = r.ReadUInt16();
                uint compression = r.ReadUInt32();
                if (compression == 1 || compression == 2) return bits == (compression == 1 ? 8 : 4);
                bool bitfields = compression == 3 || compression == 6;
                if (compression != 0 && !bitfields) return false;
                if (bits != 1 && bits != 4 && bits != 8 && bits != 16 && bits != 24 && bits != 32) return false;
                int masks = header >= 56 || compression == 6 ? 4 : header >= 52 || bitfields ? 3 : 0;
                if (pixels < 54 + 4 * masks) return false;
                input.Position = 54;
                uint used = 0;
                for (int i = 0; i < masks; i++)
                {
                    uint mask = r.ReadUInt32();
                    if (bitfields && i < 3 && mask == 0) return false;
                    if ((used & mask) != 0) return false;
                    used |= mask;
                    if (mask == 0) continue;
                    while ((mask & 1) == 0) mask >>= 1;
                    if (mask > 255 || (mask & (mask + 1)) != 0) return false;
                }
                return true;
            }
        }

        // A JPEG decoder can return just the cover frame of an MPO or a photo followed by a motion clip.
        static bool JpegIsStill(Stream input, CancellationToken token)
        {
            using (var reader = new BinaryReader(input, Encoding.ASCII, true))
            {
                input.Position = input.Length - 2;
                if (reader.ReadByte() != 255 || reader.ReadByte() != 217) return false;
                input.Position = 2;
                while (input.Position < input.Length)
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.ReadByte() != 255) throw new InvalidDataException(Error);
                    int marker;
                    do { marker = reader.ReadByte(); } while (marker == 255);
                    if (marker == 218) return EndsAtFirstJpegEnd(input, token);
                    int size = (reader.ReadByte() << 8) | reader.ReadByte();
                    if (size < 2 || size - 2 > input.Length - input.Position) throw new InvalidDataException(Error);
                    long next = input.Position + size - 2;
                    if (marker == 226 && size >= 6 && Encoding.ASCII.GetString(reader.ReadBytes(4)) == "MPF\0") return false;
                    input.Position = next;
                }
                throw new InvalidDataException(Error);
            }
        }

        static bool EndsAtFirstJpegEnd(Stream input, CancellationToken token)
        {
            // Decoding JPEG adopts only the first picture of a concatenated MJPEG stream.
            var buffer = new byte[81920];
            bool marker = false;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                for (int i = 0; i < count; i++)
                {
                    if (marker && buffer[i] == 217) return i == count - 1 && input.Position == input.Length;
                    marker = buffer[i] == 255;
                }
            }
            return false;
        }

        internal byte[] Thumbnail(DuplicateFile file, CancellationToken token)
        {
            string path = CopyInput(file, token);
            try
            {
                engine.Run(file.Format + ":photo.bin -auto-orient -thumbnail 360x216> +repage +profile \"!icc,*\" PNG:apercu.png", Error, token);
                return File.ReadAllBytes(Path.Combine(engine.DirectoryPath, "apercu.png"));
            }
            finally { staged = null; File.Delete(path); }
        }

        public void Dispose() { if (engine != null) engine.Dispose(); }
    }
}
