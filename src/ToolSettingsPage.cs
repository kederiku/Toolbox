using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        readonly ToolPreferences toolPreferences;

        NavigationPage CreateSettingsPage(NavigationPage[] tools)
        {
            var view = Get<StackPanel>("SettingsView");
            var page = new NavigationPage("SettingsNav", view, "Paramètres", "\uE713",
                "Choisissez les outils du menu latéral. Les favoris apparaissent en premier sur la page d’accueil.");
            foreach (var family in tools.GroupBy(CatalogFamily))
            {
                var rows = new StackPanel();
                var header = SettingsRow(Text(family.Key, 18, true, "TextBrush"));
                AddSettingsColumn(header, Text("Menu latéral", 13, true, "MutedBrush"), 1);
                AddSettingsColumn(header, Text("Favori", 13, true, "MutedBrush"), 2);
                header.Margin = new Thickness(0, 0, 0, 7);
                rows.Children.Add(header);
                foreach (var tool in family)
                {
                    var row = SettingsRow(Text(tool.Title, 14, false, "TextBrush"));
                    var menu = PreferenceBox(tool, ToolPreference.HiddenFromMenu, tools);
                    AddSettingsColumn(row, menu, 1);
                    AddSettingsColumn(row, PreferenceBox(tool, ToolPreference.Favorite, tools), 2);
                    if (page.PrimaryAction == null) page.PrimaryAction = menu;
                    rows.Children.Add(new Border { BorderBrush = Ink("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
                        Padding = new Thickness(0, 3, 0, 3), Child = row });
                }
                var card = Card(rows);
                card.Margin = new Thickness(0, 0, 0, 12);
                view.Children.Add(card);
            }
            return page;
        }

        static Grid SettingsRow(TextBlock title)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(61) });
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(title);
            return row;
        }

        static void AddSettingsColumn(Grid row, FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(element);
        }

        CheckBox PreferenceBox(NavigationPage tool, ToolPreference option, NavigationPage[] tools)
        {
            bool menu = option == ToolPreference.HiddenFromMenu;
            var box = new CheckBox {
                Name = (menu ? "Show" : "Favorite") + tool.NavName,
                IsChecked = menu ? !tool.HiddenFromMenu : tool.IsFavorite,
                Padding = new Thickness(2), MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = (Style)Window.FindResource("FocusRing")
            };
            string label = menu ? "Afficher dans le menu latéral" : "Ajouter aux favoris";
            box.ToolTip = label + " · " + tool.Title;
            AutomationProperties.SetName(box, (string)box.ToolTip);
            AutomationProperties.SetAutomationId(box, box.Name);
            Window.RegisterName(box.Name, box);
            RoutedEventHandler changed = delegate {
                bool selected = box.IsChecked == true;
                bool previous = menu ? !tool.HiddenFromMenu : tool.IsFavorite;
                if (selected == previous) return;
                var notice = Get<TextBlock>("SettingsNotice");
                try { toolPreferences.Write(tool.NavName, option, menu ? !selected : selected); }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)) throw;
                    box.IsChecked = previous;
                    notice.Text = "Impossible d’enregistrer ce choix pour « " + tool.Title + " ». Réessayez après avoir vérifié l’accès à votre dossier de paramètres.";
                    notice.Visibility = Visibility.Visible;
                    notice.BringIntoView();
                    return;
                }
                notice.Visibility = Visibility.Collapsed;
                if (menu)
                {
                    tool.HiddenFromMenu = !selected;
                    ApplySidebarVisibility(tools);
                }
                else
                {
                    tool.IsFavorite = selected;
                    FilterCatalog();
                }
            };
            box.Checked += changed;
            box.Unchecked += changed;
            return box;
        }
    }
}
