using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal static class UpdateFiles
    {
        internal const string ReadyVariable = "TOOLBOX_UPDATE_READY";
        internal const string ReadyPrefix = @"Local\Toolbox.Update.Ready.";
        internal const string StagePrefix = ".toolbox-update-";
        internal const string Payload = "Toolbox.next.exe";

        internal static string HashFile(string file)
        {
            using (var input = File.OpenRead(file)) return Hash(input);
        }

        internal static string Hash(Stream input)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }

        internal static bool Same(string a, string b)
        {
            return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Un lien de redirection empêche la mise à jour de ce fichier.");
        }

        internal static void CheckTarget(string target)
        {
            RejectLink(target);
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new IOException("La Toolbox est en lecture seule. Téléchargez la mise à jour manuellement.");
        }

        internal static string StageDirectory(string target, string staged)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(staged));
            string name = Path.GetFileName(dir);
            Guid id;
            if (!Same(Path.GetFileName(staged), Payload) || !name.StartsWith(StagePrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.Substring(StagePrefix.Length), "N", out id) ||
                !Same(Path.GetDirectoryName(dir), Path.GetDirectoryName(Path.GetFullPath(target))))
                throw new IOException("Le fichier téléchargé doit se trouver dans le dossier de mise à jour de Toolbox.");
            RejectLink(dir);
            return dir;
        }

        internal static void RemoveStage(string staged)
        {
            string dir = Path.GetDirectoryName(staged);
            if (!Directory.Exists(dir)) return;
            RejectLink(dir);
            if (File.Exists(staged))
            {
                RejectLink(staged);
                File.Delete(staged);
            }
            // Une sauvegarde de secours ou un fichier inconnu doit rester récupérable.
            if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        }

        sealed class ProcessExitHandle : WaitHandle
        {
            internal ProcessExitHandle(Process process)
            {
                SafeWaitHandle = new SafeWaitHandle(process.Handle, false);
            }
        }

        static void Restart(string target, string expectedDigest)
        {
            string name = ReadyPrefix + Guid.NewGuid().ToString("N");
            bool created;
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name, out created))
            {
                if (!created) throw new IOException("Impossible de créer le signal de démarrage de Toolbox.");
                var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) };
                start.EnvironmentVariables[ReadyVariable] = name;
                Process child;
                using (var pinned = File.OpenRead(target))
                {
                    if (!Same(Hash(pinned), expectedDigest)) throw new IOException("Le téléchargement a changé pendant le remplacement.");
                    child = Process.Start(start);
                }
                if (child == null) throw new IOException("La nouvelle Toolbox n’a pas pu démarrer.");
                using (child)
                using (var exited = new ProcessExitHandle(child))
                {
                    // Tant que le processus démarre, sa sauvegarde reste disponible.
                    if (WaitHandle.WaitAny(new WaitHandle[] { ready, exited }) == 1 && !ready.WaitOne(0))
                        throw new IOException("La nouvelle Toolbox s’est fermée avant d’afficher sa fenêtre.");
                }
            }
        }

        internal static void Install(string target, string staged, string expectedDigest, string installedDigest)
        {
            string dir = StageDirectory(target, staged);
            string backup = Path.Combine(dir, "Toolbox.previous.exe");
            string key;
            using (var path = new MemoryStream(Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToUpperInvariant())))
                key = Hash(path);
            using (var mutex = new Mutex(false, @"Global\AtelierPC.Toolbox.Update." + key))
            {
                try { if (!mutex.WaitOne(0)) throw new IOException("Une autre mise à jour de cette Toolbox est en cours."); }
                catch (AbandonedMutexException) { /* Le contrôle des deux fichiers suit la reprise du verrou. */ }
                try
                {
                    CheckTarget(target);
                    RejectLink(staged);
                    if (File.Exists(backup)) throw new IOException("Le dossier contient déjà une sauvegarde de Toolbox.");
                    if (!Same(HashFile(target), installedDigest))
                        throw new IOException("La Toolbox installée a changé pendant le téléchargement.");
                    if (!Same(HashFile(staged), expectedDigest))
                        throw new IOException("L’empreinte SHA-256 du téléchargement ne correspond plus à la release.");
                    var before = AssemblyName.GetAssemblyName(target);
                    var after = AssemblyName.GetAssemblyName(staged);
                    if (!Same(before.Name, "Toolbox") || !Same(after.Name, before.Name) || after.Version <= before.Version)
                        throw new IOException("La nouvelle version doit être une Toolbox plus récente que la version installée.");

                    try
                    {
                        File.Replace(staged, target, backup);
                        // Contrôler les fichiers effectivement échangés avant le premier lancement.
                        if (!Same(HashFile(backup), installedDigest))
                            throw new IOException("La Toolbox installée a changé pendant le remplacement.");
                        Restart(target, expectedDigest);
                    }
                    catch (Exception failure)
                    {
                        if (!File.Exists(backup)) throw;
                        try
                        {
                            if (File.Exists(target)) File.Replace(backup, target, null);
                            else File.Move(backup, target);
                        }
                        catch (Exception restore)
                        {
                            throw new IOException("Restauration impossible. Votre copie précédente se trouve ici : " + backup +
                                Environment.NewLine + restore.Message, failure);
                        }
                        throw new IOException("La mise à jour a échoué. La version précédente a été restaurée." +
                            Environment.NewLine + failure.Message, failure);
                    }
                    try { File.Delete(backup); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
