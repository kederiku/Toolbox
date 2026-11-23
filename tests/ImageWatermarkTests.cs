using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        internal static void RunImageWatermarkTests()
        {
            var token = CancellationToken.None;
            var options = new WatermarkOptions("Été • 2026", true, WatermarkPosition.Center, .09, .65);
            using (var engine = new ImageEngine())
            {
                string dir = engine.DirectoryPath;
                WatermarkTestPng(Path.Combine(dir, "vierge.png"), 180, 120, 0, 0, 0, 0);
                string output = Path.Combine(dir, "résultat [0].png");
                using (var image = new ImageWatermark(Path.Combine(dir, "vierge.png"), token))
                {
                    Check(image.Preview.IsFrozen && image.Width == 180 && image.Height == 120 && image.Extension == ".png",
                        "Aperçu de l’image transparente aux dimensions d’origine");
                    image.WriteCopy(output, options, token);
                }
                using (var file = File.OpenRead(output))
                {
                    var frame = new PngBitmapDecoder(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    var ink = WatermarkTestAlpha(WatermarkArt.Render(180, 120, options));
                    var copy = WatermarkTestAlpha(frame);
                    Check(ink.Any(a => a > 30) && ink.Any(a => a == 0) && ink.Zip(copy, (a, b) => Math.Abs(a - b)).Max() <= 2,
                        "Transparence préservée et motif répété identique à celui de l’aperçu");
                }
                WatermarkTestPng(Path.Combine(dir, "rouge.png"), 180, 120, 220, 25, 20, 255);
                WatermarkTestPng(Path.Combine(dir, "bleu.png"), 180, 120, 30, 45, 230, 255);
                WatermarkTestPng(Path.Combine(dir, "portrait.png"), 78, 151, 30, 45, 230, 255);
                var corner = new WatermarkOptions("Copie Été", false, WatermarkPosition.BottomRight, .12, .8);
                foreach (var extension in new[] { "gif", "webp", "tiff" })
                {
                    bool pages = extension == "tiff";
                    string coder = extension.ToUpperInvariant() + ":";
                    engine.Run((pages ? "rouge.png portrait.png " : "-delay 7 rouge.png -delay 19 bleu.png -loop 3 ") +
                        coder + "entree." + extension, "Création de l’essai", token);
                    using (var image = new ImageWatermark(Path.Combine(dir, "entree." + extension), token))
                        image.WriteCopy(Path.Combine(dir, "sortie." + extension), corner, token);
                    string[] frames = engine.Run("identify -format \"%w %h %T %k\\n\" sortie." + extension, "Lecture du résultat", token)
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    Check(frames.Length == 2, "Les deux images sont conservées en " + extension);
                    string[] first = frames[0].Split(' '), second = frames[1].Split(' ');
                    Check(first[0] == "180" && first[1] == "120" && second[0] == (pages ? "78" : "180") &&
                        second[1] == (pages ? "151" : "120") && int.Parse(first[3], CultureInfo.InvariantCulture) > 3 &&
                        int.Parse(second[3], CultureInfo.InvariantCulture) > 3, "Motif sur chaque image et dimensions en " + extension);
                    if (!pages)
                    {
                        string timing = engine.Run("identify -format \"%T\\n\" entree." + extension, "Lecture des délais", token);
                        Check(timing == first[2] + "\n" + second[2] + "\n" ||
                            timing.Replace("\r", "") == first[2] + "\n" + second[2] + "\n", "Délais de l’animation " + extension);
                        Check(engine.Run("identify -verbose sortie." + extension + "[0]", "Lecture de la boucle", token)
                            .Contains("Iterations: 3"), "Nombre de lectures de l’animation " + extension);
                    }
                }
                // Repeat the first ANMF verbatim. The bundled WebP writer normally folds such frames together.
                byte[] webp = File.ReadAllBytes(Path.Combine(dir, "entree.webp"));
                int chunk = 12;
                while (System.Text.Encoding.ASCII.GetString(webp, chunk, 4) != "ANMF")
                {
                    int length = BitConverter.ToInt32(webp, chunk + 4);
                    chunk += 8 + length + (length & 1);
                }
                int frameLength = BitConverter.ToInt32(webp, chunk + 4);
                byte[] doubled = webp.Take(chunk + 8 + frameLength + (frameLength & 1)).Concat(webp.Skip(chunk)).ToArray();
                BitConverter.GetBytes(doubled.Length - 8).CopyTo(doubled, 4);
                File.WriteAllBytes(Path.Combine(dir, "trois.webp"), doubled);
                using (var image = new ImageWatermark(Path.Combine(dir, "trois.webp"), token))
                    image.WriteCopy(Path.Combine(dir, "trois-copie.webp"), options, token);
                Check(engine.Run("identify -format \"%T\\n\" trois-copie.webp", "Trames WebP", token).Replace("\r", "") == "70\n70\n190\n",
                    "Les trames WebP identiques gardent leur nombre et leur durée individuelle");
                // Une image HEVC blanche 80 × 48 avec la propriété HEIF irot = 1.
                File.WriteAllBytes(Path.Combine(dir, "rotation.heic"), Convert.FromBase64String(
                    "AAAAGGZ0eXBoZWljAAAAAG1pZjFoZWljAAABNG1ldGEAAAAAAAAAIWhkbHIAAAAAAAAAAHBpY3QAAAAAAAAAAAAAAAAAAAAADnBpdG0AAAAAAAEAAAAeaWxvYwAAAABEAAABAAEAAAABAAABVAAAABgAAAAjaWluZgAAAAAAAQAAABVpbmZlAgAAAAABAABodmMxAAAAALhpcHJwAAAAmmlwY28AAAB1aHZjQwEBYAAAAJAAAAAAAB7wAPz9+PgAAA8DoAABABhAAQwB//8BYAAAAwCQAAADAAADAB6VmAmhAAEAKUIBAQFgAAADAJAAAAMAAAMAHqAogxZZWa5MrwFoCAAAAwAIAAADAMhAogABAAZEAcFz0YkAAAAUaXNwZQAAAAAAAABQAAAAMAAAAAlpcm90AQAAABZpcG1hAAAAAAAAAAEAAQOBAoMAAAAgbWRhdAAAABQoAa8dRIjuI48J1h919qiomG4zcA=="));
                using (var image = new ImageWatermark(Path.Combine(dir, "rotation.heic"), token))
                {
                    Check(image.Extension == ".png" && image.Notice.Contains("PNG") && image.Width == 48 && image.Height == 80 &&
                        image.Preview.PixelWidth == 48 && image.Preview.PixelHeight == 80, "Orientation HEIC et sortie PNG annoncée");
                    image.WriteCopy(Path.Combine(dir, "rotation.png"), corner, token);
                    Check(engine.Run("identify -format \"%w %h\" rotation.png", "Vérification HEIC", token) == "48 80",
                        "La rotation HEIC est respectée dans la copie");
                }
                File.WriteAllText(Path.Combine(dir, "dessin.svg"), "<svg xmlns='http://www.w3.org/2000/svg' width='90' height='60'><rect width='90' height='60' fill='blue'/></svg>");
                using (var image = new ImageWatermark(Path.Combine(dir, "dessin.svg"), token))
                    Check(image.Extension == ".png" && image.Notice.Contains("90") && image.Notice.Contains("60") &&
                        image.Notice.Contains("PNG"), "Dimensions du rendu vectoriel et format annoncés avant l’enregistrement");
                engine.Run("rouge.png -orient RightTop -depth 16 TIFF64:grand.tif", "Création BigTIFF", token);
                using (var image = new ImageWatermark(Path.Combine(dir, "grand.tif"), token))
                {
                    Check(image.Extension == ".tif" && image.Width == 120 && image.Height == 180, "BigTIFF et orientation EXIF conservés");
                    Check(image.Notice.Contains("16 bits") && image.Notice.Contains("8 bits"), "Précision du moteur annoncée avant la copie");
                    image.WriteCopy(Path.Combine(dir, "grand-copie.tif"), corner, token);
                    Check(engine.Run("identify -format \"%m %w %h\" grand-copie.tif", "Vérification BigTIFF", token) == "TIFF64 120 180",
                        "La copie reste une image BigTIFF orientée");
                }
            }
        }

        static byte[] WatermarkTestAlpha(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
            converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
            return pixels.Where((b, index) => index % 4 == 3).ToArray();
        }

        static void WatermarkTestPng(string path, int w, int h, byte r, byte g, byte b, byte a)
        {
            var pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4)));
            using (var file = File.Create(path)) png.Save(file);
        }
    }
}
