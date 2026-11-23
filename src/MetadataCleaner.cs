using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal sealed class MetadataResult
    {
        readonly SortedSet<string> removed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        internal bool DocumentProperties, PhotoProperties, PreviousRevisions;
        internal int EmbeddedPhotos;
        internal string Destination;
        internal bool Changed { get { return DocumentProperties || PhotoProperties || PreviousRevisions || EmbeddedPhotos != 0; } }
        internal bool HasReportedRemoval { get { return removed.Count != 0 || DocumentProperties || PreviousRevisions; } }

        internal void Removed(string label) { removed.Add(label); }

        internal string Summary()
        {
            if (!Changed) return "Le fichier était déjà dépourvu des métadonnées ciblées.";
            var parts = new List<string>();
            if (removed.Count != 0) parts.Add("Éléments retirés — " + string.Join(" · ", removed) + ".");
            else
            {
                if (PhotoProperties) parts.Add("Structure de la photo normalisée.");
                if (DocumentProperties) parts.Add("Propriétés du document nettoyées.");
            }
            if (PreviousRevisions) parts.Add("Anciennes révisions du PDF supprimées.");
            if (EmbeddedPhotos == 1) parts.Add("1 image intégrée réécrite.");
            if (EmbeddedPhotos > 1) parts.Add(EmbeddedPhotos + " images intégrées réécrites.");
            return string.Join(" ", parts);
        }
    }

    internal static class MetadataCleaner
    {
        internal const string Filter = "Photos et documents|*.jpg;*.jpeg;*.png;*.webp;*.pdf;*.docx;*.xlsx;*.pptx";

        internal static string Extension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".jpeg": return ".jpg";
                case ".jpg": case ".png": case ".webp": case ".pdf":
                case ".docx": case ".xlsx": case ".pptx": return ext;
                default: throw new InvalidDataException("Choisissez un fichier JPEG, PNG, WebP, PDF, Word (.docx), Excel (.xlsx) ou PowerPoint (.pptx).");
            }
        }

        // Renommer la copie complète évite de fusionner les flux NTFS de l’ancienne destination.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string replacement, int flags);

        [StructLayout(LayoutKind.Sequential)]
        struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

        internal static void CheckDistinctFiles(FileStream original, string destination)
        {
            if (!File.Exists(destination)) return;
            using (var other = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                FileInformation source, target;
                if (!GetFileInformationByHandle(original.SafeFileHandle, out source) ||
                    !GetFileInformationByHandle(other.SafeFileHandle, out target))
                    throw new IOException("Impossible de vérifier l’identité de la destination. Choisissez un nouveau nom.");
                if (source.Volume == target.Volume && source.IndexHigh == target.IndexHigh && source.IndexLow == target.IndexLow)
                    throw new IOException("Cette destination désigne le fichier d’origine. Choisissez un autre fichier.");
            }
        }

        internal static MetadataResult Save(string source, string destination, bool overwrite, CancellationToken token, Action<FileProgress> progress = null)
        {
            return SaveCore(source, destination, overwrite, null, token, progress);
        }

        internal static string CopyName(string source)
        {
            return Path.GetFileNameWithoutExtension(source) + "-nettoye" + Path.GetExtension(source);
        }

        internal static MetadataResult SaveToFolder(string source, string folder, ISet<string> originals, CancellationToken token, Action<FileProgress> progress = null)
        {
            string name = CopyName(source);
            return SaveCore(source, Path.Combine(folder, name), false, originals, token, progress);
        }

        static string Publish(string temp, string preferred, bool overwrite, ISet<string> originals, CancellationToken token)
        {
            string target = preferred;
            for (int number = 2; ; number++)
            {
                token.ThrowIfCancellationRequested();
                if (originals == null || !(originals.Contains(target) || File.Exists(target) || Directory.Exists(target)))
                {
                    if (MoveFileEx(temp, target, overwrite ? 1 : 0)) return target;
                    int error = Marshal.GetLastWin32Error();
                    // Deux lots peuvent choisir le même nom avant que l’un publie sa copie.
                    if (originals == null || (error != 80 && error != 183 && !File.Exists(target) && !Directory.Exists(target)))
                        throw new IOException("Impossible d’enregistrer la copie. " + new Win32Exception(error).Message);
                }
                target = Path.Combine(Path.GetDirectoryName(preferred), Path.GetFileNameWithoutExtension(preferred)
                    + " (" + number + ")" + Path.GetExtension(preferred));
            }
        }

        static MetadataResult SaveCore(string source, string destination, bool overwrite, ISet<string> originals, CancellationToken token, Action<FileProgress> progress)
        {
            token.ThrowIfCancellationRequested();
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            string ext = Extension(source);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choisissez un nom différent pour conserver le fichier d’origine.");
            if (Extension(destination) != ext) throw new IOException("Conservez l’extension du fichier d’origine pour cette copie.");
            if (originals == null && !overwrite && File.Exists(destination)) throw new IOException("Un fichier porte déjà ce nom.");
            string directory = Path.GetDirectoryName(destination);
            string temp = Path.Combine(directory, ".atelier-" + Guid.NewGuid().ToString("N") + ext);
            string verification = Path.Combine(directory, ".atelier-" + Guid.NewGuid().ToString("N") + ext);
            using (var original = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (PhotoMetadata.BeginBatch())
            {
                try
                {
                    if (progress != null) progress(FileProgress.Step("Nettoyage", 1, 3));
                    var result = Process(source, temp, ext, token);
                    if (progress != null) progress(FileProgress.Step("Vérification", 2, 3));
                    var remaining = Process(temp, verification, ext, token);
                    if (remaining.Changed) throw new InvalidDataException("La vérification a trouvé des métadonnées résiduelles. La copie ne peut pas être enregistrée.");
                    File.Delete(verification);
                    if (progress != null) progress(FileProgress.Step("Enregistrement", 3, 3));
                    if (overwrite) CheckDistinctFiles(original, destination);
                    result.Destination = Publish(temp, destination, overwrite, originals, token);
                    return result;
                }
                finally
                {
                    File.Delete(temp);
                    File.Delete(verification);
                }
            }
        }

        static MetadataResult Process(string input, string output, string ext, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new MetadataResult();
            switch (ext)
            {
                case ".docx": case ".xlsx": case ".pptx":
                    OfficeMetadata.Clean(input, output, ext, result, token);
                    break;
                case ".pdf":
                    PdfMetadata.Clean(input, output, result, token);
                    break;
                default:
                    byte[] data;
                    using (var file = File.OpenRead(input))
                    {
                        if (file.Length > 256L * 1024 * 1024)
                            throw new InvalidDataException("Cette photo dépasse la limite de traitement de 256 Mio.");
                        data = new byte[(int)file.Length];
                        int read;
                        for (int offset = 0; offset < data.Length; offset += read)
                        {
                            token.ThrowIfCancellationRequested();
                            read = file.Read(data, offset, Math.Min(81920, data.Length - offset));
                            if (read == 0) throw new EndOfStreamException("La photo est incomplète.");
                        }
                    }
                    byte[] clean = PhotoMetadata.Clean(data, ext, token, out result.PhotoProperties, result);
                    using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        for (int offset = 0; offset < clean.Length; offset += 81920)
                        {
                            token.ThrowIfCancellationRequested();
                            file.Write(clean, offset, Math.Min(81920, clean.Length - offset));
                        }
                    }
                    break;
            }
            return result;
        }
    }
}
