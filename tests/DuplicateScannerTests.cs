using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal static partial class Tests
    {
        internal static void CheckDuplicateScanner()
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-doublons-test-" + Guid.NewGuid().ToString("N")));
            var sub = root.CreateSubdirectory("autre dossier");
            var token = CancellationToken.None;
            try
            {
                string document = Path.Combine(root.FullName, "note.png");
                File.WriteAllText(document, "%PDF-1.4\nA");
                bool streams = new DriveInfo(Path.GetPathRoot(root.FullName)).DriveFormat == "NTFS";
                if (streams) WriteScanStream(document, 1);
                File.Copy(document, Path.Combine(sub.FullName, "renommé"));
                if (streams)
                {
                    string annotated = Path.Combine(root.FullName, "autre annotation");
                    File.Copy(document, annotated);
                    WriteScanStream(annotated, 2);
                    WriteScanStream(document, 3, "Zone.Identifier");
                }
                File.WriteAllText(Path.Combine(root.FullName, "distinct"), "%PDF-1.4\nB");
                string locked = Path.Combine(root.FullName, "verrouillé");
                var source = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgr24, null,
                    new byte[] { 0, 0, 255, 0, 255, 0, 255, 0, 0, 22, 33, 44, 55, 66, 77, 88, 99, 111 }, 9);
                string photo = Path.Combine(root.FullName, "photo sans extension");
                var orientation = new BitmapMetadata("jpg");
                orientation.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
                var srgb = new ReadOnlyCollection<ColorContext>(new[] { new ColorContext(PixelFormats.Bgr24) });
                SaveScanFrames(photo, new JpegBitmapEncoder(), BitmapFrame.Create(source, null, orientation, srgb));
                File.Copy(photo, Path.Combine(sub.FullName, "copie.pdf"));
                if (streams)
                {
                    string annotatedPhoto = Path.Combine(root.FullName, "photo annotée");
                    File.Copy(photo, annotatedPhoto);
                    WriteScanStream(annotatedPhoto, 3);
                }
                string sequence = Path.Combine(sub.FullName, "deux vues.mjpg");
                var jpeg = File.ReadAllBytes(photo);
                File.WriteAllBytes(sequence, jpeg.Concat(jpeg).ToArray());
                string png = Path.Combine(sub.FullName, "photo.png");
                using (var input = ImageConversion.Open(photo, token))
                    input.Save(png, ImageOutput.Formats.Single(o => o.Code == "PNG"), false, token);
                File.Move(png, Path.Combine(sub.FullName, "convertie.dat"));

                byte[] gray = { 33, 86, 129 };
                var still = BitmapFrame.Create(BitmapSource.Create(3, 1, 96, 96, PixelFormats.Gray8, null, gray, 3));
                SaveScanFrames(Path.Combine(root.FullName, "fixe.gif"), new GifBitmapEncoder(), still);
                gray[1]++;
                var altered = BitmapFrame.Create(BitmapSource.Create(3, 1, 96, 96, PixelFormats.Gray8, null, gray, 3));
                SaveScanFrames(Path.Combine(root.FullName, "un pixel.png"), new PngBitmapEncoder(), altered);
                SaveScanFrames(Path.Combine(root.FullName, "animée.gif"), new GifBitmapEncoder(), still, altered);
                foreach (byte low in new byte[] { 0, 1 })
                {
                    var fine = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray16, null, new byte[] { low, 96 }, 2);
                    SaveScanFrames(Path.Combine(root.FullName, "précision" + low), new PngBitmapEncoder(), BitmapFrame.Create(fine));
                }
                string paletteTiff = Path.Combine(root.FullName, "palette.tif");
                var indexed = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Indexed8,
                    new BitmapPalette(new[] { Colors.Orange, Colors.Teal }), new byte[] { 0 }, 1);
                SaveScanFrames(paletteTiff, new TiffBitmapEncoder(), BitmapFrame.Create(indexed));
                var tiffData = File.ReadAllBytes(paletteTiff);
                int directory = BitConverter.ToInt32(tiffData, 4);
                int entries = BitConverter.ToUInt16(tiffData, directory);
                bool paletteFound = false;
                for (int i = 0; i < entries; i++)
                {
                    int entry = directory + 2 + 12 * i;
                    if (BitConverter.ToUInt16(tiffData, entry) != 320) continue;
                    int table = BitConverter.ToInt32(tiffData, entry + 8);
                    tiffData[table] ^= 1; // Le bit faible de la composante rouge de l’unique pixel.
                    paletteFound = true;
                }
                Check(paletteFound, "Doublons : le TIFF de contrôle transporte une palette 16 bits");
                File.WriteAllBytes(Path.Combine(sub.FullName, "autre palette.tif"), tiffData);
                DuplicateScanResult scan;
                using (File.Create(locked))
                    scan = DuplicateScanner.Scan(root.FullName, token, null);
                Check(scan.Groups.Count == 2, "Doublons : un pixel, deux trames et les nuances sur 16 bits restent distincts");
                var documents = scan.Groups.Single(g => g.SameBytes);
                Check(documents.Files.Count == 2 && documents.Files.All(f => f.Format == "PDF") &&
                    documents.Files.Any(f => f.FullPath == document), "Doublons : PDF renommés identifiés par contenu dans les sous-dossiers");
                var photos = scan.Groups.Single(g => !g.SameBytes);
                Check(photos.Files.Count == 3 && photos.Files.Select(f => f.Length).Distinct().Count() == 2 &&
                    photos.Files.All(f => f.Width == 2 && f.Height == 3 && f.Thumbnail != null),
                    "Doublons : le JPEG orienté, sa copie et son export PNG forment un seul groupe illustré");
                using (var hash = SHA256.Create())
                    foreach (var f in scan.Groups.SelectMany(g => g.Files))
                        using (var stream = File.OpenRead(f.FullPath))
                            Check(f.ByteHash == BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""), "Doublons : empreinte de chaque exemplaire");
                Check(scan.Issues.Any(s => s.Contains(locked)), "Doublons : chemin illisible signalé sans abandonner les autres fichiers");
                var bin = DuplicateScanner.Scan(Path.Combine(Path.GetPathRoot(root.FullName), "$Recycle.Bin", root.Name), token, null);
                Check(bin.Scanned == 0 && bin.Issues.Any(s => s.Contains("Corbeille")), "Doublons : un dossier choisi dans la Corbeille est écarté et expliqué");
                Check(DuplicateRecycleBin.ContainsPath(@"C:\$Recycle.Bin\exemplaire") &&
                    !DuplicateRecycleBin.ContainsPath(@"C:\Photos\$Recycle.Bin"), "Doublons : seuls les chemins de la Corbeille du volume sont exclus");
                using (var cancel = new CancellationTokenSource())
                {
                    bool stopped = false;
                    try { DuplicateScanner.Scan(root.FullName, cancel.Token, message => cancel.Cancel()); }
                    catch (OperationCanceledException) { stopped = true; }
                    Check(stopped, "Doublons : annulation pendant le parcours");
                }
            }
            finally { root.Delete(true); }
        }

        static void SaveScanFrames(string path, BitmapEncoder encoder, params BitmapFrame[] frames)
        {
            foreach (var frame in frames) encoder.Frames.Add(frame);
            using (var file = File.Create(path)) encoder.Save(file);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        static extern SafeFileHandle CreateScanTestStream(string path, uint access, uint share, IntPtr security,
            uint disposition, uint attributes, IntPtr template);

        static void WriteScanStream(string file, byte value, string name = "annotation")
        {
            using (var handle = CreateScanTestStream(file + ":" + name, 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                using (var stream = new FileStream(handle, FileAccess.Write)) stream.WriteByte(value);
            }
        }
    }
}
