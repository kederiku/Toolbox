using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        readonly UsbTracker tracker = new UsbTracker();
        readonly DispatcherTimer mapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        bool mapUpdatePending, cleaningUsb;
        readonly UsbConnectionMonitor connectionMonitor = new UsbConnectionMonitor();
        bool monitorStarted, monitorActive, usbWindowClosed, loggedFirstMap;
        int monitorGeneration, observedUsbEvents;
        Dictionary<string, string> previousMoves = new Dictionary<string, string>();

        void RecordUsbSnapshot(List<Device> current, List<UsbChange> changes, bool hasHistory)
        {
            var ids = new HashSet<string>(current.Where(d => d.Kind == UsbKind.Device || d.Kind == UsbKind.Hub)
                .Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            if (!loggedFirstMap)
            {
                RecordIntervention("Carte USB", "Premier relevé : " + ids.Count + " entrées USB de type appareil ou hub."
                    + (hasHistory ? "" : " L’historique des installations est indisponible."));
                loggedFirstMap = true;
            }
            var displayed = new Dictionary<string, string>();
            foreach (var change in changes)
            {
                string key = change.Fingerprint;
                if (displayed.ContainsKey(key)) continue;
                displayed.Add(key, change.Now.Device.Id);
                if (!previousMoves.ContainsKey(key))
                    RecordIntervention(change.Title, change.Before.Label + " → " + change.Now.Label
                        + (change.Settings.Length == 0 ? "" : Environment.NewLine + change.Settings));
            }
            // An incomplete history cannot disprove a previous match to a device which is still present.
            if (!hasHistory)
                foreach (var move in previousMoves.Where(m => ids.Contains(m.Value)))
                    displayed[move.Key] = move.Value;
            previousMoves = displayed;
        }

        void WatchUsbChanges()
        {
            Get<Button>("MonitorToggle").Click += async delegate {
                SetUsbMonitor(!monitorActive);
                if (monitorActive) await ReadMapAsync();
            };
            mapTimer.Tick += async delegate {
                mapTimer.Stop();
                if (!monitorActive || usbWindowClosed || cleaningUsb) { mapUpdatePending = false; return; }
                if (readingMap) return;
                mapUpdatePending = false;
                await ReadMapAsync(currentPage != usbMapPage);
            };
            Window.SourceInitialized += delegate {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                source.AddHook(OnDeviceChange);
                Window.Closed += delegate { source.RemoveHook(OnDeviceChange); };
            };
            Window.Closed += delegate { usbWindowClosed = true; mapTimer.Stop(); };
        }

        void SetUsbMonitor(bool active)
        {
            monitorStarted = true;
            monitorActive = active;
            monitorGeneration++;
            connectionMonitor.ResetBaseline();
            mapTimer.Stop();
            mapUpdatePending = false;
            Get<Button>("MonitorToggle").Content = active ? "Suspendre le suivi" : "Reprendre le suivi";
            Get<TextBlock>("MonitorStatus").Text = active
                ? "Suivi USB actif sur toutes les pages de Toolbox."
                : "Suivi USB suspendu. Les observations précédentes restent consultables.";
        }

        void ObserveUsbConnections(List<Device> current, DateTime at)
        {
            var observed = connectionMonitor.Observe(current, at);
            if (observed.Count == 0) return;
            var list = Get<ListBox>("MonitorEvents");
            Get<TextBlock>("MonitorEmpty").Visibility = Visibility.Collapsed;
            list.Visibility = Visibility.Visible;
            foreach (var entry in observed)
            {
                list.Items.Insert(0, entry.LocalText);
            }
            observedUsbEvents += observed.Count;
            Get<Expander>("MonitorHistory").Header = "Activité des branchements (" + observedUsbEvents + ")";
            RecordIntervention("Activité USB", string.Join(Environment.NewLine, observed.Select(e => e.SharedText)));
        }

        IntPtr OnDeviceChange(IntPtr hwnd, int message, IntPtr change, IntPtr data, ref bool handled)
        {
            const int DeviceChange = 0x0219;
            if (message == DeviceChange && monitorActive && !cleaningUsb && !usbWindowClosed)
            {
                long kind = change.ToInt64();
                if (kind == 7 || kind == 0x8000 || kind == 0x8004) QueueMapUpdate();
            }
            return IntPtr.Zero;
        }

        void QueueMapUpdate()
        {
            mapUpdatePending = true;
            if (!mapTimer.IsEnabled) mapTimer.Start();
        }

        void ShowPortChanges(List<UsbChange> changes, bool historyAvailable)
        {
            var list = Get<StackPanel>("MapChanges");
            Get<Expander>("MapTracking").Header = changes.Count == 0 ? "Suivi des changements de port"
                : "Suivi des changements de port (" + changes.Count + ")";
            list.Children.Clear();
            var text = changes.Count == 0
                ? "Aucun changement de port identifié dans ce relevé. Sélectionnez un adaptateur sur la carte pour consulter son affectation COM actuelle."
                : "Les anciens branchements sont comparés aux appareils actuellement connectés.";
            if (!historyAvailable) text += " L’historique de Windows est indisponible pour ce relevé.";
            list.Children.Add(Text(text, 14, false, "MutedBrush"));
            foreach (var change in changes)
            {
                var lines = new StackPanel();
                lines.Children.Add(Text(change.Title, 14, true, "TextBrush"));
                lines.Children.Add(Text(change.Now.Name, 17, true, "TextBrush"));
                string before = change.Before.Label;
                string now = change.Now.Label;
                if (before == now && change.Before.DifferentLocation(change.Now))
                {
                    before += " · " + change.Before.Location;
                    now += " · " + change.Now.Location;
                }
                lines.Children.Add(Text("Ancien branchement : " + before, 14, false, "MutedBrush"));
                lines.Children.Add(Text("Branchement actuel : " + now, 14, true, "TextBrush"));
                if (change.Settings.Length > 0)
                {
                    var settings = Text(change.Settings, 14, false, "TextBrush");
                    settings.Margin = new Thickness(0, 6, 0, 0);
                    lines.Children.Add(settings);
                }
                if (!change.Recognized)
                    lines.Children.Add(Text("Deux exemplaires de ce modèle peuvent présenter les mêmes références.", 13, false, "MutedBrush"));
                if (change.CanClean) AddCleanupOption(lines, change);
                var card = Card(lines);
                card.Margin = new Thickness(0, 8, 0, 0);
                list.Children.Add(card);
            }
        }

        void AddCleanupOption(StackPanel lines, UsbChange change)
        {
            var panel = new StackPanel { Margin = new Thickness(4, 5, 0, 0) };
            panel.Children.Add(Text("Windows conserve cette ancienne installation déconnectée. Si le logiciel fonctionne avec ses réglages actuels, vous pouvez la conserver.", 14, false, "MutedBrush"));
            var confirm = new CheckBox { Margin = new Thickness(0, 8, 0, 0),
                Content = Text("Je reconnais l’ancien branchement et cette entrée me pose problème.", 14, false, "TextBrush") };
            var remove = ActionButton("Retirer l’ancienne installation…", async delegate { await ReviewCleanupAsync(change); });
            remove.IsEnabled = false;
            confirm.Checked += delegate { remove.IsEnabled = true; };
            confirm.Unchecked += delegate { remove.IsEnabled = false; };
            panel.Children.Add(confirm);
            panel.Children.Add(remove);
            lines.Children.Add(new Expander { Header = "Examiner l’ancienne installation", Content = panel, Margin = new Thickness(0, 8, 0, 0) });
        }

        async Task ReviewCleanupAsync(UsbChange change)
        {
            if (readingMap || cleaningUsb || !change.CanClean) return;
            cleaningUsb = true;
            Get<Button>("MapRefresh").IsEnabled = false;
            Get<Expander>("MapTracking").IsEnabled = false;
            try
            {
                string message = change.Now.Name + "\n\nAncienne entrée déconnectée : " + change.Before.Label + " " + change.Before.Com
                    + "\nEmplacement Windows : " + change.Before.Location
                    + "\n\nEntrée active : " + change.Now.Label + " " + change.Now.Com
                    + "\n\nWindows retirera l’ancienne installation et ses réglages. Le pilote restera disponible. "
                    + "Une demande d’administrateur peut apparaître.\n\nConfirmez-vous le retrait de cette ancienne entrée ?";
                var result = CleanupResult.Cancelled;
                if (MessageBox.Show(Window, message, "Retirer une ancienne installation USB", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    var owner = new WindowInteropHelper(Window).Handle;
                    result = await Task.Run(() => UsbCleanup.Request(change, owner));
                }
                string feedback;
                switch (result)
                {
                    case CleanupResult.Removed: feedback = "Ancienne installation retirée. Windows détecte toujours l’appareil actuel."; break;
                    case CleanupResult.Changed: feedback = "Le branchement ou l’ancienne installation a changé. Vérifiez la carte actualisée."; break;
                    case CleanupResult.PendingRestart: feedback = "Windows demande un redémarrage pour terminer le retrait."; break;
                    case CleanupResult.Unverified: feedback = "Windows a accepté le retrait. Son résultat reste à vérifier."; break;
                    case CleanupResult.Cancelled: feedback = "Retrait annulé."; break;
                    case CleanupResult.Denied: feedback = "Windows demande une autorisation d’administrateur pour ce retrait."; break;
                    default: feedback = "Windows n’a pas pu terminer le retrait."; break;
                }
                var notice = Get<TextBlock>("CleanupNotice");
                RecordIntervention("Retrait d’une ancienne installation USB", feedback);
                notice.Text = feedback;
                notice.Visibility = Visibility.Visible;
            }
            finally
            {
                cleaningUsb = false;
                Get<Expander>("MapTracking").IsEnabled = true;
                Get<Button>("MapRefresh").IsEnabled = true;
            }
            await ReadMapAsync();
        }
    }
}
