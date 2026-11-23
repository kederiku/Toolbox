using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace AtelierPC
{
    internal sealed class ImageEngine : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "AtelierPC-image-" + Guid.NewGuid().ToString("N"));

        internal ImageEngine()
        {
            Directory.CreateDirectory(DirectoryPath);
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var data = assembly.GetManifestResourceStream("AtelierPC.ImageEngine.zip"))
                using (var archive = new ZipArchive(data, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name != entry.FullName) throw new InvalidDataException("Archive du convertisseur incorrecte.");
                        using (var input = entry.Open())
                        using (var file = File.Create(Path.Combine(DirectoryPath, entry.Name))) input.CopyTo(file);
                    }
                }
                using (var policy = assembly.GetManifestResourceStream("AtelierPC.ImagePolicy.xml"))
                using (var file = File.Create(Path.Combine(DirectoryPath, "policy.xml"))) policy.CopyTo(file);
                File.WriteAllText(Path.Combine(DirectoryPath, "delegates.xml"), "<delegatemap/>");
            }
            catch { Dispose(); throw; }
        }

        internal string Run(string arguments, string error, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(Path.Combine(DirectoryPath, "magick.exe"), arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true, WorkingDirectory = DirectoryPath
            };
            start.EnvironmentVariables["MAGICK_CONFIGURE_PATH"] = DirectoryPath;
            start.EnvironmentVariables["MAGICK_TEMPORARY_PATH"] = DirectoryPath;
            start.EnvironmentVariables["MAGICK_OCL_DEVICE"] = "OFF";
            start.EnvironmentVariables["PATH"] = Environment.SystemDirectory;
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                var elapsed = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100))
                    {
                        token.ThrowIfCancellationRequested();
                        if (elapsed.Elapsed.TotalMinutes >= 5)
                            throw new InvalidDataException("Cette image dépasse le temps de traitement disponible.");
                    }
                    token.ThrowIfCancellationRequested();
                    string detail = errors.GetAwaiter().GetResult().Trim();
                    if (process.ExitCode != 0 || detail.IndexOf("@ warning/", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidDataException(error, new InvalidOperationException(detail));
                    return output.GetAwaiter().GetResult();
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

        internal static void CopyFile(string source, string destination, CancellationToken token)
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
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
