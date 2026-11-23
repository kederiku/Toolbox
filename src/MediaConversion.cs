using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal enum ConversionQuality { Balanced, Smaller, Higher }

    internal abstract class MediaOutput
    {
        internal readonly string Code, Extension;

        protected MediaOutput(string code, string extension) { Code = code; Extension = extension; }
    }

    internal interface IConversionSource : IDisposable
    {
        string Source { get; }
    }

    internal abstract class MediaConversion : IConversionSource
    {
        protected static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        protected readonly VideoEngine engine;
        protected readonly FileStream sourceFile;
        readonly bool ownsEngine;
        public string Source { get; private set; }
        internal double Duration { get; set; }

        protected MediaConversion(string path, CancellationToken token, VideoEngine sharedEngine)
        {
            token.ThrowIfCancellationRequested();
            Source = WindowsFilePath(path);
            ownsEngine = sharedEngine == null;
            engine = sharedEngine ?? new VideoEngine(token);
            try { sourceFile = File.Open(Source, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch { if (ownsEngine) engine.Dispose(); throw; }
        }

        internal static string WindowsFilePath(string path)
        {
            const string error = "Choisissez un fichier sur un disque ou dans un dossier partagé.";
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(error, "path");
            int colon = path.IndexOf(':');
            if (colon >= 0 && (colon != 1 || !char.IsLetter(path[0]) || path.IndexOf(':', colon + 1) >= 0))
                throw new InvalidDataException(error);
            string native = path.Replace('/', '\\');
            if (native.StartsWith(@"\\.\", StringComparison.Ordinal) || native.StartsWith(@"\\?\", StringComparison.Ordinal))
                throw new InvalidDataException(error);
            string full = Path.GetFullPath(native);
            if (Regex.IsMatch(Path.GetFileName(full), @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³]) *(\.|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidDataException(error);
            string root = Path.GetPathRoot(full).TrimEnd('\\');
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string share = root.Substring(root.LastIndexOf('\\') + 1);
                if (share.Equals("pipe", StringComparison.OrdinalIgnoreCase) || share.Equals("mailslot", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(error);
            }
            return full;
        }

        protected static int Number(XmlElement element, string attribute)
        {
            int number;
            return int.TryParse(element.GetAttribute(attribute), NumberStyles.Integer, Invariant, out number) ? number : 0;
        }

        protected static double Seconds(XmlElement element)
        {
            double value;
            return double.TryParse(element.GetAttribute("duration"), NumberStyles.Float, Invariant, out value) &&
                value > 0 && !double.IsInfinity(value) ? value : 0;
        }

        protected static bool Flag(XmlElement stream, string name)
        {
            var disposition = stream["disposition"];
            return disposition != null && disposition.GetAttribute(name) == "1";
        }

        protected void SaveCopy(string destination, string extension, string formatName, double duration, bool overwrite,
            CancellationToken token, Action<double> progress, Func<string, List<string>> arguments)
        {
            token.ThrowIfCancellationRequested();
            destination = WindowsFilePath(destination);
            if (string.Equals(Source, destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choisissez un autre nom pour conserver le fichier d’origine.");
            if (!string.Equals(Path.GetExtension(destination), "." + extension, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Utilisez l’extension ." + extension + " pour ce format.");
            if (!overwrite && File.Exists(destination)) throw new IOException("Un fichier existe déjà à cet emplacement.");

            string temp = Path.Combine(Path.GetDirectoryName(destination), ".AtelierPC-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try
            {
                var args = arguments(temp);
                double last = 0;
                engine.Run("ffmpeg", args, token, line => {
                    const string key = "out_time_us=";
                    long us;
                    if (progress == null || duration <= 0 || !line.StartsWith(key, StringComparison.Ordinal) ||
                        !long.TryParse(line.Substring(key.Length), NumberStyles.Integer, Invariant, out us)) return;
                    double value = Math.Min(99, us / (10000.0 * duration));
                    if (value > last) { last = value; progress(value); }
                });
                if (new FileInfo(temp).Length == 0) throw new InvalidDataException("Le moteur n’a produit aucune donnée.");
                token.ThrowIfCancellationRequested();
                // La source reste ouverte en lecture seule, y compris lorsque la destination désigne un alias Windows.
                if (overwrite && File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("Échec de la conversion en " + formatName +
                    ". Vérifiez que le fichier est lisible et que le dossier de destination dispose d’assez d’espace.", ex);
            }
            finally { File.Delete(temp); }
            if (progress != null) progress(100);
        }

        public void Dispose() { sourceFile.Dispose(); if (ownsEngine) engine.Dispose(); }
    }
}
