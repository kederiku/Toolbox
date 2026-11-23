using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        public Window Window { get; private set; }
        readonly Func<List<Device>> scan;
        readonly Func<List<Device>> scanDisconnected;
        readonly Action<string> copyText;
        readonly int build = Environment.OSVersion.Version.Build;
        bool busy;
        bool enabling;
        List<Device> devices;
        DateTime? lastScan;
        string lastRepair;
        UsbProbe usbProbe;
        string lastUsbTest;

        public Shell(Func<List<Device>> scan, Action<string> copyText = null, Func<List<Device>> scanDisconnected = null,
            InternetSpeedTest speedTest = null, WindowsIntegrity integrity = null, SpeedHistory speedHistory = null,
            string toolPreferencesPath = null)
        {
            this.scan = scan;
            toolPreferences = new ToolPreferences(toolPreferencesPath);
            this.scanDisconnected = scanDisconnected ?? DeviceReader.DisconnectedUsb;
            this.copyText = copyText ?? Clipboard.SetText;
            using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                Window = (Window)XamlReader.Load(xaml);
            Window.Width = Math.Min(Window.Width, SystemParameters.WorkArea.Width);
            Window.Height = Math.Min(Window.Height, SystemParameters.WorkArea.Height);
            var os = Get<TextBlock>("OsLabel");
            os.Text = DiagnosticReport.WindowsName(build);
            this.speedTest = speedTest ?? new InternetSpeedTest();
            this.speedHistory = speedHistory ?? new SpeedHistory();
            this.integrity = integrity ?? new WindowsIntegrity();
            Get<Button>("SpeedStart").Click += async delegate { await RunSpeedTestAsync(); };
            Get<Button>("SpeedCancel").Click += delegate { CancelSpeedTest(); };
            Get<Button>("MapRefresh").Click += async delegate { await ReadMapAsync(); };
            Get<Button>("ObserveMissingUsb").Click += async delegate {
                Get<Expander>("MonitorHistory").IsExpanded = true;
                if (!monitorActive) SetUsbMonitor(true);
                await OpenPageAsync(usbMapPage);
                if (currentPage == usbMapPage) Get<Expander>("MonitorHistory").BringIntoView();
            };
            WatchUsbChanges();
            UpdateNotice.WaitForTool(Window, () => busy || enabling || cleaningUsb || readingMap ||
                performanceRun != null || speedCancellation != null || integrity.IsRunning);
            Get<StackPanel>("MapPage").SizeChanged += delegate(object sender, SizeChangedEventArgs e) {
                if (e.WidthChanged) SizeMapLayout(e.NewSize.Width);
            };
            Get<TreeView>("MapTree").SelectedItemChanged += delegate {
                var item = Get<TreeView>("MapTree").SelectedItem as TreeViewItem;
                ShowMapDetail(item == null ? null : item.Tag as UsbNode);
            };
            Get<Button>("ScanButton").Click += async delegate { await ScanAsync(); };
            Get<Button>("CopySummary").Click += delegate { CopySummary(); };
            Get<Button>("UsbCheck").Click += async delegate { await CheckUsbAsync(); };
            Get<Button>("UsbClose").Click += delegate { CloseUsb(); };
            Window.SizeChanged += delegate { SizeLayout(Window.ActualWidth); };
            Window.Closing += (s, e) => {
                if (cleaningUsb)
                {
                    e.Cancel = MessageBox.Show(Window, "Le retrait de l’ancienne installation est en cours. Quitter avant d’en connaître le résultat ?",
                        "Quitter Toolbox", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
                    return;
                }
                if (enabling)
                    e.Cancel = MessageBox.Show(Window, "La réactivation est en cours. Voulez-vous quitter avant la vérification du résultat ?", "Quitter Toolbox", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
            };
            InitializeNavigation();
            SizeLayout(Window.Width);
            Get<StackPanel>("Results").Children.Add(StatusCard("\uE9D9", "En attente d’une analyse",
                "Lancez la vérification pour savoir si Windows signale un problème.", "AccentSoftBrush", "MutedBrush"));
        }

        T Get<T>(string name) where T : class { return (T)Window.FindName(name); }
        Brush Ink(string color)
        {
            if (color.StartsWith("#", StringComparison.Ordinal))
                return (Brush)new BrushConverter().ConvertFromString(color);
            return (Brush)Window.FindResource(color);
        }
        TextBlock Text(string value, double size, bool bold, string color)
        {
            return new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
                Foreground = Ink(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        }
        Border Card(UIElement content)
        {
            return new Border { Style = (Style)Window.FindResource("Card"), Child = content };
        }
        Button ActionButton(string label, RoutedEventHandler action, bool primary = false)
        {
            var button = new Button { Content = label, Style = (Style)Window.FindResource(primary ? "PrimaryAction" : "Action"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 8, 0) };
            button.Click += action;
            return button;
        }
        TextBlock Glyph(string symbol, double size, string color)
        {
            var icon = Text(symbol, size, false, color);
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            return icon;
        }
        Border StatusCard(string icon, string title, string detail, string fill, string color)
        {
            var layout = new DockPanel();
            layout.Children.Add(new Border { Background = Ink(fill), CornerRadius = new CornerRadius(10),
                Width = 43, Height = 43, Margin = new Thickness(0, 0, 15, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = Glyph(icon, 21, color) });
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(Text(title, 16, true, "TextBrush"));
            var description = Text(detail, 14, false, "MutedBrush");
            description.Margin = new Thickness(0, 3, 0, 0);
            lines.Children.Add(description);
            layout.Children.Add(lines);
            return Card(layout);
        }
        void SizeLayout(double width)
        {
            ResizeMenu(width);
            double contentWidth = width - Get<ColumnDefinition>("RailWidth").Width.Value;
            bool narrow = contentWidth < 600;
            Get<System.Windows.Controls.Primitives.UniformGrid>("SymptomLinks").Columns = narrow ? 1 : 2;
            double artWidth = width < 650 ? 0 : width < 1000 ? 155 : 215;
            Get<ColumnDefinition>("ArtWidth").Width = new GridLength(artWidth);
            Get<Viewbox>("Art").Visibility = artWidth == 0 ? Visibility.Collapsed : Visibility.Visible;
            double inset = narrow ? 14 : 26;
            SizeSpeedLayout(contentWidth - 2 * inset);
            Get<StackPanel>("Body").Margin = new Thickness(inset, narrow ? 14 : 22, inset, 24);
            Get<Border>("Topbar").Padding = new Thickness(narrow ? 10 : 22, 0, narrow ? 10 : 22, 0);
            Get<TextBlock>("PageTitle").FontSize = narrow ? 26 : 32;
            Get<TextBlock>("SearchLabel").Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("SearchKeys").Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        }

        void SetBusy(bool value, string activity)
        {
            busy = value;
            Get<Button>("ScanButton").IsEnabled = !value;
            Get<StackPanel>("Results").IsEnabled = !value;
            Get<Border>("UsbGuide").IsEnabled = !value;
            Get<StackPanel>("ShareBar").Visibility = !value && lastScan.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (value) Get<TextBlock>("CopyFeedback").Text = "";
            var label = value ? activity : devices == null ? "Lancer l’analyse" : "Relancer l’analyse";
            Get<TextBlock>("ScanLabel").Text = label;
            AutomationProperties.SetName(Get<Button>("ScanButton"), label);
            var progress = Get<ProgressBar>("Progress");
            progress.IsIndeterminate = value;
            progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(progress, activity);
        }
        void Notice(string message)
        {
            var notice = Get<TextBlock>("Notice");
            notice.Text = message ?? "";
            notice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        async Task ScanAsync()
        {
            if (busy) return;
            CloseUsb();
            Notice(null);
            SetBusy(true, "Analyse en cours…");
            try
            {
                await Refresh();
                RecordIntervention("Diagnostic des périphériques", DiagnosticReport.Findings(devices));
            }
            finally { SetBusy(false, ""); }
        }
        async Task<bool> Refresh()
        {
            try
            {
                devices = await Task.Run(scan);
                if (devices == null || devices.Count == 0) throw new InvalidOperationException();
                ShowResults();
                lastScan = DateTime.Now;
                Get<TextBlock>("Stamp").Text = lastScan.Value.ToString("HH:mm");
                return true;
            }
            catch (Exception ex)
            {
                devices = null;
                lastScan = DateTime.Now;
                var results = Get<StackPanel>("Results");
                results.Children.Clear();
                Get<TextBlock>("Stamp").Text = "";
                var card = StatusCard("\uE946", "L’analyse n’a pas pu aboutir",
                    "Windows n’a pas fourni l’inventaire complet. Attendez un instant puis relancez l’analyse.", "WarningSoftBrush", "WarningBrush");
                card.ToolTip = ex.Message;
                results.Children.Add(card);
                return false;
            }
        }

        void CopySummary()
        {
            if (busy || !lastScan.HasValue) return;
            var feedback = Get<TextBlock>("CopyFeedback");
            try
            {
                copyText(DiagnosticReport.Format(build, lastScan.Value, devices, lastRepair, lastUsbTest));
                feedback.Text = "Résumé copié. Vous pouvez le coller dans votre message.";
            }
            catch (ExternalException)
            {
                feedback.Text = "Le presse-papiers est indisponible. Patientez un instant, puis réessayez.";
            }
        }

        void ShowResults()
        {
            var results = Get<StackPanel>("Results");
            results.Children.Clear();
            var issues = devices.Where(d => d.HasProblem).ToList();
            var unknown = devices.Where(d => d.Code == Device.Unreadable).ToList();
            string summary = issues.Count > 0 ? (issues.Count == 1 ? "1 périphérique à vérifier" : issues.Count + " périphériques à vérifier")
                : unknown.Count > 0 ? "Certains états restent à vérifier" : "Aucun problème signalé par Windows";
            string totals = devices.Count + (devices.Count > 1 ? " périphériques détectés." : " périphérique détecté.");
            if (unknown.Count > 0) totals += " État illisible pour " + unknown.Count + (unknown.Count > 1 ? " appareils." : " appareil.");
            bool attention = issues.Count + unknown.Count > 0;
            results.Children.Add(StatusCard(attention ? "\uE946" : "\uE73E", summary, totals, attention ? "WarningSoftBrush" : "SuccessSoftBrush", attention ? "WarningBrush" : "SuccessBrush"));
            foreach (var device in issues.Concat(unknown))
                results.Children.Add(ProblemCard(device, issues.Count + unknown.Count == 1));
            var inventory = new Expander { Header = "Voir tous les périphériques (" + devices.Count + ")", Margin = new Thickness(0, 15, 0, 0) };
            inventory.Expanded += delegate { if (inventory.Content == null) inventory.Content = Inventory(); };
            results.Children.Add(inventory);
        }
        UIElement Inventory()
        {
            var group = new StackPanel { Margin = new Thickness(5, 8, 0, 0) };
            var field = new TextBox { MinHeight = 30, Padding = new Thickness(6, 3, 6, 3) };
            var label = new Label { Content = "Rechercher un appareil", Target = field, Padding = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(field, "Rechercher un appareil");
            var list = new ListBox { MaxHeight = 210, Margin = new Thickness(0, 5, 0, 0) };
            AutomationProperties.SetName(list, "Périphériques détectés");
            var rows = devices.Select(d => d.Name + "  ·  " + (d.Code == 0 ? "Aucun problème signalé" : Advice.For(d).Title)).ToList();
            list.ItemsSource = rows;
            field.TextChanged += delegate {
                var matches = rows.Where(r => r.IndexOf(field.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                list.ItemsSource = matches.Count == 0 ? new List<string> { "Aucun appareil correspondant." } : matches;
            };
            group.Children.Add(label); group.Children.Add(field); group.Children.Add(list);
            return group;
        }
        Border ProblemCard(Device device, bool expanded)
        {
            Advice advice = Advice.For(device);
            var body = new StackPanel();
            if (device.Code > 0) body.Children.Add(Text("Code Windows " + device.Code, 12, true, "WarningBrush"));
            body.Children.Add(Text(UsbEvidence.CompositeInterface(device.Id) ? UsbNode.FunctionName(device) : device.Name, 19, true, "TextBrush"));
            var title = Text(advice.Title, 15, true, "TextBrush");
            title.Margin = new Thickness(0, 7, 0, 3);
            body.Children.Add(title);
            body.Children.Add(Text(advice.Cause, 14, false, "MutedBrush"));
            if (device.Usb != null)
            {
                if (device.Usb.Product != null && !string.Equals(device.Usb.Product, device.Name, StringComparison.OrdinalIgnoreCase))
                    body.Children.Add(Text("Nom transmis par l’appareil : " + device.Usb.Product, 14, true, "TextBrush"));
                if (device.Usb.PortLabel != null)
                    body.Children.Add(Text(device.Usb.PortLabel + " (numérotation de Windows).", 14, true, "TextBrush"));
                string ids = UsbEvidence.ProductIds(device.Id);
                if (ids != null) body.Children.Add(Text("Référence USB du modèle : " + ids, 13, false, "MutedBrush"));
                if (device.Usb.PriorModels.Count > 0)
                {
                    var previous = new Expander { Header = "Appareils déjà enregistrés sur cette connexion", IsExpanded = true,
                        Margin = new Thickness(0, 8, 0, 3) };
                    var hints = new StackPanel { Margin = new Thickness(6, 4, 0, 0) };
                    hints.Children.Add(Text("Windows conserve ces références d’anciens branchements. Le test ci-dessous permet de vérifier quelle prise réagit aujourd’hui.", 14, false, "MutedBrush"));
                    foreach (string hint in device.Usb.PriorModels)
                        hints.Children.Add(Text("• " + hint, 14, true, "TextBrush"));
                    previous.Content = hints;
                    body.Children.Add(previous);
                }
            }
            bool guidedUsb = UsbEvidence.ForDevice(device) != UsbFailure.None && UsbEvidence.PhysicalEntry(device);
            if (guidedUsb)
            {
                var prompt = Text("L’assistant compare les branchements pendant que vous retirez puis rebranchez un accessoire.", 14, false, "MutedBrush");
                prompt.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(prompt);
                var locate = ActionButton("Retrouver cet appareil", async delegate { await StartUsbAsync(device); }, true);
                body.Children.Add(locate);
            }
            var guidance = new Expander { Header = guidedUsb ? "Tester la prise, le câble et l’accessoire" : "Étapes de résolution", IsExpanded = expanded && !guidedUsb,
                Margin = new Thickness(0, 9, 0, 0) };
            var steps = new StackPanel { Margin = new Thickness(5, 5, 0, 0) };
            for (int i = 0; i < advice.Steps.Length; i++)
            {
                var step = Text((i + 1) + ".  " + advice.Steps[i], 14, false, "MutedBrush");
                step.Margin = new Thickness(0, 2, 0, 2);
                steps.Children.Add(step);
            }
            steps.Children.Add(ActionButton(advice.Destination == Destination.Updates ? "Ouvrir Windows Update" : "Ouvrir le Gestionnaire de périphériques",
                delegate { OpenWindows(advice.Destination); }));
            guidance.Content = steps;
            body.Children.Add(guidance);
            if (!string.IsNullOrEmpty(device.Id) && (device.Kind != UsbKind.None || UsbTopology.FunctionOwner(device, devices) != null))
                body.Children.Add(ActionButton("Voir sur la carte USB", async delegate { await OpenPageAsync(usbMapPage, device.Id); }));
            if (Repair.CanEnable(device, build))
            {
                var enable = ActionButton("Réactiver cet appareil", async delegate { await EnableAsync(device); }, true);
                body.Children.Add(enable);
            }
            var card = Card(body); card.Margin = new Thickness(0, 9, 0, 0);
            return card;
        }

        void CloseUsb()
        {
            usbProbe = null;
            Get<Border>("UsbGuide").Visibility = Visibility.Collapsed;
            Get<StackPanel>("Results").Visibility = Visibility.Visible;
        }

        async Task StartUsbAsync(Device selected)
        {
            if (busy) return;
            CloseUsb();
            Notice(null);
            SetBusy(true, "Lecture des branchements…");
            try
            {
                if (!await Refresh())
                {
                    RecordIntervention("Essai de branchement USB", "L’inventaire de départ est indisponible. Relancez l’analyse avant l’essai.");
                    return;
                }
                var target = devices.FirstOrDefault(d => string.Equals(d.Id, selected.Id, StringComparison.OrdinalIgnoreCase)
                    && UsbEvidence.SamePort(selected, d) != false && UsbEvidence.ForDevice(d) != UsbFailure.None);
                if (target == null)
                {
                    RecordIntervention("Essai de branchement USB", "Le branchement ou l’état du périphérique a changé avant l’essai. Vérifiez l’inventaire actualisé.");
                    Notice("Le branchement a changé depuis l’analyse. Consultez l’état actualisé de vos appareils.");
                    return;
                }
                usbProbe = new UsbProbe(target, devices);
                if (usbProbe.Stage == UsbStage.Done) RecordIntervention("Essai de branchement USB", usbProbe.Report);
                string port = target.Usb == null ? null : target.Usb.PortLabel;
                Get<TextBlock>("UsbLocation").Text = port ?? "Windows ne précise pas le numéro de prise.";
                ShowUsb();
            }
            finally { SetBusy(false, ""); }
        }

        void ShowUsb()
        {
            bool done = usbProbe.Stage == UsbStage.Done;
            bool unplug = usbProbe.Stage == UsbStage.Unplug;
            Get<TextBlock>("UsbTitle").Text = done ? "Résultat du test USB" : unplug ? "Retrouver l’accessoire USB" : "Confirmer le branchement";
            Get<TextBlock>("UsbInstruction").Text = usbProbe.Detail;
            Get<Button>("UsbCheck").Content = unplug ? "Vérifier le débranchement" : "Vérifier le rebranchement";
            Get<Button>("UsbCheck").Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            Get<Button>("UsbClose").Content = done ? "Voir les résultats" : "Quitter le test";
            Get<StackPanel>("Results").Visibility = Visibility.Collapsed;
            var panel = Get<Border>("UsbGuide");
            panel.Visibility = Visibility.Visible;
            panel.BringIntoView();
        }

        async Task CheckUsbAsync()
        {
            if (busy || usbProbe == null || usbProbe.Stage == UsbStage.Done) return;
            SetBusy(true, "Comparaison en cours…");
            try
            {
                if (!await Refresh())
                {
                    RecordIntervention("Essai de branchement USB", "Vérification incomplète. Windows n’a pas fourni l’inventaire.");
                    Get<TextBlock>("UsbInstruction").Text = "La lecture des branchements a échoué. Laissez les appareils en place et cliquez à nouveau sur le bouton de vérification.";
                    return;
                }
                usbProbe.Observe(devices);
                RecordIntervention("Essai de branchement USB", Get<TextBlock>("UsbLocation").Text + Environment.NewLine + usbProbe.Report);
                lastUsbTest = "Test USB du " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + " — " + Get<TextBlock>("UsbLocation").Text
                    + Environment.NewLine + usbProbe.Report;
                ShowUsb();
            }
            finally { SetBusy(false, ""); }
        }
        void OpenWindows(Destination destination)
        {
            try
            {
                var start = destination == Destination.Updates ? new ProcessStartInfo("ms-settings:windowsupdate")
                    : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mmc.exe"), "\"" + Path.Combine(Environment.SystemDirectory, "devmgmt.msc") + "\"");
                start.UseShellExecute = true;
                using (Process.Start(start)) { }
            }
            catch (Exception)
            {
                string name = destination == Destination.Updates ? "Windows Update" : "Gestionnaire de périphériques";
                MessageBox.Show(Window, "Ouvrez le menu Démarrer et recherchez « " + name + " ».", "Ouverture impossible", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        async Task EnableAsync(Device device)
        {
            if (busy || !Repair.CanEnable(device, build)) return;
            if (MessageBox.Show(Window, "Remettre en service cet appareil ?\n\n" + device.Name
                + "\n\nWindows pourra demander l’autorisation d’un administrateur. Son état sera ensuite vérifié.",
                "Réactiver cet appareil", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            enabling = true;
            Notice(null);
            SetBusy(true, "Réactivation en cours…");
            try
            {
                var owner = new WindowInteropHelper(Window).Handle;
                string outcome;
                try { outcome = await Task.Run(() => Repair.Enable(device, build, owner)); }
                catch (Exception) { outcome = "La réactivation n’a pas pu aboutir. Suivez les étapes manuelles de l’appareil."; }
                SetBusy(true, "Vérification en cours…");
                bool read = await Refresh();
                if (outcome == null)
                {
                    var state = read ? Repair.Verify(device.Id, devices) : RepairState.Unverified;
                    outcome = state == RepairState.Healthy ? "Appareil réactivé. Windows ne signale plus de problème."
                        : state == RepairState.Unverified ? "Réactivation demandée. L’état de l’appareil reste à confirmer. Relancez l’analyse."
                        : "Réactivation demandée. Windows signale encore un problème. Consultez les étapes de résolution.";
                }
                lastRepair = DiagnosticReport.Reactivation(DateTime.Now, device, outcome);
                RecordIntervention("Réactivation d’un périphérique", "Avant la demande : appareil désactivé (code 22)." + Environment.NewLine + outcome);
                Notice(device.Name + " — " + outcome);
                if (currentPage == diagnosticPage) Get<ScrollViewer>("Scroll").ScrollToTop();
            }
            finally { enabling = false; SetBusy(false, ""); }
        }
    }
}
