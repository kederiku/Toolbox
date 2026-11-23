using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal sealed class ImageOutput : MediaOutput
    {
        internal readonly bool Multiple;
        readonly string label;

        ImageOutput(string code, string extension = null, bool multiple = false, string name = null)
            : base(code, extension ?? code.ToLowerInvariant())
        {
            Multiple = multiple;
            label = (name ?? code) + "  (." + Extension + ")";
        }

        public override string ToString() { return label; }

        internal bool AcceptsExtension(string path)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return ext == Extension || ext == Code.ToLowerInvariant();
        }

        internal static readonly ImageOutput[] Formats = {
            new ImageOutput("PNG"), new ImageOutput("JPEG", "jpg"), new ImageOutput("WEBP", multiple: true, name: "WebP"),
            new ImageOutput("GIF", multiple: true), new ImageOutput("AVIF", multiple: true), new ImageOutput("TIFF", "tif", true),
            new ImageOutput("BMP"), new ImageOutput("ICO"), new ImageOutput("JXL", multiple: true, name: "JPEG XL"),
            new ImageOutput("SVG", name: "SVG avec image incorporée"), new ImageOutput("PDF", multiple: true),
            new ImageOutput("PSD", name: "Photoshop PSD"), new ImageOutput("QOI"), new ImageOutput("TGA"),
            new ImageOutput("AAI"), new ImageOutput("ART"), new ImageOutput("ASE", name: "Aseprite"), new ImageOutput("AVS"), new ImageOutput("CIN"),
            new ImageOutput("CUR"), new ImageOutput("DCX", multiple: true), new ImageOutput("DDS"), new ImageOutput("DPX"),
            new ImageOutput("EXR", name: "OpenEXR"), new ImageOutput("FARBFELD", "ff"), new ImageOutput("FITS"),
            new ImageOutput("HDR"), new ImageOutput("J2K"), new ImageOutput("JNG"), new ImageOutput("JP2", name: "JPEG 2000"),
            new ImageOutput("MIFF", multiple: true), new ImageOutput("MNG", multiple: true), new ImageOutput("MTV"),
            new ImageOutput("OTB"), new ImageOutput("PAM"), new ImageOutput("PBM"), new ImageOutput("PCX"),
            new ImageOutput("PFM"), new ImageOutput("PGM"), new ImageOutput("PICT", "pct"), new ImageOutput("PNM"),
            new ImageOutput("PPM"), new ImageOutput("PSB"), new ImageOutput("RAS"), new ImageOutput("SGI"),
            new ImageOutput("VICAR", "vicar"), new ImageOutput("VIFF"),
            new ImageOutput("WBMP"), new ImageOutput("XBM"), new ImageOutput("XPM")
        };
    }

    internal sealed class ImageConversion : IConversionSource
    {
        public string Source { get; private set; }
        internal string Format { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int FrameCount { get; private set; }
        readonly ImageEngine engine;
        readonly bool ownsEngine;
        readonly string input;
        internal string InputName { get { return input; } }
        int[] delays;

        ImageConversion(string source, ImageEngine sharedEngine)
        {
            Source = Path.GetFullPath(source);
            string ext = Path.GetExtension(Source);
            input = Guid.NewGuid().ToString("N") + (Regex.IsMatch(ext, @"^\.[a-zA-Z0-9]{1,10}$") ? ext : ".bin");
            ownsEngine = sharedEngine == null;
            engine = sharedEngine ?? new ImageEngine();
        }

        internal static ImageConversion Open(string source, CancellationToken token)
        {
            return OpenWithEngine(source, token, null);
        }

        internal static ImageConversion OpenWithEngine(string source, CancellationToken token, ImageEngine sharedEngine)
        {
            token.ThrowIfCancellationRequested();
            var image = new ImageConversion(source, sharedEngine);
            try
            {
                string local = Path.Combine(image.engine.DirectoryPath, image.input);
                ImageEngine.CopyFile(image.Source, local, token);
                const string error = "Impossible de lire cette image. Son format ou sa compression est incompatible, ou le fichier est endommagé.";
                string info = image.engine.Run("identify -ping -format \"%m %w %h %W %H %T\\n\" " + image.input, error, token);
                var lines = info.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var fields = lines.Length == 0 ? new string[0] : lines[0].Split(' ');
                int width, height;
                bool canvas = lines.Length > 1 && (fields[0] == "GIF" || fields[0] == "WEBP");
                if (fields.Length != 6 || !int.TryParse(fields[canvas ? 3 : 1], out width) || !int.TryParse(fields[canvas ? 4 : 2], out height)
                    || width <= 0 || height <= 0) throw new InvalidDataException(error);
                image.Format = fields[0];
                image.Width = width;
                image.Height = height;
                image.FrameCount = lines.Length;
                image.delays = lines.Select(line => int.Parse(line.Substring(line.LastIndexOf(' ') + 1), CultureInfo.InvariantCulture)).ToArray();
                if (image.Format == "PNG" && image.FrameCount == 1 && HasPngAnimation(local))
                    throw new InvalidDataException("Ce PNG animé nécessite un décodeur APNG. Le moteur intégré peut convertir les GIF et WebP animés.");
                return image;
            }
            catch { image.Dispose(); throw; }
        }

        static bool HasPngAnimation(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                reader.BaseStream.Position = 8;
                while (reader.BaseStream.Length - reader.BaseStream.Position >= 12)
                {
                    var bytes = reader.ReadBytes(4);
                    long length = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                    string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if (chunk == "acTL") return true;
                    if (chunk == "IDAT" || chunk == "IEND") return false;
                    reader.BaseStream.Seek(length + 4, SeekOrigin.Current);
                }
                return false;
            }
        }

        internal bool LosesFrames(ImageOutput format) { return FrameCount > 1 && !format.Multiple; }

        internal byte[] ReadThumbnail(CancellationToken token)
        {
            try
            {
                engine.Run(input + "[0] -auto-orient -thumbnail 160x160> +repage -strip PNG:apercu.png",
                    "Aperçu indisponible.", token);
                return File.ReadAllBytes(Path.Combine(engine.DirectoryPath, "apercu.png"));
            }
            catch (InvalidDataException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        string JxlTiming()
        {
            // JPEG XL traite une trame de durée nulle comme une trame intermédiaire invisible.
            int zero = delays.Count(delay => delay == 0);
            if (zero == 0 || FrameCount == 1) return "";
            bool setAll = zero > FrameCount / 2;
            var options = new StringBuilder(setAll ? " -set delay 1" : "");
            for (int i = 0; i < delays.Length; i++)
            {
                if (setAll ? delays[i] <= 1 : delays[i] != 0) continue;
                options.AppendFormat(CultureInfo.InvariantCulture, " ( -clone {0} -set delay {1} ) -swap {0},-1 +delete", i, Math.Max(1, delays[i]));
            }
            return options.ToString();
        }

        internal void Save(string destination, ImageOutput format, bool overwrite, CancellationToken token, Action writing = null)
        {
            destination = Path.GetFullPath(destination);
            if (string.Equals(Source, destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choisissez un autre nom pour conserver l’image d’origine.");
            if (!overwrite && File.Exists(destination)) throw new IOException("Un fichier existe déjà à cet emplacement.");
            if (!format.AcceptsExtension(destination)) throw new IOException("Utilisez l’extension ." + format.Extension + " pour ce format.");
            string name = Guid.NewGuid().ToString("N");
            string encoded = Path.Combine(engine.DirectoryPath, name);
            string temp = Path.Combine(Path.GetDirectoryName(destination), ".AtelierPC-" + name + ".tmp");
            try
            {
                string source = input + (LosesFrames(format) ? "[0]" : "");
                string options = FrameCount > 1 && (Format == "GIF" || Format == "WEBP") ? " -coalesce" : "";
                options += " -auto-orient";
                if (format.Code == "JXL") options += JxlTiming();
                if (format.Code == "JPEG") options += " -background white -alpha remove -alpha off";
                if (format.Code == "ICO" || format.Code == "CUR") options += " -resize 256x256>";
                string coder = format.Code == "SVG" ? "PNG" : format.Code;
                engine.Run(source + options + " " + coder + ":" + name,
                    "Échec de la conversion en " + format.Code + ". Vérifiez que ce format accepte les dimensions et le contenu de l’image.", token);
                if (!File.Exists(encoded) || new FileInfo(encoded).Length == 0)
                    throw new InvalidDataException("Le moteur n’a pas produit d’image.");
                if (writing != null) writing();
                if (format.Code == "SVG") WriteSvg(encoded, temp);
                else ImageEngine.CopyFile(encoded, temp, token);
                token.ThrowIfCancellationRequested();
                // Garder l’original ouvert durant le remplacement protège aussi ses alias Windows.
                using (var original = File.Exists(Source) ? File.Open(Source, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                {
                    if (overwrite && File.Exists(destination)) File.Replace(temp, destination, null);
                    else File.Move(temp, destination);
                }
            }
            finally
            {
                File.Delete(encoded);
                File.Delete(temp);
            }
        }

        static void WriteSvg(string png, string destination)
        {
            using (var input = File.OpenRead(png))
            using (var writer = XmlWriter.Create(destination, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
            {
                // IHDR contient les dimensions finales après application de l’orientation.
                var header = new byte[24];
                if (input.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("PNG incomplet.");
                string width = ReadDimension(header, 16), height = ReadDimension(header, 20);
                const string svg = "http://www.w3.org/2000/svg";
                writer.WriteStartElement("svg", svg);
                writer.WriteAttributeString("width", width);
                writer.WriteAttributeString("height", height);
                writer.WriteStartElement("image", svg);
                writer.WriteAttributeString("width", width);
                writer.WriteAttributeString("height", height);
                writer.WriteStartAttribute("xlink", "href", "http://www.w3.org/1999/xlink");
                writer.WriteString("data:image/png;base64,");
                input.Position = 0;
                var buffer = new byte[49152];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) writer.WriteBase64(buffer, 0, read);
                writer.WriteEndAttribute();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
        }

        static string ReadDimension(byte[] header, int index)
        {
            return ((header[index] << 24) | (header[index + 1] << 16) | (header[index + 2] << 8) | header[index + 3])
                .ToString(CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (ownsEngine) engine.Dispose();
            else File.Delete(Path.Combine(engine.DirectoryPath, input));
        }
    }
}
