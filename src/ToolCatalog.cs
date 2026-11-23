using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed class NavigationPage
    {
        internal readonly string NavName, Label, Icon, Description;
        internal readonly FrameworkElement View;
        internal string Title, Category, OpenLabel;
        internal Control PrimaryAction;
        internal bool HiddenFromMenu, IsFavorite;
        internal Func<string, Task> OnOpen;
        internal Action OnLeave;

        internal NavigationPage(string navName, FrameworkElement view, string label, string icon, string description)
        {
            NavName = navName;
            View = view;
            Label = Title = label;
            Icon = icon;
            Description = description;
        }
    }

    internal sealed partial class Shell
    {
        NavigationPage diagnosticPage, usbMapPage;

        NavigationPage[] CreateToolCatalog()
        {
            diagnosticPage = new NavigationPage("DiagNav", Get<StackPanel>("DiagnosticPage"), "Périphériques", "\uE772",
                "Identifiez un problème et découvrez comment le résoudre.") {
                Category = "Matériel", Title = "Diagnostic des périphériques", OpenLabel = "Ouvrir le diagnostic",
                PrimaryAction = Get<Button>("ScanButton")
            };
            usbMapPage = new NavigationPage("MapNav", Get<StackPanel>("MapPage"), "Carte USB", "\uE88E",
                "Sélectionnez un appareil pour diagnostiquer une panne, repérer une coupure ou retrouver son port COM.") {
                Category = "Matériel", Title = "Carte des branchements USB", OpenLabel = "Ouvrir la carte USB",
                OnOpen = ReadMapSelectionAsync
            };
            var speed = new NavigationPage("SpeedNav", Get<StackPanel>("SpeedPage"), "Test de débit", "\uE774",
                "Mesurez la latence et les débits de votre connexion vers le serveur de test.") {
                Category = "Réseau", Title = "Test de débit Internet", OpenLabel = "Ouvrir le test de débit",
                PrimaryAction = Get<Button>("SpeedStart"), OnLeave = CancelSpeedTest,
                OnOpen = delegate { LoadSpeedHistory(); return Task.CompletedTask; }
            };
            new NetworkRepairPage(Window, NetworkTroubleshooter.Diagnose, NetworkTroubleshooter.Repair, RecordIntervention);
            var networkRepair = new NavigationPage("NetworkRepairNav", Get<StackPanel>("NetworkRepairView"), "Dépannage réseau", "\uE90F",
                "Trouvez pourquoi le PC ne se connecte pas et appliquez la correction proposée.") {
                Category = "Réseau", OpenLabel = "Ouvrir le dépannage réseau", PrimaryAction = Get<Button>("NetworkCheck")
            };
            var devicesPage = new NetworkDevicesPage(Window, LocalNetworkDiscovery.Connections, new LocalNetworkDiscovery().Scan);
            var networkDevices = new NavigationPage("NetworkDevicesNav", Get<StackPanel>("NetworkDevicesView"), "Appareils du réseau", "\uE968",
                "Retrouvez le nom et l’adresse IP locale des appareils joignables depuis ce PC.") {
                Category = "Réseau", OpenLabel = "Voir les appareils du réseau", PrimaryAction = Get<Button>("DevicesSearch"),
                OnOpen = delegate { devicesPage.Open(); return Task.CompletedTask; }, OnLeave = devicesPage.Leave
            };
            return new[] { diagnosticPage, usbMapPage, speed, networkRepair, networkDevices, CreatePerformancePage(), CreateIntegrityPage(),
                CreateImagePage(), CreateVideoPage(), CreateYoutubePage(), CreateAudioPage(), CreateWatermarkPage(), CreateMetadataPage(), CreateDuplicatePage() };
        }
    }
}
