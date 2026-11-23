using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AtelierPC
{
    internal static class RouterBrowser
    {
        internal const int PageLimit = 120000;
        const string PasteHelp = "Vous pouvez aussi fermer cette fenêtre et coller la liste depuis votre navigateur.";

        internal static string Capture(Window owner, LocalNetwork network)
        {
            if (network == null)
            {
                MessageBox.Show(owner, "Sélectionnez une connexion réseau pour ouvrir la box. " + PasteHelp,
                    "Liste de la box", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            try
            {
                RouterBrowserRuntime.Prepare();
                return OpenBrowser(owner, network);
            }
            catch (Exception)
            {
                MessageBox.Show(owner, "Le navigateur intégré est indisponible. " + PasteHelp,
                    "Liste de la box", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string OpenBrowser(Window owner, LocalNetwork network)
        {
            var window = new CaptureWindow(owner, network);
            window.ShowDialog();
            return window.Result;
        }

        static bool UsableAddress(IPAddress ip)
        {
            return ip != null && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) &&
                !ip.Equals(IPAddress.Any) && ip.GetAddressBytes()[0] < 224;
        }

        internal static bool TryWebAddress(string address, out Uri uri)
        {
            return Uri.TryCreate(address, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.UserInfo.Length == 0 &&
                (uri.HostNameType == UriHostNameType.Dns || uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6) &&
                address.IndexOf('\\') < 0;
        }

        internal static bool IsLocalManagement(LocalNetwork network, Uri uri, Func<string, IPAddress[]> resolve)
        {
            IPAddress ip;
            var answers = IPAddress.TryParse(uri.IdnHost, out ip) ? new[] { ip } : resolve(uri.IdnHost);
            return answers.Length > 0 && answers.All(answer => UsableAddress(answer) && !answer.Equals(network.Source) &&
                (network.Gateways.Contains(answer) || network.Contains(answer)));
        }

        static async Task<bool> VerifyAsync(LocalNetwork network, Uri uri)
        {
            IPAddress ip;
            if (IPAddress.TryParse(uri.IdnHost, out ip)) return IsLocalManagement(network, uri, Dns.GetHostAddresses);
            try
            {
                var lookup = Task.Run(() => {
                    try { return IsLocalManagement(network, uri, Dns.GetHostAddresses); }
                    catch (SocketException) { return false; }
                    catch (ArgumentException) { return false; }
                });
                return await Task.WhenAny(lookup, Task.Delay(4000)) == lookup && await lookup;
            }
            catch (System.Security.SecurityException) { return false; }
        }

        internal static string CaptureScript(Uri uri)
        {
            var origin = new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
            using (var input = typeof(RouterBrowser).Assembly.GetManifestResourceStream("AtelierPC.RouterPageCapture.js"))
            using (var reader = new StreamReader(input, Encoding.UTF8))
            using (var json = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(string)).WriteObject(json, origin);
                return reader.ReadToEnd() + "(" + Encoding.UTF8.GetString(json.ToArray()) + ")";
            }
        }

        internal static string DecodePage(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 6 * PageLimit + 2) return null;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var text = (string)new DataContractJsonSerializer(typeof(string)).ReadObject(stream);
                return text != null && text.Length <= PageLimit ? text : null;
            }
        }

        sealed class CaptureWindow : Window
        {
            internal string Result { get; private set; }
            readonly LocalNetwork network;
            string selectedHost;
            readonly List<string> pages = new List<string>();
            readonly WebView2 browser = new WebView2();
            readonly TextBox address = new TextBox { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
            readonly TextBlock location = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
            readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 3) };
            readonly Button open, read, add, http;
            readonly string profile = Path.Combine(Path.GetTempPath(), "AtelierPC-box-" + Guid.NewGuid().ToString("N"));
            bool closed, ready, reading, starting, opening, finishSaved;
            int entryNumber, navigationNumber;
            ulong navigationId;
            Uri httpAlternative;

            internal CaptureWindow(Window owner, LocalNetwork network)
            {
                Owner = owner;
                this.network = network;
                Title = "Lire les appareils de la box";
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                Width = Math.Min(1020, SystemParameters.WorkArea.Width);
                Height = Math.Min(760, SystemParameters.WorkArea.Height);
                MinWidth = 380;
                MinHeight = 350;
                Resources.MergedDictionaries.Add(owner.Resources);
                FontFamily = owner.FontFamily;
                FontSize = owner.FontSize;
                Background = owner.Background;
                Foreground = owner.Foreground;
                var gateway = network.Gateways.FirstOrDefault(UsableAddress);
                if (gateway != null) address.Text = "https://" + gateway + "/";
                address.ToolTip = "Adresse de l’interface d’administration sur " + network.Name;
                AutomationProperties.SetName(address, "Adresse de l’interface de la box");
                var layout = new DockPanel();
                var top = new StackPanel { Margin = new Thickness(14, 10, 14, 7) };
                DockPanel.SetDock(top, Dock.Top);
                layout.Children.Add(top);
                top.Children.Add(new TextBlock { Text = "Connectez-vous à votre box, puis ouvrez sa liste d’appareils connectés.",
                    TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
                var bar = new DockPanel();
                open = MakeButton(owner, "Ouvrir", false);
                DockPanel.SetDock(open, Dock.Right);
                bar.Children.Add(open);
                bar.Children.Add(address);
                top.Children.Add(bar);
                top.Children.Add(location);
                var footer = new StackPanel { Margin = new Thickness(14, 2, 14, 10) };
                DockPanel.SetDock(footer, Dock.Bottom);
                layout.Children.Add(footer);
                footer.Children.Add(status);
                var controls = new WrapPanel();
                read = MakeButton(owner, "Lire la liste affichée", true);
                add = MakeButton(owner, "Ajouter cette page", false);
                add.ToolTip = "Ajouter les appareils affichés, puis passer à la page suivante dans l’interface de la box.";
                http = MakeButton(owner, "Essayer en HTTP", false);
                http.Visibility = Visibility.Collapsed;
                var cancel = MakeButton(owner, "Fermer", false);
                controls.Children.Add(read);
                controls.Children.Add(add);
                controls.Children.Add(http);
                controls.Children.Add(cancel);
                footer.Children.Add(controls);
                layout.Children.Add(browser);
                Content = layout;
                open.IsEnabled = read.IsEnabled = add.IsEnabled = false;
                status.Text = "Ouverture du navigateur privé…";
                open.Click += async delegate { await OpenAddressAsync(); };
                address.KeyDown += async delegate(object sender, KeyEventArgs e) {
                    if (e.Key == Key.Enter && open.IsEnabled) { e.Handled = true; await OpenAddressAsync(); }
                };
                read.Click += async delegate { await ReadAsync(true); };
                add.Click += async delegate { await ReadAsync(false); };
                http.Click += async delegate {
                    if (httpAlternative != null) { address.Text = httpAlternative.AbsoluteUri; await OpenAddressAsync(); }
                };
                cancel.Click += delegate { Close(); };
                Loaded += async delegate { await StartAsync(); };
                Closed += delegate {
                    closed = true;
                    browser.Dispose();
                    if (!starting) CleanProfile();
                };
            }

            static Button MakeButton(Window owner, string caption, bool primary)
            {
                return new Button { Content = caption, Margin = new Thickness(3, 2, 3, 2),
                    Padding = new Thickness(10, 6, 10, 6), Style = (Style)owner.FindResource(primary ? "PrimaryAction" : "Action") };
            }

            async Task StartAsync()
            {
                starting = true;
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, profile);
                    if (closed) return;
                    // A WebView2 policy may override the requested data directory.
                    if (!string.Equals(Path.GetFullPath(env.UserDataFolder), Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException();
                    var options = env.CreateCoreWebView2ControllerOptions();
                    options.IsInPrivateModeEnabled = true;
                    await browser.EnsureCoreWebView2Async(env, options);
                    if (closed) return;
                    var core = browser.CoreWebView2;
                    if (!core.Profile.IsInPrivateModeEnabled) throw new InvalidOperationException();
                    core.Settings.IsPasswordAutosaveEnabled = false;
                    core.Settings.IsGeneralAutofillEnabled = false;
                    core.Settings.AreHostObjectsAllowed = false;
                    core.Settings.IsWebMessageEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs e) {
                        e.Handled = true;
                        Uri target;
                        if (e.IsUserInitiated && TryWebAddress(e.Uri, out target)) Suggest(target);
                    };
                    core.DownloadStarting += delegate(object s, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; };
                    core.PermissionRequested += delegate(object s, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
                    core.LaunchingExternalUriScheme += delegate(object s, CoreWebView2LaunchingExternalUriSchemeEventArgs e) { e.Cancel = true; };
                    core.NavigationStarting += OnStarting;
                    core.FrameNavigationStarting += delegate(object s, CoreWebView2NavigationStartingEventArgs e) {
                        Uri frame;
                        if (e.Uri != "about:blank" && e.Uri != "about:srcdoc" &&
                            !(TryWebAddress(e.Uri, out frame) && Selected(frame))) e.Cancel = true;
                    };
                    core.SourceChanged += delegate {
                        Uri current;
                        if (!closed && TryWebAddress(core.Source, out current))
                        {
                            location.Text = "Page ouverte · " + current.AbsoluteUri;
                            location.ToolTip = current.AbsoluteUri;
                        }
                    };
                    core.NavigationCompleted += OnCompleted;
                    open.IsEnabled = true;
                    if (address.Text.Length != 0) await OpenAddressAsync();
                    else status.Text = "Saisissez l’adresse locale de l’interface d’administration de votre box.";
                }
                catch (Exception)
                {
                    if (!closed) status.Text = "Le navigateur privé WebView2 n’a pas pu démarrer. " + PasteHelp;
                }
                finally
                {
                    starting = false;
                    if (closed) CleanProfile();
                }
            }

            async Task OpenAddressAsync()
            {
                int attempt = ++entryNumber;
                ++navigationNumber;
                opening = true;
                RefreshButtons();
                Uri requested;
                string entry = address.Text.Trim();
                if (!entry.Contains("://")) entry = "https://" + entry;
                try
                {
                    if (!TryWebAddress(entry, out requested))
                    {
                        status.Text = "Saisissez une adresse HTTP ou HTTPS de votre box.";
                        return;
                    }
                    bool valid = await VerifyAsync(network, requested);
                    if (closed || attempt != entryNumber) return;
                    if (!valid)
                    {
                        status.Text = "Cette adresse ne correspond pas à une interface locale sur " + network.Name + ". " + PasteHelp;
                        return;
                    }
                    if (!Selected(requested))
                    {
                        if (selectedHost != null) browser.CoreWebView2.Stop();
                        pages.Clear();
                        selectedHost = requested.IdnHost;
                    }
                    ready = finishSaved = false;
                    navigationId = 0;
                    browser.CoreWebView2.Navigate(requested.AbsoluteUri);
                }
                catch (Exception) { if (!closed) status.Text = "Impossible d’ouvrir cette adresse. " + PasteHelp; }
                finally
                {
                    if (!closed && attempt == entryNumber) { opening = false; RefreshButtons(); }
                }
            }

            bool Selected(Uri uri)
            {
                return string.Equals(selectedHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase);
            }

            void Suggest(Uri requested)
            {
                // URL tokens from a redirect belong to the original request. Offer its origin only.
                address.Text = requested.GetLeftPart(UriPartial.Authority) + "/";
                status.Text = "La box propose une autre adresse. Cliquez sur Ouvrir pour vérifier qu’elle appartient au réseau sélectionné.";
            }

            void OnStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if (e.IsRedirected && e.NavigationId != navigationId) { e.Cancel = true; return; }
                Uri target;
                ++navigationNumber;
                ready = false;
                finishSaved = false;
                RefreshButtons();
                httpAlternative = null;
                http.Visibility = Visibility.Collapsed;
                if (!TryWebAddress(e.Uri, out target) || !Selected(target))
                {
                    e.Cancel = true;
                    navigationId = 0;
                    if (TryWebAddress(e.Uri, out target)) Suggest(target);
                    else status.Text = "Cette navigation quitte l’interface locale de la box. " + PasteHelp;
                    return;
                }
                navigationId = e.NavigationId;
                address.Text = target.AbsoluteUri;
                status.Text = "Ouverture de l’interface de la box…";
            }

            void OnCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (closed || e.NavigationId != navigationId) return;
                if (e.IsSuccess)
                {
                    ready = true;
                    status.Text = "Ouvrez la liste des appareils, puis cliquez sur Lire la liste affichée. Pour une liste paginée, ajoutez chaque page.";
                }
                else
                {
                    Uri attempted;
                    if (TryWebAddress(address.Text, out attempted) && attempted.Scheme == Uri.UriSchemeHttps &&
                        (e.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect || e.WebErrorStatus == CoreWebView2WebErrorStatus.Timeout ||
                         e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionReset || e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted))
                    {
                        httpAlternative = new UriBuilder(Uri.UriSchemeHttp, attempted.IdnHost).Uri;
                        http.Visibility = Visibility.Visible;
                    }
                    status.Text = "La page de la box n’a pas pu s’ouvrir. Vérifiez son adresse et le protocole HTTP ou HTTPS. " + PasteHelp;
                }
                RefreshButtons();
            }

            void RefreshButtons()
            {
                add.IsEnabled = ready && !reading && !opening;
                read.IsEnabled = !reading && !opening && (ready || pages.Count > 0);
                read.Content = pages.Count == 0 ? "Lire la liste affichée" : "Terminer la lecture";
            }

            async Task ReadAsync(bool finish)
            {
                if (reading || opening) return;
                reading = true;
                RefreshButtons();
                int version = navigationNumber;
                try
                {
                    if (ready && !(finish && finishSaved))
                    {
                        var core = browser.CoreWebView2;
                        Uri current;
                        bool trusted = TryWebAddress(core.Source, out current) && Selected(current) && await VerifyAsync(network, current);
                        if (closed || version != navigationNumber) return;
                        if (!trusted)
                        {
                            ready = false;
                            if (!closed) status.Text = "L’origine de la page ne correspond plus à l’interface locale choisie. " + PasteHelp;
                            return;
                        }
                        string json = await core.ExecuteScriptAsync(CaptureScript(current));
                        if (closed || version != navigationNumber) return;
                        Uri after;
                        if (!TryWebAddress(core.Source, out after) || current.GetLeftPart(UriPartial.Authority) != after.GetLeftPart(UriPartial.Authority)) return;
                        if (json == "false")
                        {
                            finishSaved = pages.Count > 0;
                            status.Text = "La page dépasse la limite de lecture. Réduisez le nombre d’appareils affichés par page ou collez la liste depuis votre navigateur.";
                            if (finishSaved) status.Text += " Terminer utilisera les pages déjà ajoutées.";
                            return;
                        }
                        string page = DecodePage(json);
                        if (!string.IsNullOrWhiteSpace(page) && !pages.Contains(page))
                        {
                            if (pages.Sum(p => p.Length + 2) + page.Length > 500000)
                            {
                                status.Text = "La limite de lecture est atteinte. Cliquez sur Terminer pour utiliser les pages déjà ajoutées.";
                                finishSaved = true;
                                return;
                            }
                            pages.Add(page);
                            finishSaved = false;
                        }
                        else if (string.IsNullOrWhiteSpace(page) && (!finish || pages.Count == 0))
                        {
                            status.Text = "Aucun appareil lisible sur cette page. Ouvrez la liste des appareils connectés. " + PasteHelp;
                            return;
                        }
                    }
                    if (finish && pages.Count > 0)
                    {
                        Result = string.Join("\n\n", pages);
                        Close();
                    }
                    else status.Text = pages.Count + (pages.Count == 1 ? " page ajoutée." : " pages ajoutées.") +
                        " Passez à la page suivante dans la box ou terminez la lecture.";
                }
                catch (Exception) { if (!closed) status.Text = "La lecture de la page a échoué. " + PasteHelp; }
                finally { reading = false; if (!closed) RefreshButtons(); }
            }

            void CleanProfile()
            {
                // Dispose can precede the release of Edge's files. Let the browser finish closing.
                if (!Directory.Exists(profile)) return;
                var cleanup = new Thread(delegate() {
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            var dir = new DirectoryInfo(profile);
                            Guid id;
                            const string prefix = "AtelierPC-box-";
                            if (!dir.Exists || !dir.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                                !Guid.TryParseExact(dir.Name.Substring(prefix.Length), "N", out id) ||
                                !string.Equals(dir.Parent.FullName.TrimEnd('\\'), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                                (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;
                            dir.Delete(true);
                            return;
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        Thread.Sleep(250);
                    }
                });
                cleanup.Name = "Fermeture du profil temporaire de la box";
                cleanup.Start();
            }
        }
    }
}
