using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        sealed class Visit
        {
            internal NavigationPage Page, Tool;
            internal string Selection, Query, Family;
            internal double Offset;
        }

        NavigationPage currentPage, reportPage, catalogPage;
        string currentSelection;
        readonly Stack<Visit> history = new Stack<Visit>();
        readonly Dictionary<string, ToggleButton> familyToggles = new Dictionary<string, ToggleButton>();
        TextBlock familyHeading;
        bool compactMenu, menuOpen;

        void InitializeNavigation()
        {
            var guided = new NavigationPage("StartNav", Get<StackPanel>("StartView"), "Dépannage guidé", "\uE90F",
                "Choisissez un symptôme pour ouvrir le contrôle adapté à votre PC.");
            reportPage = new NavigationPage("ReportNav", Get<StackPanel>("ReportView"), "Rapport", "\uE9F9",
                "Retrouvez les contrôles et les actions de cette session dans l’ordre de leur réalisation.") {
                    Title = "Rapport d’intervention"
                };
            catalogPage = new NavigationPage("HomeNav", Get<StackPanel>("OtherPage"), "Catalogue", "\uECA5",
                "Accédez aux outils de diagnostic, de conversion et de confidentialité.") { Title = "Catalogue d’outils" };
            var help = new NavigationPage("HelpNav", Get<StackPanel>("HelpPage"), "Guide d’utilisation", "\uE897",
                "Choisissez une question pour afficher la réponse.");
            var tools = CreateToolCatalog();
            foreach (var tool in tools)
            {
                tool.HiddenFromMenu = toolPreferences.Read(tool.NavName, ToolPreference.HiddenFromMenu);
                tool.IsFavorite = toolPreferences.Read(tool.NavName, ToolPreference.Favorite);
            }
            var sidebar = Get<StackPanel>("SidebarLinks");
            AddSidebarLink(sidebar, catalogPage);
            AddSidebarLink(sidebar, guided);
            familyHeading = Text("Familles d’outils", 12, true, "#B3C0D9");
            familyHeading.Margin = new Thickness(12, 24, 0, 6);
            sidebar.Children.Add(familyHeading);
            foreach (var group in tools.GroupBy(CatalogFamily))
                AddMenuFamily(sidebar, group);
            var footer = Get<StackPanel>("SidebarFooter");
            AddSidebarLink(footer, CreateSettingsPage(tools));
            AddSidebarLink(footer, reportPage);
            AddSidebarLink(footer, help);
            ApplySidebarVisibility(tools);

            BuildStartView(tools, catalogPage);
            InitializeCatalogSearch(tools, catalogPage);
            BuildGuide((StackPanel)help.View);
            Get<Button>("CrumbHome").Click += async delegate { await OpenPageAsync(catalogPage); };
            Get<Button>("BackNav").Click += async delegate { await GoBackAsync(); };
            Get<Button>("SearchNav").Click += async delegate { await SearchAsync(); };
            Get<Button>("MenuToggle").Click += delegate { SetMenuOpen(true, true); };
            Get<Button>("MenuClose").Click += delegate { SetMenuOpen(false, true); };
            Get<Button>("MenuShade").Click += delegate { SetMenuOpen(false, true); };
            Get<ScrollViewer>("Scroll").ScrollChanged += delegate {
                if (currentPage != catalogPage || !catalogPage.View.IsVisible) return;
                var search = Get<TextBox>("CatalogSearch");
                bool aboveViewport = search.TranslatePoint(new Point(0, search.ActualHeight), Get<ScrollViewer>("Scroll")).Y < 0;
                Get<Button>("SearchNav").Visibility = aboveViewport ? Visibility.Visible : Visibility.Collapsed;
                Get<StackPanel>("CatalogAccess").Visibility = aboveViewport ? Visibility.Collapsed : Visibility.Visible;
            };
            Window.PreviewKeyDown += async delegate(object sender, KeyEventArgs e) {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape && menuOpen)
                {
                    e.Handled = true;
                    SetMenuOpen(false, true);
                }
                else if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
                {
                    e.Handled = true;
                    if (menuOpen) SetMenuOpen(false, true);
                    else await GoBackAsync();
                }
                else if (key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    await SearchAsync();
                }
            };
            Window.Loaded += delegate { if (currentPage == catalogPage) FocusCatalogSearch(); };
            Window.Closed += delegate { if (currentPage.OnLeave != null) currentPage.OnLeave(); };
            ShowPage(catalogPage);
        }

        void AddMenuFamily(StackPanel sidebar, IGrouping<string, NavigationPage> group)
        {
            string family = group.Key;
            string key = family == "Matériel" ? "Materiel" : family == "Réseau" ? "Reseau" : family == "Système" ? "Systeme" : "Fichiers";
            string icon = family == "Matériel" ? "\uE772" : family == "Réseau" ? "\uE968" : family == "Système" ? "\uE770" : "\uE8B7";
            var header = new ToggleButton { Name = "MenuFamily" + key, Content = MenuLabel(icon, family),
                Style = (Style)Get<Border>("Sidebar").FindResource("FamilyToggle") };
            AutomationProperties.SetName(header, family);
            AutomationProperties.SetAutomationId(header, header.Name);
            Window.RegisterName(header.Name, header);
            familyToggles.Add(family, header);
            sidebar.Children.Add(header);
            var items = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(15, 0, 0, 3) };
            sidebar.Children.Add(items);
            foreach (var tool in group)
            {
                var label = family == "Fichiers" && tool.Category != family
                    ? (tool.Category == "Confidentialité" ? "Métadonnées" : tool.Category) : tool.Label;
                AddSidebarLink(items, tool, label);
            }
            header.Checked += delegate {
                foreach (var other in familyToggles.Values)
                    if (other != header && other.IsChecked == true) other.IsChecked = false;
                items.Visibility = Visibility.Visible;
            };
            header.Unchecked += delegate { items.Visibility = Visibility.Collapsed; };
        }

        void ApplySidebarVisibility(NavigationPage[] tools)
        {
            foreach (var group in tools.GroupBy(CatalogFamily))
            {
                foreach (var tool in group)
                    Get<Button>(tool.NavName).Visibility = tool.HiddenFromMenu ? Visibility.Collapsed : Visibility.Visible;
                var header = familyToggles[group.Key];
                bool visible = group.Any(tool => !tool.HiddenFromMenu);
                if (!visible) header.IsChecked = false;
                header.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            familyHeading.Visibility = tools.Any(tool => !tool.HiddenFromMenu) ? Visibility.Visible : Visibility.Collapsed;
        }

        DockPanel MenuLabel(string icon, string label)
        {
            var content = new DockPanel();
            content.Children.Add(new TextBlock { Text = icon, Width = 20, Style = (Style)Window.FindResource("Glyph") });
            content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            return content;
        }

        void AddSidebarLink(StackPanel parent, NavigationPage target, string menuLabel = null)
        {
            target.View.Visibility = Visibility.Collapsed;
            var button = new Button { Name = target.NavName, Content = MenuLabel(target.Icon, menuLabel ?? target.Label), ToolTip = target.Title,
                Style = (Style)Window.FindResource("Nav"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, target.Title);
            AutomationProperties.SetAutomationId(button, target.NavName);
            button.Click += async delegate { await OpenPageAsync(target); };
            Window.RegisterName(target.NavName, button);
            parent.Children.Add(button);
        }

        void ResizeMenu(double width)
        {
            bool compact = width < 875;
            var sidebar = Get<Border>("Sidebar");
            var toggle = Get<Button>("MenuToggle");
            bool moveFocus = compact && !compactMenu && sidebar.IsKeyboardFocusWithin;
            compactMenu = compact;
            Grid.SetColumnSpan(sidebar, compact ? 2 : 1);
            sidebar.Width = compact ? 270 : 222;
            Get<ColumnDefinition>("RailWidth").Width = new GridLength(compact ? 0 : sidebar.Width);
            toggle.Visibility = Get<Button>("MenuClose").Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            SetMenuOpen(compact && menuOpen, false);
            if (moveFocus) toggle.Focus();
        }

        void SetMenuOpen(bool open, bool moveFocus)
        {
            menuOpen = compactMenu && open;
            var sidebar = Get<Border>("Sidebar");
            sidebar.Visibility = !compactMenu || menuOpen ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("MenuShade").Visibility = menuOpen ? Visibility.Visible : Visibility.Collapsed;
            Get<Grid>("Workspace").IsEnabled = !menuOpen;
            KeyboardNavigation.SetTabNavigation(sidebar, menuOpen ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
            if (moveFocus && compactMenu)
                Get<Button>(menuOpen ? "MenuClose" : "MenuToggle").Focus();
        }

        async Task SearchAsync()
        {
            if (currentPage != catalogPage) await OpenPageAsync(catalogPage);
            else if (menuOpen) SetMenuOpen(false, false);
            FocusCatalogSearch();
            Get<ScrollViewer>("Scroll").ScrollToTop();
        }

        Task OpenPageAsync(NavigationPage target, string selection = null)
        {
            if (currentPage != target)
            {
                var scroll = Get<ScrollViewer>("Scroll");
                var visit = new Visit { Page = currentPage, Selection = currentSelection, Offset = scroll.VerticalOffset };
                if (currentPage == catalogPage)
                {
                    visit.Query = Get<TextBox>("CatalogSearch").Text;
                    visit.Family = SelectedCatalogFamily;
                    visit.Tool = target.Category == null ? null : target;
                }
                history.Push(visit);
            }
            return ActivatePageAsync(target, selection, null);
        }

        Task GoBackAsync()
        {
            if (history.Count == 0) return Task.CompletedTask;
            var visit = history.Pop();
            return ActivatePageAsync(visit.Page, visit.Selection, visit);
        }

        async Task ActivatePageAsync(NavigationPage target, string selection, Visit visit)
        {
            if (menuOpen) SetMenuOpen(false, false);
            ShowPage(target);
            currentSelection = selection;
            if (target == catalogPage)
            {
                if (visit == null) ResetCatalog();
                else
                {
                    SetCatalogFamily(visit.Family);
                    Get<TextBox>("CatalogSearch").Text = visit.Query;
                }
                if (visit != null && visit.Tool != null) FocusCatalogTool(visit.Tool);
                else FocusCatalogSearch();
            }
            else
            {
                if (target.PrimaryAction == null || !target.PrimaryAction.Focus())
                    Get<TextBlock>("PageTitle").Focus();
            }
            var scroll = Get<ScrollViewer>("Scroll");
            if (visit != null) scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(visit == null ? 0 : visit.Offset);
            if (target.OnOpen != null) await target.OnOpen(selection);
        }

        void ShowPage(NavigationPage target)
        {
            if (currentPage != target)
            {
                if (currentPage != null)
                {
                    if (currentPage.OnLeave != null) currentPage.OnLeave();
                    currentPage.View.Visibility = Visibility.Collapsed;
                    var previous = Get<Button>(currentPage.NavName);
                    previous.ClearValue(AutomationProperties.ItemStatusProperty);
                    previous.ClearValue(Control.BackgroundProperty);
                    previous.ClearValue(Control.ForegroundProperty);
                }
                currentPage = target;
                target.View.Visibility = Visibility.Visible;
                if (target.Category != null && !target.HiddenFromMenu) familyToggles[CatalogFamily(target)].IsChecked = true;
                var selected = Get<Button>(target.NavName);
                AutomationProperties.SetItemStatus(selected, "Page actuelle");
                selected.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                selected.Foreground = Brushes.White;
                if (selected.IsVisible) selected.BringIntoView();
                bool catalog = target == catalogPage;
                var pageElements = catalog ? Visibility.Collapsed : Visibility.Visible;
                Get<StackPanel>("CatalogAccess").Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
                Get<TextBlock>("Crumb").Text = target.Label;
                Get<DockPanel>("CrumbTrail").Visibility = pageElements;
                Get<TextBlock>("PageTitle").Text = target.Title;
                Get<TextBlock>("PageTitle").Visibility = pageElements;
                Get<TextBlock>("PageIntro").Text = target.Description;
                Get<TextBlock>("PageIntro").Visibility = pageElements;
                Get<Button>("SearchNav").Visibility = pageElements;
            }
            Get<Button>("BackNav").IsEnabled = history.Count > 0;
        }

        void BuildGuide(StackPanel guide)
        {
            AddHelp(guide, "Comment retrouver un outil ?", "Le catalogue s’ouvre au démarrage. Choisissez une famille ou saisissez un nom, un format ou un usage. Depuis un outil, Ctrl+K ouvre la recherche. Retour (Alt+Gauche) vous ramène à votre sélection.");
            AddHelp(guide, "Par où commencer pour dépanner le PC ?", "Ouvrez Dépannage guidé dans le menu et choisissez le symptôme. Pour un PC lent, lancez la mesure pendant le ralentissement. Pour Internet, le diagnostic recherche une panne de connexion et le test de débit mesure la vitesse.");
            AddHelp(guide, "Comment conserver le rapport d’intervention ?", "Le rapport rassemble les contrôles de la session et leurs résultats. Ouvrez Rapport et copiez son contenu avant de fermer Toolbox.");
            AddHelp(guide, "Comment identifier une panne USB ?", "Dans Matériel, ouvrez Carte USB et sélectionnez l’accessoire. Sa fiche indique les erreurs de ses fonctions et ses ports COM actuels. Pour une coupure intermittente, ouvrez Activité des branchements. Le suivi continue sur les autres pages jusqu’à sa suspension ou la fermeture.");
            AddHelp(guide, "Quand utiliser les droits administrateur ?", "Dépannage guidé indique vos droits actuels. Dans Intégrité de Windows, choisissez la vérification souhaitée et lisez le résultat dans la console Windows. Une réparation nécessite une confirmation et peut demander une autorisation Windows.");
            AddHelp(guide, "Comment analyser un périphérique ?", "Branchez et allumez l’appareil puis ouvrez Périphériques et cliquez sur « Lancer l’analyse ». Dans les résultats, retrouvez le nom de l’appareil et ouvrez « Étapes de résolution ». Après avoir suivi les étapes, relancez l’analyse.");
            AddHelp(guide, "Quand utiliser la réactivation ?", "Pour remettre en service un appareil désactivé, choisissez « Réactiver ». Windows peut demander l’autorisation d’un administrateur. Toolbox vérifie ensuite son nouvel état.");
            AddHelp(guide, "L’analyse est normale mais l’appareil fonctionne mal", "Un câble défectueux, un mauvais réglage ou une panne intermittente peuvent échapper au contrôle de Windows. Vérifiez le branchement et essayez l’appareil dans une autre application.");
            guide.Children.Add(ActionButton("Ouvrir le diagnostic", async delegate { await OpenPageAsync(diagnosticPage); }));
        }

        void AddHelp(StackPanel container, string title, string body)
        {
            var paragraph = Text(body, 14, false, "MutedBrush");
            paragraph.Margin = new Thickness(26, 7, 4, 2);
            var question = new Expander { Header = Text(title, 16, true, "TextBrush"), Content = paragraph };
            AutomationProperties.SetName(question, title);
            var card = Card(question);
            card.Padding = new Thickness(15, 10, 15, 10);
            card.Margin = new Thickness(0, 0, 0, 7);
            container.Children.Add(card);
        }
    }
}
