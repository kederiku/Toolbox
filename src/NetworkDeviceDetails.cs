using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed class NetworkDeviceDetails
    {
        readonly Func<List<LocalNetwork>> connections;
        readonly Border panel;
        readonly TextBlock title, identity, service, ports, result;
        readonly TextBox number;
        readonly Button test;
        LocalDevice current;
        LocalNetwork network;
        CancellationTokenSource testing;
        int selection;

        internal NetworkDeviceDetails(Window window, Func<List<LocalNetwork>> connections)
        {
            this.connections = connections;
            panel = (Border)window.FindName("DeviceDetails");
            title = (TextBlock)window.FindName("DeviceDetailTitle");
            identity = (TextBlock)window.FindName("DeviceIdentity");
            service = (TextBlock)window.FindName("DeviceServicePort");
            ports = (TextBlock)window.FindName("DevicePorts");
            number = (TextBox)window.FindName("DevicePortNumber");
            test = (Button)window.FindName("DeviceTestPort");
            result = (TextBlock)window.FindName("DevicePortResult");
            test.Click += async delegate { await TestPortAsync(); };
        }

        internal void Show(LocalNetwork link, LocalDevice device)
        {
            bool changed = current != device || (link != null && !link.SameConnection(network));
            network = link;
            current = device;
            if (changed)
            {
                selection++;
                if (testing != null) { testing.Cancel(); testing = null; }
                number.Clear();
                result.Text = "";
                test.IsEnabled = number.IsEnabled = true;
            }
            panel.Visibility = device == null || link == null ? Visibility.Collapsed : Visibility.Visible;
            if (panel.Visibility == Visibility.Collapsed) return;

            title.Text = device.Name;
            var facts = new List<string> { "IPv4 · " + device.Address, "Adresse MAC · " + device.MacAddress, "Connexion · " + link.Name };
            if (device.IsGateway) facts.Add("Rôle · Passerelle configurée sur cette connexion");
            var announcement = device.Announcement;
            if (announcement != null)
            {
                if (!string.IsNullOrWhiteSpace(announcement.Name)) facts.Add("Nom annoncé par l’appareil (UPnP) · " + announcement.Name);
                if (!string.IsNullOrWhiteSpace(announcement.Manufacturer)) facts.Add("Fabricant annoncé · " + announcement.Manufacturer);
                if (!string.IsNullOrWhiteSpace(announcement.Model)) facts.Add("Modèle annoncé · " + announcement.Model);
            }
            if (device.HostName != null) facts.Add("Nom fourni par Windows · " + device.HostName);
            if (device.RouterName != null) facts.Add("Nom fourni par la box · " + device.RouterName + " (même adresse MAC)");
            if (device.RouterName == null && device.HostName == null && !device.IsThisPc && (announcement == null || string.IsNullOrWhiteSpace(announcement.Name)))
                facts.Add("L’appareil a répondu à la recherche. Aucun nom reçu via Windows ou les annonces UPnP pour le moment.");
            identity.Text = string.Join(Environment.NewLine, facts);
            bool hasPort = device.DescriptionPorts.Count != 0;
            service.Visibility = hasPort ? Visibility.Visible : Visibility.Collapsed;
            service.Text = hasPort ? "Réponse HTTP du service UPnP pendant le relevé · " +
                string.Join(", ", device.DescriptionPorts.Select(port => port + "/TCP")) : "";
            if (changed) ReadPortsAsync(link, device, selection);
        }

        async void ReadPortsAsync(LocalNetwork link, LocalDevice device, int version)
        {
            ports.Text = "Lecture des ports TCP…";
            string observations;
            try { observations = await Task.Run(() => LocalDevicePorts.Describe(link.Source, device.Ip)); }
            catch (Exception) { observations = "Windows n’a pas pu lire les ports TCP."; }
            if (selection == version) ports.Text = observations;
        }

        async Task TestPortAsync()
        {
            if (current == null || network == null || testing != null) return;
            int port;
            if (!int.TryParse(number.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            {
                result.Text = "Saisissez un numéro de port entre 1 et 65535.";
                number.Focus();
                return;
            }
            var job = new CancellationTokenSource();
            testing = job;
            test.IsEnabled = number.IsEnabled = false;
            try
            {
                if (!network.Contains(current.Ip) || !connections().Any(link => link.SameConnection(network)))
                {
                    result.Text = "La connexion a changé. Relancez la recherche des appareils.";
                    return;
                }
                result.Text = "Test du port " + port + " en cours…";
                string outcome = await LocalDevicePorts.CheckAsync(network.Source, current.Ip, port, job.Token);
                if (testing == job) result.Text = outcome;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (testing == job) result.Text = "Le test a échoué. Vérifiez la connexion sélectionnée."; }
            finally
            {
                if (testing == job) { testing = null; test.IsEnabled = number.IsEnabled = true; }
                job.Dispose();
            }
        }
    }
}
