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
    internal static partial class Tests
    {
        internal static void RunPdfWatermarkTests()
        {
            using (var engine = new PdfMetadataEngine())
            {
                var token = CancellationToken.None;
                string input = Path.Combine(engine.DirectoryPath, "source.pdf");
                string output = Path.Combine(engine.DirectoryPath, "sortie.pdf");
                string contents = "q 700 0 0 700 -50 -70 cm /Scan Do Q\nBT /F1 8 Tf 100 100 Td (Texte selectionnable) Tj ET\n0.5 0 0 0.5 0 0 cm\n";
                var objects = new [] {
                    "<< /Type /Catalog /Pages 2 0 R >>",
                    "<< /Type /Pages /Count 4 /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /MediaBox [-50 -70 450 370] /CropBox [30 20 330 220] /Resources << /Font << /F1 10 0 R >> /XObject << /Scan 8 0 R >> >> >>",
                    "<< /Type /Page /Parent 2 0 R /Rotate 90 /Contents 7 0 R /Annots [9 0 R] >>",
                    "<< /Type /Page /Parent 2 0 R /Rotate 180 /Contents 7 0 R >>",
                    "<< /Type /Page /Parent 2 0 R /Rotate 270 /CropBox [-10 -20 190 280] /Contents 7 0 R >>",
                    "<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>",
                    "<< /Length " + contents.Length + " >>\nstream\n" + contents + "endstream",
                    "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /ASCIIHexDecode /Length 7 >>\nstream\nFFEECC>\nendstream",
                    "<< /Type /Annot /Subtype /Link /Rect [30 20 40 30] /Border [0 0 0] /A << /S /URI /URI (https://example.org/lien) >> >>",
                    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
                };
                WritePdfObjects(input, objects, "");
                var corner = new WatermarkOptions("Été", false, WatermarkPosition.TopLeft, .70, .95);
                using (var source = new PdfWatermark(input, token))
                {
                    Check(Math.Abs(source.Width / source.Height - 2.0 / 3) < 0.001, "Filigrane PDF : proportions de la zone visible après rotation");
                    Check(Math.Abs(source.Preview.PixelWidth / (double)source.Preview.PixelHeight - 2.0 / 3) < .005,
                        "Filigrane PDF : aperçu aux proportions de la zone rognée");
                    byte[] original = PdfWatermarkPixels(source.Preview);
                    int middle = 4 * ((source.Preview.PixelHeight / 2) * source.Preview.PixelWidth + source.Preview.PixelWidth / 2);
                    Check(original[middle + 2] - original[middle] > 28, "Filigrane PDF : aperçu du scan original en couleur");
                    source.WriteCopy(output, corner, token);
                    string repeated = Path.Combine(engine.DirectoryPath, "répété.pdf");
                    source.WriteCopy(repeated, new WatermarkOptions("Été", true, WatermarkPosition.Center, .55, .90), token);
                    using (var marked = new PdfWatermark(repeated, token))
                    {
                        int[] changes = PdfWatermarkDifferences(source.Preview, marked.Preview);
                        Check(changes.All(n => n > 30), "Filigrane PDF : texte répété visible dans les quatre quadrants du scan");
                    }
                }
                for (int page = 1; page <= 4; page++)
                {
                    string before = Path.Combine(engine.DirectoryPath, "avant.pdf");
                    string after = Path.Combine(engine.DirectoryPath, "après.pdf");
                    string range = page.ToString(CultureInfo.InvariantCulture);
                    engine.Run(token, false, "--empty", "--pages", input, range, "--", before);
                    engine.Run(token, false, "--empty", "--pages", output, range, "--", after);
                    using (var a = new PdfWatermark(before, token))
                    using (var b = new PdfWatermark(after, token))
                    {
                        int[] changes = PdfWatermarkDifferences(a.Preview, b.Preview);
                        Check(changes[0] > 50 && changes.Skip(1).Sum() < changes[0] / 5,
                            "Filigrane PDF : texte au premier plan en haut à gauche de la page " + page);
                        CheckPdfWatermarkPlacement(a.Preview, b.Preview, corner, page);
                    }
                }
                string plain = Path.Combine(engine.DirectoryPath, "relu.pdf");
                engine.Run(token, false, "--stream-data=uncompress", "--object-streams=disable", output, plain);
                var text = Encoding.ASCII.GetString(File.ReadAllBytes(plain));
                Check(text.Contains("Texte selectionnable") && text.Contains("https://example.org/lien"),
                    "Filigrane PDF : texte et lien d’origine conservés");
                string encrypted = Path.Combine(engine.DirectoryPath, "chiffré.pdf");
                engine.Run(token, false, "--encrypt", "lecture", "proprietaire", "256", "--", input, encrypted);
                CheckPdfWatermarkRefused(encrypted, "chiffr");
                objects[0] = "<< /Type /Catalog /Pages 2 0 R /Perms << /DocMDP 11 0 R >> >>";
                WritePdfObjects(input, objects.Concat(new [] { "<< /Type /Sig /ByteRange [0 1 2 3] /Contents <00> >>" }).ToArray(), "");
                CheckPdfWatermarkRefused(input, "sign");
            }
        }

        static byte[] PdfWatermarkPixels(BitmapSource image)
        {
            BitmapSource converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return pixels;
        }

        static int[] PdfWatermarkDifferences(BitmapSource before, BitmapSource after)
        {
            Check(before.PixelWidth == after.PixelWidth && before.PixelHeight == after.PixelHeight,
                "Filigrane PDF : géométrie conservée");
            byte[] a = PdfWatermarkPixels(before), b = PdfWatermarkPixels(after);
            var quadrants = new int[4];
            for (int y = 0; y < before.PixelHeight; y++)
                for (int x = 0; x < before.PixelWidth; x++)
                {
                    int i = (y * before.PixelWidth + x) * 4;
                    if (a[i + 2] - b[i + 2] > 20)
                        quadrants[(y >= before.PixelHeight / 2 ? 2 : 0) + (x >= before.PixelWidth / 2 ? 1 : 0)]++;
                }
            return quadrants;
        }

        static void CheckPdfWatermarkPlacement(BitmapSource before, BitmapSource after, WatermarkOptions options, int page)
        {
            byte[] preview = PdfWatermarkPixels(WatermarkArt.Render(before.PixelWidth, before.PixelHeight, options));
            byte[] a = PdfWatermarkPixels(before), b = PdfWatermarkPixels(after);
            int intersection = 0, union = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                bool marked = a[i + 2] - b[i + 2] > 35;
                bool expected = preview[i + 3] > 65;
                if (marked || expected) union++;
                if (marked && expected) intersection++;
            }
            Check(union > 0 && intersection > .70 * union,
                "Filigrane PDF : glyphes orientés comme dans l’aperçu sur la page " + page);
        }

        static void CheckPdfWatermarkRefused(string path, string reason)
        {
            try { using (new PdfWatermark(path, CancellationToken.None)) { } }
            catch (InvalidDataException e)
            {
                Check(e.Message.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0, "Filigrane PDF : motif de refus " + reason);
                return;
            }
            throw new Exception("Filigrane PDF : refus attendu pour " + reason);
        }
    }
}
