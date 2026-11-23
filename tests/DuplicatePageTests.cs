using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckDuplicatePage()
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-doublons-vue-" + Guid.NewGuid().ToString("N")));
            var window = new Shell(() => new List<Device>()).Window;
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                ShowNavigationWindow(window);
                var entry = FindAction((StackPanel)window.FindName("OtherPage"), "Ouvrir la recherche de doublons");
                Check(entry != null, "La recherche de doublons est accessible depuis le catalogue");
                ClickVisible(entry);
                var path = (TextBox)window.FindName("DuplicateFolder");
                var start = (Button)window.FindName("DuplicateStart");
                var review = (Button)window.FindName("DuplicateReview");
                var back = (Button)window.FindName("DuplicateModify");
                var groups = (ListBox)window.FindName("DuplicateGroups");
                var cards = (UniformGrid)window.FindName("DuplicateCards");
                var summary = (TextBlock)window.FindName("DuplicateStatus");
                Check(!start.IsEnabled, "La recherche attend un dossier");

                foreach (string name in new[] { "A1.bin", "A2.bin", "A3.bin", "B1.bin", "B2.bin" })
                    File.WriteAllText(Path.Combine(folder.FullName, name), new string(name[0], name[0] - 'A' + 3));
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 1, 96, 96, PixelFormats.Gray8, null, new byte[] { 17, 222 }, 2)));
                string photo = Path.Combine(folder.FullName, "C1.bin");
                using (var stream = File.Create(photo)) png.Save(stream);
                File.Copy(photo, Path.Combine(folder.FullName, "C2.bin"));
                path.Text = folder.FullName;
                ClickVisible(start);
                WaitDuplicatePage(window, () => start.IsEnabled);
                Check(groups.Items.Count == 3 && !review.IsEnabled, "Tous les exemplaires sont conservés avant le premier choix");
                Func<CheckBox[]> marks = () => Descendants(cards).OfType<CheckBox>().ToArray();
                Action<string> selectGroup = name => {
                    for (int i = 0; i < groups.Items.Count; i++)
                    {
                        groups.SelectedIndex = i;
                        if (Descendants(cards).OfType<TextBlock>().Any(t => t.Text == name)) return;
                    }
                    throw new Exception("Groupe introuvable pour " + name);
                };
                selectGroup("C1.bin");
                var previews = Descendants(cards).OfType<Image>().ToArray();
                Check(previews.Length == 2 && previews.All(i => i.Source != null && i.Stretch == Stretch.Uniform)
                    && Descendants(cards).OfType<TextBlock>().Count(t => t.Text == "2 × 1 px") == 2
                    && Descendants(cards).OfType<TextBlock>().Any(t => t.Text == "PNG"),
                    "Les deux photos renommées présentent leurs aperçus, leurs dimensions et leur format réel");
                selectGroup("A1.bin");
                var a = marks();
                Check(a.Length == 3 && a.All(c => c.IsEnabled && c.IsChecked == false), "Un groupe de trois présente trois choix libres");
                a[0].IsChecked = true;
                a[1].IsChecked = true;
                Check(!a[2].IsEnabled && a[2].IsChecked == false, "Le dernier exemplaire est protégé");
                a[1].IsChecked = false;
                Check(a[2].IsEnabled, "Décocher une copie permet de changer d’exemplaire conservé");
                string markedA = (string)a[0].ToolTip;
                selectGroup("B1.bin");
                marks()[0].IsChecked = true;
                string markedB = (string)marks()[0].ToolTip;
                selectGroup("A1.bin");
                Check(marks()[0].IsChecked == true && marks().Skip(1).All(c => c.IsChecked == false),
                    "Les choix sont conservés en changeant de groupe");

                window.Width = 415;
                UpdateNavigationLayout(window);
                var workspace = (Grid)window.FindName("DuplicateWorkspace");
                var chooser = (FrameworkElement)window.FindName("DuplicateGroupPane");
                var details = (FrameworkElement)window.FindName("DuplicateDetails");
                var left = chooser.TransformToAncestor(workspace).TransformBounds(new Rect(chooser.RenderSize));
                var right = details.TransformToAncestor(workspace).TransformBounds(new Rect(details.RenderSize));
                Check(cards.Columns == 1 && right.Top >= left.Bottom - 1 && right.Right <= workspace.ActualWidth + 1,
                    "À 415 pixels les fiches sont empilées sous le sélecteur dans la largeur disponible");
                window.Width = 1150;
                UpdateNavigationLayout(window);
                left = chooser.TransformToAncestor(workspace).TransformBounds(new Rect(chooser.RenderSize));
                right = details.TransformToAncestor(workspace).TransformBounds(new Rect(details.RenderSize));
                Check(cards.Columns == 2 && right.Left >= left.Right - 1, "La comparaison large présente deux fiches à côté du sélecteur");

                window.Height = 795;
                selectGroup("C1.bin");
                var scroll = (ScrollViewer)window.FindName("Scroll");
                scroll.ScrollToTop();
                UpdateNavigationLayout(window);
                foreach (var control in marks().Cast<FrameworkElement>())
                {
                    var bounds = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
                    Check(control.IsVisible && bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight + 1,
                        "À 1150 × 795 la commande " + control.Name + " doit être visible sans défiler (bas " +
                        bounds.Bottom + ", hauteur disponible " + scroll.ActualHeight + ").");
                }
                var root = (FrameworkElement)window.Content;
                var toolbarPosition = review.TranslatePoint(new Point(), root);
                scroll.ScrollToEnd();
                UpdateNavigationLayout(window);
                Check(review.IsVisible && toolbarPosition == review.TranslatePoint(new Point(), root)
                    && toolbarPosition.Y >= scroll.TranslatePoint(new Point(0, scroll.ActualHeight), root).Y - 1
                    && toolbarPosition.Y + review.ActualHeight <= root.ActualHeight + 1,
                    "La sélection reste visible dans son pied de page pendant le défilement");
                selectGroup("A1.bin");
                ClickVisible(review);
                var rows = (Panel)window.FindName("DuplicateReviewGroups");
                var texts = Descendants(rows).OfType<TextBlock>().Select(t => t.Text).ToArray();
                var paths = Directory.GetFiles(folder.FullName);
                Check(rows.Children.Count == 2 && paths.Where(p => Path.GetFileName(p)[0] != 'C').All(p => texts.Any(t => t.Contains(p)))
                    && !texts.Any(t => t.Contains("C1.bin") || t.Contains("C2.bin")), "Le récapitulatif présente tous les chemins des seuls groupes concernés");
                var discarded = ReviewFileTexts(rows, "À la Corbeille");
                var kept = ReviewFileTexts(rows, "À conserver");
                Check(discarded.Contains(markedA) && discarded.Contains(markedB) && !kept.Contains(markedA) && !kept.Contains(markedB),
                    "Chaque chemin marqué apparaît dans la partie destinée à la Corbeille");
                ClickVisible(back);
                Check(marks()[0].IsChecked == true && review.IsEnabled, "Modifier la sélection restaure les choix");
                ClickVisible(review);
                File.AppendAllText(markedA, "Modification après analyse");
                File.AppendAllText(markedB, "Modification après analyse");
                ClickVisible((Button)window.FindName("DuplicateConfirm"));
                WaitDuplicatePage(window, () => ((FrameworkElement)window.FindName("DuplicateOutcome")).IsVisible);
                Check(File.Exists(markedA) && File.Exists(markedB) && ((TextBlock)window.FindName("DuplicateMoved")).Text.StartsWith("0 fichier")
                    && ((Expander)window.FindName("DuplicateIssues")).IsVisible, "Les échecs restent sur disque et le bilan distingue les déplacements réels");
                path.Text = folder.CreateSubdirectory("vide").FullName;
                ClickVisible(start);
                WaitDuplicatePage(window, () => start.IsEnabled);
                Check(summary.Text.Contains("Aucun groupe") && !review.IsVisible, "Un dossier vide indique clairement l’absence de groupe");
                ClickVisible(start);
                ClickVisible((Button)window.FindName("DuplicateCancel"));
                WaitDuplicatePage(window, () => start.IsEnabled);
                Check(summary.Text.Contains("annulée") && !review.IsVisible, "Annuler la recherche efface le résultat en cours");
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); folder.Delete(true); }
            CheckDuplicateCategories();
        }

        static void CheckDuplicateCategories()
        {
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                const string prefix = "voyage-en-famille-au-bord-de-la-mer-coucher-de-soleil";
                var shared = CategoryPhoto(prefix, 40);
                var copy = CategoryPhoto(prefix + "-copie", 40);
                var variant = CategoryPhoto(prefix + "-variante", 160);
                variant.HasDownloadInfo = true;
                variant.StreamHash = "marque A";
                var exact = new DuplicateGroup { SameBytes = true, Files = { shared, copy } };
                var similar = new DuplicateGroup { Files = { shared, variant } };
                var result = new DuplicateScanResult { Groups = { exact }, SimilarGroups = { similar }, Scanned = 3 };
                List<DuplicateGroup> submitted = null;
                string[] paths = null;
                new DuplicatePage(window, (root, token, progress) => result, (items, chosen, token) => {
                    submitted = items.ToList();
                    paths = chosen.ToArray();
                    return new DuplicateRecycleResult();
                });
                ((FrameworkElement)window.FindName("DuplicateView")).Visibility = Visibility.Visible;
                ShowNavigationWindow(window);
                ((TextBox)window.FindName("DuplicateFolder")).Text = Path.GetTempPath();
                var start = (Button)window.FindName("DuplicateStart");
                var review = (Button)window.FindName("DuplicateReview");
                var categories = (ListBox)window.FindName("DuplicateCategories");
                var cards = (UniformGrid)window.FindName("DuplicateCards");
                var proof = (TextBlock)window.FindName("DuplicateProof");
                var total = (TextBlock)window.FindName("DuplicateReviewTotal");
                Func<int, CheckBox> mark = index => (CheckBox)((Border)cards.Children[index]).FindName("MarkFile");
                ClickVisible(start);
                WaitDuplicatePage(window, () => start.IsEnabled);
                var copyCard = (Border)cards.Children[1];
                var shortName = (TextBlock)copyCard.FindName("DisplayName");
                Check(shortName != null && shortName.Text.StartsWith("…") && shortName.Text.EndsWith("-copie.png") &&
                    (string)shortName.ToolTip == copy.FullPath, "La fin du nom reste identifiable et le chemin complet est accessible");
                var labels = categories.Items.Cast<object>().Select(item => {
                    var container = (ListBoxItem)categories.ItemContainerGenerator.ContainerFromItem(item);
                    return ((TextBlock)container.Template.FindName("CategoryLabel", container)).Text;
                }).ToArray();
                Check(categories.SelectedIndex == 0 && labels.Contains("Doublons exacts · 1 groupe") &&
                    labels.Contains("Photos visuellement proches · 1 groupe") && proof.Text.Contains("octet"),
                    "Les catégories et leurs nombres de groupes sont visibles, les doublons exacts en premier (index " +
                    categories.SelectedIndex + ", libellés " + string.Join(" / ", labels) + ", explication " + proof.Text + ")");
                mark(0).IsChecked = true;
                ClickVisible(review);
                Check(total.Text.Contains("Doublons exacts"), "La vérification nomme la catégorie exacte");
                ClickVisible((Button)window.FindName("DuplicateModify"));
                categories.SelectedIndex = 1;
                Check(!review.IsEnabled && mark(0).IsChecked == false && proof.Text.Contains("différer"),
                    "La photo commune commence sans sélection dans la catégorie des photos proches");
                var thumbnails = cards.Children.Cast<Border>().Select(card => {
                    var image = (BitmapSource)((Image)card.FindName("Picture")).Source;
                    var pixel = new byte[1];
                    new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0).CopyPixels(pixel, 1, 0);
                    return pixel[0];
                }).ToArray();
                var provenance = cards.Children.Cast<Border>().Select(card => (TextBlock)card.FindName("FileOrigin")).ToList();
                Check(thumbnails[0] != thumbnails[1] && provenance.All(t => t.IsVisible) &&
                    provenance[0].Text.Contains("Aucune marque") && provenance[1].Text.Contains("téléchargement"),
                    "Chaque photo proche présente sa propre miniature et sa marque de provenance éventuelle");
                mark(1).IsChecked = true;
                Check(!mark(0).IsEnabled, "Un exemplaire reste protégé dans le groupe de photos proches");
                categories.SelectedIndex = 0;
                Check(mark(0).IsChecked == true && mark(1).IsChecked == false, "Revenir aux doublons exacts retrouve leurs seuls choix");
                categories.SelectedIndex = 1;
                Check(mark(1).IsChecked == true && mark(0).IsChecked == false, "Revenir aux photos proches retrouve leurs seuls choix");
                ClickVisible(review);
                var rows = (Panel)window.FindName("DuplicateReviewGroups");
                var lines = Descendants(rows).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Check(total.Text.Contains("Photos visuellement proches") && total.Text.Contains("différer") &&
                    ReviewFileTexts(rows, "À la Corbeille").Contains(variant.FullPath) &&
                    ReviewFileTexts(rows, "À conserver").Contains(shared.FullPath) && !lines.Any(t => t.Contains(copy.FullPath)),
                    "La vérification des photos proches rappelle la différence possible et distingue les chemins choisis et conservés");
                ClickVisible((Button)window.FindName("DuplicateConfirm"));
                WaitDuplicatePage(window, () => ((FrameworkElement)window.FindName("DuplicateOutcome")).IsVisible);
                Check(submitted.SequenceEqual(new[] { similar }) && paths.SequenceEqual(new[] { variant.FullPath }) &&
                    categories.Items.Count == 0 && cards.Children.Count == 0 && !review.IsEnabled,
                    "La confirmation transmet uniquement les groupes et choix approuvés puis efface les deux catégories");
                result.Groups.Clear();
                shared.HasDownloadInfo = true;
                shared.StreamHash = "marque B";
                ClickVisible(start);
                WaitDuplicatePage(window, () => start.IsEnabled);
                Check(proof.IsVisible && proof.Text.Contains("différer") && !categories.IsVisible && !review.IsEnabled &&
                    ((TextBlock)window.FindName("DuplicateSingleCategory")).Text == "Photos visuellement proches · 1 groupe",
                    "Un résultat uniquement probable ouvre directement les photos proches sans ancien choix");
                Check(proof.Text.Contains("marques de téléchargement Windows diffèrent") && cards.Children.Cast<Border>()
                    .All(card => ((TextBlock)card.FindName("FileOrigin")).IsVisible),
                    "Les marques de téléchargement différentes sont signalées même si les deux fichiers en possèdent une");
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
        }

        static IEnumerable<string> ReviewFileTexts(Panel rows, string section)
        {
            return Descendants(rows).OfType<Border>().Where(b => Equals(b.Tag, section))
                .SelectMany(b => Descendants(b).OfType<TextBlock>()).Select(t => t.Text);
        }

        static DuplicateFile CategoryPhoto(string name, byte value)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new[] { value }, 1)));
            using (var stream = new MemoryStream())
            {
                png.Save(stream);
                return new DuplicateFile { FullPath = Path.Combine(Path.GetTempPath(), name + ".png"), Format = "PNG",
                    Width = 1, Height = 1, Length = stream.Length, Thumbnail = stream.ToArray(), LastWriteUtc = DateTime.UtcNow };
            }
        }

        static void WaitDuplicatePage(Window window, Func<bool> done)
        {
            var limit = DateTime.UtcNow.AddSeconds(45);
            while (!done() && DateTime.UtcNow < limit)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            Check(done(), "La page de recherche termine son opération");
            UpdateNavigationLayout(window);
        }
    }
}
