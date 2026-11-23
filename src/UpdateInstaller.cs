using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AtelierPC
{
    internal static class UpdateInstaller
    {
        static readonly Dictionary<string, string> stages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string CreateStage(string target)
        {
            target = Path.GetFullPath(target);
            UpdateFiles.CheckTarget(target);
            string dir = Path.Combine(Path.GetDirectoryName(target), UpdateFiles.StagePrefix + Guid.NewGuid().ToString("N"));
            CreatePrivateDirectory(dir);
            string staged = Path.Combine(dir, UpdateFiles.Payload);
            lock (stages) stages.Add(staged, target);
            return staged;
        }

        static void CreatePrivateDirectory(string path)
        {
            var access = new DirectorySecurity();
            access.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
                access.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path, access);
            UpdateFiles.RejectLink(path);
        }

        internal static void CleanStage(string stagedFile)
        {
            if (stagedFile == null) return;
            lock (stages)
            {
                if (!stages.ContainsKey(stagedFile)) return;
                try { UpdateFiles.RemoveStage(stagedFile); stages.Remove(stagedFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static UpdateHandoff Prepare(string target, string stagedFile, string sha256)
        {
            target = Path.GetFullPath(target);
            string owner;
            lock (stages)
                if (!stages.TryGetValue(stagedFile, out owner) || !UpdateFiles.Same(owner, target))
                    throw new IOException("Le téléchargement ne correspond pas à cette installation de Toolbox.");
            using (var parent = Process.GetCurrentProcess())
            {
                if (!UpdateFiles.Same(target, parent.MainModule.FileName))
                    throw new IOException("Seule la Toolbox en cours d’exécution peut être mise à jour.");
                UpdateFiles.CheckTarget(target);
                UpdateFiles.StageDirectory(target, stagedFile);
                UpdateFiles.RejectLink(stagedFile);
                if (sha256 == null || sha256.Length != 64 || !Array.TrueForAll(sha256.ToCharArray(), Uri.IsHexDigit))
                    throw new IOException("L’empreinte SHA-256 de la release est invalide.");

                string installedDigest = UpdateFiles.HashFile(target);
                var start = new ProcessStartInfo {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory
                };
                Process worker;
                using (var pinned = OpenWorker())
                {
                    start.FileName = pinned.Name;
                    worker = Process.Start(start);
                }
                var handoff = new UpdateHandoff(worker);
                try
                {
                    worker.StandardInput.WriteLine(parent.Id.ToString(CultureInfo.InvariantCulture));
                    worker.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(target)));
                    worker.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(stagedFile)));
                    worker.StandardInput.WriteLine(sha256);
                    worker.StandardInput.WriteLine(installedDigest);
                    worker.StandardInput.Flush();
                    var ready = worker.StandardOutput.ReadLineAsync();
                    if (!ready.Wait(10000)) throw new IOException("L’assistant de mise à jour ne répond pas.");
                    if (ready.Result != "READY")
                        throw new IOException(ready.Result != null && ready.Result.StartsWith("ERROR ", StringComparison.Ordinal)
                            ? ready.Result.Substring(6) : "Impossible de démarrer l’assistant de mise à jour.");
                    return handoff;
                }
                catch { handoff.Dispose(); throw; }
            }
        }

        // L’assistant ne peut pas effacer sa propre image en cours d’exécution sous Windows.
        // Un cache privé par empreinte permet sa réutilisation et le nettoyage complet du téléchargement.
        static FileStream OpenWorker()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AtelierPC", "UpdateHelper");
            CreatePrivateDirectory(dir);
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtelierPC.UpdateHelper.exe"))
            {
                if (resource == null) throw new IOException("L’assistant intégré de mise à jour est absent.");
                string digest = UpdateFiles.Hash(resource);
                string file = Path.Combine(dir, digest + ".exe");
                if (!File.Exists(file))
                {
                    string temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        resource.Position = 0;
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            resource.CopyTo(output);
                        try { File.Move(temp, file); }
                        catch (IOException) { if (!File.Exists(file)) throw; }
                    }
                    finally { File.Delete(temp); }
                }
                UpdateFiles.RejectLink(file);
                var pinned = File.OpenRead(file);
                try
                {
                    if (!UpdateFiles.Same(UpdateFiles.Hash(pinned), digest))
                        throw new IOException("L’assistant local de mise à jour est endommagé.");
                    return pinned;
                }
                catch { pinned.Dispose(); throw; }
            }
        }
    }

    internal sealed class UpdateHandoff : IDisposable
    {
        Process worker;

        internal UpdateHandoff(Process worker)
        {
            this.worker = worker;
        }

        internal void Commit()
        {
            if (worker == null) throw new ObjectDisposedException("UpdateHandoff");
            try
            {
                worker.StandardInput.WriteLine("INSTALL");
                worker.StandardInput.Flush();
            }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            if (worker == null) return;
            try { worker.StandardInput.Close(); }
            finally { worker.Dispose(); worker = null; }
        }
    }
}
