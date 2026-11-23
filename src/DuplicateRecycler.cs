using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal static class DuplicateRecycler
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

        internal static DuplicateRecycleResult Apply(IEnumerable<DuplicateGroup> groups, ISet<string> selected, CancellationToken token)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return Apply(groups, selected, token, DuplicateRecycleBin.Move);

            DuplicateRecycleResult result = null;
            Exception failure = null;
            var thread = new Thread(new ThreadStart(delegate {
                try { result = Apply(groups, selected, token, DuplicateRecycleBin.Move); }
                catch (Exception ex) { failure = ex; }
            }));
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new InvalidOperationException("Le traitement de la Corbeille a été interrompu.", failure);
            return result;
        }

        internal static DuplicateRecycleResult Apply(IEnumerable<DuplicateGroup> groups, ISet<string> selected,
            CancellationToken token, Action<string, Action> move)
        {
            var result = new DuplicateRecycleResult();
            var pending = new HashSet<string>(selected, StringComparer.Ordinal);
            var marked = new HashSet<string>(selected, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var keepers = group.Files.Where(f => !marked.Contains(f.FullPath)).ToList();
                foreach (var file in group.Files)
                {
                    if (!pending.Remove(file.FullPath)) continue;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (keepers.Count == 0) throw new IOException("Conservez au moins un exemplaire dans ce groupe.");
                        DuplicateFile keeper;
                        using (var kept = OpenKeeper(keepers, file, group.SameBytes, token, out keeper))
                        using (var source = OpenVerified(file, token))
                        using (var transfer = File.Open(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                        {
                            move(file.FullPath, delegate {
                                token.ThrowIfCancellationRequested();
                                CheckPath(kept.Name, kept);
                                CheckPath(file.FullPath, transfer);
                                VerifyStreams(kept.Name, keeper.StreamHash, token);
                                VerifyStreams(file.FullPath, file.StreamHash, token);
                                // Le second handle continue d’empêcher l’écriture. Seul le renommage devient possible.
                                source.Dispose();
                            });
                            result.Moved.Add(file.FullPath);
                        }
                    }
                    catch (OperationCanceledException) { result.Issues.Add(file.FullPath + " — Traitement annulé."); }
                    catch (Exception ex)
                    {
                        if (!IsFileError(ex)) throw;
                        result.Issues.Add(file.FullPath + " — " + Reason(ex));
                    }
                }
            }
            foreach (string path in pending) result.Issues.Add(path + " — Fichier absent des résultats de la recherche.");
            return result;
        }

        static FileStream OpenKeeper(IEnumerable<DuplicateFile> keepers, DuplicateFile selected, bool sameBytes,
            CancellationToken token, out DuplicateFile keeper)
        {
            foreach (var file in keepers)
            {
                token.ThrowIfCancellationRequested();
                if (file.ContentStreamHash != selected.ContentStreamHash || (sameBytes && file.ByteHash != selected.ByteHash)) continue;
                try
                {
                    var stream = OpenVerified(file, token);
                    keeper = file;
                    return stream;
                }
                catch (Exception ex) { if (!IsFileError(ex)) throw; }
            }
            throw new IOException("Aucun exemplaire conservé ne peut être vérifié. Il a pu être modifié, déplacé ou verrouillé.");
        }

        static FileStream OpenVerified(DuplicateFile file, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                CheckPath(file.FullPath, stream);
                if (stream.Length != file.Length || File.GetLastWriteTimeUtc(file.FullPath) != file.LastWriteUtc ||
                    string.IsNullOrEmpty(file.ByteHash) || DuplicateScanner.Hash(stream, token) != file.ByteHash)
                    throw new IOException("Le fichier a changé depuis la recherche.");
                VerifyStreams(file.FullPath, file.StreamHash, token);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        static void CheckPath(string path, FileStream stream)
        {
            if (DuplicateRecycleBin.ContainsPath(path))
                throw new IOException("Ce fichier se trouve déjà dans la Corbeille.");
            for (string part = path; part != null; part = Path.GetDirectoryName(part))
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Le chemin contient un lien ou un point de jonction.");
            }
            var resolved = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, resolved, (uint)resolved.Capacity, 0);
            if (length == 0 || length >= resolved.Capacity)
                throw new IOException("Impossible de vérifier le chemin du fichier ouvert.");
            string expected = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
            if (!string.Equals(expected, resolved.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Le fichier a été déplacé ou son chemin a changé.");
        }

        static void VerifyStreams(string path, string expected, CancellationToken token)
        {
            if (DuplicateStreams.Fingerprint(path, token) != expected)
                throw new IOException("Les données complémentaires du fichier ont changé depuis la recherche.");
        }

        static bool IsFileError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                ex is COMException || ex is Win32Exception || ex is ArgumentException || ex is NotSupportedException ||
                ex is NotImplementedException || ex is InvalidCastException;
        }

        static string Reason(Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return "Le fichier ou son dossier a disparu.";
            if (ex is UnauthorizedAccessException || ex is SecurityException) return "Windows refuse l’accès à ce fichier ou à la Corbeille.";
            if (ex is NotImplementedException || ex is InvalidCastException) return "Windows ne propose pas de Corbeille pour cet emplacement.";
            if (ex is COMException || ex is Win32Exception) return "La Corbeille Windows a refusé le déplacement. " + ex.Message;
            return ex.Message;
        }
    }
}
