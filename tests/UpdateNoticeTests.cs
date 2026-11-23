using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckUpdateNotice()
        {
            Window window;
            using (var stream = typeof(App).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)XamlReader.Load(stream);
            var notice = new UpdateNotice(window);
            var banner = (Border)window.FindName("UpdateBanner");
            var primary = (Button)window.FindName("UpdatePrimary");
            var later = (Button)window.FindName("UpdateSecondary");
            var message = (TextBlock)window.FindName("UpdateMessage");
            Check(banner.Visibility == Visibility.Collapsed, "Le démarrage garde le catalogue accessible");
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                string signal = UpdateFiles.ReadyPrefix + Guid.NewGuid().ToString("N");
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal))
                {
                    Environment.SetEnvironmentVariable(UpdateFiles.ReadyVariable, signal);
                    App.AcknowledgeUpdateOnFirstRender(window);
                    Check(!ready.WaitOne(0) && Environment.GetEnvironmentVariable(UpdateFiles.ReadyVariable) == null,
                        "Le signal privé attend le premier affichage");
                    window.ShowActivated = false;
                    window.ShowInTaskbar = false;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -20000;
                    window.Show();
                    var displayed = Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(4)));
                    AwaitUpdateNotice(displayed);
                    Check(displayed.Result, "La fenêtre affichée confirme le démarrage à l’assistant");
                }
                AwaitUpdateNotice(notice.CheckAsync(delegate { throw new System.Net.WebException("hors ligne"); }, false));
                Check(banner.Visibility == Visibility.Collapsed, "Une vérification hors ligne reste discrète");
                AwaitUpdateNotice(notice.CheckAsync(delegate { return null; }, false));
                Check(banner.Visibility == Visibility.Collapsed, "Une version à jour ne demande aucune action");
                string json = "{\"tag_name\":\"v1.8.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{" +
                    "\"name\":\"Toolbox.exe\",\"state\":\"uploaded\",\"size\":167772160," +
                    "\"digest\":\"sha256:" + new string('a', 64) + "\"," +
                    "\"browser_download_url\":\"https://github.com/kederiku/Toolbox/releases/download/v1.8.0/Toolbox.exe\"}]}";
                var available = UpdateRelease.Parse(json, new Version(1, 7, 0, 0));
                AwaitUpdateNotice(notice.CheckAsync(delegate { return available; }, false));
                Check(banner.Visibility == Visibility.Visible && (string)primary.Content == "Mettre à jour"
                    && message.Text.Contains("1.8.0") && message.Text.Contains("160"),
                    "Une mise à jour attend le clic et annonce sa version et sa taille");
                later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(banner.Visibility == Visibility.Collapsed, "Plus tard referme la proposition");
                AwaitUpdateNotice(notice.CheckAsync(delegate { return available; }, true));
                Check((string)primary.Content == "Ouvrir la version sur GitHub", "En mode administrateur, le lien officiel est proposé");
                bool busy = true, veto = true;
                int closing = 0;
                window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args) { closing++; args.Cancel = veto; };
                UpdateNotice.WaitForTool(window, () => busy);
                Check(!notice.TryCloseForUpdate() && closing == 0, "Un outil actif termine son travail sans demande de fermeture");
                busy = false;
                Check(!notice.TryCloseForUpdate() && closing == 1, "Un refus de fermeture reporte le redémarrage");
                veto = false;
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(context); }
        }

        static void AwaitUpdateNotice(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { Thread.Sleep(2); }));
            Check(task.IsCompleted, "La vérification de mise à jour rend la main");
            task.GetAwaiter().GetResult();
        }
    }
}
