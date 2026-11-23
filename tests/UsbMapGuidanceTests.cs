using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        internal static void CheckUsbMapGuidance()
        {
            var webcam = new Device(@"USB\VID_1234&PID_1000\CAMERA", "Webcam", 0);
            var other = new Device(@"USB\VID_1234&PID_1000\OTHER", "Autre webcam", 0);
            const string noNameId = @"USB\VID_1234&PID_1000&MI_01\SECRET";
            var noName = new Device(noNameId, noNameId, Device.Unreadable, null, null, webcam.Id);
            var audio = new Device(@"USB\VID_1234&PID_1000&MI_02\AUDIO", "Microphone", 0, null, null, webcam.Id);
            var bridge = new Device(@"SWD\GROUP\VIDEO", "Groupe vidéo", 0, null, null, webcam.Id);
            var video = new Device(@"USB\VID_1234&PID_1000&MI_00\VIDEO", "Caméra vidéo", 28, null, null, bridge.Id);
            var remote = new Device(@"USB\VID_1234&PID_1000&MI_00\REMOTE", "Fonction distante", 10, null, null, other.Id);
            var snapshot = new[] { webcam, other, noName, audio, video, remote, bridge };
            var node = UsbTopology.Build(snapshot).Unattached.Single(n => n.Device == webcam);
            Check(node.Functions.Any(s => s.Contains("indéterminé")) && !node.Functions.Any(s => s.Contains("SECRET")),
                "Une fonction sans nom reste consultable avec son état illisible et un libellé lisible");
            Check(node.Functions.Count == 3 && node.Functions.Any(s => s.Contains("Caméra vidéo")) &&
                !node.Functions.Any(s => s.Contains("distante")), "Les fonctions suivent la chaîne des parents vers leur propre appareil");
            var duplicateParent = new Device(webcam.Id.ToLowerInvariant(), "Identifiant ambigu", 0);
            var ambiguous = UsbTopology.Build(snapshot.Concat(new[] { duplicateParent }));
            Check(ambiguous.Unattached.Where(n => n.Device != other).All(n => n.Functions.Count == 0),
                "Un parent ambigu ne reçoit aucune fonction");
            Check(node.NeedsAttention && node.RowIssue.Contains("en erreur") && node.RowIssue.Contains("indéterminé"),
                "La ligne de l’appareil distingue un échec et un état illisible parmi ses fonctions");
            Check(node.StateText.StartsWith("1 fonction en erreur") && node.StateText.Contains("parent"),
                "La fiche annonce d’abord les fonctions en difficulté sur un périphérique parent détecté");
            var conflicting = new Device(@"USB\VID_5678&PID_ABCD&MI_00\OTHER", "Autre modèle", 28, null, null, webcam.Id);
            Check(UsbTopology.Build(new[] { webcam, conflicting }).Unattached.Single().Functions.Count == 0 &&
                UsbTopology.FunctionOwner(conflicting, new[] { webcam, conflicting }) == null,
                "Un VID/PID contradictoire empêche de proposer une fonction sur le mauvais modèle");
            var uncertain = new Device(webcam.Id, "Webcam", Device.Unreadable);
            var unreadable = UsbTopology.Build(new[] { uncertain, video, bridge }).Unattached.Single();
            Check(unreadable.StateText.Contains("indéterminé") && unreadable.StateText.Contains("en erreur") &&
                !unreadable.StateText.Contains("-1"), "L’état illisible de l’appareil coexiste avec la panne d’une fonction");

            var fixture = new List<Device> { webcam, video, bridge, noName };
            var window = new Shell(() => fixture.ToList(), null, () => new List<Device>()).Window;
            try
            {
                var refresh = (Button)window.FindName("MapRefresh");
                RunUsbGuidanceAction((Button)window.FindName("MapNav"), refresh);
                var guidance = (StackPanel)window.FindName("MapGuidance");
                var state = (TextBlock)window.FindName("MapState");
                Check(state.Text.IndexOf("fonction", StringComparison.OrdinalIgnoreCase) >= 0,
                    "L’état du parent sain distingue les fonctions à vérifier");
                var labels = string.Join("\n", Descendants(guidance).OfType<TextBlock>().Select(t => t.Text));
                Check(labels.Contains("28") && labels.Contains("Caméra vidéo") && labels.Contains("Windows Update") &&
                    labels.Contains("indéterminé") && !labels.Contains("SECRET") && !labels.Contains("-1"),
                    "La fiche sépare le pilote manquant et la fonction dont Windows ne peut pas lire l’état");
                Check(FindAction(guidance, "Retrouver cet appareil") == null, "Un pilote manquant n’ouvre pas le test de débranchement");
                var scan = (Button)window.FindName("ScanButton");
                ((Button)window.FindName("DiagNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.Add(other);
                RunUsbGuidanceAction(scan, scan);
                var results = (StackPanel)window.FindName("Results");
                RunUsbGuidanceAction(FindAction(results, "Voir sur la carte USB"), refresh);
                var tree = (TreeView)window.FindName("MapTree");
                var selected = (TreeViewItem)tree.SelectedItem;
                Check(((UsbNode)selected.Tag).Device == webcam && !((TextBlock)window.FindName("MapStatus")).Text.Contains("plus sur la carte"),
                    "La fiche d’une fonction ouvre sa webcam parmi deux appareils identiques");
                var failed = UsbDevice(@"USB\VID_0000&PID_0001\RESET", 43, @"USB\RESET_FAILURE", null, null);
                fixture.Clear();
                fixture.Add(failed);
                RunUsbGuidanceAction(refresh, refresh);
                var locate = FindAction(guidance, "Retrouver cet appareil");
                labels = string.Join("\n", Descendants(guidance).OfType<TextBlock>().Select(t => t.Text));
                Check(locate != null && labels.Contains("câble") && state.Text.Contains("43") && !labels.Contains("Caméra vidéo"),
                    "La fiche du code 43 remplace les conseils précédents et propose l’assistant ciblé");
                RunUsbGuidanceAction(locate, scan);
                var guide = (Border)window.FindName("UsbGuide");
                Check(((StackPanel)window.FindName("DiagnosticPage")).Visibility == Visibility.Visible && guide.Visibility == Visibility.Visible,
                    "Le bouton de la fiche ouvre l’assistant de débranchement sur le diagnostic");
                RunUsbGuidanceAction((Button)window.FindName("MapNav"), refresh);
                fixture.Clear();
                fixture.Add(other);
                RunUsbGuidanceAction(FindAction(guidance, "Retrouver cet appareil"), scan);
                Check(guide.Visibility == Visibility.Collapsed, "Le test relit le matériel et refuse un périphérique disparu");
                RunUsbGuidanceAction((Button)window.FindName("MapNav"), refresh);
                Check(guidance.Children.Count == 0 && !state.Text.Contains("43"), "Une fiche saine efface les anciens conseils");
            }
            finally { window.Close(); }
        }

        static void RunUsbGuidanceAction(Button action, Button busyIndicator)
        {
            Check(action != null, "Commande USB disponible");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!busyIndicator.IsEnabled && DateTime.UtcNow < deadline)
                action.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
            Check(busyIndicator.IsEnabled, "Fin de la lecture USB simulée");
        }
    }
}
