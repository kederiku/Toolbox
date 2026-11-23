using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckNetworkDevices()
        {
            var window = new Shell(() => new List<Device>()).Window;
            Check(window.FindName("RouterImport") is Button && window.FindName("RouterTable") is DataGrid,
                "La page donne accès à la lecture de la box et à ses clients");
            Check(((DataGrid)window.FindName("DevicesTable")).Columns.Any(column => Equals(column.Header, "Adresse MAC")),
                "Le tableau permet de lire et trier l’adresse MAC des appareils");
            var nav = window.FindName("NetworkDevicesNav") as Button;
            Check(nav != null, "Appareils du réseau accessible depuis le menu Réseau");
            nav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((FrameworkElement)window.FindName("NetworkDevicesView")).Visibility == Visibility.Visible,
                "Ouvrir la liste des appareils depuis le menu");
            ((Button)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(FindAction((StackPanel)window.FindName("OtherPage"), "Voir les appareils du réseau") != null,
                "Appareils du réseau accessible depuis le catalogue");
            window.Close();
            CheckLocalNetworkDiscovery();
            CheckNetworkDevicesTable();
            CheckLocalDevicePorts();
            CheckLocalDeviceAnnouncements();
            CheckRouterClientList();
            CheckRouterBrowser();
            CheckRouterPageMerge();
        }

        static LocalNetwork TestNetwork(string address, string mask)
        {
            return new LocalNetwork("Ethernet", 4, IPAddress.Parse(address), IPAddress.Parse(mask),
                new[] { IPAddress.Parse("192.168.8.10") }, PhysicalAddress.Parse("021122334455"));
        }

        static void CheckLocalNetworkDiscovery()
        {
            var network = TestNetwork("192.168.8.13", "255.255.255.248");
            Check(network.Hosts().Select(ip => ip.ToString()).OrderBy(s => IPAddress.Parse(s).GetAddressBytes()[3])
                .SequenceEqual(new[] { "192.168.8.9", "192.168.8.10", "192.168.8.11", "192.168.8.12", "192.168.8.13", "192.168.8.14" }),
                "La recherche respecte le masque et exclut les adresses de réseau et de diffusion");
            Check(TestNetwork("10.0.0.1", "255.255.255.254").Hosts().Count() == 2 &&
                TestNetwork("10.0.0.1", "255.255.255.255").Hosts().Single().ToString() == "10.0.0.1",
                "Les masques /31 et /32 conservent leurs hôtes");
            var replies = new ConcurrentBag<LocalDevice>();
            bool visibleBeforeDns = false;
            var discovery = new LocalNetworkDiscovery((link, ip) => ip.ToString() == "192.168.8.10" ? PhysicalAddress.Parse("102030405060") : null, ip => {
                visibleBeforeDns = replies.Any(d => d.Address == ip.ToString());
                return null;
            });
            discovery.Scan(network, replies.Add, CancellationToken.None);
            Check(visibleBeforeDns, "Une adresse confirmée apparaît même si la résolution du nom tarde");
            Check(replies.Count == 2 && replies.Any(d => d.Address == "192.168.8.13" && d.Name.Contains(Environment.MachineName) &&
                d.MacAddress == "02:11:22:33:44:55") && replies.Any(d => d.Address == "192.168.8.10" && d.IsGateway &&
                d.Name == "Passerelle du réseau" && d.MacAddress == "10:20:30:40:50:60"),
                "Afficher les MAC de la carte et de la passerelle même sans DNS, omettre les adresses silencieuses");
            Check(network.Contains(IPAddress.Parse("192.168.8.9")) && !network.Contains(IPAddress.Parse("192.168.8.8")) &&
                !network.Contains(IPAddress.Parse("192.168.8.15")) && !network.Contains(IPAddress.Parse("192.168.9.10")),
                "Les informations d’un voisin doivent appartenir à la plage d’hôtes de la carte");
            replies = new ConcurrentBag<LocalDevice>();
            bool cancelled = false;
            try { discovery.Scan(network, replies.Add, new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && replies.IsEmpty, "Une recherche annulée ne publie aucune réponse tardive");
        }

        static void CheckNetworkDevicesTable()
        {
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher, DispatcherPriority.Send));
            var release = new ManualResetEventSlim(false);
            try
            {
                var networks = new List<LocalNetwork>();
                int scans = 0;
                var page = new NetworkDevicesPage(window, () => networks, (network, found, token) => {
                    int run = Interlocked.Increment(ref scans);
                    if (run == 2)
                    {
                        token.WaitHandle.WaitOne(5000);
                        release.Wait(5000);
                        found(new LocalDevice(IPAddress.Parse("192.168.1.99"), "Réponse tardive"));
                        token.ThrowIfCancellationRequested();
                    }
                    found(new LocalDevice(IPAddress.Parse("192.168.1.10"), "Imprimante"));
                    found(new LocalDevice(IPAddress.Parse("192.168.1.2"), null, PhysicalAddress.Parse("3CAABB112233")));
                    found(new LocalDevice(IPAddress.Parse("192.168.1.2"), "Téléphone"));
                    var printer = IPAddress.Parse("192.168.1.10");
                    found(new LocalDevice(printer, null, null, new LocalDeviceAnnouncement(printer, "Imprimante du salon", "M1", "Fabricant X", 8002)));
                    found(new LocalDevice(printer, null, null, new LocalDeviceAnnouncement(printer, null, null, null, 8080)));
                });
                var start = (Button)window.FindName("DevicesSearch");
                var grid = (DataGrid)window.FindName("DevicesTable");
                var filter = (TextBox)window.FindName("DevicesFilter");
                var status = (TextBlock)window.FindName("DevicesStatus");
                Action click = () => start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                page.Open();
                Check(status.Text.Contains("IPv4") && grid.Items.Count == 0, "Expliquer l’absence de connexion IPv4");
                networks.Add(TestNetwork("192.168.1.5", "255.255.255.0"));
                page.Open();
                Check(scans == 0, "Ouvrir la page sans lancer de recherche");
                click();
                WaitForDevicePage(window, () => start.IsEnabled);
                Check(grid.Items.Cast<LocalDevice>().Select(d => d.Address).SequenceEqual(new[] { "192.168.1.2", "192.168.1.10" }),
                    "Le tableau présente les adresses dans l’ordre numérique");
                grid.SelectedIndex = 1;
                var detail = (Border)window.FindName("DeviceDetails");
                var title = (TextBlock)window.FindName("DeviceDetailTitle");
                var identity = (TextBlock)window.FindName("DeviceIdentity");
                var service = (TextBlock)window.FindName("DeviceServicePort");
                Check(detail.Visibility == Visibility.Visible && title.Text == "Imprimante du salon" &&
                    identity.Text.Contains("Fabricant X") && identity.Text.Contains("M1") && identity.Text.Contains("Imprimante") &&
                    service.Text.Contains("8002/TCP") && service.Text.Contains("8080/TCP"),
                    "Les descriptions partielles conservent l’identité et les ports confirmés du bon appareil");
                ((TextBox)window.FindName("DevicePortNumber")).Text = "65536";
                ((Button)window.FindName("DeviceTestPort")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((TextBlock)window.FindName("DevicePortResult")).Text.Contains("65535"), "Un numéro de port invalide est expliqué");
                ((FrameworkElement)window.FindName("NetworkDevicesView")).Visibility = Visibility.Visible;
                grid.Measure(new Size(520, 200));
                grid.Arrange(new Rect(0, 0, 520, 200));
                grid.UpdateLayout();
                var headers = DeviceTableHeaders(grid);
                Check(headers != null, "Le tableau affiche ses en-têtes de tri");
                Action<int> clickHeader = column => {
                    var header = (DataGridColumnHeader)headers.ItemContainerGenerator.ContainerFromIndex(column);
                    typeof(DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic).Invoke(header, null);
                };
                clickHeader(1);
                Check(((LocalDevice)grid.Items[0]).Address == "192.168.1.10", "Un clic inverse le tri numérique des IP");
                clickHeader(0);
                Check(((LocalDevice)grid.Items[0]).Name == "Imprimante du salon", "Le nom se trie aussi depuis son en-tête");
                clickHeader(0);
                Check(((LocalDevice)grid.Items[0]).Name == "Téléphone", "Un second clic inverse le tri des noms");
                filter.Text = "  TELE  ";
                Check(grid.Items.Count == 1 && ((LocalDevice)grid.Items[0]).Name == "Téléphone", "Filtrer le nom sans casse ni accents");
                filter.Text = "192.168.1.1";
                Check(grid.Items.Count == 1 && ((LocalDevice)grid.Items[0]).Name == "Imprimante du salon", "Filtrer par début d’adresse IP");
                filter.Text = "3c-aa-bb";
                Check(grid.Items.Count == 1 && ((LocalDevice)grid.Items[0]).Name == "Téléphone",
                    "Le filtrage par MAC fonctionne aussi après réception du nom");
                grid.SelectedIndex = 0;
                Check(identity.Text.Contains("3C:AA:BB:11:22:33") && service.Visibility == Visibility.Collapsed,
                    "Sélectionner une autre ligne efface les informations du précédent appareil");
                filter.Text = "introuvable";
                Check(grid.Items.Count == 0 && detail.Visibility == Visibility.Collapsed, "Le filtre masque aussi une fiche devenue invisible");
                filter.Clear();
                Check(grid.Items.Count == 2, "Effacer le filtre restaure le relevé");
                click();
                WaitForDevicePage(window, () => Volatile.Read(ref scans) == 2);
                page.Cancel();
                Check(start.IsEnabled && grid.Items.Count == 0 && status.Text.Contains("interrompue"),
                    "La recherche peut être relancée dès l’annulation, même si Windows termine une requête");
                click();
                release.Set();
                WaitForDevicePage(window, () => start.IsEnabled);
                Check(grid.Items.Count == 2 && status.Text.Contains("terminée"), "Une réponse tardive ne modifie pas le nouveau relevé");
            }
            finally
            {
                release.Set();
                window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        static DataGridColumnHeadersPresenter DeviceTableHeaders(DependencyObject element)
        {
            var presenter = element as DataGridColumnHeadersPresenter;
            if (presenter != null) return presenter;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
            {
                presenter = DeviceTableHeaders(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
                if (presenter != null) break;
            }
            return presenter;
        }

        static void WaitForDevicePage(Window window, Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!done() && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate {}));
            Check(done(), "La page des appareils du réseau répond dans le délai prévu");
        }
    }
}
