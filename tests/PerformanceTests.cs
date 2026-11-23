using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckPerformanceFeature()
        {
            CheckPerformanceReadings();
            CheckPerformanceGraph();
            string copied = null;
            var window = new Shell(() => new List<Device>(), value => copied = value).Window;
            try
            {
                ShowNavigationWindow(window);
                var open = window.FindName("PerformanceNav") as Button;
                Check(open != null, "Le bilan de lenteur est accessible dans la navigation");
                Action<Button> click = button => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                click(open);
                var memoryValue = window.FindName("LiveMemoryValue") as TextBlock;
                Check(memoryValue != null && window.FindName("LiveCpuValue") != null && window.FindName("LiveDiskValue") != null,
                    "La page présente les trois ressources en direct dès son ouverture");
                Check(WaitForPerformance(window, () => memoryValue.Text.Contains("%")),
                    "La mémoire physique est échantillonnée sans lancer le bilan");
                var start = (Button)window.FindName("PerformanceStart");
                var cancel = (Button)window.FindName("PerformanceCancel");
                var copy = (Button)window.FindName("PerformanceCopy");
                var feedback = (TextBlock)window.FindName("PerformanceFeedback");
                Check(start != null && start.IsEnabled && !copy.IsEnabled && cancel.Visibility == Visibility.Collapsed,
                    "Le bilan attend un lancement explicite");
                click(start);
                Check(!start.IsEnabled && cancel.Visibility == Visibility.Visible, "Mesure unique et annulable");
                var progress = window.FindName("PerformanceProgress") as ProgressBar;
                Check(progress != null && progress.Visibility == Visibility.Visible, "La progression est visible pendant le relevé");
                click(cancel);
                Check(WaitForPerformance(window, () => start.IsEnabled), "L’interface rend la main après l’annulation");
                Check(feedback.Text.Contains("annulée"), "L’annulation est visible");
                bool sawIntermediate = false;
                progress.ValueChanged += delegate { if (progress.Value > 0 && progress.Value < progress.Maximum) sawIntermediate = true; };
                click(start);
                Check(WaitForPerformance(window, () => start.IsEnabled), "L’interface rend la main après la mesure");
                Check(sawIntermediate, "La progression avance avant la fin de la collecte");
                click(copy);
                Check(copied != null && copied.Contains("Processeur") && copied.Contains("Mémoire physique")
                    && copied.Contains("Volume de Windows") && copied.Contains("Gio"),
                    "Les trois lectures locales sont copiées dans le bilan");
                string result = copied;
                var findings = window.FindName("PerformanceFindings") as ItemsControl;
                Check(findings != null && findings.Items.Count == 3, "Les trois ressources du bilan sont consultables séparément");
                UpdateNavigationLayout(window);
                var presenter = (ContentPresenter)findings.ItemContainerGenerator.ContainerFromIndex(0);
                var row = (Expander)findings.ItemTemplate.FindName("FindingRow", presenter);
                var toggle = (ToggleButton)row.Template.FindName("HeaderSite", row);
                var details = (ContentPresenter)row.Template.FindName("DetailsSite", row);
                Check(row.IsExpanded == (((PerformanceFinding)row.DataContext).State == FindingState.Warning),
                    "Les conseils des ressources en alerte sont ouverts dès la présentation du bilan");
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                Check(!row.IsExpanded && !details.IsVisible, "Replier la ressource masque son détail");
                toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Check(row.IsExpanded && details.IsVisible, "Déplier la ressource révèle son seuil");
                click(start);
                Check(copy.IsEnabled && findings.IsVisible,
                    "Le dernier bilan reste consultable et copiable pendant la nouvelle mesure");
                click(cancel);
                Check(WaitForPerformance(window, () => start.IsEnabled) && progress.Visibility == Visibility.Collapsed,
                    "L’annulation rend les commandes disponibles et masque la progression");
                click(copy);
                Check(copied == result, "Le bilan précédent reste disponible après une annulation");
                click((Button)window.FindName("PerformanceReport"));
                Check(((StackPanel)window.FindName("ReportView")).Visibility == Visibility.Visible,
                    "Le bilan permet de consulter le rapport de la session");
                click((Button)window.FindName("CopyReport"));
                Check(copied.Contains("Rapport d’intervention") && copied.Contains(result),
                    "Le rapport conserve le bilan copié");
                click(open);
                click((Button)window.FindName("HomeNav"));
                click(open);
                Check(((TextBlock)window.FindName("LiveCpuValue")).Text == "—", "Un retour sur la page démarre une nouvelle série");
                Check(WaitForPerformance(window, () => memoryValue.Text.Contains("%")),
                    "Le suivi reprend après deux changements rapides de page");
            }
            finally { window.Close(); }
        }

        static bool WaitForPerformance(Window window, Func<bool> finished)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!finished() && DateTime.UtcNow < deadline)
            {
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
                Thread.Sleep(10);
            }
            return finished();
        }

        static void CheckPerformanceGraph()
        {
            var graph = new PerformanceGraph(Brushes.Blue, Brushes.LightGray);
            for (int second = 0; second <= 70; second++)
            {
                // An old peak, an unavailable counter, then a pause in sampling.
                if (second >= 60 && second < 69) continue;
                double? value = second >= 39 && second <= 50 ? (double?)null : second < 10 ? 99 : 25;
                graph.Append(second, value);
            }
            graph.Measure(new Size(305, 105));
            graph.Arrange(new Rect(0, 0, 305, 105));
            var area = VisualTreeHelper.GetDrawing(graph).Children.OfType<GeometryDrawing>()
                .Single(d => d.Geometry is StreamGeometry).Geometry;
            Check(area.Bounds.Top > 50 && area.FillContains(new Point(52, 92)),
                "Un pic de plus d’une minute sort de la courbe et les mesures récentes restent tracées");
            Check(!area.FillContains(new Point(177, 92)) && !area.FillContains(new Point(277, 92)),
                "Le tracé laisse des trous pour un compteur indisponible et une interruption de la collecte");
        }

        static void CheckPerformanceReadings()
        {
            const long gib = 1073741824;
            var start = new DateTime(2026, 11, 20, 11, 22, 33);
            var duration = TimeSpan.FromMilliseconds(3250);
            var normal = PerformanceCapacity.FromBytes(8 * gib, 16 * gib);
            var low = PerformanceCapacity.FromBytes(gib, 10 * gib);
            var full = PerformanceCapacity.FromBytes(0, 10 * gib);
            var baseline = new PerformanceDiagnosis(start, duration, 84, normal, normal);
            Check(baseline.Advice.Contains("reproduisez") && baseline.Format().Contains("11:22:33") &&
                baseline.Details.Contains("3,25 s") && baseline.Details.Contains("8 Gio") && baseline.Details.Contains("16 Gio"),
                "Le bilan sain situe les mesures dans le temps et invite à reproduire la lenteur");
            var loaded = new PerformanceDiagnosis(start, duration, 85, low, full);
            Check(loaded.Advice.Contains("Processeur") && loaded.Advice.Contains("Mémoire") && loaded.Advice.Contains("Stockage"),
                "Chaque seuil atteint fournit sa propre piste, y compris avec zéro octet libre");
            Check(loaded.Details.Contains("85 %") && loaded.Details.Contains("10 %") && loaded.Details.Contains("0 Gio"),
                "Les valeurs et les seuils sont consultables");
            var largeMachine = new PerformanceDiagnosis(start, duration, 15,
                PerformanceCapacity.FromBytes(16 * gib, 256 * gib), PerformanceCapacity.FromBytes(100 * gib, 2048 * gib));
            Check(!largeMachine.Advice.Contains("Mémoire disponible faible") && !largeMachine.Advice.Contains("Stockage presque plein")
                && largeMachine.Advice.Contains("reproduisez"),
                "Le pourcentage seul ne classe pas une réserve physique importante comme faible");
            var boundary = new PerformanceDiagnosis(start, duration, 15,
                PerformanceCapacity.FromBytes(2 * gib, 20 * gib), PerformanceCapacity.FromBytes(20 * gib, 200 * gib));
            Check(boundary.Advice.Contains("Mémoire") && boundary.Advice.Contains("Stockage") &&
                boundary.Details.Contains("2 Gio") && boundary.Details.Contains("20 Gio"),
                "Les deux critères inclusifs sont documentés et appliqués à la limite");
            var partial = new PerformanceDiagnosis(start, duration, null, low, null);
            Check(partial.State == FindingState.Warning && partial.Findings.Count(f => f.State == FindingState.Unavailable) == 2 &&
                partial.Findings.Single(f => f.State == FindingState.Warning).Advice.Contains("Mémoire"),
                "Un bilan partiel distingue les valeurs absentes de la ressource qui déclenche une alerte");
            Check(partial.Title.Contains("partiel") && partial.Advice.Contains("Mémoire") && !partial.Advice.Contains("Stockage") &&
                partial.Details.Contains("Processeur moyen · indisponible") && partial.Details.Contains("Volume de Windows · indisponible"),
                "Deux lectures absentes laissent la mémoire exploitable");
            var missing = new PerformanceDiagnosis(start, duration, double.NaN, null, PerformanceCapacity.FromBytes(2, 0));
            Check(missing.Title.Contains("indisponibles") && !missing.Advice.Contains("reproduisez") &&
                !missing.Format().Contains("NaN"), "Un bilan vide n’annonce aucune conclusion de santé");
            Check(PerformanceCapacity.FromBytes(3, 2) == null && PerformanceCapacity.FromBytes(-1, 2) == null,
                "Une valeur hors limites est marquée indisponible");
        }
    }
}
