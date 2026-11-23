using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckIntegrityFeature()
        {
            var commands = new[] { IntegrityCommand.VerifyFiles, IntegrityCommand.RepairFiles,
                IntegrityCommand.VerifyStore, IntegrityCommand.RepairStore };
            var arguments = new[] { "/verifyonly", "/scannow", "/Online /Cleanup-Image /ScanHealth", "/Online /Cleanup-Image /RestoreHealth" };
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            for (int i = 0; i < commands.Length; i++)
            {
                string filename = i < 2 ? "sfc.exe" : "dism.exe";
                foreach (bool wow64 in new[] { true, false })
                {
                    var info = commands[i].StartInfo(IntPtr.Zero, false, wow64);
                    Check(string.Equals(info.FileName, Path.Combine(windows, wow64 ? "Sysnative" : "System32", filename),
                        StringComparison.OrdinalIgnoreCase) && info.Arguments == arguments[i], "Commande native prédéfinie pour " + filename);
                    Check(info.UseShellExecute && info.Verb == "runas" && !info.CreateNoWindow && info.WindowStyle == ProcessWindowStyle.Normal
                        && string.Equals(info.WorkingDirectory, Path.Combine(windows, "System32"), StringComparison.OrdinalIgnoreCase),
                        "Élévation de la commande dans une console visible depuis le répertoire système");
                }
                Check(string.IsNullOrEmpty(commands[i].StartInfo(IntPtr.Zero, true, false).Verb), "Utilisation des droits déjà acquis");
            }
            int starts = 0, confirmations = 0;
            var messages = new List<string>();
            var service = new WindowsIntegrity(delegate { starts++; return delegate { return 0; }; });
            foreach (var command in commands)
            {
                bool repair = command == IntegrityCommand.RepairFiles || command == IntegrityCommand.RepairStore;
                int before = starts, asked = confirmations;
                messages.Clear();
                WaitIntegrity(service.RunAsync(command, IntPtr.Zero, delegate { confirmations++; return false; }, messages.Add));
                Check(confirmations - asked == (repair ? 1 : 0) && starts - before == (repair ? 0 : 1),
                    "Seules les réparations demandent une confirmation préalable");
                Check(repair ? messages.Single().Contains("annulée") : messages.Last().Contains("à confirmer"),
                    "Le compte rendu distingue annulation et diagnostic terminé");
            }
            foreach (int code in new[] { 1223, 2 })
            {
                messages.Clear();
                service = new WindowsIntegrity(delegate { throw new Win32Exception(code, @"C:\Users\IDENTIFIANT-PRIVE"); });
                WaitIntegrity(service.RunAsync(IntegrityCommand.VerifyFiles, IntPtr.Zero, null, messages.Add));
                Check(messages.Count == 1 && messages[0].Contains(code == 1223 ? "refusée" : "Échec du lancement") &&
                    !messages[0].Contains("IDENTIFIANT") && !service.IsRunning, "Refus UAC et panne de lancement distingués sans détail privé");
            }
            using (var release = new ManualResetEventSlim())
            {
                starts = 0;
                messages.Clear();
                service = new WindowsIntegrity(delegate { Interlocked.Increment(ref starts); return delegate { release.Wait(); return 87; }; });
                var first = service.RunAsync(IntegrityCommand.RepairStore, IntPtr.Zero, delegate { return true; }, messages.Add);
                try
                {
                    WaitIntegrity(service.RunAsync(IntegrityCommand.VerifyFiles, IntPtr.Zero, null, messages.Add));
                    Check(service.IsRunning && !first.IsCompleted, "Une seule opération d’intégrité à la fois");
                }
                finally { release.Set(); }
                WaitIntegrity(first);
                Check(starts == 1 && !service.IsRunning && messages.Count == 2 && messages[0].Contains("lancée") &&
                    messages[1].Contains("87") && messages[1].Contains("erreur") && messages[1].Contains("à confirmer"),
                    "Le lancement et l’erreur signalée par DISM restent distincts");
            }
            messages.Clear();
            service = new WindowsIntegrity(delegate { return delegate { throw new InvalidOperationException("IDENTIFIANT"); }; });
            WaitIntegrity(service.RunAsync(IntegrityCommand.VerifyStore, IntPtr.Zero, null, messages.Add));
            Check(messages.Count == 2 && messages[1].Contains("Fin de commande inconnue") && !messages[1].Contains("IDENTIFIANT"),
                "Une perte du suivi après lancement reste explicite");
            string opened = null;
            service = new WindowsIntegrity(null, path => { opened = path; throw new UnauthorizedAccessException("IDENTIFIANT"); });
            foreach (var command in commands)
            {
                string detail = service.ReadLog(command);
                string file = command == IntegrityCommand.VerifyFiles || command == IntegrityCommand.RepairFiles
                    ? Path.Combine("CBS", "CBS.log") : Path.Combine("DISM", "dism.log");
                Check(string.Equals(opened, Path.Combine(windows, "Logs", file), StringComparison.OrdinalIgnoreCase) &&
                    detail.Contains("accès") && !detail.Contains("IDENTIFIANT"), "Journal Windows fixe et refus de lecture compréhensible");
            }
            string log = "ANCIENNE-ENTRÉE\r\n" + new string('a', 100000) + "\r\nDERNIÈRE-ENTRÉE";
            foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode })
                using (var input = new MemoryStream(encoding.GetPreamble().Concat(encoding.GetBytes(log)).ToArray()))
                {
                    string tail = WindowsIntegrity.ReadTail(input);
                    Check(tail.Contains("DERNIÈRE-ENTRÉE") && !tail.Contains("ANCIENNE") && tail.Length < 65536,
                        "Lecture bornée de la fin du journal dans son encodage");
                }
            using (var input = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 100001) + "\nfin\n")))
                Check(WindowsIntegrity.ReadTail(input).EndsWith("fin\n"), "Une fin de journal non alignée conserve les derniers caractères");
            CheckIntegrityNavigation();
        }

        static void CheckIntegrityNavigation()
        {
            using (var release = new ManualResetEventSlim())
            {
                int starts = 0;
                string copied = null;
                int reads = 0;
                var service = new WindowsIntegrity(delegate { Interlocked.Increment(ref starts); return delegate { release.Wait(); return 0; }; },
                    delegate { Interlocked.Increment(ref reads); return "ÉVÉNEMENT-PRIVÉ"; });
                var shell = new Shell(delegate { return new List<Device>(); }, text => copied = text, integrity: service);
                var window = shell.Window;
                Action<string> click = name => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                try
                {
                    click("IntegrityNav");
                    var view = (StackPanel)window.FindName("IntegrityView");
                    var buttons = Descendants(view).OfType<Button>().ToList();
                    Check(view.Visibility == Visibility.Visible && starts == 0 && buttons.Count == 4 && buttons.All(b => b.Focusable),
                        "La page expose quatre actions au clavier et attend un choix explicite");
                    click("IntegrityVerifyFiles");
                    Check(buttons.All(b => !b.IsEnabled), "Les quatre actions sont verrouillées pendant l’opération");
                    click("ReportNav");
                    release.Set();
                    var timer = Stopwatch.StartNew();
                    while (service.IsRunning && timer.Elapsed < TimeSpan.FromSeconds(8))
                        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                    click("CopyReport");
                    Check(starts == 1 && !service.IsRunning && copied != null && copied.Contains("SFC") &&
                        copied.Contains("lancée") && copied.Contains("code 0") && copied.Contains("à confirmer"),
                        "Le rapport reçoit la fin de la commande après un changement de page");
                    Check(buttons.All(b => b.IsEnabled), "Une autre vérification peut ensuite être lancée");
                    click("IntegrityNav");
                    var logButton = Descendants(view).OfType<Button>().Single(b => ((string)b.Content).Contains("journal"));
                    Check(reads == 0, "Le journal est consulté à la demande");
                    logButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    timer.Restart();
                    while (!logButton.IsEnabled && timer.Elapsed < TimeSpan.FromSeconds(8))
                        window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                    Check(Descendants(view).OfType<TextBox>().Any(b => b.IsReadOnly && b.Text.Contains("ÉVÉNEMENT-PRIVÉ")),
                        "Le journal s’ouvre en lecture seule");
                    click("ReportNav");
                    click("CopyReport");
                    Check(copied != null && !copied.Contains("ÉVÉNEMENT-PRIVÉ"), "Le journal natif reste local");
                }
                finally { release.Set(); window.Close(); }
            }
        }

        static void WaitIntegrity(Task operation)
        {
            var timer = Stopwatch.StartNew();
            while (!operation.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                Thread.Sleep(1);
            }
            Check(operation.IsCompleted, "Suivi asynchrone du contrôle d’intégrité terminé");
            operation.GetAwaiter().GetResult();
        }
    }
}
