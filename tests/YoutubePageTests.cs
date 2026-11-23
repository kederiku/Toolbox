using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckYoutubePage()
        {
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-Youtube-" + Guid.NewGuid().ToString("N")));
            string output = Path.Combine(directory.FullName, "mon choix.mp4");
            bool closed = false;
            window.Closed += delegate { closed = true; };
            try
            {
                var video = YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Essai vidéo",
                    YoutubeStream("22", 1280, 720, "avc1", "mp4a"), YoutubeStream("18", 640, 360, "avc1", "mp4a")));
                string destination = null, suggested = null, extension = null, filter = null, opened = null, confirmed = null;
                bool replace = false, appearDuringSave = false, slowSave = false, failSave = false;
                var page = new YoutubePage(window,
                    (id, token) => {
                        if (window.Dispatcher.CheckAccess()) throw new Exception("Inspection sur le fil WPF");
                        if (id == "ancien lien") token.WaitHandle.WaitOne(5000);
                        if (id == "indisponible") throw new IOException("Vidéo privée");
                        if (id == "uniquement audio") return YoutubeVideo.FromJson(YoutubeFixtureId,
                            YoutubeInfo("Entretien", YoutubeStream("140", 0, 0, "none", "mp4a")));
                        if (id == "vidéo muette") return YoutubeVideo.FromJson(YoutubeFixtureId,
                            YoutubeInfo("Sans parole", YoutubeStream("136", 1280, 720, "avc1", "none")));
                        return video;
                    },
                    (item, choice, path, overwrite, token, report) => {
                        if (window.Dispatcher.CheckAccess()) throw new Exception("Téléchargement sur le fil WPF");
                        report("Assemblage du MP4");
                        if (slowSave) { token.WaitHandle.WaitOne(5000); token.ThrowIfCancellationRequested(); }
                        if (failSave) throw new IOException("Résolution indisponible");
                        if (appearDuringSave) File.WriteAllText(path, "créé pendant le transfert");
                        if (File.Exists(path) && !overwrite) throw new IOException("Remplacement non confirmé");
                        File.WriteAllText(path, item.Title + " " + choice.Height);
                    },
                    (item, path, overwrite, token, report) => {
                        if (window.Dispatcher.CheckAccess()) throw new Exception("Téléchargement audio sur le fil WPF");
                        report("Encodage MP3");
                        if (slowSave) { token.WaitHandle.WaitOne(5000); token.ThrowIfCancellationRequested(); }
                        if (File.Exists(path) && !overwrite) throw new IOException("Remplacement non confirmé");
                        File.WriteAllText(path, item.Title + " en MP3");
                    },
                    dialog => {
                        suggested = dialog.FileName;
                        extension = dialog.DefaultExt;
                        filter = dialog.Filter;
                        if (destination == null) return false;
                        dialog.FileName = destination;
                        return true;
                    },
                    path => { confirmed = path; return replace; },
                    path => opened = path);

                ((FrameworkElement)window.FindName("YoutubeView")).Visibility = Visibility.Visible;
                ShowNavigationWindow(window);
                var videoMode = (RadioButton)window.FindName("YoutubeVideoMode");
                var audioMode = (RadioButton)window.FindName("YoutubeAudioMode");
                Check(videoMode != null && audioMode != null && videoMode.IsVisible && audioMode.IsVisible
                    && videoMode.IsChecked == true && audioMode.IsChecked == false,
                    "Le choix vidéo et audio est proposé dès l’ouverture, avec la vidéo sélectionnée");
                var input = (TextBox)window.FindName("YoutubeInput");
                var title = (TextBlock)window.FindName("YoutubeTitle");
                var options = (ComboBox)window.FindName("YoutubeResolution");
                var status = (TextBlock)window.FindName("YoutubeStatus");
                var progress = (ProgressBar)window.FindName("YoutubeProgress");
                var read = (Button)window.FindName("YoutubeInspect");
                var save = (Button)window.FindName("YoutubeSave");
                var cancel = (Button)window.FindName("YoutubeCancel");
                var open = (Button)window.FindName("YoutubeOpenFolder");
                var hint = (TextBlock)window.FindName("YoutubeModeHint");
                var resultTitle = (TextBlock)window.FindName("YoutubeResultTitle");
                Check(!read.IsEnabled && !save.IsEnabled, "La page attend la saisie d’un identifiant ou d’un lien");
                audioMode.IsChecked = true;
                Check(hint.IsVisible && hint.Text.Contains("audio") && !save.IsEnabled,
                    "Le mode audio indique la prochaine étape avant l’analyse");
                videoMode.IsChecked = true;
                input.Text = "ancien lien";
                ClickVisible(read);
                input.Text = "indisponible";
                WaitForVideoPage(window, () => read.IsEnabled);
                Check(options.Items.Count == 0 && title.Text.Length == 0, "Une réponse tardive est écartée après un changement de saisie");
                ClickVisible(read);
                WaitForVideoPage(window, () => read.IsEnabled);
                Check(status.Text.Contains("privée") && !save.IsEnabled, "L’échec d’analyse affiche son motif et permet de recommencer");
                input.Text = YoutubeFixtureId;
                ClickVisible(read);
                WaitForVideoPage(window, () => read.IsEnabled);
                Check(title.Text == "Essai vidéo" && options.Items.Count == 2 && !save.IsEnabled,
                    "Le titre et les résolutions de la vidéo sont présentés avant le choix");
                options.SelectedIndex = 1;
                ClickVisible(save);
                Check(suggested == "Essai vidéo.mp4" && extension == "mp4" && filter.Contains("*.mp4")
                    && save.IsEnabled && !File.Exists(output),
                    "Fermer Enregistrer sous conserve le choix sans télécharger");
                destination = output;
                appearDuringSave = true;
                ClickVisible(save);
                WaitForVideoPage(window, () => save.IsEnabled);
                Check(confirmed == null && File.ReadAllText(output) == "créé pendant le transfert" && !open.IsVisible,
                    "Le nouveau chemin choisi ne donne aucun droit de remplacer un fichier apparu après la boîte de dialogue");
                appearDuringSave = false;
                ClickVisible(save);
                Check(confirmed == output && File.ReadAllText(output) == "créé pendant le transfert" && !open.IsVisible && save.IsEnabled,
                    "Refuser le remplacement conserve le fichier exact choisi");
                replace = true;
                ClickVisible(save);
                WaitForVideoPage(window, () => open.IsVisible);
                Check(File.ReadAllText(output) == "Essai vidéo 360" && ((TextBlock)window.FindName("YoutubeSavedPath")).Text == output
                    && resultTitle.Text.Contains("Vidéo"),
                    "Le téléchargement enregistre la résolution choisie au chemin personnalisé");
                ClickVisible(open);
                Check(opened == directory.FullName, "Ouvrir le dossier utilise le répertoire de destination");
                slowSave = true;
                ClickVisible(save);
                WaitForVideoPage(window, () => status.Text == "Assemblage du MP4");
                Check(progress.IsVisible && cancel.IsEnabled && !save.IsEnabled && !open.IsVisible,
                    "Le téléchargement en cours indique sa progression et peut être annulé");
                ClickVisible(cancel);
                WaitForVideoPage(window, () => save.IsEnabled);
                Check(status.Text.Contains("annul") && !progress.IsVisible && !open.IsVisible, "L’annulation réactive les commandes");
                slowSave = false;
                failSave = true;
                ClickVisible(save);
                WaitForVideoPage(window, () => save.IsEnabled);
                Check(status.Text.Contains("indisponible") && !open.IsVisible && read.IsEnabled,
                    "Le téléchargement échoué affiche son motif et permet de relire les résolutions");
                audioMode.IsChecked = true;
                Check(!options.IsVisible && save.IsEnabled && status.Text.Length == 0, "Passer à l’audio utilise l’analyse existante");
                videoMode.IsChecked = true;
                Check(options.SelectedIndex == 1 && options.IsVisible, "Revenir à la vidéo retrouve la résolution choisie");
                audioMode.IsChecked = true;
                input.Text = "uniquement audio";
                ClickVisible(read);
                WaitForVideoPage(window, () => read.IsEnabled);
                Check(title.Text == "Entretien" && options.Items.Count == 0 && !options.IsVisible && save.IsEnabled,
                    "Une source uniquement sonore peut être enregistrée sans choisir de résolution");
                videoMode.IsChecked = true;
                Check(!save.IsEnabled && hint.Text.Contains("résolution") && hint.IsVisible,
                    "L’absence de résolution est expliquée dans le mode vidéo");
                audioMode.IsChecked = true;
                destination = null;
                ClickVisible(save);
                Check(suggested == "Entretien.mp3" && extension == "mp3" && filter.Contains("*.mp3") && !open.IsVisible,
                    "L’audio ouvre Enregistrer sous avec un nom et un filtre MP3, puis accepte l’annulation");
                destination = Path.Combine(directory.FullName, "sortie personnalisée.mp3");
                File.WriteAllText(destination, "original");
                replace = false;
                ClickVisible(save);
                Check(confirmed == destination && File.ReadAllText(destination) == "original" && !open.IsVisible,
                    "L’audio respecte le refus de remplacement au chemin MP3 choisi");
                replace = true;
                slowSave = true;
                ClickVisible(save);
                WaitForVideoPage(window, () => status.Text == "Encodage MP3");
                Check(!videoMode.IsEnabled && !audioMode.IsEnabled, "Le mode de sortie reste fixé pendant l’enregistrement");
                ClickVisible(cancel);
                WaitForVideoPage(window, () => save.IsEnabled);
                Check(File.ReadAllText(destination) == "original" && !open.IsVisible && status.Text.Contains("annul"),
                    "L’annulation MP3 conserve le fichier d’origine et permet de réessayer");
                slowSave = false;
                ClickVisible(save);
                WaitForVideoPage(window, () => open.IsVisible);
                Check(File.ReadAllText(destination) == "Entretien en MP3" && resultTitle.Text.Contains("Audio")
                    && ((TextBlock)window.FindName("YoutubeSavedPath")).Text == destination,
                    "Le résultat audio identifie le MP3 enregistré au chemin choisi");
                videoMode.IsChecked = true;
                Check(!open.IsVisible && !save.IsEnabled, "Changer de mode efface le résultat du mode précédent");
                audioMode.IsChecked = true;
                input.Text = "vidéo muette";
                ClickVisible(read);
                WaitForVideoPage(window, () => read.IsEnabled);
                Check(!save.IsEnabled && hint.IsVisible && hint.Text.Contains("audio"),
                    "Une source muette indique l’indisponibilité de l’audio");
                videoMode.IsChecked = true;
                options.SelectedIndex = 0;
                Check(save.IsEnabled && options.IsVisible && hint.IsVisible && hint.Text.Contains("sans son"),
                    "Une vidéo muette reste enregistrable avec une indication explicite");
                input.Text = "nouveau lien";
                Check(title.Text.Length == 0 && options.Items.Count == 0 && !save.IsEnabled, "Changer de vidéo retire les choix précédents");
                audioMode.IsChecked = true;
                input.Text = "ancien lien";
                ClickVisible(read);
                page.Leave();
                WaitForVideoPage(window, () => !progress.IsVisible);
                Check(input.Text.Length == 0 && options.Items.Count == 0 && !save.IsEnabled && videoMode.IsChecked == true,
                    "Quitter la page restaure le mode vidéo, annule et efface la recherche");
                input.Text = "ancien lien";
                ClickVisible(read);
                window.Close();
                Check(!closed, "La fermeture attend la fin de l’opération en cours");
                WaitForVideoPage(window, () => closed);
            }
            finally
            {
                if (!closed) window.Close();
                SynchronizationContext.SetSynchronizationContext(previous);
                directory.Delete(true);
            }
        }
    }
}
