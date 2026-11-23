using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        bool readingMap, mapViewPending;
        string requestedMapId;
        UsbSerialPorts serialPorts;
        string lastSharedSerialPorts = "";
        readonly Dictionary<string, TreeViewItem> mapItems = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

        void SizeMapLayout(double width)
        {
            bool stacked = width < 750;
            Get<ColumnDefinition>("MapDetailWidth").Width = stacked ? new GridLength(0) : new GridLength(0.73, GridUnitType.Star);
            var detail = Get<Border>("MapDetail");
            Grid.SetRow(detail, stacked ? 1 : 0);
            Grid.SetColumn(detail, stacked ? 0 : 1);
            detail.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
            Get<TreeView>("MapTree").MaxHeight = stacked ? 270 : 470;
            Get<TextBlock>("MapRefreshLabel").Visibility = width < 530 ? Visibility.Collapsed : Visibility.Visible;
            Get<Border>("MapOverviewIcon").Visibility = width < 420 ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("MapStatus").FontSize = width < 420 ? 15 : 17;
        }

        Task ReadMapSelectionAsync(string id)
        {
            if (!monitorStarted) SetUsbMonitor(true);
            requestedMapId = id;
            return ReadMapAsync();
        }

        async Task ReadMapAsync(bool background = false)
        {
            if (cleaningUsb || usbWindowClosed) return;
            if (readingMap)
            {
                if (!background) mapViewPending = true;
                return;
            }
            do
            {
                mapViewPending = false;
                await ReadMapSnapshotAsync(background);
                background = false;
            } while (mapViewPending && currentPage == usbMapPage && !usbWindowClosed);
            if (mapUpdatePending && monitorActive && !usbWindowClosed) QueueMapUpdate();
        }

        async Task ReadMapSnapshotAsync(bool background)
        {
            int generation = monitorGeneration;
            readingMap = true;
            var refresh = Get<Button>("MapRefresh");
            var tree = Get<TreeView>("MapTree");
            var previousRow = tree.SelectedItem as TreeViewItem;
            var previous = previousRow == null ? null : previousRow.Tag as UsbNode;
            var collapsed = new HashSet<string>(mapItems.Where(p => p.Value.HasItems && !p.Value.IsExpanded)
                .Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            refresh.IsEnabled = false;
            var status = Get<TextBlock>("MapStatus");
            if (!background)
            {
                status.Text = "Lecture des branchements USB…";
                Get<TextBlock>("MapStamp").Text = "";
                Get<Grid>("MapContent").Visibility = Visibility.Collapsed;
            }
            Get<Expander>("MapTracking").IsEnabled = false;
            try
            {
                List<Device> current = null, historical = null;
                DateTime observedAt = default(DateTime);
                var map = await Task.Run(delegate {
                    current = scan();
                    if (current == null || current.Count == 0) throw new InvalidOperationException();
                    observedAt = DateTime.Now;
                    if (background) return null;
                    try { historical = scanDisconnected(); }
                    catch (Exception) { historical = null; }
                    return UsbTopology.Build(current);
                });
                if (usbWindowClosed) return;
                if (monitorActive && monitorGeneration == generation) ObserveUsbConnections(current, observedAt);
                if (background && (!monitorActive || monitorGeneration != generation)) return;
                var currentPorts = UsbSerialPorts.Read(current);
                string portSummary = currentPorts.SharedAssignments();
                if (portSummary != lastSharedSerialPorts)
                {
                    RecordIntervention("Affectations série USB", portSummary.Length == 0
                        ? "Aucune affectation COM lisible dans ce relevé."
                        : "Ports COM actuels, une ligne par appareil :" + Environment.NewLine + portSummary);
                    lastSharedSerialPorts = portSummary;
                }
                if (background)
                {
                    RecordUsbSnapshot(current, tracker.Update(current, new Device[0]), false);
                    return;
                }
                serialPorts = currentPorts;
                var changes = tracker.Update(current, historical ?? new List<Device>());
                RecordUsbSnapshot(current, changes, historical != null);
                ShowPortChanges(changes, historical != null);
                tree.Items.Clear();
                mapItems.Clear();
                foreach (var host in map.Controllers) tree.Items.Add(MapRow(host, collapsed));
                if (map.Unattached.Count > 0)
                {
                    const string unknown = "Rattachement indéterminé";
                    var separate = new TreeViewItem { Header = Text(unknown, 14, true, "MutedBrush"), IsExpanded = true };
                    AutomationProperties.SetName(separate, unknown);
                    foreach (var node in map.Unattached) separate.Items.Add(MapRow(node, collapsed));
                    tree.Items.Add(separate);
                }
                Get<TextBlock>("MapStamp").Text = "Relevé à " + observedAt.ToString("HH:mm:ss");
                if (tree.Items.Count == 0)
                {
                    status.Text = "Aucun branchement USB détecté par Windows. Branchez un appareil puis actualisez la carte.";
                    return;
                }
                status.Text = CountLabel(map.Controllers.Count, "contrôleur") + "  ·  " + CountLabel(map.HubCount, "hub")
                    + "  ·  " + CountLabel(map.DeviceCount, "périphérique");
                Get<Grid>("MapContent").Visibility = Visibility.Visible;
                string selectedId = requestedMapId ?? (previous == null ? null : previous.Device.Id);
                TreeViewItem selection = null;
                if (!string.IsNullOrEmpty(selectedId) && !mapItems.TryGetValue(selectedId, out selection))
                    status.Text += "  ·  L’appareil recherché ne figure plus sur la carte.";
                if (selection == null)
                {
                    selection = mapItems.Values.FirstOrDefault(row => ((UsbNode)row.Tag).NeedsAttention)
                        ?? (TreeViewItem)tree.Items[0];
                    if (selection.Tag == null) selection = (TreeViewItem)selection.Items[0];
                }
                // An explicitly requested device remains visible even inside a previously folded branch.
                var nodeToShow = (UsbNode)selection.Tag;
                for (var parent = nodeToShow.Parent; parent != null; parent = parent.Parent)
                {
                    TreeViewItem row;
                    if (parent.Device.Id != null && mapItems.TryGetValue(parent.Device.Id, out row)) row.IsExpanded = true;
                }
                selection.IsSelected = true;
                ShowMapDetail(nodeToShow);
                if (currentPage == usbMapPage && requestedMapId != null) selection.BringIntoView();
            }
            catch (Exception)
            {
                if (usbWindowClosed) return;
                if (generation == monitorGeneration) connectionMonitor.ResetBaseline();
                tree.Items.Clear();
                mapItems.Clear();
                Get<StackPanel>("MapChanges").Children.Clear();
                Get<Grid>("MapContent").Visibility = Visibility.Collapsed;
                Get<TextBlock>("MapStamp").Text = "";
                status.Text = "Lecture impossible. Attendez quelques secondes, puis choisissez « Actualiser la carte ».";
                RecordIntervention("Carte USB", "Lecture incomplète. Relancez le relevé pour vérifier les branchements.");
            }
            finally
            {
                if (!mapViewPending && !background) requestedMapId = null;
                readingMap = false;
                refresh.IsEnabled = true;
                Get<Expander>("MapTracking").IsEnabled = true;
            }
        }

        static string CountLabel(int count, string singular)
        {
            return count + " " + singular + (count > 1 ? "s" : "");
        }

        TreeViewItem MapRow(UsbNode node, HashSet<string> collapsed)
        {
            var d = node.Device;
            bool controller = d.Kind == UsbKind.Controller;
            bool device = d.Kind == UsbKind.Device;
            string symbol = controller ? "\uE7F4" : device ? "\uE88E" : "\uE8CE";
            string fill = controller ? "InkBrush" : device ? "CanvasBrush" : "AccentSoftBrush";
            string ink = controller ? "SurfaceBrush" : device ? "MutedBrush" : "AccentBrush";
            var header = new DockPanel();
            header.Children.Add(new Border { Background = Ink(fill), CornerRadius = new CornerRadius(7),
                Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center, Child = Glyph(symbol, 16, ink) });
            var lines = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(Text(node.Title, 14, !device, "TextBrush"));
            string meta = device || node.Title == node.KindLabel ? "" : node.KindLabel;
            if (node.PortLabel.Length > 0) meta += (meta.Length == 0 ? "" : "  ·  ") + node.PortLabel;
            if (meta.Length > 0) lines.Children.Add(Text(meta, 12, false, "MutedBrush"));
            if (node.NeedsAttention)
            {
                var issue = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                var warning = Glyph("\uE7BA", 12, "WarningBrush");
                warning.Margin = new Thickness(0, 2, 5, 0);
                warning.VerticalAlignment = VerticalAlignment.Top;
                issue.Children.Add(warning);
                issue.Children.Add(Text(node.RowIssue, 12, true, "WarningBrush"));
                lines.Children.Add(issue);
            }
            header.Children.Add(lines);
            var row = new TreeViewItem { Tag = node, Header = header,
                IsExpanded = string.IsNullOrEmpty(d.Id) || !collapsed.Contains(d.Id) };
            AutomationProperties.SetName(row, string.Join(", ", new[] { node.KindLabel, node.Title, node.PortLabel, node.RowIssue }
                .Where(s => s.Length > 0).Distinct()));
            if (!string.IsNullOrEmpty(d.Id)) mapItems[d.Id] = row;
            foreach (var function in node.FunctionDevices)
                if (!string.IsNullOrEmpty(function.Id)) mapItems[function.Id] = row;
            foreach (var child in node.Children) row.Items.Add(MapRow(child, collapsed));
            return row;
        }

        void ShowMapDetail(UsbNode node)
        {
            var detail = Get<Border>("MapDetail");
            detail.Visibility = node == null ? Visibility.Collapsed : Visibility.Visible;
            if (node == null) return;
            Get<TextBlock>("MapRole").Text = node.KindLabel;
            Get<TextBlock>("MapName").Text = node.Title;
            var d = node.Device;
            var state = Get<TextBlock>("MapState");
            var icon = Get<TextBlock>("MapStateIcon");
            state.Text = node.StateText;
            state.Foreground = icon.Foreground = Ink(node.NeedsAttention ? "WarningBrush" : "SuccessBrush");
            icon.Text = node.NeedsAttention ? "\uE7BA" : "\uE73E";
            Get<Border>("MapStateFrame").Background = Ink(node.NeedsAttention ? "WarningSoftBrush" : "SuccessSoftBrush");
            var serial = Get<TextBlock>("MapSerial");
            serial.Text = serialPorts == null ? "" : serialPorts.Describe(d);
            serial.Visibility = serial.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ShowUsbGuidance(node);
            var trail = new Stack<string>();
            for (var part = node; part != null; part = part.Parent)
            {
                string port = part.PortLabel;
                string title = part.ControllerNumber > 0 ? part.KindLabel + " · " + part.Title : part.Title;
                trail.Push(port.Length == 0 ? title : port + " · " + title);
                if (part.Parent == null && part.Device.Kind != UsbKind.Controller) trail.Push("Rattachement indéterminé");
            }
            Get<TextBlock>("MapPath").Text = string.Join(Environment.NewLine + "↳  ", trail);
            var connections = Get<TextBlock>("MapConnections");
            if (d.Kind == UsbKind.Device)
                connections.Text = UsbEvidence.ProductIds(d.Id) ?? "";
            else connections.Text = node.Children.Count == 0 ? "Aucun appareil directement rattaché dans ce relevé."
                : CountLabel(node.Children.Count, "branchement") + (node.Children.Count > 1 ? " directs." : " direct.");
            connections.Visibility = connections.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var functions = Get<TextBlock>("MapFunctions");
            functions.Visibility = node.Functions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            functions.Text = node.Functions.Count == 0 ? "" : "Fonctions de cet appareil" + Environment.NewLine
                + string.Join(Environment.NewLine, node.Functions.Select(f => "• " + f));
        }
    }
}
