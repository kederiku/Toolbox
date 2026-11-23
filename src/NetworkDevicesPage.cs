using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AtelierPC
{
    internal sealed class NetworkDevicesPage
    {
        readonly Func<List<LocalNetwork>> connections;
        readonly Action<LocalNetwork, Action<LocalDevice>, CancellationToken> scan;
        readonly ComboBox connection;
        readonly Button search, cancel;
        readonly TextBox filter;
        readonly TextBlock status, count;
        readonly ProgressBar progress;
        readonly ObservableCollection<LocalDevice> devices = new ObservableCollection<LocalDevice>();
        readonly Dictionary<uint, int> rowByAddress = new Dictionary<uint, int>();
        readonly ListCollectionView view;
        readonly DataGrid table;
        readonly NetworkDeviceDetails details;
        readonly TabControl sources;
        readonly Button import;
        readonly TextBlock routerStatus;
        readonly List<RouterClient> routerRows = new List<RouterClient>();
        readonly ListCollectionView routerView;
        RouterInventory router;
        DateTime importedAt;
        CancellationTokenSource running;
        bool refreshing;

        internal NetworkDevicesPage(Window window, Func<List<LocalNetwork>> connections,
            Action<LocalNetwork, Action<LocalDevice>, CancellationToken> scan,
            Func<Window, LocalNetwork, List<RouterClient>> readRouter = null)
        {
            this.connections = connections;
            this.scan = scan;
            connection = (ComboBox)window.FindName("DevicesConnection");
            search = (Button)window.FindName("DevicesSearch");
            cancel = (Button)window.FindName("DevicesCancel");
            filter = (TextBox)window.FindName("DevicesFilter");
            status = (TextBlock)window.FindName("DevicesStatus");
            count = (TextBlock)window.FindName("DevicesCount");
            progress = (ProgressBar)window.FindName("DevicesProgress");
            table = (DataGrid)window.FindName("DevicesTable");
            sources = (TabControl)window.FindName("DevicesSource");
            import = (Button)window.FindName("RouterImport");
            routerStatus = (TextBlock)window.FindName("RouterStatus");
            var boxTable = (DataGrid)window.FindName("RouterTable");
            RouterImportDialog.AddColumns(boxTable);
            routerView = new ListCollectionView(routerRows);
            routerView.Filter = item => {
                var client = (RouterClient)item;
                return Matches(client.Name, client.Address, client.Mac);
            };
            boxTable.ItemsSource = routerView;
            details = new NetworkDeviceDetails(window, connections);
            view = new ListCollectionView(devices);
            view.Filter = MatchesFilter;
            table.ItemsSource = view;
            var ipColumn = table.Columns.Single(c => c.SortMemberPath == "AddressNumber");
            ipColumn.SortDirection = ListSortDirection.Ascending;
            view.SortDescriptions.Add(new SortDescription(ipColumn.SortMemberPath, ListSortDirection.Ascending));
            table.SelectionChanged += delegate { if (!refreshing) ShowSelection(); };
            sources.SelectionChanged += delegate(object sender, SelectionChangedEventArgs args) {
                if (args.OriginalSource == sources) ShowSelection();
            };
            filter.TextChanged += delegate { view.Refresh(); routerView.Refresh(); UpdateCount(); UpdateRouterStatus(); };
            connection.SelectionChanged += delegate { if (!refreshing) ResetResults(); };
            search.Click += async delegate { await SearchAsync(); };
            cancel.Click += delegate { Cancel(); };
            import.Click += delegate {
                if (!RefreshConnections()) return;
                var link = (LocalNetwork)connection.SelectedItem;
                var entries = (readRouter ?? RouterImportDialog.Read)(window, link);
                if (entries == null) return;
                if (!RefreshConnections() || !link.SameConnection(connection.SelectedItem as LocalNetwork))
                {
                    status.Text = "La connexion a changé pendant la lecture. Rouvrez la liste sur la connexion souhaitée.";
                    return;
                }
                SetRouterInventory(new RouterInventory(entries));
                sources.SelectedIndex = 1;
            };
        }

        void ShowSelection()
        {
            details.Show(connection.SelectedItem as LocalNetwork, sources.SelectedIndex == 0 ? table.SelectedItem as LocalDevice : null);
        }

        internal void Leave()
        {
            Cancel();
            table.UnselectAll();
            details.Show(null, null);
        }

        internal void Open()
        {
            if (running == null) RefreshConnections();
        }

        bool RefreshConnections()
        {
            var previous = connection.SelectedItem as LocalNetwork;
            List<LocalNetwork> choices;
            bool failed = false;
            try { choices = connections(); }
            catch (System.Net.NetworkInformation.NetworkInformationException) { choices = new List<LocalNetwork>(); failed = true; }
            var selected = choices.FirstOrDefault(link => link.SameConnection(previous)) ?? choices.FirstOrDefault();
            refreshing = true;
            try
            {
                connection.ItemsSource = choices;
                connection.SelectedItem = selected;
            }
            finally { refreshing = false; }
            bool same = selected != null && selected.SameConnection(previous);
            if (!same) ResetResults();
            if (failed) status.Text = "Windows n’a pas pu lire les connexions réseau. Actualisez la liste pour réessayer.";
            return same;
        }

        void ResetResults()
        {
            Cancel();
            devices.Clear();
            rowByAddress.Clear();
            bool available = connection.SelectedItem != null;
            import.IsEnabled = available;
            SetRouterInventory(null);
            search.Content = available ? "Rechercher les appareils" : "Actualiser les connexions";
            status.Text = available ? "Lancez la recherche pour voir les appareils de cette connexion."
                : "Aucune connexion Wi-Fi ou Ethernet avec une adresse IPv4. Connectez le PC au réseau puis actualisez la liste.";
            UpdateCount();
        }

        async Task SearchAsync()
        {
            if (running != null) return;
            // Recheck the selected address and subnet before sending any requests.
            if (!RefreshConnections()) return;
            var selected = (LocalNetwork)connection.SelectedItem;
            sources.SelectedIndex = 0;
            var job = new CancellationTokenSource();
            var token = job.Token;
            running = job;
            devices.Clear();
            rowByAddress.Clear();
            UpdateCount();
            search.IsEnabled = connection.IsEnabled = false;
            cancel.IsEnabled = true;
            cancel.Visibility = progress.Visibility = Visibility.Visible;
            progress.IsIndeterminate = true;
            status.Text = "Recherche en cours sur " + selected.Name + "…";
            try
            {
                Action<LocalDevice> found = device => search.Dispatcher.Invoke(new Action(delegate {
                    if (running != job || token.IsCancellationRequested) return;
                    int index;
                    if (rowByAddress.TryGetValue(device.AddressNumber, out index))
                    {
                        var selectedDevice = table.SelectedItem;
                        refreshing = true;
                        try
                        {
                            devices[index].Merge(device);
                            if (router != null) devices[index].SetRouterName(router.NameFor(devices[index]));
                            view.Refresh();
                            if (selectedDevice != null && view.Contains(selectedDevice)) table.SelectedItem = selectedDevice;
                        }
                        finally { refreshing = false; }
                        ShowSelection();
                    }
                    else
                    {
                        if (router != null) device.SetRouterName(router.NameFor(device));
                        rowByAddress.Add(device.AddressNumber, devices.Count);
                        devices.Add(device);
                    }
                    UpdateCount();
                }));
                await Task.Run(() => scan(selected, found, token), token);
                if (running == job) status.Text = "Recherche terminée à " + DateTime.Now.ToString("T") + ".";
            }
            catch (OperationCanceledException) { if (running == job) status.Text = "Recherche interrompue."; }
            catch (Exception) { if (running == job) status.Text = "Recherche interrompue. Vérifiez que la connexion est active puis réessayez."; }
            finally
            {
                if (running == job) SetIdle();
                job.Dispose();
            }
        }

        internal void Cancel()
        {
            if (running == null) return;
            running.Cancel();
            SetIdle();
            status.Text = "Recherche interrompue.";
        }

        void SetIdle()
        {
            running = null;
            progress.IsIndeterminate = false;
            cancel.Visibility = progress.Visibility = Visibility.Collapsed;
            search.IsEnabled = connection.IsEnabled = true;
            UpdateCount();
            UpdateRouterStatus();
        }

        bool MatchesFilter(object item)
        {
            var device = (LocalDevice)item;
            return Matches(device.Name, device.Address, device.HardwareAddress == null ? null : device.MacAddress);
        }

        bool Matches(string name, string address, string mac)
        {
            string query = filter.Text.Trim();
            if (mac != null)
            {
                string hex = query.Replace(":", "").Replace("-", "");
                if (hex.Length > 0 && mac.Replace(":", "").IndexOf(hex, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return CultureInfo.CurrentCulture.CompareInfo.IndexOf(name ?? "", query,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
                (address ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void SetRouterInventory(RouterInventory inventory)
        {
            router = inventory;
            routerRows.Clear();
            if (router != null)
            {
                importedAt = DateTime.Now;
                routerRows.AddRange(router.Clients);
            }
            routerView.Refresh();
            var selected = table.SelectedItem;
            foreach (var device in devices) device.SetRouterName(router == null ? null : router.NameFor(device));
            view.Refresh();
            if (selected != null && view.Contains(selected)) table.SelectedItem = selected;
            ShowSelection();
            UpdateCount();
            UpdateRouterStatus();
        }

        void UpdateRouterStatus()
        {
            if (router == null)
            {
                routerStatus.Text = "Utilisez « Lire depuis la box… » pour charger les clients de votre réseau.";
                return;
            }
            int named = devices.Count(d => d.UsesRouterName);
            routerStatus.Text = "Relevé de " + importedAt.ToString("t") + " · " + routerView.Count + " / " + routerRows.Count +
                (routerRows.Count == 1 ? " ligne affichée." : " lignes affichées.") +
                (router.OfflineCount == 0 ? "" : " Hors ligne écartés : " + router.OfflineCount + ".") +
                (named == 0 ? "" : " Noms retrouvés dans la liste du PC : " + named + ".");
        }

        void UpdateCount()
        {
            int total = devices.Count;
            string summary = total == 1 ? "1 adresse IPv4 détectée" : total + " adresses IPv4 détectées";
            if (string.IsNullOrWhiteSpace(filter.Text)) count.Text = summary;
            else count.Text = view.Count == 0 ? "Aucune correspondance · " + summary
                : view.Count + (view.Count == 1 ? " ligne affichée · " : " lignes affichées · ") + summary;
        }
    }
}
