using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckVideoPage()
        {
            var window = new Shell(() => new List<Device>()).Window;
            try
            {
                ShowNavigationWindow(window);
                var catalog = (StackPanel)window.FindName("OtherPage");
                ClickVisible(FindAction(catalog, "Convertir une vidéo"));
                var view = (StackPanel)window.FindName("VideoView");
                Check(view.IsVisible, "Le catalogue initial ouvre la conversion vidéo");
                ((Button)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBox)window.FindName("CatalogSearch")).Text = "video";
                var entry = FindAction(catalog, "Convertir une vidéo");
                Check(entry != null, "La recherche vidéo retrouve le convertisseur dans le catalogue");
                entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(view.Visibility == Visibility.Visible, "La fiche du catalogue ouvre la conversion vidéo");
                ((Button)window.FindName("StartNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                OpenFilesToolFromMenu(window, "VideoNav");
                Check(view.Visibility == Visibility.Visible && !((Button)window.FindName("VideoSave")).IsEnabled,
                    "Le convertisseur du menu attend une vidéo avant d’activer l’enregistrement");

                var formats = (ComboBox)window.FindName("VideoFormat");
                var codecs = (ComboBox)window.FindName("VideoCodec");
                var quality = (ComboBox)window.FindName("VideoQuality");
                Check(formats.SelectedItem.ToString().Contains("MP4") && codecs.SelectedItem.ToString().Contains("H.264")
                    && quality.Text == "Équilibrée", "Les réglages initiaux proposent MP4, H.264 et une qualité équilibrée");
                var av1 = codecs.Items.Cast<object>().Single(c => c.ToString().Contains("AV1"));
                codecs.SelectedItem = av1;
                formats.SelectedItem = formats.Items.Cast<object>().Single(f => f.ToString().Contains("WebM"));
                Check(codecs.SelectedItem.ToString().Contains("AV1") && codecs.Items.Count == 2,
                    "Le changement vers WebM conserve AV1 et limite les codecs compatibles");
                formats.SelectedItem = formats.Items.Cast<object>().Single(f => f.ToString().Contains("AVI"));
                Check(codecs.SelectedIndex == 0 && !codecs.SelectedItem.ToString().Contains("AV1") && codecs.Items.Count == 2,
                    "Le changement vers AVI remplace le codec incompatible");
                Check(quality.Items.Count == 3, "Les trois qualités peuvent être sélectionnées");
            }
            finally { window.Close(); }
            CheckVideoPageOperations();
        }

        static void CheckVideoPageOperations()
        {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-page-vidéo-" + Guid.NewGuid().ToString("N")));
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            bool closed = false;
            window.Closed += delegate { closed = true; };
            try
            {
                string source = Path.Combine(directory.FullName, "extrait.mkv");
                string raw = Path.Combine(directory.FullName, "sans durée.h264");
                string captions = Path.Combine(directory.FullName, "piste.srt");
                File.WriteAllText(captions, "1\r\n00:00:00,000 --> 00:00:00,750\r\nBonjour\r\n");
                using (var engine = new VideoEngine(CancellationToken.None))
                {
                    engine.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=96x64:r=12:d=1",
                        "-i", captions, "-c:v", "libx264", "-c:s", "srt", source }, CancellationToken.None);
                    engine.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-map", "0:v", "-c:v", "copy", raw }, CancellationToken.None);
                }
                string opened = null;
                var page = new VideoConversionPage(window, folder => opened = folder);
                var progress = (ProgressBar)window.FindName("VideoProgress");
                var save = (Button)window.FindName("VideoSave");
                var cancel = (Button)window.FindName("VideoCancel");
                var details = (TextBlock)window.FindName("VideoDetails");
                var warning = (TextBlock)window.FindName("VideoWarning");
                var result = (Border)window.FindName("VideoResult");
                var open = (Button)window.FindName("VideoOpenFolder");
                var reading = page.LoadAsync(source);
                WaitForVideoPage(window, () => reading.IsCompleted);
                reading.GetAwaiter().GetResult();
                Check(save.IsEnabled && details.Text.Contains("96 × 64") && details.Text.Contains("0:01")
                    && warning.Visibility == Visibility.Visible && warning.Text.Length > 0,
                    "La lecture présente les dimensions, la durée et la perte de pistes avant l’export");
                string destination = Path.Combine(directory.FullName, "copie.mp4");
                var saving = page.SaveToAsync(destination, false);
                Check(!save.IsEnabled && progress.Visibility == Visibility.Visible && !progress.IsIndeterminate
                    && cancel.Visibility == Visibility.Visible, "L’encodage affiche sa progression et permet d’annuler");
                WaitForVideoPage(window, () => saving.IsCompleted);
                saving.GetAwaiter().GetResult();
                Check(File.Exists(destination) && result.Visibility == Visibility.Visible && open.IsEnabled,
                    "Une conversion réussie propose le dossier de la copie");
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(opened == directory.FullName, "La commande ouvre le dossier où la vidéo a été écrite");
                var cancelled = page.SaveToAsync(destination, true);
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForVideoPage(window, () => cancelled.IsCompleted);
                cancelled.GetAwaiter().GetResult();
                Check(save.IsEnabled && !open.IsEnabled && result.Visibility == Visibility.Collapsed
                    && progress.Visibility == Visibility.Collapsed && ((TextBlock)window.FindName("VideoStatus")).Text.Contains("annul"),
                    "L’annulation réactive la page et efface le résultat précédent");
                page.Leave();
                Check(!save.IsEnabled && warning.Visibility == Visibility.Collapsed, "Quitter la page efface la sélection vidéo");
                var rawRead = page.LoadAsync(raw);
                WaitForVideoPage(window, () => rawRead.IsCompleted);
                rawRead.GetAwaiter().GetResult();
                Check(save.IsEnabled && warning.Visibility == Visibility.Collapsed, "Une vidéo sans durée ni sous-titres peut être convertie");
                string unfinished = Path.Combine(directory.FullName, "interrompue.mp4");
                var finalSave = page.SaveToAsync(unfinished, false);
                Check(progress.Visibility == Visibility.Visible && progress.IsIndeterminate, "Une durée inconnue affiche une progression indéterminée");
                window.Close();
                Check(!closed, "La fenêtre attend le nettoyage de l’encodage en cours");
                WaitForVideoPage(window, () => closed);
                Check(finalSave.IsCompleted && !File.Exists(unfinished), "La fermeture annule l’écriture et se poursuit après nettoyage");
            }
            finally
            {
                if (!closed) window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
                directory.Delete(true);
            }
        }

        static void WaitForVideoPage(Window window, Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!done() && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            Check(done(), "L’opération de la page vidéo se termine dans le délai prévu");
        }
    }
}
