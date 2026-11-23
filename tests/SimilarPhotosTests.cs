using System;
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
        internal static void CheckSimilarPhotos()
        {
            var token = CancellationToken.None;
            using (var fixture = new ImageEngine())
            {
                var folder = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "photos"));
                string source = Path.Combine(folder.FullName, "source");
                var pixels = new byte[192 * 120 * 3];
                for (int y = 0; y < 120; y++)
                    for (int x = 0; x < 192; x++)
                    {
                        int p = (y * 192 + x) * 3;
                        pixels[p] = (byte)(30 + (x / 16) * 12);
                        pixels[p + 1] = (byte)(40 + (y / 10) * 11);
                        pixels[p + 2] = (byte)(35 + ((x / 17 + y / 9) % 7) * 22);
                    }
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(192, 120, 96, 96, PixelFormats.Bgr24, null, pixels, 192 * 3)));
                using (var stream = File.Create(source)) png.Save(stream);
                File.Copy(source, Path.Combine(folder.FullName, "copie"));
                fixture.Run("PNG:photos/source -quality 76 JPEG:photos/export1", "Test JPEG", token);
                fixture.Run("PNG:photos/source -quality 78 WEBP:photos/export2", "Test WebP", token);
                fixture.Run("PNG:photos/source -quality 76 AVIF:photos/export3", "Test AVIF", token);
                fixture.Run("AVIF:photos/export3 BMP:photos/decoded", "Test BMP", token);
                fixture.Run("PNG:photos/source -flop PNG:photos/autre", "Test miroir", token);
                fixture.Run("PNG:photos/source -resize 80x120! PNG:photos/deformee", "Test proportions", token);
                fixture.Run("PNG:photos/source -alpha set -channel A -evaluate set 45% +channel PNG:photos/translucide", "Test transparence", token);
                fixture.Run("PNG:photos/source PNG:photos/autre GIF:photos/animee", "Test GIF", token);
                // Some decoders expose only the primary image of an AVIF sequence.
                string avif = Path.Combine(folder.FullName, "export3");
                byte[] sequence = File.ReadAllBytes(avif);
                Encoding.ASCII.GetBytes("avis").CopyTo(sequence, 8);
                File.WriteAllBytes(Path.Combine(folder.FullName, "sequence"), sequence);
                File.WriteAllBytes(Path.Combine(folder.FullName, "piste"), File.ReadAllBytes(avif).Concat(
                    new byte[] { 0, 0, 0, 8, 109, 111, 111, 118 }).ToArray());
                byte[] jpeg = File.ReadAllBytes(Path.Combine(folder.FullName, "export1"));
                File.WriteAllBytes(Path.Combine(folder.FullName, "mjpeg"), jpeg.Concat(jpeg).ToArray());
                fixture.Run("PNG:photos/source -depth 10 AVIF:precision.avif", "Test AVIF 10 bits", token);
                using (var photos = new DuplicatePhotos())
                {
                    Check(photos.Read(new DuplicateFile { Format = "AVIF", FullPath = Path.Combine(fixture.DirectoryPath, "precision.avif") }, token) == null,
                        "Photos identiques : le décodage Q8 ne peut pas prouver l’égalité de deux images AVIF 10 bits");
                    string[] signatures = new string[2];
                    for (int i = 0; i < 2; i++)
                    {
                        string path = Path.Combine(fixture.DirectoryPath, "bmp-" + i);
                        WriteTenBitBmp(path, 512 + i);
                        signatures[i] = photos.Read(new DuplicateFile { Format = "BMP", FullPath = path }, token);
                    }
                    Check(signatures[0] == null || signatures[0] != signatures[1],
                        "Photos identiques : distinguer deux nuances BMP 10 bits malgré l’identification 8 bits du décodeur");
                }

                var scan = DuplicateScanner.Scan(folder.FullName, token, null);
                Check(scan.Groups.Any(g => !g.SameBytes && g.Files.Select(f => Path.GetFileName(f.FullPath)).OrderBy(n => n)
                    .SequenceEqual(new[] { "decoded", "export3" })), "Photos identiques : AVIF 8 bits fixe et BMP de son décodage");
                var similar = scan.SimilarGroups;
                Check(similar.Count == 1 && similar[0].Files.Select(f => Path.GetFileName(f.FullPath)).OrderBy(n => n)
                    .SequenceEqual(new[] { "copie", "decoded", "export1", "export2", "export3", "source" }),
                    "Photos similaires : une famille avec toutes les copies PNG, JPEG, WebP et AVIF, seules les images fixes du même sujet");
                Check(similar[0].Files.All(f => f.Width == 192 && f.Height == 120 && f.Thumbnail != null && f.Thumbnail.Length > 8),
                    "Photos similaires : dimensions et aperçu de chaque fichier");
            }
            var a = SampleFamily(0, "");
            var b = SampleFamily(4, "");
            var c = SampleFamily(8, "");
            Check(SimilarPhotos.Group(new[] { a, b }, token).Count == 1 && SimilarPhotos.Group(new[] { b, c }, token).Count == 1 &&
                SimilarPhotos.Group(new[] { a, c }, token).Count == 0 &&
                SimilarPhotos.Group(new[] { b, a, c }, token).Single().Files.Count == 2,
                "Photos similaires : aucun chaînage entre deux extrêmes dissemblables");
            Check(SimilarPhotos.Group(new[] { a, SampleFamily(0, "annotation") }, token).Count == 0,
                "Photos similaires : un flux de contenu différent sépare les photos");
            a.Files[0].VisualSample = Enumerable.Repeat((byte)100, a.Files[0].VisualSample.Length).ToArray();
            b.Files[0].VisualSample = (byte[])a.Files[0].VisualSample.Clone();
            Check(SimilarPhotos.Group(new[] { a, b }, token).Count == 0,
                "Photos similaires : un aplat ne suffit pas à identifier un sujet");
        }

        static DuplicateGroup SampleFamily(int tint, string contentStream)
        {
            var group = new DuplicateGroup();
            var sample = new byte[SimilarPhotos.Side * SimilarPhotos.Side * 4];
            for (int i = 0; i < sample.Length; i += 4)
            {
                sample[i] = sample[i + 1] = sample[i + 2] = (byte)(32 + ((i / 4) % 7) * 20 + tint);
                sample[i + 3] = 255;
            }
            group.Files.Add(new DuplicateFile { Width = 192, Height = 120, ContentStreamHash = contentStream, VisualSample = sample });
            return group;
        }

        static void WriteTenBitBmp(string path, int red)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0x4d42); writer.Write(70); writer.Write(0); writer.Write(66);
                writer.Write(40); writer.Write(1); writer.Write(1); writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(3); writer.Write(4); writer.Write(new byte[16]);
                writer.Write(0x3ff00000); writer.Write(0x000ffc00); writer.Write(0x000003ff);
                writer.Write((red << 20) | (512 << 10) | 512);
            }
        }
    }
}
