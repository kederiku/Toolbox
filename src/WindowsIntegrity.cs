using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    internal sealed class IntegrityCommand
    {
        internal static readonly IntegrityCommand VerifyFiles = new IntegrityCommand("sfc.exe", "/verifyonly",
            "Vérifier les fichiers", "Vérification des fichiers protégés (SFC)", null);
        internal static readonly IntegrityCommand RepairFiles = new IntegrityCommand("sfc.exe", "/scannow",
            "Réparer les fichiers", "Réparation des fichiers protégés (SFC)",
            "Windows va vérifier les fichiers système protégés et tenter de remplacer ceux qui sont endommagés. Lancer la réparation ?");
        internal static readonly IntegrityCommand VerifyStore = new IntegrityCommand("dism.exe", "/Online /Cleanup-Image /ScanHealth",
            "Vérifier le magasin", "Vérification du magasin de composants (DISM)", null);
        internal static readonly IntegrityCommand RepairStore = new IntegrityCommand("dism.exe", "/Online /Cleanup-Image /RestoreHealth",
            "Réparer le magasin", "Réparation du magasin de composants (DISM)",
            "Windows va tenter de réparer le magasin de composants. Il peut télécharger des fichiers depuis Windows Update. Lancer la réparation ?");

        readonly string fileName, arguments;
        internal readonly string Label, Title, Confirmation;
        internal bool UsesDism { get { return fileName == "dism.exe"; } }
        internal string LogName { get { return UsesDism ? "dism.log" : "CBS.log"; } }

        IntegrityCommand(string fileName, string arguments, string label, string title, string confirmation)
        {
            this.fileName = fileName;
            this.arguments = arguments;
            Label = label;
            Title = title;
            Confirmation = confirmation;
        }

        internal ProcessStartInfo StartInfo(IntPtr owner, bool elevated, bool wow64)
        {
            string windows = WindowsIntegrity.WindowsDirectory();
            return new ProcessStartInfo {
                FileName = Path.Combine(windows, wow64 ? "Sysnative" : "System32", fileName),
                WorkingDirectory = Path.Combine(windows, "System32"),
                Arguments = arguments, UseShellExecute = true, Verb = elevated ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Normal, ErrorDialogParentHandle = owner
            };
        }
    }

    internal enum IntegrityOutcome { NotStarted, Finished, Unknown }

    internal sealed class WindowsIntegrity
    {
        readonly Func<ProcessStartInfo, Func<int>> start;
        readonly Func<string, string> readLog;
        int running;
        internal bool IsRunning { get { return Volatile.Read(ref running) != 0; } }

        internal WindowsIntegrity(Func<ProcessStartInfo, Func<int>> start = null, Func<string, string> readLog = null)
        {
            this.start = start ?? StartNative;
            this.readLog = readLog ?? ReadLogFile;
        }

        internal static string WindowsDirectory()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows) || !Path.IsPathRooted(windows))
                throw new InvalidOperationException("Répertoire de Windows introuvable.");
            return windows;
        }

        internal string ReadLog(IntegrityCommand command)
        {
            try
            {
                string path = Path.Combine(WindowsDirectory(), "Logs", command.UsesDism ? "DISM" : "CBS", command.LogName);
                string excerpt = readLog(path);
                return string.IsNullOrWhiteSpace(excerpt) ? "Le journal est vide." : excerpt;
            }
            catch (UnauthorizedAccessException) { return "Windows refuse l’accès au journal avec les droits actuels."; }
            catch (SecurityException) { return "Windows refuse l’accès au journal avec les droits actuels."; }
            catch (FileNotFoundException) { return "Windows n’a pas créé ce journal ou l’a déjà archivé."; }
            catch (Exception) { return "Lecture du journal impossible. Vous pouvez réessayer dans un instant."; }
        }

        static string ReadLogFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return ReadTail(stream);
        }

        internal static string ReadTail(Stream stream)
        {
            const int limit = 48 * 1024;
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true))
            {
                reader.Peek(); // Detect a byte order mark before seeking into a large log.
                long start = (Math.Max(0, stream.Length - limit) + 3) & ~3L;
                if (start > 0)
                {
                    stream.Position = start;
                    reader.DiscardBufferedData();
                }
                var buffer = new char[limit];
                int count = reader.ReadBlock(buffer, 0, buffer.Length);
                string text = new string(buffer, 0, count);
                if (start == 0) return text;
                int newline = text.IndexOf('\n');
                return newline >= 0 && newline + 1 < count ? text.Substring(newline + 1) : "…" + text;
            }
        }

        internal static bool IsElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static Func<int> StartNative(ProcessStartInfo info)
        {
            var child = Process.Start(info);
            if (child == null) throw new InvalidOperationException();
            return delegate {
                using (child)
                {
                    child.WaitForExit();
                    return child.ExitCode;
                }
            };
        }

        internal async Task<IntegrityOutcome> RunAsync(IntegrityCommand command, IntPtr owner,
            Func<IntegrityCommand, bool> confirm, Action<string> record)
        {
            if (command == null) throw new ArgumentNullException("command");
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return IntegrityOutcome.NotStarted;
            try
            {
                if (command.Confirmation != null && (confirm == null || !confirm(command)))
                {
                    record("Réparation annulée avant le lancement.");
                    return IntegrityOutcome.NotStarted;
                }

                Func<int> waitForExit;
                try
                {
                    var info = command.StartInfo(owner, IsElevated(), Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess);
                    waitForExit = await Task.Run(() => start(info));
                }
                catch (Win32Exception ex)
                {
                    record(ex.NativeErrorCode == 1223 ? "Autorisation Windows refusée. Commande annulée."
                        : "Échec du lancement par Windows (code " + ex.NativeErrorCode + ").");
                    return IntegrityOutcome.NotStarted;
                }
                catch (Exception)
                {
                    record("Échec du lancement de la commande Windows.");
                    return IntegrityOutcome.NotStarted;
                }

                var completion = Task.Run(waitForExit);
                record("Commande lancée. Suivez sa progression dans la console Windows.");
                int code;
                try { code = await completion; }
                catch (Exception)
                {
                    record("Fin de commande inconnue. Consultez la console ou le journal Windows pour vérifier le résultat.");
                    return IntegrityOutcome.Unknown;
                }
                string detail = "Commande terminée (code " + code + "). Résultat à confirmer.";
                if (command.UsesDism && (code == 3010 || code == 1641))
                    detail += " Windows demande un redémarrage.";
                else if (command.UsesDism && code != 0)
                    detail += " DISM signale une erreur. Consultez le journal pour en connaître la cause.";
                else if (command.Confirmation != null)
                    detail += " Relancez la vérification correspondante et contrôlez le symptôme.";
                else
                    detail += " Consultez les conclusions de Windows pour savoir si une réparation est indiquée.";
                record(detail);
                return IntegrityOutcome.Finished;
            }
            finally { Volatile.Write(ref running, 0); }
        }
    }
}
