using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AtelierPC
{
    internal sealed class NetworkRepairPage
    {
        readonly Window window;
        readonly Button check, fix;
        readonly Func<NetworkDiagnosis> diagnose;
        readonly Func<NetworkDiagnosis, IntPtr, string> repair;
        readonly Action<string, string> record;
        readonly TextBlock observations = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        readonly Expander details;
        NetworkDiagnosis last;
        bool running;
        bool repairing;

        public NetworkRepairPage(Window window, Func<NetworkDiagnosis> diagnose, Func<NetworkDiagnosis, IntPtr, string> repair,
            Action<string, string> record = null)
        {
            this.window = window;
            UpdateNotice.WaitForTool(window, () => running || repairing);
            this.diagnose = diagnose;
            this.repair = repair;
            this.record = record;
            check = (Button)window.FindName("NetworkCheck");
            fix = (Button)window.FindName("NetworkFix");
            var resultPanel = (StackPanel)((Border)window.FindName("NetworkResult")).Child;
            details = new Expander {
                Header = "Observations techniques", Content = observations, Margin = new Thickness(0, 8, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            resultPanel.Children.Add(details);
            check.Click += async delegate { await RunAsync(null); };
            fix.Click += async delegate { if (last != null && last.Fix != NetworkFix.None) await RunAsync(last); };
            window.Closing += (sender, args) => {
                if (repairing && !args.Cancel)
                    args.Cancel = MessageBox.Show(window, "Une correction réseau est en cours. Quitter avant de connaître le résultat de la vérification ?",
                        "Quitter Toolbox", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
            };
            ((Button)window.FindName("NetworkSettings")).Click += delegate {
                try { using (Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true })) { } }
                catch (Exception) { Feedback("Ouvrez les paramètres Windows, puis choisissez « Réseau et Internet »."); }
            };
        }

        async Task RunAsync(NetworkDiagnosis requested)
        {
            if (running) return;
            running = true;
            repairing = requested != null;
            check.IsEnabled = false;
            last = null;
            fix.Visibility = Visibility.Collapsed;
            var result = (Border)window.FindName("NetworkResult");
            result.Visibility = Visibility.Collapsed;
            string action = "";
            try
            {
                if (requested != null)
                {
                    Feedback("Demande de correction en cours…");
                    var owner = new WindowInteropHelper(window).Handle;
                    try { action = await Task.Run(() => repair(requested, owner)); }
                    catch (Exception) { action = NetworkTroubleshooter.Unconfirmed; }
                }
                Feedback(requested == null ? "Vérification de la connexion…" : action + " Nouvelle vérification…");
                try { last = await Task.Run(diagnose); }
                catch (Exception) { last = null; }
                if (last == null)
                    last = NetworkDiagnosis.Assess(new NetworkLink[0], WebProbe.Unknown, DnsProbe.Unknown, GatewayProbe.Unknown, false);
                ((TextBlock)window.FindName("NetworkResultTitle")).Text = last.Title;
                string detail = last.Detail;
                if (last.Fix != NetworkFix.None)
                {
                    detail += " Windows peut demander une autorisation administrateur.";
                    fix.Content = last.Fix == NetworkFix.FlushDns ? "Vider le cache DNS" : "Renouveler l’adresse IP";
                    fix.Visibility = Visibility.Visible;
                }
                ((TextBlock)window.FindName("NetworkDetail")).Text = detail;
                string shared = last.SharedSummary;
                if (requested != null)
                {
                    string command = requested.Fix == NetworkFix.FlushDns ? "Vider le cache DNS" : "Renouveler l’adresse IP";
                    shared = "Avant la correction" + Environment.NewLine + requested.SharedSummary + Environment.NewLine +
                        Environment.NewLine + "Commande — " + command + Environment.NewLine + NetworkTroubleshooter.SharedAction(action) +
                        Environment.NewLine + Environment.NewLine + "Après la correction" + Environment.NewLine + last.SharedSummary;
                }
                observations.Text = requested == null ? last.Observations : shared;
                details.Header = requested == null ? "Observations techniques" : "Comparer avant et après";
                result.Visibility = Visibility.Visible;
                Feedback(action);
                if (record != null) record(requested == null ? "Diagnostic réseau" : "Correction réseau", shared);
            }
            finally
            {
                running = false;
                repairing = false;
                check.IsEnabled = true;
                check.Content = "Relancer le diagnostic";
            }
        }

        void Feedback(string text)
        {
            var status = (TextBlock)window.FindName("NetworkFeedback");
            status.Text = text;
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
