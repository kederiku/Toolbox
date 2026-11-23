using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal sealed partial class ImageWatermark : WatermarkSource
    {
        const string ReadError = "Le moteur intégré ne peut pas décoder cette image ou l’une de ses pages.";
        const string WriteError = "Le moteur intégré n’a pas pu inscrire le filigrane sur toutes les images.";
        readonly ImageEngine engine;
        readonly string input, coder;
        readonly bool coalesce;
        readonly Frame[] frames;

        struct Frame
        {
            internal int Width, Height, Delay, Depth;
        }

        internal ImageWatermark(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            engine = new ImageEngine();
            try
            {
                // The shared engine stages a snapshot under a generated, command-line-safe name.
                var source = ImageConversion.OpenWithEngine(path, token, engine);
                input = source.InputName;
                string format = source.Format;
                bool layers = format == "PSD" || format == "PSB" || format == "XCF";
                bool vector = IsOneOf(format, "SVG RSVG SVGZ MSVG EMF WMF WPG PICT PCT");
                if (format == "PSD" || format == "PSB")
                    input += "[0]"; // Photoshop stores the merged image before its layers.
                if (format == "XCF")
                {
                    engine.Run(input + " -background none -flatten MIFF:fusion.miff", ReadError, token);
                    input = "fusion.miff";
                }
                coalesce = IsOneOf(format, "GIF WEBP MNG");
                frames = Identify(input, coalesce, token);
                Width = frames[0].Width;
                Height = frames[0].Height;
                ImageOutput native = ImageOutput.Formats.FirstOrDefault(item => item.Code == format);
                bool limited = layers || vector || IsOneOf(format, "PBM WBMP OTB ART XBM RGF");
                // Other read/write coders allowed by the embedded policy but absent from the conversion menu.
                bool otherWriter = IsOneOf(format, "BMP2 BMP3 TIFF64 ICON J2C JPC PHM PGX SUN FF SIX SIXEL PALM PDB FL32 VIPS ASEPRITE");
                if (!limited && (native != null || otherWriter) && (frames.Length == 1 || (native != null && native.Multiple) ||
                    Regex.IsMatch(engine.Run("-list format", ReadError, token), @"^\s*" + Regex.Escape(format) + @"\*?\s+rw\+", RegexOptions.Multiline)))
                {
                    coder = format;
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (native != null) Extension = native.AcceptsExtension(path) ? ext : "." + native.Extension;
                    else if (format == "TIFF64") Extension = ext == ".tiff" || ext == ".tf8" || ext == ".btf" ? ext : ".tif";
                    else if (format == "BMP2" || format == "BMP3") Extension = ".bmp";
                    else Extension = format == "ICON" ? ".ico" : "." + format.ToLowerInvariant();
                }
                else
                {
                    bool animation = frames.Length > 1 && frames.Any(frame => frame.Delay > 0);
                    coder = frames.Length == 1 ? "PNG" : animation ? "MNG" : "TIFF";
                    Extension = "." + coder.ToLowerInvariant();
                    if (vector)
                        Notice = String.Format(CultureInfo.CurrentCulture, "L’image vectorielle sera rendue en {0}, à {1} × {2} pixels.", coder, Width, Height);
                    else if (layers)
                        Notice = "Les calques seront aplatis dans une copie PNG.";
                    else
                        Notice = "La copie sera enregistrée en " + coder + " pour conserver le rendu" +
                            (frames.Length == 1 ? " du filigrane." : animation ? " et l’animation." : " et toutes les pages.");
                }
                if (frames.Length > 1 && String.IsNullOrEmpty(Notice))
                    Notice = String.Format(CultureInfo.CurrentCulture, "Aperçu de la première image. Le filigrane sera appliqué aux {0} images.", frames.Length);
                int depth = frames.Max(frame => frame.Depth);
                if (depth > 8)
                    Notice = (String.IsNullOrEmpty(Notice) ? "" : Notice + " ") + String.Format(CultureInfo.CurrentCulture,
                        "L’original contient des données sur {0} bits par canal. Le moteur intégré limite la précision de la copie à 8 bits par canal.", depth);
                string first = frames.Length > 1 ? input + "[0]" : input;
                engine.Run(first + (coalesce ? " -coalesce" : "") +
                    " -auto-orient +repage -thumbnail 1200x1200> -colorspace sRGB -strip PNG:apercu-filigrane.png", ReadError, token);
                using (var file = File.OpenRead(Path.Combine(engine.DirectoryPath, "apercu-filigrane.png")))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = file;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    Preview = bitmap;
                }
            }
            catch { Dispose(); throw; }
        }

        static bool IsOneOf(string format, string names)
        {
            return (" " + names + " ").Contains(" " + format + " ");
        }

        Frame[] Identify(string name, bool canvas, CancellationToken token)
        {
            string text = engine.Run("identify -ping -format \"%w %h %W %H %[orientation] %T %z\\n\" " + name, ReadError, token);
            return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(line => {
                var fields = line.Split(' ');
                int w, h, delay, depth;
                if (fields.Length != 7 || !Int32.TryParse(fields[canvas ? 2 : 0], out w) ||
                    !Int32.TryParse(fields[canvas ? 3 : 1], out h) || w < 1 || h < 1 || !Int32.TryParse(fields[5], out delay) ||
                    !Int32.TryParse(fields[6], out depth))
                    throw new InvalidDataException(ReadError);
                bool swap = IsOneOf(fields[4], "LeftTop RightTop RightBottom LeftBottom");
                return new Frame { Width = swap ? h : w, Height = swap ? w : h, Delay = delay, Depth = depth };
            }).ToArray();
        }

        internal override void WriteCopy(string temporary, WatermarkOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string folder = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(engine.DirectoryPath, folder);
            Directory.CreateDirectory(directory);
            try
            {
                string output = folder + "/copie" + Extension;
                string encoding = (coder == "TIFF" || coder == "TIFF64" ? " -compress Zip" : coder == "WEBP" ? " -define webp:lossless=true" : "") +
                    " -depth 8 " + coder + ":" + output;
                if (frames.All(frame => frame.Width == frames[0].Width && frame.Height == frames[0].Height))
                {
                    string drawing = DrawCommand(folder, frames[0], options);
                    engine.Run(input + (coalesce ? " -coalesce" : "") + " -auto-orient +repage" + drawing + encoding, WriteError, token);
                }
                else
                {
                    // Independent pages can have different aspect ratios and EXIF orientations.
                    for (int i = 0; i < frames.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        string index = i.ToString(CultureInfo.InvariantCulture);
                        string drawing = DrawCommand(folder, frames[i], options);
                        string page = folder + "/page-" + i.ToString("D5", CultureInfo.InvariantCulture) + ".miff";
                        engine.Run(input + "[" + index + "] -auto-orient +repage" + drawing + " -depth 8 MIFF:" + page, WriteError, token);
                    }
                    engine.Run(folder + "/page-*.miff" + encoding, WriteError, token);
                }
                var written = Identify(output, coder == "WEBP" || coder == "GIF" || coder == "MNG", token);
                if (coder == "WEBP" && frames.Length > 1 &&
                    !written.Select(f => f.Delay).SequenceEqual(frames.Select(f => f.Delay)))
                {
                    WriteSeparateWebpFrames(folder, output, options, token);
                    written = Identify(output, true, token);
                }
                if (written.Length != frames.Length || written.Where((frame, i) => frame.Width != frames[i].Width || frame.Height != frames[i].Height).Any())
                    throw new InvalidDataException(WriteError);
                ImageEngine.CopyFile(Path.Combine(engine.DirectoryPath, output), temporary, token);
            }
            finally { Directory.Delete(directory, true); }
        }

        string DrawCommand(string folder, Frame frame, WatermarkOptions options)
        {
            // Limit the transparent drawing surface for panoramas and large scans.
            double scale = Math.Min(1, Math.Min(8192.0 / Math.Max(frame.Width, frame.Height),
                Math.Sqrt(16000000.0 / ((double)frame.Width * frame.Height))));
            int w = Math.Max(1, (int)Math.Round(frame.Width * scale)), h = Math.Max(1, (int)Math.Round(frame.Height * scale));
            string overlay = String.Format(CultureInfo.InvariantCulture, "{0}/motif-{1}x{2}.png", folder, w, h);
            string path = Path.Combine(engine.DirectoryPath, overlay);
            if (!File.Exists(path)) WatermarkArt.WritePng(path, w, h, options);
            return String.Format(CultureInfo.InvariantCulture, " -draw \"image over 0,0 {0},{1} '{2}'\"", frame.Width, frame.Height, overlay);
        }

        public override void Dispose() { if (engine != null) engine.Dispose(); }
    }
}
