using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        readonly List<CatalogEntry> catalogEntries = new List<CatalogEntry>();
        string SelectedCatalogFamily { get; set; }

        static string CatalogFamily(NavigationPage tool)
        {
            switch (tool.Category)
            {
                case "Images":
                case "Vidéos":
                case "Audio":
                case "Confidentialité":
                    return "Fichiers";
                default:
                    return tool.Category;
            }
        }

        sealed class CatalogEntry
        {
            internal readonly NavigationPage Tool;
            internal readonly string Keywords;
            internal readonly Button Button;
            public string Title { get { return Tool.Title; } }
            public string Icon { get { return Tool.Icon; } }
            public string Family { get { return CatalogFamily(Tool); } }
            public string Summary { get; private set; }

            internal CatalogEntry(NavigationPage tool, Style style)
            {
                Tool = tool;
                Summary = CatalogSummary(tool);
                Keywords = string.Join(" ", tool.Title, tool.Label, tool.OpenLabel, tool.Description, tool.Category,
                    Family, Summary, CatalogKeywords(tool));
                Button = new Button { Content = tool.OpenLabel, Style = style, DataContext = this };
                AutomationProperties.SetName(Button, tool.Title);
                AutomationProperties.SetHelpText(Button, tool.Description);
            }
        }

        static string CatalogSummary(NavigationPage tool)
        {
            switch (tool.NavName)
            {
                case "DiagNav": return "Identifiez les appareils en erreur et leurs pilotes.";
                case "MapNav": return "Repérez un port USB ou suivez les déconnexions.";
                case "SpeedNav": return "Mesurez la latence et la vitesse de votre connexion.";
                case "NetworkRepairNav": return "Trouvez la cause d’une panne de connexion.";
                case "NetworkDevicesNav": return "Repérez les appareils et leur adresse IP locale.";
                case "PerformanceNav": return "Suivez le processeur, la mémoire et le disque en direct.";
                case "IntegrityNav": return "Vérifiez les fichiers système et l’image de Windows.";
                case "ImageNav": return "Changez le format d’une image ou d’un lot de photos.";
                case "VideoNav": return "Choisissez le format, le codec et la qualité vidéo.";
                case "YoutubeNav": return "Enregistrez une vidéo en MP4 ou uniquement le son en MP3.";
                case "AudioNav": return "Convertissez vos pistes audio en MP3, FLAC ou WAV.";
                case "MetadataNav": return "Supprimez les données GPS, EXIF et d’auteur.";
                case "DuplicateNav": return "Comparez les copies et choisissez les fichiers à garder.";
                default: return tool.Description;
            }
        }

        static string CatalogKeywords(NavigationPage tool)
        {
            switch (tool.NavName)
            {
                case "PerformanceNav": return "Mon PC est lent diagnostic de lenteur CPU RAM";
                case "SpeedNav": return "Ma connexion est lente";
                case "NetworkRepairNav": return "Internet ne fonctionne plus";
                case "DiagNav": return "Un appareil ne fonctionne plus";
                case "ImageNav": return string.Join(" ", ImageOutput.Formats.Select(format => format.ToString()));
                case "VideoNav": return string.Join(" ", VideoOutput.Formats.Select(format => format.ToString())
                    .Concat(VideoOutput.Formats.SelectMany(format => format.Codecs).Distinct().Select(codec => codec.ToString())));
                case "AudioNav": return string.Join(" ", AudioOutput.Formats.Select(format => format.Code + " " + format));
                case "MetadataNav": return "PDF Office Word Excel PowerPoint DOCX XLSX PPTX JPEG PNG WebP";
                case "WatermarkNav": return "filigrane watermark tampon mention texte répété PDF Word DOCX photo image JPEG JPG PNG HEIC GIF WebP TIFF";
                case "DuplicateNav": return "doublons doubles copies fichiers photos identiques comparer trier supprimer corbeille";
                default: return "";
            }
        }

        void InitializeCatalogSearch(NavigationPage[] tools, NavigationPage catalog)
        {
            var view = catalog.View;
            var style = (Style)view.FindResource("CatalogCardButton");
            foreach (var tool in tools)
            {
                var entry = new CatalogEntry(tool, style);
                entry.Button.Click += async delegate {
                    await OpenPageAsync(entry.Tool);
                    if (currentPage == entry.Tool && entry.Tool.PrimaryAction != null) entry.Tool.PrimaryAction.Focus();
                };
                catalogEntries.Add(entry);
            }
            SelectedCatalogFamily = "";
            foreach (RadioButton option in Get<WrapPanel>("CatalogFilters").Children)
                option.Checked += delegate {
                    SelectedCatalogFamily = (string)option.Tag;
                    FilterCatalog();
                };
            var search = Get<TextBox>("CatalogSearch");
            search.TextChanged += delegate { FilterCatalog(); };
            Get<Button>("CatalogClear").Click += delegate { search.Clear(); search.Focus(); };
            Get<Button>("CatalogReset").Click += delegate { ResetCatalog(); FocusCatalogSearch(); };
            Get<Border>("CatalogSearchFrame").MouseLeftButtonDown += delegate { search.Focus(); };
            view.SizeChanged += delegate { SizeCatalog(view.ActualWidth); };
            FilterCatalog();
        }

        void FilterCatalog()
        {
            var search = Get<TextBox>("CatalogSearch");
            var words = search.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var compare = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
            var grid = Get<UniformGrid>("CatalogGrid");
            grid.Children.Clear();
            foreach (var entry in catalogEntries.OrderByDescending(item => item.Tool.IsFavorite))
            {
                AutomationProperties.SetItemStatus(entry.Button, entry.Tool.IsFavorite ? "Favori" : "");
                if ((SelectedCatalogFamily.Length == 0 || entry.Family == SelectedCatalogFamily) &&
                    words.All(word => compare.IndexOf(entry.Keywords, word,
                        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
                    grid.Children.Add(entry.Button);
            }
            int count = grid.Children.Count;
            Get<TextBlock>("CatalogCount").Text = count + (count > 1 ? " outils" : " outil");
            Get<TextBlock>("CatalogEmpty").Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Get<Button>("CatalogClear").Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void SetCatalogFamily(string family)
        {
            var options = Get<WrapPanel>("CatalogFilters").Children.OfType<RadioButton>();
            var selected = options.FirstOrDefault(option => (string)option.Tag == family) ?? Get<RadioButton>("CatalogAll");
            selected.IsChecked = true;
        }

        void ResetCatalog()
        {
            SetCatalogFamily("");
            Get<TextBox>("CatalogSearch").Clear();
        }

        void FocusCatalogSearch()
        {
            var search = Get<TextBox>("CatalogSearch");
            search.Focus();
            search.SelectAll();
            search.BringIntoView();
        }

        void FocusCatalogTool(NavigationPage tool)
        {
            var entry = catalogEntries.FirstOrDefault(item => item.Tool == tool);
            if (entry == null || !entry.Button.IsVisible || !entry.Button.Focus()) FocusCatalogSearch();
        }

        void SizeCatalog(double width)
        {
            if (width <= 0) return;
            Get<UniformGrid>("CatalogGrid").Columns = width >= 755 ? 3 : width >= 482 ? 2 : 1;
            Get<WrapPanel>("CatalogFilters").Tag = width < 482 ? "Compact" : null;
            var art = Get<Viewbox>("CatalogArt");
            art.Visibility = width < 610 ? Visibility.Collapsed : Visibility.Visible;
            art.Width = width < 755 ? 162 : 234;
            Get<Grid>("CatalogHeroContent").Margin = width < 482 ? new Thickness(17, 17, 17, 19) : new Thickness(26, 20, 25, 23);
            Get<TextBlock>("CatalogTitle").FontSize = width < 482 ? 28 : 35;
            var count = Get<TextBlock>("CatalogCount");
            Grid.SetRow(count, width < 570 ? 1 : 0);
            Grid.SetColumn(count, width < 570 ? 0 : 1);
            count.HorizontalAlignment = width < 570 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
    }
}
