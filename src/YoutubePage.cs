using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        NavigationPage CreateYoutubePage()
        {
            var page = new YoutubePage(Window);
            return new NavigationPage("YoutubeNav", Get<StackPanel>("YoutubeView"), "Télécharger une vidéo YouTube", "\uE896",
                "Collez un lien ou un identifiant YouTube pour enregistrer la vidéo en MP4 ou sa piste audio en MP3.") {
                Category = "Fichiers", OpenLabel = "Télécharger une vidéo YouTube",
                PrimaryAction = Get<TextBox>("YoutubeInput"), OnLeave = page.Leave
            };
        }
    }

    internal sealed class YoutubePage
    {
        internal delegate void SaveVideo(YoutubeVideo video, YoutubeResolution resolution, string path,
            bool overwrite, CancellationToken token, Action<string> progress);
        internal delegate void SaveAudio(YoutubeVideo video, string path, bool overwrite, CancellationToken token, Action<string> progress);

        readonly Window window;
        readonly Func<string, CancellationToken, YoutubeVideo> inspect;
        readonly SaveVideo save;
        readonly SaveAudio saveAudio;
        readonly Func<SaveFileDialog, bool?> showDialog;
        readonly Func<string, bool> confirmOverwrite;
        readonly Action<string> openFolder;
        readonly TextBox input;
        readonly Button inspectButton, saveButton, cancelButton, openButton;
        readonly TextBlock title, status, savedPath, modeHint, resultTitle;
        readonly RadioButton videoMode, audioMode;
        readonly ComboBox resolutions;
        readonly StackPanel choices, resolutionFields;
        readonly Border result;
        readonly ProgressBar progress;
        YoutubeVideo video;
        CancellationTokenSource running;
        bool saving, discarded, closeAfterOperation, closed;

        internal YoutubePage(Window window, Func<string, CancellationToken, YoutubeVideo> inspect = null,
            SaveVideo save = null, SaveAudio saveAudio = null, Func<SaveFileDialog, bool?> showDialog = null, Func<string, bool> confirmOverwrite = null,
            Action<string> openFolder = null)
        {
            this.window = window;
            this.inspect = inspect ?? YoutubeDownloader.Inspect;
            this.save = save ?? YoutubeDownloader.Save;
            this.saveAudio = saveAudio ?? YoutubeDownloader.SaveAudio;
            this.showDialog = showDialog ?? (dialog => dialog.ShowDialog(window));
            this.confirmOverwrite = confirmOverwrite ?? ConfirmOverwrite;
            this.openFolder = openFolder ?? delegate(string path) {
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" })) { }
            };
            input = Get<TextBox>("Input");
            videoMode = Get<RadioButton>("VideoMode");
            audioMode = Get<RadioButton>("AudioMode");
            modeHint = Get<TextBlock>("ModeHint");
            choices = Get<StackPanel>("Choices");
            resolutionFields = Get<StackPanel>("ResolutionFields");
            title = Get<TextBlock>("Title");
            resolutions = Get<ComboBox>("Resolution");
            inspectButton = Get<Button>("Inspect");
            saveButton = Get<Button>("Save");
            cancelButton = Get<Button>("Cancel");
            status = Get<TextBlock>("Status");
            progress = Get<ProgressBar>("Progress");
            result = Get<Border>("Result");
            resultTitle = Get<TextBlock>("ResultTitle");
            savedPath = Get<TextBlock>("SavedPath");
            openButton = Get<Button>("OpenFolder");
            input.TextChanged += delegate { Discard(); };
            input.KeyDown += async delegate(object sender, KeyEventArgs e) {
                if (e.Key != Key.Enter || !inspectButton.IsEnabled) return;
                e.Handled = true;
                await InspectAsync();
            };
            resolutions.SelectionChanged += delegate { Refresh(); };
            audioMode.Checked += ModeChanged;
            videoMode.Checked += ModeChanged;
            inspectButton.Click += async delegate { await InspectAsync(); };
            saveButton.Click += async delegate { await SaveAsync(); };
            cancelButton.Click += delegate {
                if (running == null) return;
                status.Text = "Annulation en cours…";
                running.Cancel();
                Refresh();
            };
            openButton.Click += delegate { OpenSavedFolder(); };
            window.Closing += delegate(object sender, CancelEventArgs e) {
                if (running == null || e.Cancel) return;
                e.Cancel = true;
                closeAfterOperation = true;
                Leave();
            };
            window.Closed += delegate { closed = true; Leave(); };
            Refresh();
        }

        T Get<T>(string name) where T : FrameworkElement { return (T)window.FindName("Youtube" + name); }

        bool AudioOnly { get { return audioMode.IsChecked == true; } }

        void ModeChanged(object sender, RoutedEventArgs e)
        {
            savedPath.Text = "";
            if (video != null) status.Text = "";
            Refresh();
        }

        void Refresh()
        {
            bool idle = running == null;
            bool hasVideo = video != null && video.Resolutions.Length > 0;
            bool hasAudio = video != null && !string.IsNullOrEmpty(video.AudioSelector);
            bool silent = hasVideo && !hasAudio;
            videoMode.IsEnabled = audioMode.IsEnabled = idle;
            videoMode.Content = silent ? "Vidéo sans son (MP4)" : "Vidéo avec son (MP4)";
            modeHint.Text = "";
            if (idle)
            {
                if (video == null)
                    modeHint.Text = AudioOnly ? "Analysez le lien pour vérifier la présence d’une piste audio."
                        : "Analysez le lien pour afficher les résolutions disponibles.";
                else if (AudioOnly && !hasAudio)
                    modeHint.Text = "Aucune piste audio n’est disponible pour cette source.";
                else if (!AudioOnly && !hasVideo)
                    modeHint.Text = "Aucune résolution vidéo n’est disponible. Choisissez « Audio seul (MP3) » pour enregistrer le son.";
                else if (!AudioOnly && silent)
                    modeHint.Text = "Cette source est muette. Le MP4 sera enregistré sans son.";
            }
            input.IsReadOnly = saving;
            inspectButton.IsEnabled = idle && !string.IsNullOrWhiteSpace(input.Text);
            inspectButton.SetResourceReference(FrameworkElement.StyleProperty, video == null ? "PrimaryAction" : "Action");
            choices.Visibility = video == null ? Visibility.Collapsed : Visibility.Visible;
            resolutionFields.Visibility = !AudioOnly && hasVideo ? Visibility.Visible : Visibility.Collapsed;
            resolutions.IsEnabled = idle;
            saveButton.IsEnabled = idle && (AudioOnly ? hasAudio : hasVideo && resolutions.SelectedItem != null);
            progress.IsIndeterminate = !idle;
            cancelButton.Visibility = progress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            cancelButton.IsEnabled = !idle && !running.IsCancellationRequested;
            result.Visibility = savedPath.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        void Discard()
        {
            discarded = true;
            if (running != null) running.Cancel();
            video = null;
            resolutions.ItemsSource = null;
            title.Text = savedPath.Text = status.Text = "";
            Refresh();
        }

        internal void Leave()
        {
            if (input.Text.Length != 0) input.Clear();
            else Discard();
            videoMode.IsChecked = true;
        }

        async Task InspectAsync()
        {
            if (!inspectButton.IsEnabled) return;
            string source = input.Text;
            Discard();
            await RunAsync("Analyse du lien YouTube…", async token => {
                var found = await Task.Run(() => inspect(source, token), token);
                token.ThrowIfCancellationRequested();
                video = found;
                title.Text = found.Title;
                resolutions.ItemsSource = found.Resolutions;
                resolutions.SelectedIndex = -1;
                status.Text = "";
            });
            if (video == null || closed) return;
            if (resolutionFields.IsVisible) resolutions.Focus();
            else if (saveButton.IsEnabled) saveButton.Focus();
        }

        string ChoosePath(string name, bool audio)
        {
            string extension = audio ? ".mp3" : ".mp4";
            var dialog = new SaveFileDialog {
                Title = audio ? "Enregistrer l’audio YouTube sous" : "Enregistrer la vidéo YouTube sous", FileName = name,
                Filter = audio ? "Audio MP3 (*.mp3)|*.mp3" : "Vidéo MP4 (*.mp4)|*.mp4",
                DefaultExt = extension, AddExtension = true, OverwritePrompt = false
            };
            dialog.FileOk += delegate(object sender, CancelEventArgs e) {
                if (string.Equals(Path.GetExtension(dialog.FileName), extension, StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                MessageBox.Show(window, "Choisissez un nom se terminant par " + extension + ".", "Nom du fichier",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            };
            return showDialog(dialog) == true ? dialog.FileName : null;
        }

        bool ConfirmOverwrite(string path)
        {
            return MessageBox.Show(window, "Remplacer le fichier existant ?\n\n" + path, "Remplacer le fichier",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        async Task SaveAsync()
        {
            var selected = video;
            var resolution = resolutions.SelectedItem as YoutubeResolution;
            bool audio = AudioOnly;
            if (!saveButton.IsEnabled) return;
            string path;
            bool overwrite;
            try
            {
                path = ChoosePath(audio ? selected.AudioFileName : selected.FileName, audio);
                if (path == null) return;
                path = Path.GetFullPath(path);
                overwrite = File.Exists(path);
                if (overwrite && !confirmOverwrite(path)) return;
            }
            catch (Exception ex) { status.Text = ex.Message; return; }
            if (video != selected || AudioOnly != audio || (!audio && resolutions.SelectedItem != resolution) || closed) return;
            saving = true;
            savedPath.Text = "";
            await RunAsync("Préparation du téléchargement…", async token => {
                IProgress<string> messages = new Progress<string>(message => {
                    if (running != null && running.Token == token && !token.IsCancellationRequested) status.Text = message;
                });
                await Task.Run(delegate {
                    if (audio) saveAudio(selected, path, overwrite, token, messages.Report);
                    else save(selected, resolution, path, overwrite, token, messages.Report);
                }, token);
                if (discarded) return;
                savedPath.Text = path;
                resultTitle.Text = audio ? "Audio enregistré" : "Vidéo enregistrée";
                status.Text = "";
            });
            if (savedPath.Text.Length != 0 && !closed) { openButton.Focus(); openButton.BringIntoView(); }
        }

        async Task RunAsync(string message, Func<CancellationToken, Task> work)
        {
            using (var operation = new CancellationTokenSource())
            {
                running = operation;
                discarded = false;
                status.Text = message;
                Refresh();
                try { await work(operation.Token); }
                catch (OperationCanceledException) { if (!discarded) status.Text = "Opération annulée."; }
                catch (Exception ex) { if (!discarded) status.Text = ex.Message; }
                finally
                {
                    running = null;
                    saving = false;
                    Refresh();
                    if (closeAfterOperation) ScheduleClose();
                }
            }
        }

        void ScheduleClose()
        {
            closeAfterOperation = false;
            window.Dispatcher.BeginInvoke(new Action(delegate { if (!closed) window.Close(); }));
        }

        void OpenSavedFolder()
        {
            if (savedPath.Text.Length == 0) return;
            var directory = Path.GetDirectoryName(savedPath.Text);
            if (!Directory.Exists(directory)) { status.Text = "Le dossier d’enregistrement n’est plus accessible."; return; }
            try { openFolder(directory); }
            catch (Win32Exception) { status.Text = "Windows n’a pas pu ouvrir le dossier " + directory; }
        }
    }
}
