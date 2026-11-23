using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        CancellationTokenSource performanceRun;
        PerformanceDiagnosis lastPerformanceResult;

        NavigationPage CreatePerformancePage()
        {
            AddLivePerformance(Get<StackPanel>("PerformanceLive"));
            var header = Get<Grid>("PerformanceHeader");
            header.SizeChanged += delegate {
                bool narrow = header.ActualWidth < 535;
                var commands = Get<Grid>("PerformanceCommands");
                Grid.SetColumnSpan(Get<DockPanel>("PerformanceHeading"), narrow ? 2 : 1);
                Grid.SetRow(commands, narrow ? 1 : 0);
                Grid.SetColumn(commands, narrow ? 0 : 1);
                commands.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(14, 0, 0, 0);
            };
            Get<Button>("PerformanceStart").Click += async delegate { await RunPerformanceDiagnosis(); };
            Get<Button>("PerformanceCancel").Click += delegate { CancelPerformanceDiagnosis(); };
            Get<Button>("PerformanceCopy").Click += delegate { CopyPerformanceDiagnosis(); };
            Get<Button>("PerformanceReport").Click += async delegate { await OpenPageAsync(reportPage); };

            return new NavigationPage("PerformanceNav", Get<StackPanel>("PerformanceView"), "Performances", "\uE9D9",
                "Suivez les ressources du PC pour repérer ce qui se passe pendant un ralentissement.") {
                Title = "Performances du PC", Category = "Système", OpenLabel = "Ouvrir le bilan de lenteur",
                OnOpen = delegate { StartLivePerformance(); return Task.CompletedTask; },
                OnLeave = delegate { StopLivePerformance(); CancelPerformanceDiagnosis(); }
            };
        }

        void CancelPerformanceDiagnosis()
        {
            if (performanceRun != null) performanceRun.Cancel();
        }

        async Task RunPerformanceDiagnosis()
        {
            if (performanceRun != null) return;
            var start = Get<Button>("PerformanceStart");
            var cancel = Get<Button>("PerformanceCancel");
            var feedback = Get<TextBlock>("PerformanceFeedback");
            var intro = Get<TextBlock>("PerformanceIntro");
            var bar = Get<ProgressBar>("PerformanceProgress");
            using (var stop = new CancellationTokenSource())
            {
                performanceRun = stop;
                bool moveFocus = start.IsKeyboardFocusWithin;
                cancel.Visibility = Visibility.Visible;
                if (moveFocus) cancel.Focus();
                start.IsEnabled = false;
                start.Visibility = Visibility.Collapsed;
                Get<TextBlock>("PerformanceCopyFeedback").Text = "";
                intro.Text = lastPerformanceResult == null ? "Continuez l’activité qui ralentit le PC."
                    : "Le dernier bilan reste consultable pendant ce relevé.";
                feedback.Text = "Préparation du relevé…";
                bar.Value = 0;
                bar.Maximum = PerformanceDiagnosis.DurationSeconds;
                bar.IsIndeterminate = true;
                bar.Visibility = Visibility.Visible;
                var progress = new Progress<int>(elapsed => {
                    if (performanceRun != stop || stop.IsCancellationRequested) return;
                    bar.IsIndeterminate = false;
                    bar.Value = elapsed;
                    int remaining = PerformanceDiagnosis.DurationSeconds - elapsed;
                    feedback.Text = remaining == 0 ? "Lecture des résultats…" : remaining == 1
                        ? "Mesure en cours · encore 1 seconde" : "Mesure en cours · encore " + remaining + " secondes";
                });
                string entry;
                bool completed = false;
                try
                {
                    var measured = await Task.Run(() => PerformanceDiagnosis.Measure(stop.Token, progress), stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    ShowPerformanceDiagnosis(measured);
                    lastPerformanceResult = measured;
                    feedback.Text = "";
                    entry = measured.Format();
                    completed = true;
                }
                catch (OperationCanceledException) { entry = feedback.Text = "Mesure annulée."; }
                catch (Exception) { entry = feedback.Text = "La mesure a échoué. Vous pouvez la relancer."; }
                finally
                {
                    performanceRun = null;
                    bool restoreFocus = cancel.IsKeyboardFocusWithin && Get<StackPanel>("PerformanceView").IsVisible;
                    start.Content = "Relancer la mesure";
                    intro.Text = lastPerformanceResult == null ? "Quand le PC ralentit, lancez un relevé de trois secondes."
                        : "Ce bilan figure dans le rapport d’intervention de la session.";
                    start.IsEnabled = true;
                    start.Visibility = Visibility.Visible;
                    bar.Visibility = Visibility.Collapsed;
                    bar.IsIndeterminate = false;
                    if (restoreFocus)
                    {
                        if (completed) Get<TextBlock>("PerformanceVerdict").Focus();
                        else start.Focus();
                    }
                    cancel.Visibility = Visibility.Collapsed;
                }
                RecordIntervention("Bilan de lenteur", entry);
            }
        }

        void ShowPerformanceDiagnosis(PerformanceDiagnosis result)
        {
            bool warning = result.State == FindingState.Warning, normal = result.State == FindingState.Normal;
            var band = Get<Border>("PerformanceVerdictBand");
            band.Background = Ink(warning ? "WarningSoftBrush" : normal ? "SuccessSoftBrush" : "CanvasBrush");
            var tone = Ink(warning ? "WarningBrush" : normal ? "SuccessBrush" : "MutedBrush");
            var icon = Get<TextBlock>("PerformanceVerdictIcon");
            icon.Text = warning ? "\uE7BA" : normal ? "\uE73E" : "\uE946";
            icon.Foreground = tone;
            var verdict = Get<TextBlock>("PerformanceVerdict");
            verdict.Text = result.Title;
            verdict.Foreground = tone;
            AutomationProperties.SetName(verdict, result.Title + " · " + result.Stamp);
            Get<TextBlock>("PerformanceStamp").Text = result.Stamp;
            Get<ItemsControl>("PerformanceFindings").ItemsSource = result.Findings.OrderByDescending(f => f.State).ToArray();
            Get<TextBlock>("PerformanceNextStep").Text = result.NextStep;
            Get<TextBlock>("PerformanceCopyFeedback").Text = "";
            Get<Button>("PerformanceCopy").IsEnabled = true;
            Get<StackPanel>("PerformanceResult").Visibility = Visibility.Visible;
        }

        void CopyPerformanceDiagnosis()
        {
            if (lastPerformanceResult == null) return;
            var feedback = Get<TextBlock>("PerformanceCopyFeedback");
            try { copyText(lastPerformanceResult.Format()); feedback.Text = "Bilan copié dans le presse-papiers."; }
            catch (ExternalException) { feedback.Text = "Presse-papiers indisponible. Réessayez dans un instant."; }
        }
    }
}
