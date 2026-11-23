using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AtelierPC
{
    internal static class PdfWatermarkPreview
    {
        internal const string Command = "--apercu-page-pdf";
        const string PreviewFile = "apercu.bmp";

        internal static BitmapSource Read(string directory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, Command) {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed.TotalMinutes >= 2)
                            throw new InvalidDataException("Windows n’a pas pu afficher la première page dans le temps prévu.");
                    }
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidDataException("Windows ne parvient pas à afficher la première page de ce PDF.");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        process.WaitForExit();
                    }
                }
            }
            using (var file = File.OpenRead(Path.Combine(directory, PreviewFile)))
            {
                var image = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                image.Freeze();
                return image;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);

        internal static int RunWorker()
        {
            int result = 1;
            try
            {
                var file = Result(StorageFile.GetFileFromPathAsync(Path.GetFullPath("document.pdf")));
                using (var input = Result(file.OpenAsync(FileAccessMode.Read)))
                {
                    var pdf = Result(PdfDocument.LoadFromStreamAsync(input));
                    try { RenderFirstPage(pdf); }
                    finally { Marshal.FinalReleaseComObject(pdf); }
                }
                result = 0;
            }
            catch (Exception) { }
            // Le moteur Windows utilise Direct3D. Certains pilotes graphiques se bloquent ou
            // provoquent une violation d’accès lors du déchargement de leur DLL. À ce stade
            // le processus auxiliaire a fermé tous les fichiers et peut se terminer directement.
            TerminateProcess(new IntPtr(-1), (uint)result);
            return result;
        }

        static void RenderFirstPage(PdfDocument pdf)
        {
            using (var page = pdf.GetPage(0))
            using (var rendered = new InMemoryRandomAccessStream())
            {
                var size = page.Size;
                double scale = 1000 / Math.Max(size.Width, size.Height);
                var options = new PdfPageRenderOptions {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale)),
                    IsIgnoringHighContrast = true
                };
                var render = page.RenderToStreamAsync(rendered, options);
                try { Wait(render); render.GetResults(); }
                finally { render.Close(); }
                using (var reader = new DataReader(rendered.GetInputStreamAt(0)))
                {
                    var bytes = new byte[checked((int)rendered.Size)];
                    Result<uint>(reader.LoadAsync((uint)bytes.Length));
                    reader.ReadBytes(bytes);
                    using (var file = new FileStream(PreviewFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        file.Write(bytes, 0, bytes.Length);
                }
            }
        }

        static T Result<T>(IAsyncOperation<T> operation)
        {
            try { Wait(operation); return operation.GetResults(); }
            finally { operation.Close(); }
        }

        // Les métadonnées WinRT livrées avec Windows sont réparties par espace de noms.
        // Utiliser les opérations natives évite de dépendre des contrats du SDK Windows.
        static void Wait(IAsyncInfo operation)
        {
            while (operation.Status == AsyncStatus.Started) Thread.Sleep(15);
        }
    }
}
