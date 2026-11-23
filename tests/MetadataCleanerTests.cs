using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal static partial class Tests
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newName, string existing, IntPtr reserved);

        static void CheckMetadataNavigation()
        {
            Window window = new Shell(() => new List<Device>()).Window;
            try
            {
                var direct = window.FindName("MetadataNav") as Button;
                Check(direct != null, "Le nettoyeur est accessible depuis Confidentialité");
                direct.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((StackPanel)window.FindName("MetadataView")).Visibility == Visibility.Visible,
                    "Le menu ouvre le nettoyeur");
                ((Button)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBox)window.FindName("CatalogSearch")).Text = "EXIF";
                var card = FindAction((StackPanel)window.FindName("CatalogItems"), "Ouvrir le nettoyeur");
                Check(card != null, "La recherche EXIF retrouve l’outil dans le catalogue");
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((StackPanel)window.FindName("MetadataView")).Visibility == Visibility.Visible &&
                    !((Button)window.FindName("MetadataSave")).IsEnabled, "Le nettoyeur attend une sélection");
            }
            finally { window.Close(); }
        }

        static void CheckMetadataPublication()
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-metadata-test-" + Guid.NewGuid().ToString("N")));
            try
            {
                string source = Path.Combine(folder.FullName, "original.png");
                string destination = Path.Combine(folder.FullName, "copie.png");
                var metadata = new BitmapMetadata("png");
                const string author = "Marc-METADATA-TEST";
                metadata.SetQuery("/tEXt/{str=Author}", author);
                var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[8], 8);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
                using (var file = File.Create(source)) encoder.Save(file);
                byte[] original = File.ReadAllBytes(source);
                Check(Encoding.ASCII.GetString(original).Contains(author), "Le témoin contient l’auteur");
                bool failed = false;
                try { MetadataCleaner.Save(source, source, true, CancellationToken.None); }
                catch (IOException) { failed = true; }
                Check(failed && original.SequenceEqual(File.ReadAllBytes(source)), "Le nettoyeur protège l’original");
                string alias = Path.Combine(folder.FullName, "alias.png");
                if (CreateHardLink(alias, source, IntPtr.Zero))
                {
                    failed = false;
                    try { MetadataCleaner.Save(source, alias, true, CancellationToken.None); }
                    catch (IOException) { failed = true; }
                    Check(failed && original.SequenceEqual(File.ReadAllBytes(alias)), "Un lien physique vers l’original est protégé");
                    File.Delete(alias);
                }
                File.WriteAllText(destination, "copie existante");
                failed = false;
                try { MetadataCleaner.Save(source, destination, false, CancellationToken.None); }
                catch (IOException) { failed = true; }
                Check(failed && File.ReadAllText(destination) == "copie existante", "Remplacement refusé sans accord");
                failed = false;
                try { MetadataCleaner.Save(source, destination, true, new CancellationToken(true)); }
                catch (OperationCanceledException) { failed = true; }
                Check(failed && File.ReadAllText(destination) == "copie existante", "Une annulation préserve la destination");
                var result = MetadataCleaner.Save(source, destination, true, CancellationToken.None);
                Check(result.PhotoProperties && !Encoding.ASCII.GetString(File.ReadAllBytes(destination)).Contains(author) &&
                    original.SequenceEqual(File.ReadAllBytes(source)), "La copie est vérifiée, le fichier d’origine est intact");
                Window window;
                using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                    window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
                try
                {
                    string opened = null;
                    var page = new MetadataPage(window, directory => opened = directory);
                    var previous = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
                    try
                    {
                        page.SelectFile(source);
                        WaitForImageAction(window, page.SaveToAsync(destination, true));
                        ((Button)window.FindName("MetadataFolder")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(opened == folder.FullName && ((Border)window.FindName("MetadataResult")).Visibility == Visibility.Visible,
                            "La page permet d’ouvrir la copie après vérification");
                        page.SelectFile(destination);
                        string clean = Path.Combine(folder.FullName, "bis.png");
                        WaitForImageAction(window, page.SaveToAsync(clean, false));
                        Check(((TextBlock)window.FindName("MetadataSummary")).Text.Contains("déjà") && File.Exists(clean),
                            "Un deuxième nettoyage signale que le fichier était déjà propre");
                        File.WriteAllText(source, "ce fichier ne contient pas une image");
                        page.SelectFile(source);
                        WaitForImageAction(window, page.SaveToAsync(destination, true));
                        Check(((Border)window.FindName("MetadataResult")).Visibility == Visibility.Collapsed &&
                            File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(clean)),
                            "Après un refus, le résultat précédent est masqué et la destination protégée");
                        Check(folder.GetFiles().Length == 3, "Aucune copie provisoire après succès ou erreur");
                    }
                    finally { SynchronizationContext.SetSynchronizationContext(previous); }
                }
                finally { window.Close(); }
            }
            finally { folder.Delete(true); }
        }
    }
}
