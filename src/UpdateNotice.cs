using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed class UpdateNotice
    {
        enum Step { Available, Downloading, Ready, Preparing, Manual }

        static readonly ConditionalWeakTable<Window, List<Func<bool>>> activeTools = new ConditionalWeakTable<Window, List<Func<bool>>>();

        readonly Window window;
        readonly Border banner;
        readonly TextBlock message;
        readonly ProgressBar progress;
        readonly Button primary, secondary;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly string target = Assembly.GetExecutingAssembly().Location;
        UpdateRelease release;
        CancellationTokenSource operation;
        UpdateHandoff handoff;
        string stagedFile;
        Step step;
        bool closed, closeWhenIdle;

        internal UpdateNotice(Window window)
        {
            this.window = window;
            banner = (Border)window.FindName("UpdateBanner");
            message = (TextBlock)window.FindName("UpdateMessage");
            progress = (ProgressBar)window.FindName("UpdateProgress");
            primary = (Button)window.FindName("UpdatePrimary");
            secondary = (Button)window.FindName("UpdateSecondary");
            primary.Click += async delegate {
                if (step == Step.Available) await DownloadAsync();
                else if (step == Step.Ready) await RestartAsync();
                else if (step == Step.Manual) OpenRelease();
            };
            secondary.Click += delegate {
                if (operation != null) { operation.Cancel(); secondary.IsEnabled = false; }
                else { DiscardStage(); banner.Visibility = Visibility.Collapsed; }
            };
            window.Closing += Closing;
            window.Closed += Closed;
        }

        internal void Start()
        {
            window.ContentRendered += FirstRender;
        }

        async void FirstRender(object sender, EventArgs e)
        {
            window.ContentRendered -= FirstRender;
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            await CheckAsync(token => UpdateRelease.Check(current, token), WindowsIntegrity.IsElevated());
        }

        internal async Task CheckAsync(Func<CancellationToken, UpdateRelease> lookup, bool elevated)
        {
            var token = lifetime.Token;
            UpdateRelease found;
            try { found = await Task.Run(() => lookup(token), token); }
            catch (Exception) { return; } // Le catalogue reste disponible si GitHub est inaccessible.
            if (closed || found == null) return;
            release = found;
            string announcement = "Toolbox " + release.Version + " est disponible (" +
                (release.Size / 1048576.0).ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) + " Mio).";
            if (elevated)
                Manual(announcement + " Relancez Toolbox en mode standard pour installer la mise à jour, ou téléchargez-la sur GitHub.");
            else Available(announcement);
        }

        void SetStep(Step next, string text, string action, string other)
        {
            step = next;
            message.Text = text;
            primary.Content = action;
            primary.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            secondary.Content = other;
            secondary.IsEnabled = true;
            secondary.Visibility = other == null ? Visibility.Collapsed : Visibility.Visible;
            progress.Visibility = next == Step.Downloading ? Visibility.Visible : Visibility.Collapsed;
            banner.Visibility = Visibility.Visible;
        }

        void Available(string text) { SetStep(Step.Available, text, "Mettre à jour", "Plus tard"); }
        void Manual(string text) { SetStep(Step.Manual, text, "Ouvrir la version sur GitHub", "Fermer"); }
        void Ready(string text) { SetStep(Step.Ready, text, "Redémarrer et installer", "Annuler la mise à jour"); }

        async Task DownloadAsync()
        {
            var stop = operation = new CancellationTokenSource();
            progress.Value = 0;
            SetStep(Step.Downloading, "Téléchargement de Toolbox " + release.Version + "…", null, "Annuler");
            IProgress<int> report = new Progress<int>(percent => {
                if (!closed && operation == stop && step == Step.Downloading) progress.Value = percent;
            });
            try
            {
                stagedFile = UpdateInstaller.CreateStage(target);
                await Task.Run(() => release.Download(stagedFile, stop.Token, report.Report), stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                Ready("La mise à jour vers Toolbox " + release.Version + " est prête. Redémarrez pour l’installer.");
            }
            catch (OperationCanceledException) { DiscardStage(); Available("Téléchargement annulé."); }
            catch (Exception)
            {
                bool noStage = stagedFile == null;
                DiscardStage();
                if (noStage) Manual("Impossible de préparer la mise à jour dans ce dossier. Vous pouvez télécharger la version officielle.");
                else Available("Le téléchargement ou sa vérification a échoué. Vous pouvez réessayer.");
            }
            finally { EndOperation(stop); }
        }

        async Task RestartAsync()
        {
            if (ToolIsBusy()) return;
            var stop = operation = new CancellationTokenSource();
            SetStep(Step.Preparing, "Préparation du redémarrage…", null, null);
            try
            {
                handoff = await Task.Run(() => UpdateInstaller.Prepare(target, stagedFile, release.Sha256));
                if (stop.IsCancellationRequested)
                {
                    CancelHandoff();
                    Ready("Le redémarrage a été reporté. Vous pouvez installer la mise à jour quand vous le souhaitez.");
                }
            }
            catch (Exception)
            {
                DiscardStage();
                Manual("Impossible de préparer le remplacement. Vous pouvez télécharger la version officielle.");
            }
            finally { EndOperation(stop); }
            if (closed || handoff == null) return;
            if (!TryCloseForUpdate())
            {
                CancelHandoff();
                Ready("Le redémarrage a été reporté. Vous pouvez terminer votre travail puis installer la mise à jour.");
            }
        }

        internal static void WaitForTool(Window window, Func<bool> isRunning)
        {
            activeTools.GetOrCreateValue(window).Add(isRunning);
        }

        bool ToolIsBusy()
        {
            List<Func<bool>> checks;
            if (!activeTools.TryGetValue(window, out checks) || !checks.Exists(isRunning => isRunning())) return false;
            Ready("Un outil est en cours d’utilisation. Vous pourrez redémarrer dès qu’il aura terminé.");
            return true;
        }

        internal bool TryCloseForUpdate()
        {
            if (ToolIsBusy()) return false;
            window.Close();
            return closed;
        }

        void Closing(object sender, CancelEventArgs e)
        {
            if (e.Cancel || operation == null) return;
            e.Cancel = true;
            closeWhenIdle = true;
            operation.Cancel();
        }

        void EndOperation(CancellationTokenSource stop)
        {
            operation = null;
            stop.Dispose();
            if (!closeWhenIdle) return;
            closeWhenIdle = false;
            window.Close();
        }

        void Closed(object sender, EventArgs e)
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
            if (handoff == null) { DiscardStage(); return; }
            try { handoff.Commit(); stagedFile = null; }
            catch (Exception)
            {
                MessageBox.Show("Impossible de lancer la mise à jour. Rouvrez Toolbox pour réessayer.",
                    "Mise à jour de Toolbox", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { CancelHandoff(); DiscardStage(); }
        }

        void CancelHandoff()
        {
            if (handoff != null) { handoff.Dispose(); handoff = null; }
        }

        void DiscardStage()
        {
            if (stagedFile != null) { UpdateInstaller.CleanStage(stagedFile); stagedFile = null; }
        }

        void OpenRelease()
        {
            var url = "https://github.com/kederiku/Toolbox/releases/tag/v" + release.Version;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Win32Exception) { message.Text = "Ouvrez cette adresse dans votre navigateur : " + url; }
        }
    }
}
