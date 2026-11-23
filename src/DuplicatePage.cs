using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        NavigationPage CreateDuplicatePage()
        {
            var page = new DuplicatePage(Window);
            return new NavigationPage("DuplicateNav", Get<StackPanel>("DuplicateView"), "Rechercher les doublons", "\uE8C8",
                "Recherchez les doublons exacts et les photos visuellement proches dans un dossier et ses sous-dossiers.") {
                Category = "Fichiers", OpenLabel = "Ouvrir la recherche de doublons",
                PrimaryAction = Get<Button>("DuplicateChoose"), OnLeave = page.Leave
            };
        }
    }

    internal sealed class DuplicatePage
    {
        static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
        readonly Window window;
        readonly TextBox folder;
        readonly ListBox categories, groups;
        readonly UniformGrid cards;
        readonly Button start, cancel, review;
        readonly Func<string, CancellationToken, Action<string>, DuplicateScanResult> search;
        readonly Func<IEnumerable<DuplicateGroup>, ISet<string>, CancellationToken, DuplicateRecycleResult> recycle;
        List<DuplicateGroup> reviewedGroups;
        HashSet<string> reviewedPaths;
        DuplicateScanResult scan;
        CancellationTokenSource operation;
        bool closeAfterOperation;

        sealed class Category
        {
            internal readonly List<DuplicateGroup> Groups;
            internal readonly bool Similar;
            internal readonly HashSet<string> Marked = new HashSet<string>(StringComparer.Ordinal);
            internal decimal MarkedBytes;
            internal int GroupIndex;
            internal Category(List<DuplicateGroup> groups, bool similar) { Groups = groups; Similar = similar; }
            internal string Name { get { return Similar ? "Photos visuellement proches" : "Doublons exacts"; } }
            public string Label { get { return Name + " · " + Count(Groups.Count, "groupe", "groupes"); } }
            internal string Total { get { return Count(Marked.Count, "fichier", "fichiers") + " · " + Size(MarkedBytes); } }
        }

        Category Active { get { return categories.SelectedItem as Category; } }

        sealed class GroupChoice
        {
            internal DuplicateGroup Group;
            internal bool Similar;
            public string Name { get { return Path.GetFileName(Group.Files[0].FullPath); } }
            public string ListName { get { return CardName(Name, SharedStem(Group.Files)); } }
            public string Formats { get { return string.Join(" + ", Group.Files.Select(f => f.Format).Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).DefaultIfEmpty("Fichiers")); } }
            public string Detail { get { return Similar ? Count(Group.Files.Count, "photo à comparer", "photos à comparer") :
                Count(Group.Files.Count, "exemplaire", "exemplaires"); } }
        }

        internal DuplicatePage(Window window, Func<string, CancellationToken, Action<string>, DuplicateScanResult> search = null,
            Func<IEnumerable<DuplicateGroup>, ISet<string>, CancellationToken, DuplicateRecycleResult> recycle = null)
        {
            this.window = window;
            this.search = search ?? DuplicateScanner.Scan;
            this.recycle = recycle ?? DuplicateRecycler.Apply;
            folder = Get<TextBox>("Folder");
            categories = Get<ListBox>("Categories");
            groups = Get<ListBox>("Groups");
            cards = Get<UniformGrid>("Cards");
            start = Get<Button>("Start");
            cancel = Get<Button>("Cancel");
            review = Get<Button>("Review");
            folder.TextChanged += delegate { ResetResults(); UpdateCommands(); };
            Get<Button>("Choose").Click += delegate {
                string chosen = ConversionBatch.ChooseFolder(window, folder.Text, "Choisissez le dossier où rechercher les doublons.");
                if (chosen != null) folder.Text = chosen;
            };
            start.Click += async delegate { await ScanAsync(); };
            Get<Button>("Restart").Click += async delegate { await ScanAsync(); };
            Get<Button>("EditFolder").Click += delegate {
                Show("Source", true);
                Show("Location", false);
                folder.Focus();
                folder.SelectAll();
            };
            cancel.Click += delegate { CancelOperation(); };
            categories.SelectionChanged += delegate { ShowCategory(); };
            groups.SelectionChanged += delegate { ShowGroup(); };
            review.Click += delegate { ShowReview(); };
            Get<Button>("Modify").Click += delegate { ReturnToComparison(); };
            Get<Button>("Confirm").Click += async delegate { await RecycleAsync(); };
            Get<Grid>("Workspace").SizeChanged += (s, e) => Resize(e.NewSize.Width);
            window.Closing += delegate(object sender, CancelEventArgs e) {
                if (operation == null || e.Cancel) return;
                e.Cancel = true;
                closeAfterOperation = true;
                CancelOperation();
            };
            UpdateCommands();
        }

        T Get<T>(string suffix) where T : FrameworkElement { return (T)window.FindName("Duplicate" + suffix); }

        void Show(string part, bool visible)
        {
            Get<FrameworkElement>(part).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        void SetStatus(string message)
        {
            Get<TextBlock>("Status").Text = message;
            Show("Status", message.Length > 0);
        }

        void ShowIssues(List<string> issues, string label)
        {
            var details = Get<Expander>("Issues");
            details.IsExpanded = false;
            details.Header = label + " (" + issues.Count.ToString(French) + ")";
            Get<TextBlock>("IssueText").Text = string.Join(Environment.NewLine, issues);
            Show("Issues", issues.Count != 0);
        }

        void ResetResults()
        {
            scan = null;
            reviewedGroups = null;
            reviewedPaths = null;
            categories.ItemsSource = null;
            groups.ItemsSource = null;
            cards.Children.Clear();
            Get<StackPanel>("ReviewGroups").Children.Clear();
            Get<TextBlock>("Moved").Text = "";
            Get<TextBlock>("IssueText").Text = "";
            Show("Source", true);
            Show("Compare", false);
            Show("ReviewPane", false);
            Show("Outcome", false);
            Show("Issues", false);
            SetStatus("");
        }

        void UpdateCommands()
        {
            bool idle = operation == null;
            folder.IsEnabled = Get<Button>("Choose").IsEnabled = idle;
            start.IsEnabled = idle && !string.IsNullOrWhiteSpace(folder.Text);
            cancel.IsEnabled = !idle && !operation.IsCancellationRequested;
            Show("Cancel", !idle);
            Get<ProgressBar>("Progress").IsIndeterminate = !idle;
            Show("Progress", !idle);
            review.IsEnabled = idle && Active != null && Active.Marked.Count > 0;
            Get<Button>("Confirm").IsEnabled = idle && reviewedPaths != null;
            Get<Button>("Modify").IsEnabled = idle;
        }

        void CancelOperation()
        {
            if (operation == null || operation.IsCancellationRequested) return;
            operation.Cancel();
            cancel.IsEnabled = false;
            SetStatus("Annulation en cours…");
        }

        internal void Leave()
        {
            CancelOperation();
            if (operation == null && Get<Border>("ReviewPane").Visibility == Visibility.Visible)
                ReturnToComparison();
        }

        void FinishOperation()
        {
            operation.Dispose();
            operation = null;
            UpdateCommands();
            if (closeAfterOperation) window.Dispatcher.BeginInvoke(new Action(window.Close));
        }

        async Task ScanAsync()
        {
            if (operation != null || string.IsNullOrWhiteSpace(folder.Text)) return;
            string root = folder.Text;
            ResetResults();
            var running = new CancellationTokenSource();
            operation = running;
            UpdateCommands();
            SetStatus("Recherche des doublons en cours…");
            Action<string> progress = message => {
                window.Dispatcher.BeginInvoke(new Action(delegate {
                    if (operation == running && !running.IsCancellationRequested) SetStatus(message);
                }));
            };
            try
            {
                var result = await Task.Run(() => search(root, running.Token, progress));
                running.Token.ThrowIfCancellationRequested();
                scan = result;
                var available = new[] { new Category(result.Groups, false), new Category(result.SimilarGroups, true) }
                    .Where(c => c.Groups.Count != 0).ToList();
                string scanned = Count(result.Scanned, "fichier analysé", "fichiers analysés");
                ShowIssues(result.Issues, "Fichiers ou dossiers à vérifier");
                if (available.Count == 0)
                    SetStatus("Aucun groupe de doublons exacts ou de photos visuellement proches trouvé · " + scanned);
                else
                {
                    var location = Get<TextBlock>("LocationText");
                    location.Text = "Dossier analysé · " + root;
                    location.ToolTip = root;
                    Get<TextBlock>("ComparisonCount").Text = scanned;
                    Get<TextBlock>("SingleCategory").Text = available[0].Label;
                    Show("SingleCategory", available.Count == 1);
                    Show("Categories", available.Count > 1);
                    categories.ItemsSource = available;
                    categories.SelectedIndex = 0;
                    Show("Source", false);
                    Show("Location", true);
                    Show("Status", false);
                    Show("Compare", true);
                    UpdateSelection();
                }
            }
            catch (OperationCanceledException) { SetStatus("Recherche annulée."); }
            catch (Exception ex) { SetStatus("Recherche interrompue. " + ex.Message); }
            finally { FinishOperation(); }
        }

        static string Count(int count, string singular, string plural)
        {
            return count.ToString("N0", French) + " " + (count > 1 ? plural : singular);
        }

        static string Size(decimal bytes)
        {
            string[] units = { "o", "Ko", "Mo", "Go", "To", "Po" };
            int unit = 0;
            while (bytes >= 1000 && unit < units.Length - 1) { bytes /= 1000; unit++; }
            return bytes.ToString("0.##", French) + " " + units[unit];
        }

        void ShowCategory()
        {
            var category = Active;
            if (category == null) return;
            int index = category.GroupIndex;
            groups.ItemsSource = category.Groups.Select(g => new GroupChoice { Group = g, Similar = category.Similar }).ToList();
            groups.SelectedIndex = index;
            AutomationProperties.SetName(groups, category.Label);
            Resize(Get<Grid>("Workspace").ActualWidth);
        }

        void Resize(double width)
        {
            if (width <= 0) return;
            bool stacked = width <= 700;
            var workspace = Get<Grid>("Workspace");
            var pane = Get<StackPanel>("GroupPane");
            var details = Get<StackPanel>("Details");
            bool multiple = groups.Items.Count > 1;
            pane.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            workspace.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(multiple ? 180 : 0);
            workspace.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(details, stacked ? 0 : 1);
            Grid.SetRow(details, stacked ? 1 : 0);
            pane.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 0);
            groups.MaxHeight = stacked ? 155 : 420;
            cards.Columns = width <= 450 ? 1 : 2;
        }

        // Display the common stem once when long conversion names would otherwise repeat on every card.
        static string SharedStem(List<DuplicateFile> files)
        {
            string stem = Path.GetFileNameWithoutExtension(files[0].FullPath);
            int length = stem.Length;
            foreach (var file in files.Skip(1))
            {
                string other = Path.GetFileNameWithoutExtension(file.FullPath);
                length = Math.Min(length, other.Length);
                int i = 0;
                while (i < length && other[i] == stem[i]) i++;
                length = i;
                if (length < 32) return "";
            }
            if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
            string common = stem.Substring(0, length).TrimEnd(' ', '-', '_', '.');
            return common.Length >= 32 ? common : "";
        }

        static string CardName(string name, string stem)
        {
            if (stem.Length == 0) return name;
            int lastWord = stem.LastIndexOfAny(new[] { ' ', '-', '_', '.' }) + 1;
            int start = Math.Max(lastWord, stem.Length - 14);
            if (char.IsLowSurrogate(name[start])) start--;
            return "…" + name.Substring(start);
        }

        void ShowGroup()
        {
            cards.Children.Clear();
            var choice = groups.SelectedItem as GroupChoice;
            if (choice == null) return;
            string shared = SharedStem(choice.Group.Files);
            Get<TextBlock>("GroupTitle").Text = choice.Detail;
            var common = Get<TextBlock>("SharedName");
            common.Text = shared;
            common.ToolTip = shared.Length == 0 ? null : shared;
            string proof = choice.Similar ? "Les détails ou la résolution peuvent différer d’une photo à l’autre."
                : choice.Group.SameBytes ? "Le contenu principal des fichiers est identique, octet par octet."
                : "Les dimensions et les pixels sont identiques après prise en compte de l’orientation.";
            Get<TextBlock>("Proof").Text = proof + DifferentDownloadMarksNote(choice.Group);
            Active.GroupIndex = groups.SelectedIndex;
            var template = (DataTemplate)cards.FindResource("DuplicateFileCard");
            bool showOrigin = OriginsDiffer(choice.Group);
            bool showFolder = choice.Group.Files.Select(f => Path.GetDirectoryName(f.FullPath)).Distinct(StringComparer.Ordinal).Skip(1).Any();
            foreach (var file in choice.Group.Files) AddCard(template, file, shared, showFolder, showOrigin);
            UpdateSelection();
        }

        static bool OriginsDiffer(DuplicateGroup group)
        {
            var first = group.Files[0];
            return group.Files.Any(f => f.HasDownloadInfo != first.HasDownloadInfo || f.StreamHash != first.StreamHash);
        }

        static string DifferentDownloadMarksNote(DuplicateGroup group)
        {
            var marked = group.Files.Where(f => f.HasDownloadInfo);
            return marked.Select(f => f.StreamHash).Distinct().Skip(1).Any() ? " Les marques de téléchargement Windows diffèrent." : "";
        }

        static string DownloadOrigin(DuplicateFile file)
        {
            return file.HasDownloadInfo ? "Windows signale la provenance du téléchargement." : "Aucune marque de téléchargement Windows.";
        }

        void AddCard(DataTemplate template, DuplicateFile file, string shared, bool showFolder, bool showOrigin)
        {
            var card = (Border)template.LoadContent();
            card.Tag = file;
            Action<string, string> text = (element, value) => ((TextBlock)card.FindName(element)).Text = value;
            string name = Path.GetFileName(file.FullPath);
            string parent = Path.GetDirectoryName(file.FullPath);
            text("DisplayName", CardName(name, shared));
            var displayName = (TextBlock)card.FindName("DisplayName");
            displayName.ToolTip = file.FullPath;
            AutomationProperties.SetName(displayName, file.FullPath);
            text("FileName", name);
            text("FileFolder", parent);
            if (showFolder)
            {
                text("DistinctFolder", parent);
                ((TextBlock)card.FindName("DistinctFolder")).Visibility = Visibility.Visible;
            }
            text("FileFormat", string.IsNullOrEmpty(file.Format) ? "Non identifié" : file.Format);
            text("FileSize", Size(file.Length));
            text("FileDate", "Modifié le " + file.LastWriteUtc.ToLocalTime().ToString("g", French));
            if (showOrigin)
            {
                text("FileOrigin", DownloadOrigin(file));
                ((TextBlock)card.FindName("FileOrigin")).Visibility = Visibility.Visible;
            }
            if (file.Width > 0 && file.Height > 0)
            {
                text("FileDimensions", file.Width + " × " + file.Height + " px");
            }
            else ((TextBlock)card.FindName("FileDimensions")).Visibility = Visibility.Collapsed;
            var image = ReadThumbnail(file.Thumbnail);
            if (image == null)
            {
                text("TypeBadge", string.IsNullOrEmpty(file.Format) ? "Fichier" : file.Format);
                ((Border)card.FindName("PreviewArea")).Height = 54;
            }
            else
            {
                ((Image)card.FindName("Picture")).Source = image;
                ((Border)card.FindName("PreviewArea")).SetResourceReference(Border.BackgroundProperty, "InkBrush");
            }
            var locate = (Button)card.FindName("LocateFile");
            AutomationProperties.SetName(locate, "Voir dans l’Explorateur · " + file.FullPath);
            locate.Click += delegate {
                try
                {
                    if (!File.Exists(file.FullPath)) throw new FileNotFoundException("Le fichier est introuvable.", file.FullPath);
                    using (Process.Start("explorer.exe", "/select,\"" + file.FullPath + "\"")) { }
                }
                catch (Exception ex) { SetStatus("Ouverture impossible pour " + file.FullPath + ". " + ex.Message); }
            };
            var checkbox = (CheckBox)card.FindName("MarkFile");
            checkbox.ToolTip = file.FullPath;
            checkbox.IsChecked = Active.Marked.Contains(file.FullPath);
            checkbox.Checked += delegate { ChangeMark(file, true); };
            checkbox.Unchecked += delegate { ChangeMark(file, false); };
            cards.Children.Add(card);
        }

        static BitmapSource ReadThumbnail(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(data))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (NotSupportedException) { return null; }
            catch (FileFormatException) { return null; }
        }

        void ChangeMark(DuplicateFile file, bool value)
        {
            var choice = groups.SelectedItem as GroupChoice;
            if (choice == null) return;
            var category = Active;
            if (value && choice.Group.Files.Any(f => f != file && !category.Marked.Contains(f.FullPath)) && category.Marked.Add(file.FullPath))
                category.MarkedBytes += file.Length;
            if (!value && category.Marked.Remove(file.FullPath)) category.MarkedBytes -= file.Length;
            UpdateSelection();
        }

        void UpdateSelection()
        {
            var choice = groups.SelectedItem as GroupChoice;
            if (choice == null) return;
            var category = Active;
            var marked = category.Marked;
            int keep = choice.Group.Files.Count(f => !marked.Contains(f.FullPath));
            foreach (Border card in cards.Children)
            {
                var file = (DuplicateFile)card.Tag;
                bool selected = marked.Contains(file.FullPath);
                var checkbox = (CheckBox)card.FindName("MarkFile");
                checkbox.IsChecked = selected;
                checkbox.IsEnabled = selected || keep > 1;
                string label = selected ? "Marqué pour la Corbeille" : keep == 1 ? "Exemplaire conservé" : "Marquer pour la Corbeille";
                AutomationProperties.SetName(checkbox, label + " · " + file.FullPath);
                ((TextBlock)card.FindName("MarkLabel")).Text = label;
                var area = (Border)card.FindName("MarkArea");
                area.SetResourceReference(Border.BackgroundProperty, selected ? "WarningSoftBrush" : keep == 1 ? "SuccessSoftBrush" : "SurfaceBrush");
                card.SetResourceReference(Border.BorderBrushProperty, selected ? "WarningBrush" : "LineBrush");
            }
            Get<TextBlock>("Selection").Text = marked.Count == 0 ? "Tous les exemplaires sont conservés." : "Pour la Corbeille · " + category.Total;
            Get<TextBlock>("SelectionCategory").Text = category.Name;
            review.IsEnabled = marked.Count > 0 && operation == null;
        }

        void ShowReview()
        {
            var category = Active;
            if (operation != null || category == null || category.Marked.Count == 0) return;
            reviewedGroups = category.Groups;
            reviewedPaths = new HashSet<string>(category.Marked, category.Marked.Comparer);
            var list = Get<StackPanel>("ReviewGroups");
            list.Children.Clear();
            foreach (var group in reviewedGroups)
            {
                var chosen = group.Files.Where(f => reviewedPaths.Contains(f.FullPath)).ToList();
                if (chosen.Count == 0) continue;
                var panel = new StackPanel();
                string stem = SharedStem(group.Files);
                panel.Children.Add(new TextBlock { Text = stem.Length == 0 ? Path.GetFileName(group.Files[0].FullPath) : stem, FontSize = 16,
                    FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                bool showOrigin = OriginsDiffer(group);
                string note = DifferentDownloadMarksNote(group);
                if (note.Length > 0) panel.Children.Add(new TextBlock { Text = note.TrimStart(), TextWrapping = TextWrapping.Wrap });
                AddReviewPaths(panel, chosen, stem, true, showOrigin);
                AddReviewPaths(panel, group.Files.Where(f => !reviewedPaths.Contains(f.FullPath)).ToList(), stem, false, showOrigin);
                var row = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 5, 0, 9),
                    Margin = new Thickness(0, 0, 0, 5), Child = panel };
                row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
                list.Children.Add(row);
            }
            Get<TextBlock>("ReviewTotal").Text = category.Name + " · " + category.Total + " pour la Corbeille." +
                (category.Similar ? " Les photos peuvent différer. Vérifiez les versions à conserver." : "");
            Show("Source", false);
            Show("Compare", false);
            Show("Issues", false);
            Show("Status", false);
            Show("ReviewPane", true);
            UpdateCommands();
            Get<TextBlock>("ReviewHeading").Focus();
            Get<TextBlock>("ReviewHeading").BringIntoView();
        }

        static void AddReviewPaths(Panel panel, List<DuplicateFile> files, string stem, bool discard, bool showOrigin)
        {
            string label = discard ? "À la Corbeille" : "À conserver";
            var entries = new StackPanel();
            foreach (var file in files)
            {
                var title = new TextBlock { Text = CardName(Path.GetFileName(file.FullPath), stem) + "  ·  " + Size(file.Length),
                    TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0) };
                entries.Children.Add(title);
                var path = new TextBlock { Text = file.FullPath, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                entries.Children.Add(path);
                if (showOrigin) entries.Children.Add(new TextBlock { Text = DownloadOrigin(file), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            }
            var heading = new TextBlock { Text = label + " · " + Count(files.Count, "fichier", "fichiers"), FontWeight = FontWeights.SemiBold };
            heading.SetResourceReference(TextBlock.ForegroundProperty, discard ? "WarningBrush" : "SuccessBrush");
            var section = new Border { Tag = label, Padding = new Thickness(11, 7, 11, 8),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 0, 0) };
            section.SetResourceReference(Border.BackgroundProperty, discard ? "WarningSoftBrush" : "SuccessSoftBrush");
            if (!discard && files.Count > 2)
                section.Child = new Expander { Header = heading, Content = entries };
            else
            {
                entries.Children.Insert(0, heading);
                section.Child = entries;
            }
            panel.Children.Add(section);
        }

        void ReturnToComparison()
        {
            reviewedPaths = null;
            reviewedGroups = null;
            UpdateCommands();
            Show("ReviewPane", false);
            Show("Compare", true);
            Show("Location", true);
            Show("Source", false);
            Show("Status", false);
            Show("Issues", scan != null && scan.Issues.Count > 0);
            review.Focus();
        }

        async Task RecycleAsync()
        {
            if (operation != null || reviewedPaths == null || Get<Border>("ReviewPane").Visibility != Visibility.Visible) return;
            var confirmed = reviewedPaths;
            var snapshot = reviewedGroups;
            var running = new CancellationTokenSource();
            operation = running;
            UpdateCommands();
            SetStatus("Vérification des exemplaires et mise à la Corbeille en cours…");
            try
            {
                var result = await Task.Run(() => recycle(snapshot, confirmed, running.Token));
                ResetResults();
                Show("Outcome", true);
                string detail = Count(result.Moved.Count, "fichier envoyé", "fichiers envoyés") + " à la Corbeille sur " +
                    Count(confirmed.Count, "fichier sélectionné", "fichiers sélectionnés") + ".";
                if (result.Moved.Count > 0) detail += Environment.NewLine + string.Join(Environment.NewLine, result.Moved);
                Get<TextBlock>("Moved").Text = detail;
                ShowIssues(result.Issues, "Fichiers non déplacés");
            }
            catch (Exception ex)
            {
                ResetResults();
                SetStatus("Le bilan de la mise à la Corbeille est indisponible. Relancez la recherche pour vérifier les fichiers présents. " + ex.Message);
            }
            finally { FinishOperation(); }
        }
    }
}
