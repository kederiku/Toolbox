using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AtelierPC
{
    internal abstract class MediaConversionPage<T> where T : class, IConversionSource
    {
        readonly Window window;
        protected readonly ComboBox formats, qualities;
        protected T selected;
        readonly string kind, pluralLabel, inputFilter;
        readonly Button choose, save, cancel, openFolder;
        readonly TextBlock name, details, warning, status, outputHint;
        readonly ProgressBar progress;
        readonly Border result, dropZone;
        readonly ListBox batchList;
        readonly TextBlock resultTitle, resultName, resultFolder, resultGlyph;
        readonly string singleResultTitle;
        readonly Action<string> showFolder;
        CancellationTokenSource running;
        bool leaving, closeAfterOperation, closed;
        string savedFolder;
        bool hasResult;
        BatchFile[] batchFiles = new BatchFile[0];

        protected delegate void Encoder(string path, bool overwrite, CancellationToken token, Action<double> report);
        protected abstract T Open(string path, CancellationToken token);
        protected abstract Encoder GetEncoder();
        protected abstract Task ConvertBatchAsync(ConversionBatch batch);
        protected abstract string Details { get; }
        protected abstract string Warning { get; }
        protected abstract string BatchWarning { get; }
        protected virtual bool ConfirmWarning { get { return !string.IsNullOrEmpty(Warning); } }
        protected virtual double Duration { get { return 0; } }
        protected virtual Task PrepareAsync(T media, CancellationToken token) { return Task.CompletedTask; }
        protected abstract void RefreshChoices(bool idle);
        protected virtual void SourceChanged() { }

        protected MediaConversionPage(Window owner, string prefix, string kind, string pluralLabel, string inputFilter, Action<string> showFolder)
        {
            this.kind = kind;
            this.pluralLabel = pluralLabel;
            this.inputFilter = inputFilter;
            window = owner;
            this.showFolder = showFolder ?? OpenInExplorer;
            choose = (Button)owner.FindName(prefix + "Choose");
            save = (Button)owner.FindName(prefix + "Save");
            cancel = (Button)owner.FindName(prefix + "Cancel");
            openFolder = (Button)owner.FindName(prefix + "OpenFolder");
            formats = (ComboBox)owner.FindName(prefix + "Format");
            qualities = (ComboBox)owner.FindName(prefix + "Quality");
            name = (TextBlock)owner.FindName(prefix + "Name");
            details = (TextBlock)owner.FindName(prefix + "Details");
            warning = (TextBlock)owner.FindName(prefix + "Warning");
            status = (TextBlock)owner.FindName(prefix + "Status");
            outputHint = (TextBlock)owner.FindName(prefix + "OutputHint");
            progress = (ProgressBar)owner.FindName(prefix + "Progress");
            result = (Border)owner.FindName(prefix + "Result");
            resultTitle = (TextBlock)owner.FindName(prefix + "ResultTitle");
            resultGlyph = (TextBlock)owner.FindName(prefix + "ResultGlyph");
            resultName = (TextBlock)owner.FindName(prefix + "SavedName");
            resultFolder = (TextBlock)owner.FindName(prefix + "SavedFolder");
            singleResultTitle = resultTitle.Text;
            batchList = (ListBox)owner.FindName(prefix + "BatchFiles");

            if (qualities != null)
            {
                qualities.ItemsSource = new[] {
                    new KeyValuePair<ConversionQuality, string>(ConversionQuality.Balanced, "Équilibrée"),
                    new KeyValuePair<ConversionQuality, string>(ConversionQuality.Smaller, "Fichier plus petit"),
                    new KeyValuePair<ConversionQuality, string>(ConversionQuality.Higher, "Qualité élevée")
                };
                qualities.SelectedValue = ConversionQuality.Balanced;
            }
            choose.Click += async delegate { await ChooseAsync(); };
            save.Click += async delegate { await SaveAsync(); };
            cancel.Click += delegate { Cancel(); };
            openFolder.Click += delegate { OpenSavedFolder(); };
            FileWorkspace.BindLayout(owner, prefix, batchList);
            dropZone = (Border)owner.FindName(prefix + "DropZone");
            dropZone.PreviewDragOver += delegate(object sender, DragEventArgs e) {
                bool acceptsFiles = running == null && (e.AllowedEffects & DragDropEffects.Copy) != 0 && e.Data.GetDataPresent(DataFormats.FileDrop);
                dropZone.Tag = acceptsFiles ? "Drag" : null;
                e.Effects = acceptsFiles ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            dropZone.DragLeave += delegate { dropZone.Tag = null; };
            dropZone.Drop += async delegate(object sender, DragEventArgs e) {
                dropZone.Tag = null;
                e.Handled = true;
                await LoadDroppedAsync(e.Data);
            };
            owner.Closing += delegate(object sender, CancelEventArgs e) {
                if (running == null || e.Cancel) return;
                e.Cancel = true;
                closeAfterOperation = true;
                Leave();
            };
            owner.Closed += delegate { closed = true; Leave(); };
        }

        protected void Refresh()
        {
            bool idle = running == null;
            bool batch = batchFiles.Length > 1;
            choose.IsEnabled = formats.IsEnabled = idle;
            if (qualities != null) qualities.IsEnabled = idle;
            RefreshChoices(idle);
            save.IsEnabled = idle && formats.SelectedItem != null && (batch || (selected != null && GetEncoder() != null));
            save.Content = batch ? "Convertir le lot…" : "Convertir et enregistrer…";
            cancel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
            cancel.IsEnabled = !idle && !running.IsCancellationRequested;
            name.Text = batch ? batchFiles.Length + " fichiers sélectionnés" :
                selected == null ? "Déposez " + pluralLabel + " ici" : Path.GetFileName(selected.Source);
            choose.Content = selected == null && !batch ? "Choisir " + pluralLabel + "…" : "Changer la sélection…";
            choose.SetResourceReference(FrameworkElement.StyleProperty, selected == null && !batch ? "PrimaryAction" : "Action");
            batchList.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
            details.Text = batch ? "Un même format pour tout le lot." : selected == null ? "Un ou plusieurs fichiers à la fois." : Details;
            var format = formats.SelectedItem as MediaOutput;
            if (!idle) outputHint.Text = "";
            else if (batch) outputHint.Text = "Vous choisirez un dossier pour les copies.";
            else if (selected != null && format != null) outputHint.Text = "Nom proposé\n" + SuggestedName(format);
            else outputHint.Text = "Choisissez un fichier pour activer la conversion.";
            if (selected != null && HasDisplayableDuration(Duration))
            {
                var duration = TimeSpan.FromSeconds(Duration);
                details.Text += Duration < 1 ? " · moins d’une seconde" :
                    string.Format(" · {0}:{1:00}:{2:00}", (long)duration.TotalHours, duration.Minutes, duration.Seconds);
            }
            warning.Text = batch ? BatchWarning : selected == null ? "" : Warning;
            warning.Visibility = string.IsNullOrEmpty(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
            result.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;
            openFolder.IsEnabled = idle && savedFolder != null;
        }

        static bool HasDisplayableDuration(double seconds)
        {
            return seconds > 0 && seconds < Math.Floor(TimeSpan.MaxValue.TotalSeconds);
        }

        string SuggestedName(MediaOutput format)
        {
            return Path.GetFileNameWithoutExtension(selected.Source) + (kind == "audio" ? " - converti." : " - convertie.") + format.Extension;
        }

        internal Task LoadDroppedAsync(IDataObject data)
        {
            return LoadFilesAsync(data.GetData(DataFormats.FileDrop) as string[]);
        }

        async Task ChooseAsync()
        {
            if (running != null) return;
            var dialog = new OpenFileDialog { Title = "Choisir " + pluralLabel, Multiselect = true, CheckFileExists = true, Filter = inputFilter };
            if (dialog.ShowDialog(window) == true) await LoadFilesAsync(dialog.FileNames);
        }

        internal async Task LoadFilesAsync(string[] paths)
        {
            if (running != null) return;
            var files = (paths ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) { status.Text = "Déposez un ou plusieurs fichiers dans cette zone."; return; }
            if (files.Length == 1) { await LoadAsync(files[0]); return; }
            ClearSelection();
            batchFiles = files.Select(p => new BatchFile(p)).ToArray();
            BatchFile.DistinguishNames(batchFiles);
            batchList.ItemsSource = batchFiles;
            Refresh();
        }

        internal async Task LoadAsync(string path)
        {
            if (running != null) return;
            ClearSelection();
            await RunAsync("Lecture du fichier…", true, async token => {
                var media = await Task.Run(() => Open(path, token), token);
                try
                {
                    await PrepareAsync(media, token);
                    token.ThrowIfCancellationRequested();
                    selected = media;
                    SourceChanged();
                }
                finally { if (selected != media) media.Dispose(); }
            });
        }

        async Task SaveAsync()
        {
            var format = (MediaOutput)formats.SelectedItem;
            if (running != null || format == null) return;
            if (batchFiles.Length > 1)
            {
                string folder = ConversionBatch.ChooseFolder(window, Path.GetDirectoryName(batchFiles[0].Source));
                if (folder != null) await SaveBatchToAsync(folder);
                return;
            }
            if (selected == null) return;
            if (ConfirmWarning && MessageBox.Show(window, Warning + "\n\nContinuer la conversion ?",
                "Enregistrer la copie", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var dialog = new SaveFileDialog {
                Title = "Enregistrer la copie " + kind, Filter = format + "|*." + format.Extension, DefaultExt = "." + format.Extension,
                AddExtension = true, OverwritePrompt = false, InitialDirectory = Path.GetDirectoryName(selected.Source),
                FileName = SuggestedName(format)
            };
            if (dialog.ShowDialog(window) != true) return;
            string path = dialog.FileName;
            if (string.Equals(path, selected.Source, StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "Choisissez un autre nom pour conserver le fichier d’origine.";
                return;
            }
            bool overwrite = File.Exists(path);
            if (overwrite && MessageBox.Show(window, "Remplacer le fichier « " + Path.GetFileName(path) + " » ?",
                "Enregistrer la copie " + kind, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await SaveToAsync(path, overwrite);
        }

        internal async Task SaveToAsync(string path, bool overwrite)
        {
            var format = (MediaOutput)formats.SelectedItem;
            if (running != null || selected == null || format == null) return;
            // Lire les réglages WPF sur le thread de la fenêtre avant de lancer le moteur.
            var encode = GetEncoder();
            if (encode == null) return;
            hasResult = false;
            savedFolder = null;
            await RunAsync("Conversion en " + format.Code + "…", !HasDisplayableDuration(Duration), async token => {
                IProgress<double> report = new Progress<double>(value => {
                    if (running == null || token != running.Token || token.IsCancellationRequested || progress.IsIndeterminate) return;
                    progress.Value = Math.Max(0, Math.Min(100, value));
                    status.Text = string.Format("Conversion en {0} · {1:0} %", format.Code, progress.Value);
                });
                await Task.Run(() => encode(path, overwrite, token, report.Report), token);
                SetResult(singleResultTitle, Path.GetFileName(path), Path.GetDirectoryName(Path.GetFullPath(path)));
            });
            FocusResult();
        }

        internal async Task SaveBatchToAsync(string folder)
        {
            var format = (MediaOutput)formats.SelectedItem;
            if (running != null || batchFiles.Length < 2 || format == null) return;
            hasResult = false;
            savedFolder = null;
            await RunAsync("Préparation du lot…", false, async token => {
                foreach (var file in batchFiles) file.Set(BatchFileState.Waiting, "En attente", "");
                string directory = Path.GetFullPath(folder);
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Le dossier de destination est inaccessible.");
                var batch = new ConversionBatch(batchFiles, directory, format.Extension, token, (index, percent) => {
                    if (running == null || running.Token != token || token.IsCancellationRequested) return;
                    progress.Value = (index * 100.0 + Math.Max(0, Math.Min(100, percent))) / batchFiles.Length;
                    status.Text = string.Format("Fichier {0} sur {1} · {2}", index + 1, batchFiles.Length, batchFiles[index].Name);
                    if (percent == 0) batchList.ScrollIntoView(batchFiles[index]);
                });
                try { await ConvertBatchAsync(batch); }
                finally
                {
                    SetResult(token.IsCancellationRequested ? "Lot interrompu" : "Bilan du lot", batch.Summary,
                        batch.Saved > 0 ? directory : null, batch.Saved == batchFiles.Length);
                }
            });
            FocusResult();
        }

        void SetResult(string title, string summary, string directory, bool complete = true)
        {
            hasResult = true;
            savedFolder = directory;
            result.Tag = complete ? "Complet" : null;
            resultGlyph.Text = complete ? "\uE73E" : "\uE946";
            resultTitle.Text = title;
            resultName.Text = summary;
            resultFolder.Text = directory ?? "";
        }

        void FocusResult()
        {
            if (savedFolder != null && !closed) { openFolder.Focus(); openFolder.BringIntoView(); }
        }

        async Task RunAsync(string message, bool indeterminate, Func<CancellationToken, Task> work)
        {
            using (var operation = new CancellationTokenSource())
            {
                running = operation;
                leaving = false;
                status.Text = message;
                progress.Value = 0;
                progress.IsIndeterminate = indeterminate;
                progress.Visibility = Visibility.Visible;
                Refresh();
                try
                {
                    await work(operation.Token);
                    status.Text = "";
                }
                catch (OperationCanceledException) { status.Text = "Opération annulée."; }
                catch (Exception ex) { status.Text = ex.Message; }
                finally
                {
                    running = null;
                    progress.IsIndeterminate = false;
                    progress.Visibility = Visibility.Collapsed;
                    if (leaving) ClearSelection();
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

        static void OpenInExplorer(string folder)
        {
            using (Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true, Verb = "open" })) { }
        }

        void OpenSavedFolder()
        {
            string directory = savedFolder;
            if (directory == null) return;
            if (!Directory.Exists(directory)) { status.Text = "Le dossier d’enregistrement n’est plus accessible."; return; }
            try { showFolder(directory); }
            catch (Win32Exception) { status.Text = "Windows n’a pas pu ouvrir le dossier " + directory; }
        }

        void Cancel()
        {
            if (running == null || running.IsCancellationRequested) return;
            cancel.IsEnabled = false;
            status.Text = "Annulation en cours…";
            running.Cancel();
        }

        void ClearSelection()
        {
            if (selected != null) selected.Dispose();
            selected = null;
            batchFiles = new BatchFile[0];
            batchList.ItemsSource = null;
            SourceChanged();
            savedFolder = null;
            hasResult = false;
            status.Text = "";
        }

        internal void Leave()
        {
            dropZone.Tag = null;
            leaving = true;
            if (running != null) Cancel();
            else { ClearSelection(); Refresh(); }
        }
    }
}
