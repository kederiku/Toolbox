using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace AtelierPC
{
    internal sealed class PdfMetadataEngine : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "AtelierPC-pdf-" + Guid.NewGuid().ToString("N"));

        internal PdfMetadataEngine()
        {
            Directory.CreateDirectory(DirectoryPath);
            try
            {
                using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtelierPC.PdfEngine.zip"))
                using (var archive = new ZipArchive(data, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name != entry.FullName) throw new InvalidDataException("Archive du moteur PDF incorrecte.");
                        using (var input = entry.Open())
                        using (var file = File.Create(Path.Combine(DirectoryPath, entry.Name))) input.CopyTo(file);
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        internal int Run(CancellationToken token, bool queryEncryption, params string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            // Les arguments sont des options fixes, des chemins de fichiers Windows ou des noms internes.
            var start = new ProcessStartInfo(Path.Combine(DirectoryPath, "qpdf.exe"),
                String.Join(" ", arguments.Select(arg => "\"" + arg + "\""))) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = DirectoryPath,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            start.EnvironmentVariables["PATH"] = Environment.SystemDirectory;
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed.TotalMinutes >= 5)
                            throw new InvalidDataException("Le PDF dépasse le temps de traitement disponible.");
                    }
                    token.ThrowIfCancellationRequested();
                    output.GetAwaiter().GetResult();
                    var detail = errors.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 && !(queryEncryption && process.ExitCode == 2))
                        throw new InvalidDataException("La structure ou le contenu du PDF ne peut pas être vérifié.", new InvalidDataException(detail));
                    return process.ExitCode;
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
        }

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
