using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        sealed class LiveMetric
        {
            internal readonly Border Card;
            readonly Grid layout;
            readonly StackPanel plot;
            readonly TextBlock value, detail;
            readonly PerformanceGraph graph;
            readonly string name, description;

            internal LiveMetric(Shell shell, string id, string name, string description, string icon, string accent, string tint)
            {
                this.name = name;
                this.description = description;
                var heading = new DockPanel();
                heading.Children.Add(new Border {
                    Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(7),
                    Background = shell.Ink(tint), Child = shell.Glyph(icon, 15, accent)
                });
                var label = shell.Text(name, 15, true, "TextBrush");
                label.VerticalAlignment = VerticalAlignment.Center;
                heading.Children.Add(label);
                var numbers = new StackPanel();
                numbers.Children.Add(heading);
                value = shell.Text("—", 32, true, "TextBrush");
                value.Margin = new Thickness(0, 6, 0, 0);
                Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
                detail = shell.Text("", 12, false, "MutedBrush");
                numbers.Children.Add(value);
                numbers.Children.Add(detail);

                plot = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                graph = new PerformanceGraph(shell.Ink(accent), shell.Ink("LineBrush")) { Height = 72 };
                plot.Children.Add(graph);
                var axis = new Grid { Margin = new Thickness(1, 3, 1, 0) };
                axis.Children.Add(shell.Text("−60 s", 11, false, "MutedBrush"));
                var now = shell.Text("Maintenant", 11, false, "MutedBrush");
                now.HorizontalAlignment = HorizontalAlignment.Right;
                axis.Children.Add(now);
                plot.Children.Add(axis);

                layout = new Grid();
                layout.ColumnDefinitions.Add(new ColumnDefinition());
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.Children.Add(numbers);
                layout.Children.Add(plot);
                Card = shell.Card(layout);
                Card.Padding = new Thickness(15, 13, 15, 12);
                Card.Margin = new Thickness(4);
                shell.Window.RegisterName("Live" + id + "Value", value);
                Arrange(false);
                Reset();
            }

            internal void Arrange(bool compact)
            {
                layout.ColumnDefinitions[1].Width = compact ? new GridLength(1.2, GridUnitType.Star) : GridLength.Auto;
                Grid.SetRow(plot, compact ? 0 : 1);
                Grid.SetColumn(plot, compact ? 1 : 0);
                plot.Margin = compact ? new Thickness(12, 0, 0, 0) : new Thickness(0, 14, 0, 0);
            }

            internal void Reset()
            {
                graph.Clear();
                value.Text = "—";
                detail.Text = "Première mesure…";
                AutomationProperties.SetName(value, name + " · " + detail.Text);
            }

            internal void Update(double seconds, double? percent, string caption = null)
            {
                graph.Append(seconds, percent);
                value.Text = percent.HasValue ? percent.Value.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR")) + " %" : "—";
                detail.Text = percent.HasValue ? caption ?? description : "Mesure indisponible";
                AutomationProperties.SetName(value, name + " · " + value.Text + " · " + detail.Text);
            }
        }

        LiveMetric cpuMetric, memoryMetric, diskMetric;
        CancellationTokenSource liveSession;

        void AddLivePerformance(StackPanel view)
        {
            var heading = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            heading.Children.Add(Text("Activité en direct", 20, true, "TextBrush"));
            var scale = Text("1 relevé par seconde  ·  Échelle de 0 à 100 %", 12, false, "MutedBrush");
            scale.VerticalAlignment = VerticalAlignment.Center;
            scale.Margin = new Thickness(12, 3, 0, 0);
            heading.Children.Add(scale);
            view.Children.Add(heading);
            cpuMetric = new LiveMetric(this, "Cpu", "Processeur", "Temps processeur utilisé", "\uE950", "#3656B8", "#ECF0FD");
            memoryMetric = new LiveMetric(this, "Memory", "Mémoire", "Mémoire physique utilisée", "\uE964", "#7851AD", "#F2ECFA");
            diskMetric = new LiveMetric(this, "Disk", "Disque", "Temps actif du volume " + PerformanceSampler.WindowsVolume,
                "\uEDA2", "#16756C", "#E5F4EF");
            var cards = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 0, -4, 14) };
            foreach (var metric in new[] { cpuMetric, memoryMetric, diskMetric }) cards.Children.Add(metric.Card);
            cards.SizeChanged += delegate {
                bool compact = cards.ActualWidth < 620;
                cards.Columns = compact ? 1 : 3;
                cpuMetric.Arrange(compact);
                memoryMetric.Arrange(compact);
                diskMetric.Arrange(compact);
            };
            view.Children.Add(cards);
        }

        async void StartLivePerformance()
        {
            if (liveSession != null) return;
            cpuMetric.Reset();
            memoryMetric.Reset();
            diskMetric.Reset();
            using (var session = new CancellationTokenSource())
            {
                liveSession = session;
                var token = session.Token;
                var clock = Stopwatch.StartNew();
                try
                {
                    using (var sampler = await Task.Run(() => new PerformanceSampler(true), token))
                    {
                        bool first = true;
                        while (!token.IsCancellationRequested)
                        {
                            var sample = await Task.Run(() => sampler.Read(), token);
                            token.ThrowIfCancellationRequested();
                            double seconds = clock.Elapsed.TotalSeconds;
                            memoryMetric.Update(seconds, sample.Memory == null ? (double?)null : sample.Memory.UsedPercent,
                                sample.Memory == null ? null : sample.Memory.DescribeUsage());
                            // Rate counters need two observations before they have a value.
                            if (!first)
                            {
                                cpuMetric.Update(seconds, sample.Cpu);
                                diskMetric.Update(seconds, sample.DiskActive);
                            }
                            first = false;
                            await Task.Delay(1000, token);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    if (!token.IsCancellationRequested)
                    {
                        double seconds = clock.Elapsed.TotalSeconds;
                        cpuMetric.Update(seconds, null);
                        memoryMetric.Update(seconds, null);
                        diskMetric.Update(seconds, null);
                    }
                }
                finally { if (liveSession == session) liveSession = null; }
            }
        }

        void StopLivePerformance()
        {
            if (liveSession == null) return;
            liveSession.Cancel();
            liveSession = null;
        }
    }
}
