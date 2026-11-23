using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Threading;

namespace AtelierPC
{
    internal static class DuplicateScanner
    {
        internal static DuplicateScanResult Scan(string root, CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested();
            var result = new DuplicateScanResult();
            root = Path.GetFullPath(root);
            if (DuplicateRecycleBin.ContainsPath(root))
            {
                result.Issues.Add(root + " — Choisissez un dossier en dehors de la Corbeille Windows.");
                return result;
            }
            var elapsed = Stopwatch.StartNew();
            Action<string> report = message => {
                if (progress != null) progress(message);
                elapsed.Restart();
            };
            report("Recensement des fichiers…");
            var files = new List<DuplicateFile>();
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string current = directories.Pop();
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                if (!DuplicateRecycleBin.ContainsPath(entry.FullName)) directories.Push(entry.FullName);
                            }
                            else
                                files.Add(new DuplicateFile { FullPath = entry.FullName, Length = ((FileInfo)entry).Length, LastWriteUtc = entry.LastWriteTimeUtc });
                        }
                        catch (Exception ex) { RecordIssue(result, entry.FullName, ex); }
                        if (elapsed.ElapsedMilliseconds >= 250) report(files.Count + " fichiers recensés…");
                    }
                }
                catch (Exception ex) { RecordIssue(result, current, ex); }
            }

            report("Comparaison des fichiers et des photos…");
            var repeatedSizes = new HashSet<long>(files.GroupBy(f => f.Length).Where(g => g.Count() > 1).Select(g => g.Key));
            var byHash = new Dictionary<Tuple<string, string>, DuplicateGroup>();
            var pixelsByGroup = new Dictionary<DuplicateGroup, string>();
            using (var photos = new DuplicatePhotos())
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    result.Scanned++;
                    try
                    {
                        using (var input = OpenSnapshot(file))
                        {
                            file.Format = DuplicatePhotos.Identify(input);
                            bool photo = DuplicatePhotos.CanDecode(file.Format);
                            if (!repeatedSizes.Contains(file.Length) && !photo) continue;
                            input.Position = 0;
                            file.ByteHash = Hash(input, token);
                            file.StreamHash = DuplicateStreams.Fingerprint(file.FullPath, token, out file.ContentStreamHash, out file.HasDownloadInfo);
                            var key = Tuple.Create(file.ByteHash, file.ContentStreamHash);
                            DuplicateGroup copies;
                            if (!byHash.TryGetValue(key, out copies))
                            {
                                copies = new DuplicateGroup();
                                byHash.Add(key, copies);
                                copies.Files.Add(file);
                                if (photo)
                                {
                                    string signature = photos.Read(file, token);
                                    if (signature != null) pixelsByGroup.Add(copies, signature);
                                    photos.ReadVisual(file, token);
                                }
                            }
                            else
                            {
                                file.Width = copies.Files[0].Width;
                                file.Height = copies.Files[0].Height;
                                file.VisualSample = copies.Files[0].VisualSample;
                                copies.Files.Add(file);
                            }
                        }
                    }
                    catch (Exception ex) { RecordIssue(result, file.FullPath, ex); }
                    finally
                    {
                        if (elapsed.ElapsedMilliseconds >= 250) report(result.Scanned + " / " + files.Count + " fichiers examinés…");
                    }
                }

                var merged = new Dictionary<Tuple<bool, string, string>, DuplicateGroup>();
                foreach (var pair in byHash)
                {
                    token.ThrowIfCancellationRequested();
                    string pixels;
                    bool photo = pixelsByGroup.TryGetValue(pair.Value, out pixels);
                    var key = Tuple.Create(photo, photo ? pixels : pair.Key.Item1, pair.Key.Item2);
                    DuplicateGroup group;
                    if (!merged.TryGetValue(key, out group))
                    {
                        group = pair.Value;
                        group.SameBytes = true;
                        merged.Add(key, group);
                    }
                    else
                    {
                        group.Files.AddRange(pair.Value.Files);
                        group.SameBytes = false;
                    }
                }
                report("Recherche des photos visuellement proches…");
                var families = merged.Values.OrderBy(g => g.Files.Min(f => f.FullPath), StringComparer.Ordinal).ToList();
                result.SimilarGroups.AddRange(SimilarPhotos.Group(families, token));
                var visualMembers = new HashSet<DuplicateFile>(result.SimilarGroups.SelectMany(g => g.Files));
                report("Préparation des aperçus…");
                foreach (var group in families)
                {
                    token.ThrowIfCancellationRequested();
                    if (group.Files.Count < 2 && !visualMembers.Contains(group.Files[0])) continue;
                    group.Files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
                    if (group.Files.Count > 1) result.Groups.Add(group);
                    // Seuls les exemplaires d’une même famille exacte partagent leur aperçu.
                    foreach (var file in group.Files)
                    {
                        if (file.Width == 0) break;
                        try
                        {
                            using (var input = OpenSnapshot(file))
                            {
                                if (Hash(input, token) != file.ByteHash) throw Changed();
                                var thumbnail = photos.Thumbnail(file, token);
                                foreach (var member in group.Files) member.Thumbnail = thumbnail;
                            }
                            break;
                        }
                        catch (Exception ex) { RecordIssue(result, file.FullPath, ex); }
                    }
                }
                foreach (var file in files) file.VisualSample = null;
            }
            token.ThrowIfCancellationRequested();
            result.Groups.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Files[0].FullPath, b.Files[0].FullPath));
            return result;
        }

        static IOException Changed() { return new IOException("Le fichier a changé pendant l’analyse."); }

        static FileStream OpenSnapshot(DuplicateFile file)
        {
            if ((File.GetAttributes(file.FullPath) & FileAttributes.ReparsePoint) != 0) throw Changed();
            var input = File.Open(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (input.Length != file.Length || File.GetLastWriteTimeUtc(file.FullPath) != file.LastWriteUtc) throw Changed();
                return input;
            }
            catch { input.Dispose(); throw; }
        }

        internal static string Hash(Stream input, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[81920];
                int read;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                }
                hash.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }

        static void RecordIssue(DuplicateScanResult result, string path, Exception ex)
        {
            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException ||
                ex is SecurityException || ex is Win32Exception || ex is NotSupportedException))
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            result.Issues.Add(path + " — " + ex.Message);
        }
    }
}
