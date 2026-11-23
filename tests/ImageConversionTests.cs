using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckImageConversions()
        {
            var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-images-test-" + Guid.NewGuid().ToString("N")));
            var token = CancellationToken.None;
            try
            {
                var source = Path.Combine(dir.FullName, "été [0] %d.inconnu");
                var pixels = new byte[16 * 12 * 4];
                var bitmap = BitmapSource.Create(16, 12, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
                var frame = BitmapFrame.Create(bitmap);
                var png = new PngBitmapEncoder();
                png.Frames.Add(frame);
                using (var output = File.Create(source)) png.Save(output);
                var original = File.ReadAllBytes(source);
                using (var image = ImageConversion.Open(source, token))
                {
                    Check(image.Format == "PNG" && image.Width == 16 && image.Height == 12, "Identification par contenu malgré le nom");
                    foreach (string code in new[] { "JPEG", "WEBP", "AVIF", "JXL", "TIFF", "BMP", "ICO", "QOI" })
                    {
                        var format = ImageOutput.Formats.Single(f => f.Code == code);
                        string path = Path.Combine(dir.FullName, "résultat [2]." + format.Extension);
                        image.Save(path, format, false, token);
                        using (var saved = ImageConversion.Open(path, token))
                            Check(saved.Width == 16 && saved.Height == 12 && saved.Format == code,
                                "Véritable conversion vers " + code);
                    }
                    var jpg = ImageOutput.Formats.Single(f => f.Code == "JPEG");
                    var target = Path.Combine(dir.FullName, "résultat [2].jpg");
                    using (var file = File.OpenRead(target))
                    {
                        var decoded = new JpegBitmapDecoder(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var rgb = new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Rgb24, null, 0);
                        var color = new byte[16 * 12 * 3];
                        rgb.CopyPixels(color, 16 * 3, 0);
                        Check(color[0] > 245 && color[1] > 245 && color[2] > 245, "Fond blanc pour la transparence vers JPEG");
                    }
                    bool refused = false;
                    try { image.Save(source, jpg, true, token); } catch (IOException) { refused = true; }
                    Check(refused && original.SequenceEqual(File.ReadAllBytes(source)), "Protection de l’original");
                    File.WriteAllText(target, "destination à conserver");
                    refused = false;
                    try { image.Save(target, jpg, false, token); } catch (IOException) { refused = true; }
                    Check(refused && File.ReadAllText(target) == "destination à conserver", "Destination existante préservée");
                    image.Save(target, jpg, true, token);
                    Check(File.ReadAllBytes(target)[0] == 255, "Remplacement confirmé par un JPEG");
                    byte[] damaged = File.ReadAllBytes(target);
                    Array.Resize(ref damaged, damaged.Length - 2);
                    var broken = Path.Combine(dir.FullName, "fin manquante.jpg");
                    File.WriteAllBytes(broken, damaged);
                    var keep = Path.Combine(dir.FullName, "conserver.png");
                    File.WriteAllText(keep, "image précédente");
                    bool brokenRejected = false;
                    try
                    {
                        using (var partial = ImageConversion.Open(broken, token))
                            partial.Save(keep, ImageOutput.Formats[0], true, token);
                    }
                    catch (InvalidDataException) { brokenRejected = true; }
                    Check(brokenRejected && File.ReadAllText(keep) == "image précédente", "Destination préservée après une erreur de décodage");
                }
                var gif = new GifBitmapEncoder();
                gif.Frames.Add(frame);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
                var second = BitmapSource.Create(16, 12, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
                gif.Frames.Add(BitmapFrame.Create(second));
                string sequence = Path.Combine(dir.FullName, "animation.gif");
                using (var output = File.Create(sequence)) gif.Save(output);
                var animation = File.ReadAllBytes(sequence);
                animation[6] = 24;
                animation[8] = 20;
                File.WriteAllBytes(sequence, animation);
                CheckImagePageSelectionAndClose(sequence);
                using (var image = ImageConversion.Open(sequence, token))
                {
                    Check(image.FrameCount == 2, "Les deux images du GIF sont détectées");
                    foreach (string code in new[] { "PNG", "TIFF", "WEBP", "AVIF", "JXL" })
                    {
                        var format = ImageOutput.Formats.Single(f => f.Code == code);
                        string dest = Path.Combine(dir.FullName, "séquence." + format.Extension);
                        bool still = code == "PNG";
                        Check(image.LosesFrames(format) == still, "Avertissement adapté au format " + code);
                        image.Save(dest, format, false, token);
                        using (var converted = ImageConversion.Open(dest, token))
                            Check(converted.FrameCount == (still ? 1 : 2) && converted.Width == 24 && converted.Height == 20,
                                "Séquence et canevas après conversion vers " + code);
                    }
                }
                File.WriteAllText(source, "fichier endommagé");
                bool invalid = false;
                try { using (ImageConversion.Open(source, token)) { } } catch (InvalidDataException) { invalid = true; }
                Check(invalid, "Un fichier illisible est signalé");
            }
            finally { dir.Delete(true); }
        }

        static void CheckImagePageSelectionAndClose(string source)
        {
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            string openedFolder = null;
            var page = new ImageConversionPage(window, folder => openedFolder = folder);
            bool closed = false;
            window.Closed += delegate { closed = true; };
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var loaded = page.LoadAsync(source);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!loaded.IsCompleted && DateTime.UtcNow < deadline)
                    window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate { }));
                var save = (Button)window.FindName("ImageSave");
                var details = (TextBlock)window.FindName("ImageDetails");
                var warning = (TextBlock)window.FindName("ImageWarning");
                Check(loaded.IsCompleted && save.IsEnabled && details.Text.Contains("24 × 20") && details.Text.Contains("2 images"),
                    "La page affiche les dimensions et active l’enregistrement après lecture");
                Check(warning.Visibility == Visibility.Visible, "La page avertit de la perte de trames avant l’enregistrement PNG");
                ((ComboBox)window.FindName("ImageFormat")).SelectedItem = ImageOutput.Formats.Single(f => f.Code == "WEBP");
                Check(warning.Visibility == Visibility.Collapsed, "L’avertissement disparaît pour une sortie animée");
                var result = (Border)window.FindName("ImageResult");
                var openFolder = (Button)window.FindName("ImageOpenFolder");
                Check(result.Visibility == Visibility.Collapsed && !openFolder.IsEnabled, "Aucun dossier proposé avant une conversion");
                Check(((Image)window.FindName("ImagePreview")).Source != null, "L’image choisie a un aperçu");
                string folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(source), "mes copies [été]")).FullName;
                string output = Path.Combine(folder, "animation convertie.webp");
                var written = page.SaveToAsync(output, false);
                WaitForImageAction(window, written);
                Check(File.Exists(output) && result.Visibility == Visibility.Visible && openFolder.IsEnabled, "Le dossier est proposé après l’écriture");
                openFolder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(openedFolder == folder, "Le bouton ouvre le dossier de destination choisi");
                var failed = page.SaveToAsync(output, false);
                WaitForImageAction(window, failed);
                Check(result.Visibility == Visibility.Collapsed && !openFolder.IsEnabled, "Une conversion échouée ne propose pas un ancien résultat");
                var reading = page.LoadAsync(source);
                window.Close();
                Check(!closed, "La fermeture attend la fin de la lecture");
                deadline = DateTime.UtcNow.AddSeconds(15);
                while (!closed && DateTime.UtcNow < deadline)
                    window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate { }));
                Check(closed && reading.IsCompleted, "La fenêtre se ferme après le nettoyage du convertisseur");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        static void WaitForImageAction(Window window, System.Threading.Tasks.Task action)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!action.IsCompleted && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(delegate { }));
            Check(action.IsCompleted && !action.IsFaulted, "L’action de conversion est terminée");
        }

        static void CheckImageConversionNavigation()
        {
            var window = new Shell(() => new List<Device>()).Window;
            try
            {
                ShowNavigationWindow(window);
                var catalog = (StackPanel)window.FindName("OtherPage");
                ClickVisible(FindAction(catalog, "Convertir une image"));
                Check(((StackPanel)window.FindName("ImageView")).IsVisible, "Le catalogue initial ouvre le convertisseur d’images");
                ((Button)window.FindName("SearchNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var search = (TextBox)window.FindName("CatalogSearch");
                search.Text = "debit";
                Check(catalog.Visibility == Visibility.Visible && FindAction(catalog, "Ouvrir le test de débit") != null
                    && FindAction(catalog, "Convertir une image") == null, "Recherche d’outil insensible aux accents");
                search.Text = "zzzaucunoutil";
                Check(((TextBlock)window.FindName("CatalogEmpty")).Visibility == Visibility.Visible, "Une recherche sans résultat est expliquée");
                ((Button)window.FindName("CatalogClear")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(search.Text == "" && FindAction(catalog, "Convertir une image") != null, "Effacer retrouve tous les outils");
                search.Text = "zzzaucunoutil";
                ClickVisible((Button)window.FindName("StartNav"));
                ClickVisible((Button)window.FindName("StartCatalog"));
                Check(search.Text.Length == 0 && FindAction(catalog, "Convertir une image") != null,
                    "Le dépannage guidé donne accès au catalogue complet après une recherche");
                OpenFilesToolFromMenu(window, "ImageNav");
                var view = (StackPanel)window.FindName("ImageView");
                Check(view.Visibility == Visibility.Visible, "Page de conversion visible");
                ((Button)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var entry = FindAction((StackPanel)window.FindName("OtherPage"), "Convertir une image");
                Check(entry != null, "Conversion accessible dans Tous les outils");
                entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(view.Visibility == Visibility.Visible && !((Button)window.FindName("ImageSave")).IsEnabled,
                    "Le convertisseur attend la sélection d’une image");
            }
            finally { window.Close(); }
        }
    }
}
