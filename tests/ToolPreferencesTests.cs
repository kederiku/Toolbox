using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckToolPreferences()
        {
            string path = Path.Combine(Path.GetTempPath(), "Toolbox-preferences-" + Guid.NewGuid() + ".ini");
            var window = new Shell(() => new List<Device>(), toolPreferencesPath: path).Window;
            try
            {
                ShowNavigationWindow(window);
                var settings = window.FindName("SettingsNav") as Button;
                Check(settings != null && settings.IsVisible, "Les paramètres sont accessibles depuis le menu latéral");
                ClickVisible(settings);
                var grid = (UniformGrid)window.FindName("CatalogGrid");
                var original = grid.Children.Cast<Button>().ToArray();
                var video = FindAction(grid, "Convertir une vidéo");
                var speed = FindAction(grid, "Ouvrir le test de débit");
                var options = Descendants((StackPanel)window.FindName("SettingsView")).OfType<CheckBox>().ToArray();
                var menuOptions = options.Where(box => box.Name.StartsWith("Show")).ToArray();
                Check(menuOptions.Length == original.Length && options.Length == 2 * original.Length
                    && options.All(box => box.IsChecked == box.Name.StartsWith("Show")),
                    "Chaque outil commence visible dans le menu et sans favori");

                ((CheckBox)window.FindName("FavoriteVideoNav")).IsChecked = true;
                ((CheckBox)window.FindName("FavoriteSpeedNav")).IsChecked = true;
                foreach (var box in menuOptions) box.IsChecked = false;
                Check(!((ToggleButton)window.FindName("MenuFamilyFichiers")).IsVisible && settings.IsVisible,
                    "Une famille vide disparaît et les paramètres restent accessibles");
                ClickVisible((Button)window.FindName("HomeNav"));
                Check(grid.Children.Cast<Button>().SequenceEqual(new[] { speed, video }.Concat(original.Except(new[] { speed, video }))),
                    "Les favoris précèdent les autres outils dans l’ordre du catalogue, sans doublon");
                Check(AutomationProperties.GetItemStatus(video) == "Favori", "Le statut favori est accessible sur la carte");
                ((RadioButton)window.FindName("CatalogFiles")).IsChecked = true;
                Check(grid.Children[0] == video, "Les favoris restent en tête de leur famille");
                ((TextBox)window.FindName("CatalogSearch")).Text = "codec";
                Check(grid.Children.Contains(video), "Un outil masqué reste dans la recherche");
                ClickVisible(video);
                Check(((FrameworkElement)window.FindName("VideoView")).IsVisible
                    && !((ToggleButton)window.FindName("MenuFamilyFichiers")).IsVisible,
                    "Un outil masqué s’ouvre depuis sa carte sans réafficher sa famille");
                ClickVisible((Button)window.FindName("BackNav"));
                Check(((TextBox)window.FindName("CatalogSearch")).Text == "codec", "Retour conserve la recherche");
                window.Close();

                window = new Shell(() => new List<Device>(), toolPreferencesPath: path).Window;
                ShowNavigationWindow(window);
                grid = (UniformGrid)window.FindName("CatalogGrid");
                Check(grid.Children[0] == FindAction(grid, "Ouvrir le test de débit")
                    && !((ToggleButton)window.FindName("MenuFamilyFichiers")).IsVisible,
                    "Les favoris et le menu sont restaurés dès le redémarrage");
                window.Width = 450;
                UpdateNavigationLayout(window);
                ClickVisible((Button)window.FindName("MenuToggle"));
                ClickVisible((Button)window.FindName("SettingsNav"));
                var videoMenu = (CheckBox)window.FindName("ShowVideoNav");
                var videoFavorite = (CheckBox)window.FindName("FavoriteVideoNav");
                Check(videoMenu.IsChecked == false && videoFavorite.IsChecked == true,
                    "Les réglages du menu et des favoris sont indépendants et accessibles en fenêtre étroite");
                videoMenu.IsChecked = true;
                videoFavorite.IsChecked = false;
                ((CheckBox)window.FindName("FavoriteSpeedNav")).IsChecked = false;
                ClickVisible((Button)window.FindName("MenuToggle"));
                OpenFilesToolFromMenu(window, "VideoNav");
                ClickVisible((Button)window.FindName("MenuToggle"));
                ClickVisible((Button)window.FindName("HomeNav"));
                Check(grid.Children.Cast<Button>().Select(b => b.Content).SequenceEqual(original.Select(b => b.Content)),
                    "Retirer les favoris restaure l’ordre initial");
            }
            finally
            {
                window.Close();
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
