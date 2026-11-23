using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    internal sealed class VideoEngine : IDisposable
    {
        readonly string directory;
        internal string WorkingDirectory { get { return directory; } }

        internal VideoEngine(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Environment.Is64BitOperatingSystem)
                throw new PlatformNotSupportedException("La conversion audio et vidéo nécessite Windows 64 bits compatible x64.");
            directory = Path.Combine(Path.GetTempPath(), "AtelierPC-video-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        // Extract each tool only when needed. Reading a video needs only ffprobe.
        internal string Executable(string program, CancellationToken token)
        {
            if (program != "ffmpeg" && program != "ffprobe") throw new ArgumentException("Outil FFmpeg inconnu.");
            string name = program + ".exe";
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
            using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtelierPC.VideoEngine.zip"))
            {
                if (data == null) throw new InvalidDataException("Le moteur FFmpeg manque dans cet exécutable.");
                using (var zip = new ZipArchive(data, ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry(name);
                    if (entry == null) throw new InvalidDataException("Le moteur FFmpeg intégré est incomplet.");
                    try
                    {
                        using (var input = entry.Open())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var buffer = new byte[81920];
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                token.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, read);
                            }
                        }
                    }
                    catch { File.Delete(path); throw; }
                }
            }
            return path;
        }

        // .NET Framework exposes a single Windows command line, including the closing quote's escape rules.
        static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? 2 * slashes + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            return result.Append('\\', 2 * slashes).Append('"').ToString();
        }

        static async Task<string> ReadErrorAsync(StreamReader reader)
        {
            const int limit = 4096;
            var tail = new StringBuilder();
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > limit) tail.Remove(0, tail.Length - limit);
            }
            return tail.ToString().Trim();
        }

        internal string Run(string program, IEnumerable<string> arguments, CancellationToken token,
            Action<string> outputLine = null)
        {
            return RunProcess(Executable(program, token), arguments, token, outputLine, false,
                detail => new InvalidDataException("Le moteur FFmpeg a signalé une erreur.", new Exception(detail)));
        }

        internal string RunProcess(string executable, IEnumerable<string> arguments, CancellationToken token,
            Action<string> outputLine, bool includeChildren, Func<string, Exception> failure)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(Quote))) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables.Remove("FFREPORT");
            if (includeChildren)
            {
                start.EnvironmentVariables["PATH"] = directory + ";" + Environment.SystemDirectory;
                start.EnvironmentVariables["DENO_DIR"] = Path.Combine(directory, "deno-cache");
                start.EnvironmentVariables["DENO_NO_UPDATE_CHECK"] = "1";
            }
            using (var children = includeChildren ? new YoutubeProcessGroup() : null)
            using (var process = new Process { StartInfo = start })
            {
                token.ThrowIfCancellationRequested();
                try { process.Start(); }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 193 || ex.NativeErrorCode == 216)
                        throw new PlatformNotSupportedException("Ce Windows ne peut pas exécuter le moteur " + Path.GetFileNameWithoutExtension(executable) + " x64.", ex);
                    throw;
                }
                var errors = ReadErrorAsync(process.StandardError);
                try { if (children != null) children.Attach(process); }
                catch { Stop(process); process.WaitForExit(); throw; }
                using (token.Register(delegate { if (children != null) children.Stop(); else Stop(process); }))
                {
                    try
                    {
                        var output = new StringBuilder();
                        string line;
                        while ((line = process.StandardOutput.ReadLine()) != null)
                        {
                            token.ThrowIfCancellationRequested();
                            if (outputLine == null) output.AppendLine(line);
                            else outputLine(line);
                        }
                        process.WaitForExit();
                        string detail = errors.GetAwaiter().GetResult();
                        token.ThrowIfCancellationRequested();
                        if (process.ExitCode != 0)
                            throw failure(detail);
                        return output.ToString();
                    }
                    finally
                    {
                        if (children != null) children.Stop();
                        Stop(process);
                        process.WaitForExit();
                        errors.GetAwaiter().GetResult();
                    }
                }
            }
        }

        static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { if (!process.HasExited) throw; }
        }

        public void Dispose()
        {
            RemoveTemporaryDirectory(directory);
        }

        internal static void RemoveTemporaryDirectory(string path)
        {
            if (TryRemoveDirectory(path)) return;
            // Windows peut garder brièvement l’image de ffmpeg.exe ouverte après WaitForExit.
            // Un thread de premier plan termine le nettoyage même si la fenêtre se ferme entre-temps.
            var cleanup = new Thread(delegate() {
                foreach (var delay in new[] { 100, 250, 500, 1000, 2000, 4000 })
                {
                    Thread.Sleep(delay);
                    if (TryRemoveDirectory(path)) return;
                }
            }) { IsBackground = false, Name = "Nettoyage du moteur FFmpeg" };
            cleanup.Start();
        }

        static bool TryRemoveDirectory(string path)
        {
            try { Directory.Delete(path, true); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
