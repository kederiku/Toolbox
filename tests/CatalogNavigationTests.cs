using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckCatalogNavigation()
        {
            var window = new Shell(() => new List<Device>()).Window;
            try
            {
                ShowNavigationWindow(window);
                var catalog = (FrameworkElement)window.FindName("OtherPage");
                var cards = (FrameworkElement)window.FindName("CatalogItems");
                var search = (TextBox)window.FindName("CatalogSearch");
                var back = window.FindName("BackNav") as Button;
                Check(catalog.IsVisible, "Le catalogue des outils est visible dès l’ouverture");
                Check(back != null && !back.IsEnabled, "Le catalogue initial n’a pas de page précédente");
                Action<string> click = name => ClickVisible((Button)window.FindName(name));
                Func<int> cardCount = () => Descendants(cards).OfType<Button>().Count(b => b.IsVisible);
                Check(cardCount() == 14, "Les quatorze outils peuvent être ouverts depuis le catalogue initial");
                Check(AutomationProperties.GetName(FindAction(cards, "Ouvrir le diagnostic")) == "Diagnostic des périphériques",
                    "Le nom accessible d’une carte reprend son titre visible");
                foreach (var group in new[] {
                    new { Filter = "CatalogHardware", Count = 2, Tool = "Ouvrir la carte USB" },
                    new { Filter = "CatalogNetwork", Count = 3, Tool = "Ouvrir le dépannage réseau" },
                    new { Filter = "CatalogSystem", Count = 2, Tool = "Ouvrir les vérifications" },
                    new { Filter = "CatalogFiles", Count = 7, Tool = "Télécharger une vidéo YouTube" }
                })
                {
                    var filter = (RadioButton)window.FindName(group.Filter);
                    Check(filter.IsVisible, "Famille accessible dans le catalogue : " + filter.Content);
                    filter.IsChecked = true;
                    Check(cardCount() == group.Count && FindAction(cards, group.Tool) != null,
                        "La famille limite les résultats aux outils correspondants : " + filter.Content);
                }
                search.Text = "aucun-outil-correspondant";
                UpdateNavigationLayout(window);
                Check(cardCount() == 0 && ((TextBlock)window.FindName("CatalogEmpty")).IsVisible,
                    "Une recherche sans correspondance présente l’état vide");
                click("CatalogReset");
                Check(search.Text.Length == 0 && cardCount() == 14 && ((RadioButton)window.FindName("CatalogAll")).IsChecked == true,
                    "Afficher tous les outils enlève la recherche et le filtre de famille");
                ((RadioButton)window.FindName("CatalogFiles")).IsChecked = true;
                search.Text = "youtube";
                Check(cardCount() == 1, "La recherche YouTube identifie une seule carte dans Fichiers");
                ClickVisible(FindAction(cards, "Télécharger une vidéo YouTube"));
                var youtube = (FrameworkElement)window.FindName("YoutubeView");
                Check(youtube.IsVisible && !((FrameworkElement)window.FindName("VideoView")).IsVisible,
                    "La carte YouTube ouvre sa page dédiée");
                click("BackNav");
                OpenFilesToolFromMenu(window, "YoutubeNav");
                Check(youtube.IsVisible, "Le menu Fichiers ouvre la même page YouTube");
                click("BackNav");
                search.Clear();

                window.Width = 470;
                window.Height = 400;
                UpdateNavigationLayout(window);
                var sidebar = (FrameworkElement)window.FindName("Sidebar");
                Check(!sidebar.IsVisible, "Le menu compact laisse toute la largeur au catalogue");
                click("MenuToggle");
                Check(sidebar.IsVisible && ((Button)window.FindName("HomeNav")).IsVisible,
                    "Le bouton Menu révèle les destinations en fenêtre étroite");
                var hardwareMenu = (ToggleButton)window.FindName("MenuFamilyMateriel");
                var fileMenu = (ToggleButton)window.FindName("MenuFamilyFichiers");
                hardwareMenu.IsChecked = true;
                fileMenu.IsChecked = true;
                Check(hardwareMenu.IsChecked == false && ((Button)window.FindName("ImageNav")).IsVisible
                    && !((Button)window.FindName("MapNav")).IsVisible, "Ouvrir une famille replie la précédente dans le menu");
                click("MenuClose");
                Check(!sidebar.IsVisible && catalog.IsVisible, "Fermer le menu compact retrouve la page en cours");
                click("MenuToggle");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                    Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(escape);
                Check(escape.Handled && !sidebar.IsVisible, "Échap ferme le menu compact au clavier");

                search.Text = "convertir";
                var video = FindAction(cards, "Convertir une vidéo");
                Check(cardCount() > 1 && cardCount() < 4 && video != null,
                    "Le texte et la famille Fichiers filtrent ensemble les résultats");
                var scroll = (ScrollViewer)window.FindName("Scroll");
                UpdateNavigationLayout(window);
                scroll.ScrollToEnd();
                UpdateNavigationLayout(window);
                var offset = scroll.VerticalOffset;
                Check(offset > 0, "La consultation du catalogue en fenêtre étroite permet de défiler");
                click("SearchNav");
                UpdateNavigationLayout(window);
                Check(((RadioButton)window.FindName("CatalogFiles")).IsChecked == true && search.Text == "convertir" && scroll.VerticalOffset == 0,
                    "Rejoindre la recherche du catalogue conserve la famille et le texte");
                scroll.ScrollToEnd();
                UpdateNavigationLayout(window);
                ClickVisible(video);
                Check(((FrameworkElement)window.FindName("VideoView")).IsVisible && back.IsEnabled,
                    "La carte ouvre le convertisseur et autorise le retour");
                click("BackNav");
                UpdateNavigationLayout(window);
                Check(catalog.IsVisible && search.Text == "convertir"
                    && ((RadioButton)window.FindName("CatalogFiles")).IsChecked == true
                    && Math.Abs(scroll.VerticalOffset - offset) < 2, "Retour restaure le texte, la famille et la position de lecture");
                Check(!back.IsEnabled, "Les changements de filtre et l’ouverture du menu ne créent pas d’étapes de retour");

                ClickVisible(FindAction(cards, "Convertir une vidéo"));
                click("CrumbHome");
                Check(catalog.IsVisible && search.Text.Length == 0 && cardCount() == 14,
                    "La commande du catalogue complet réinitialise la recherche et la famille");
                search.Text = "EXIF";
                click("BackNav");
                Check(((FrameworkElement)window.FindName("VideoView")).IsVisible,
                    "Le retour depuis une deuxième recherche retrouve le dernier outil");
                click("BackNav");
                UpdateNavigationLayout(window);
                Check(catalog.IsVisible && search.Text == "convertir"
                    && ((RadioButton)window.FindName("CatalogFiles")).IsChecked == true
                    && Math.Abs(scroll.VerticalOffset - offset) < 2,
                    "Une recherche ultérieure ne modifie pas le catalogue mémorisé dans l’historique");
                click("MenuToggle");
                click("StartNav");
                var guided = (FrameworkElement)window.FindName("StartView");
                Check(guided.IsVisible && !sidebar.IsVisible, "Choisir Dépannage guidé ferme le menu compact");
                click("MenuToggle");
                click("StartNav");
                click("BackNav");
                Check(catalog.IsVisible, "Choisir deux fois la même page ne duplique pas l’historique");
            }
            finally { window.Close(); }
        }

        static void ShowNavigationWindow(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Width = 1100;
            window.Height = 690;
            window.Show();
            UpdateNavigationLayout(window);
        }

        static void UpdateNavigationLayout(Window window)
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate {}));
        }

        static void ClickVisible(Button button)
        {
            Check(button != null && button.IsVisible && button.IsEnabled,
                "Commande accessible : " + (button == null ? "introuvable" : AutomationProperties.GetName(button)));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        static void OpenFilesToolFromMenu(Window window, string name)
        {
            var family = (ToggleButton)window.FindName("MenuFamilyFichiers");
            Check(family.IsVisible, "La famille Fichiers est disponible dans le menu");
            if (family.IsChecked != true) family.IsChecked = true;
            var tool = (Button)window.FindName(name);
            ClickVisible(tool);
        }
    }
}
