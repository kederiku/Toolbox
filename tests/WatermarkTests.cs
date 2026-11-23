using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        sealed class InterruptedWatermark : WatermarkSource
        {
            internal InterruptedWatermark() { Extension = ".pdf"; }
            internal override void WriteCopy(string temporary, WatermarkOptions options, CancellationToken token)
            {
                File.WriteAllText(temporary, "incomplet");
                throw new OperationCanceledException();
            }
        }

        internal static void RunWatermarkTests()
        {
            const int width = 360, height = 240;
            var options = new WatermarkOptions("Réservé à Zoé · n° 5", false, WatermarkPosition.BottomRight, .5, .4);
            var art = WatermarkArt.Render(width, height, options);
            var pixels = new byte[width * height * 4];
            art.CopyPixels(pixels, width * 4, 0);
            var ink = Enumerable.Range(0, width * height).Where(i => pixels[i * 4 + 3] > 0).ToArray();
            Check(art.IsFrozen && ink.Length > 20 && ink.All(i => i / width > height / 2), "Texte personnalisé visible dans le coin inférieur");
            Check(ink.Max(i => pixels[i * 4 + 3]) <= 104, "Opacité du motif respectée");
            WatermarkArt.Render(width, height, new WatermarkOptions("Été", true, WatermarkPosition.Center, .4, .8)).CopyPixels(pixels, width * 4, 0);
            Check(Enumerable.Range(0,4).All(band => Enumerable.Range(band * width * height / 4, width * height / 4).Any(i => pixels[i*4+3] > 0)),
                "Le motif répété couvre plusieurs rangées");
            string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-filigrane-test-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                string input = Path.Combine(folder, "source.pdf"), target = Path.Combine(folder, "copie.pdf");
                File.WriteAllText(input, "original");
                File.WriteAllText(target, "copie existante");
                using (var source = new InterruptedWatermark())
                {
                    bool refused = false, cancelled = false;
                    try { source.Save(input, input, true, options, CancellationToken.None); } catch (IOException) { refused = true; }
                    try { source.Save(input, target, true, options, CancellationToken.None); } catch (OperationCanceledException) { cancelled = true; }
                    Check(refused && cancelled && File.ReadAllText(input) == "original" && File.ReadAllText(target) == "copie existante"
                        && Directory.GetFiles(folder).Length == 2, "Original protégé et copie existante intacte après annulation");
                    string alias = Path.Combine(folder, "alias.pdf");
                    if (CreateHardLink(alias, input, IntPtr.Zero))
                    {
                        refused = false;
                        try { source.Save(input, alias, true, options, CancellationToken.None); } catch (IOException) { refused = true; }
                        Check(refused && File.ReadAllText(alias) == "original", "Un lien physique vers l’original est protégé");
                    }
                }
                CheckWatermarkPage(folder);
            }
            finally { Directory.Delete(folder, true); }
        }

        static void CheckWatermarkPage(string folder)
        {
            string photo = Path.Combine(folder, "échantillon.png");
            WatermarkArt.WritePng(photo, 320, 180, new WatermarkOptions("Photo", false, WatermarkPosition.TopLeft, .3, 1));
            var shell = new Shell(() => new System.Collections.Generic.List<Device>()).Window;
            try
            {
                ((TextBox)shell.FindName("CatalogSearch")).Text = "filigrane";
                var action = FindAction((FrameworkElement)shell.FindName("CatalogItems"), "Ajouter un filigrane");
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((StackPanel)shell.FindName("WatermarkView")).Visibility == Visibility.Visible, "Ouverture du filigrane depuis la recherche");
            }
            finally { shell.Close(); }
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var page = new WatermarkPage(window);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var save = (Button)window.FindName("WatermarkSave");
                WaitForImageAction(window, page.LoadAsync(photo));
                Check(!save.IsEnabled && ((Image)window.FindName("WatermarkBackground")).Source != null, "Aperçu réel de la photo, en attente du texte");
                ((TextBox)window.FindName("WatermarkText")).Text = "À partager";
                ((ComboBox)window.FindName("WatermarkMode")).SelectedIndex = 1;
                Check(save.IsEnabled && ((StackPanel)window.FindName("WatermarkPositionRow")).Visibility == Visibility.Collapsed,
                    "Le texte active l’enregistrement en mode répété");
                string dest = Path.Combine(folder, "copie.png");
                WaitForImageAction(window, page.SaveAsync(dest, false));
                Check(File.Exists(dest) && ((Button)window.FindName("WatermarkFolder")).Visibility == Visibility.Visible, "Copie disponible depuis la page");
                ((TextBox)window.FindName("WatermarkText")).Text = "  ";
                Check(!save.IsEnabled, "Un texte vide désactive l’enregistrement");
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
}
