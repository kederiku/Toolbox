using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckRouterPageMerge()
        {
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var networks = new List<LocalNetwork> { TestNetwork("192.168.1.7", "255.255.255.0") };
                var snapshot = new List<RouterClient> {
                    new RouterClient("Radio du salon", "02:11:22:33:44:55", "192.168.1.8", true),
                    new RouterClient("Tablette", "02:11:22:33:44:66", "fd00::2", null),
                    new RouterClient("Enceinte retirée", "02:00:00:00:00:44", "192.168.1.44", false)
                };
                var mac = PhysicalAddress.Parse("021122334455");
                snapshot.Add(new RouterClient("Radio du salon", "02:11:22:33:44:55", "192.168.1.8", true));
                var page = new NetworkDevicesPage(window, () => networks, (link, found, token) => {
                    found(new LocalDevice(IPAddress.Parse("192.168.1.16"), null, mac));
                    found(new LocalDevice(IPAddress.Parse("192.168.1.8"), null, PhysicalAddress.Parse("021122334499")));
                }, (owner, link) => snapshot);
                var pc = (DataGrid)window.FindName("DevicesTable");
                var box = (DataGrid)window.FindName("RouterTable");
                var tabs = (TabControl)window.FindName("DevicesSource");
                var filter = (TextBox)window.FindName("DevicesFilter");
                var start = (Button)window.FindName("DevicesSearch");
                Action getBox = () => ((Button)window.FindName("RouterImport")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                page.Open();
                getBox();
                Check(box.Items.Count == 2 && tabs.SelectedIndex == 1 && pc.Items.Count == 0,
                    "La box peut être lue avant la recherche PC, les appareils explicitement hors ligne sont écartés");
                filter.Text = "fd00::";
                Check(box.Items.Count == 1 && ((RouterClient)box.Items[0]).StateLabel == "Non précisé",
                    "Un client IPv6 reste filtrable avec son état inconnu");
                filter.Clear();
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForDevicePage(window, () => start.IsEnabled);
                var matched = pc.Items.Cast<LocalDevice>().Single(d => d.Address == "192.168.1.16");
                Check(matched.Name == "Radio du salon" && pc.Items.Cast<LocalDevice>().Single(d => d.Address == "192.168.1.8").RouterName == null,
                    "Une MAC identique retrouve le nom même après un changement d’IP, une IP identique seule ne nomme personne");
                pc.SelectedItem = matched;
                Check(((TextBlock)window.FindName("DeviceIdentity")).Text.Contains("Nom fourni par la box"),
                    "La fiche de l’appareil indique l’origine du nom rapproché");
                snapshot.Add(new RouterClient("Autre radio", "02:11:22:33:44:55", "", true));
                getBox();
                Check(matched.RouterName == null && matched.Name == "Nom inconnu", "Un nom ambigu ne doit pas être attribué");
                snapshot.RemoveAt(snapshot.Count - 1);
                getBox();
                Check(matched.Name == "Radio du salon", "Une lecture après la recherche actualise les noms présents");
                matched.Merge(new LocalDevice(matched.Ip, "Nom Windows"));
                Check(matched.Name == "Nom Windows", "Un nom déjà identifié reste prioritaire");
                snapshot = null;
                getBox();
                Check(box.Items.Count == 2 && matched.RouterName != null, "Annuler conserve le dernier relevé");
                var old = networks[0];
                networks[0] = new LocalNetwork(old.Name, old.Index, old.Source, IPAddress.Parse("255.255.255.0"),
                    old.Gateways, old.HardwareAddress, Guid.NewGuid());
                page.Open();
                Check(pc.Items.Count == 0 && box.Items.Count == 0,
                    "Changer de réseau Windows efface les données de la précédente box même si les adresses IPv4 sont identiques");
            }
            finally
            {
                window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }
}
