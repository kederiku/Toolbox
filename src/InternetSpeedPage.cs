using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        readonly InternetSpeedTest speedTest;
        readonly SpeedHistory speedHistory;
        CancellationTokenSource speedCancellation;

        void SizeSpeedLayout(double width)
        {
            bool compact = width < 550;
            Get<UniformGrid>("SpeedMetrics").Columns = compact ? 1 : 2;
            var actions = Get<StackPanel>("SpeedActions");
            Grid.SetColumn(actions, compact ? 0 : 1);
            Grid.SetRow(actions, compact ? 1 : 0);
            Grid.SetColumnSpan(Get<DockPanel>("SpeedHeading"), compact ? 2 : 1);
            actions.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        }

        void SpeedHistoryMessage(string message)
        {
            var panel = Get<StackPanel>("SpeedHistory");
            panel.Children.Clear();
            var line = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
            var icon = Glyph("\uE81C", 18, "MutedBrush");
            icon.Margin = new Thickness(0, 0, 10, 0);
            line.Children.Add(icon);
            line.Children.Add(Text(message, 14, false, "MutedBrush"));
            panel.Children.Add(line);
        }

        void LoadSpeedHistory()
        {
            try { ShowSpeedHistory(speedHistory.Load()); }
            catch (Exception ex)
            {
                if (!HistoryUnavailable(ex)) throw;
                SpeedHistoryMessage("Impossible de lire l’historique sur ce PC.");
            }
        }

        static bool HistoryUnavailable(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException ||
                ex is System.Security.SecurityException || ex is InvalidDataException;
        }

        void ShowSpeedHistory(List<SpeedResult> items)
        {
            var panel = Get<StackPanel>("SpeedHistory");
            if (items.Count == 0)
            {
                SpeedHistoryMessage("Aucun test enregistré pour le moment.");
                return;
            }
            panel.Children.Clear();
            foreach (var item in items)
            {
                string date = new DateTime(item.CompletedUtcTicks, DateTimeKind.Utc).ToLocalTime()
                    .ToString("dd/MM/yyyy à HH:mm:ss", SpeedResult.French);
                var identity = new StackPanel();
                identity.Children.Add(Text(date, 14, true, "TextBrush"));
                identity.Children.Add(Text(LinkLabel(item), 13, false, "MutedBrush"));
                var rates = new UniformGrid { Columns = 2 };
                rates.Children.Add(HistoryRate("Réception", item.Download));
                rates.Children.Add(HistoryRate("Envoi", item.Upload));
                var header = new DockPanel();
                header.Children.Add(identity);
                header.Children.Add(rates);
                header.SizeChanged += delegate(object sender, SizeChangedEventArgs e) {
                    if (!e.WidthChanged) return;
                    bool stacked = e.NewSize.Width < 465;
                    DockPanel.SetDock(identity, stacked ? Dock.Top : Dock.Left);
                    identity.Width = stacked ? double.NaN : e.NewSize.Width * 0.45;
                    rates.Margin = stacked ? new Thickness(0, 7, 0, 0) : new Thickness(0);
                };
                var details = Text("Latence HTTPS : " + SpeedResult.Number(item.Latency) + " ms   ·   Gigue : " +
                    SpeedResult.Number(item.Jitter) + " ms" + Environment.NewLine +
                    "Réception : " + SpeedResult.ByteRate(item.Download) + "   ·   Envoi : " + SpeedResult.ByteRate(item.Upload) +
                    Environment.NewLine + item.TransferDetail() + Environment.NewLine + item.NetworkDetail(), 13, false, "MutedBrush");
                var entry = new Expander { Header = header, Content = details, Style = (Style)panel.FindResource("SpeedHistoryEntry") };
                AutomationProperties.SetName(entry, "Détails du test du " + date + " · " + LinkLabel(item) +
                    " · Réception " + SpeedResult.BitRate(item.Download) + " · Envoi " + SpeedResult.BitRate(item.Upload));
                if (panel.Children.Count != 0)
                    panel.Children.Add(new Border { Height = 1, Background = Ink("LineBrush"), Margin = new Thickness(14, 0, 14, 0) });
                panel.Children.Add(entry);
            }
        }

        StackPanel HistoryRate(string label, double value)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            stack.Children.Add(Text(label, 12, false, "MutedBrush"));
            stack.Children.Add(Text(SpeedResult.BitRate(value), 15, true, "TextBrush"));
            return stack;
        }

        static string LinkLabel(SpeedResult result)
        {
            var network = result.Connection;
            if (network == null || network.Link == null) return "Liaison indéterminée";
            if (network.Link == "Wi-Fi")
                return "Wi-Fi · " + (string.IsNullOrEmpty(network.WifiName) ? "nom indisponible" : network.WifiName);
            return network.Link;
        }

        void CancelSpeedTest()
        {
            if (speedCancellation != null) speedCancellation.Cancel();
        }

        async Task RunSpeedTestAsync()
        {
            if (speedCancellation != null) return;
            var start = Get<Button>("SpeedStart");
            var cancel = Get<Button>("SpeedCancel");
            var status = Get<TextBlock>("SpeedStatus");
            bool startHadFocus = start.IsKeyboardFocused;
            start.IsEnabled = false;
            start.Visibility = Visibility.Collapsed;
            cancel.Visibility = Get<DockPanel>("SpeedStage").Visibility = Visibility.Visible;
            if (startHadFocus) cancel.Focus();
            bool restoreActionFocus = false;
            foreach (string field in new[] { "SpeedLatency", "SpeedDownload", "SpeedUpload" })
                Get<TextBlock>(field).Text = "—";
            Get<TextBlock>("SpeedJitter").Text = "Gigue : —";
            Get<TextBlock>("SpeedDownloadBytes").Text = Get<TextBlock>("SpeedUploadBytes").Text = "En attente";
            UpdateSpeedBars(0, 0);
            var network = Get<TextBlock>("SpeedNetworkDetails");
            var transfer = Get<TextBlock>("SpeedTransferDetails");
            var details = Get<Expander>("SpeedDetails");
            details.Visibility = transfer.Visibility = Visibility.Collapsed;
            network.Text = transfer.Text = "";
            var result = new SpeedResult();

            using (var stop = new CancellationTokenSource())
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                speedCancellation = stop;
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                string stage = "Identification";
                try
                {
                    SetSpeedStage(1, "Identification de la connexion…");
                    using (var identification = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                    {
                        identification.CancelAfter(TimeSpan.FromSeconds(5));
                        try { result.Connection = await Task.Run(() => speedTest.ReadConnection(identification.Token), identification.Token); }
                        catch (OperationCanceledException) { }
                        catch (WebException) { }
                        catch (IOException) { }
                        catch (SerializationException) { }
                        catch (System.Security.SecurityException) { }
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    network.Text = LinkLabel(result) + Environment.NewLine + result.NetworkDetail();
                    details.Visibility = Visibility.Visible;
                    var clock = Stopwatch.StartNew();
                    stage = "Latence HTTPS";
                    SetSpeedStage(2, "Mesure de la latence HTTPS…");
                    result.Latency = await MeasureSpeedAsync(SpeedPhase.Latency, result, deadline.Token);
                    Get<TextBlock>("SpeedLatency").Text = SpeedResult.Number(result.Latency) + " ms";
                    Get<TextBlock>("SpeedJitter").Text = "Gigue : " + SpeedResult.Number(result.Jitter) + " ms";
                    stage = "Réception";
                    SetSpeedStage(3, "Mesure du débit en réception…");
                    Get<TextBlock>("SpeedDownloadBytes").Text = "Mesure en cours…";
                    result.Download = await MeasureSpeedAsync(SpeedPhase.Download, result, deadline.Token);
                    ShowRate("SpeedDownload", result.Download);
                    stage = "Envoi";
                    SetSpeedStage(4, "Mesure du débit en envoi…");
                    Get<TextBlock>("SpeedUploadBytes").Text = "Mesure en cours…";
                    result.Upload = await MeasureSpeedAsync(SpeedPhase.Upload, result, deadline.Token);
                    clock.Stop();
                    ShowRate("SpeedUpload", result.Upload);
                    UpdateSpeedBars(result.Download, result.Upload);
                    result.Seconds = clock.Elapsed.TotalSeconds;
                    result.CompletedUtcTicks = DateTime.UtcNow.Ticks;
                    transfer.Text = result.TransferDetail();
                    transfer.Visibility = Visibility.Visible;
                    status.Text = "Test terminé.";
                    Get<DockPanel>("SpeedStage").Visibility = Visibility.Collapsed;
                    restoreActionFocus = cancel.IsKeyboardFocused;
                    cancel.Visibility = Visibility.Collapsed;
                    try { ShowSpeedHistory(await Task.Run(() => speedHistory.Add(result))); }
                    catch (Exception ex)
                    {
                        if (!HistoryUnavailable(ex)) throw;
                        status.Text = "Test terminé. Impossible d’enregistrer ce résultat dans l’historique.";
                    }
                }
                catch (Exception ex)
                {
                    foreach (string field in new[] { "SpeedDownload", "SpeedUpload" })
                        if (Get<TextBlock>(field).Text == "—") Get<TextBlock>(field + "Bytes").Text = "Non mesuré";
                    var networkError = ex as WebException;
                    if (stop.IsCancellationRequested) status.Text = "Test annulé.";
                    else if (deadline.IsCancellationRequested || (networkError != null && networkError.Status == WebExceptionStatus.Timeout))
                        status.Text = stage + " · Le délai de mesure est dépassé. Vous pouvez relancer le test.";
                    else status.Text = stage + " · Mesure impossible. Vérifiez votre connexion et réessayez.";
                }
                finally
                {
                    RecordIntervention("Test de débit Internet", status.Text + Environment.NewLine
                        + "Latence HTTPS : " + Get<TextBlock>("SpeedLatency").Text
                        + " · Réception : " + Get<TextBlock>("SpeedDownload").Text
                        + " · Envoi : " + Get<TextBlock>("SpeedUpload").Text);
                    speedCancellation = null;
                    restoreActionFocus = restoreActionFocus || cancel.IsKeyboardFocused;
                    cancel.Visibility = Get<DockPanel>("SpeedStage").Visibility = Visibility.Collapsed;
                    start.Content = "Relancer le test";
                    start.IsEnabled = true;
                    start.Visibility = Visibility.Visible;
                    if (restoreActionFocus && start.IsVisible && Window.IsActive) start.Focus();
                }
            }
        }

        void SetSpeedStage(int step, string description)
        {
            Get<TextBlock>("SpeedStatus").Text = description;
            Get<TextBlock>("SpeedStageLabel").Text = "Étape " + step + " sur 4";
            Get<ProgressBar>("SpeedProgress").Value = step - 1;
        }

        void UpdateSpeedBars(double download, double upload)
        {
            var down = Get<ProgressBar>("SpeedDownloadBar");
            var up = Get<ProgressBar>("SpeedUploadBar");
            double maximum = Math.Max(download, upload);
            down.Maximum = up.Maximum = maximum > 0 ? maximum : 1;
            down.Value = download;
            up.Value = upload;
            var scale = Get<TextBlock>("SpeedScale");
            scale.Visibility = maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
            scale.Text = maximum > 0 ? "Échelle commune de 0 à " + SpeedResult.BitRate(maximum) : "";
        }

        async Task<double> MeasureSpeedAsync(SpeedPhase phase, SpeedResult result, CancellationToken token)
        {
            double value = await Task.Run(() => speedTest.Measure(phase, token, result.Latency, result), token);
            token.ThrowIfCancellationRequested();
            return value;
        }

        void ShowRate(string field, double megabits)
        {
            Get<TextBlock>(field).Text = SpeedResult.BitRate(megabits);
            Get<TextBlock>(field + "Bytes").Text = SpeedResult.ByteRate(megabits);
        }
    }
}
