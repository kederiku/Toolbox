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
    internal sealed partial class Shell
    {
        NavigationPage CreateMetadataPage()
        {
            var page = new MetadataPage(Window);
            return new NavigationPage("MetadataNav", Get<StackPanel>("MetadataView"), "Nettoyeur de métadonnées", "\uE72E",
                "Retirez les coordonnées GPS, les informations EXIF et le nom de l’auteur avant de partager une copie.") {
                Category = "Confidentialité", OpenLabel = "Ouvrir le nettoyeur",
                PrimaryAction = Get<Button>("MetadataChoose"), OnLeave = page.Leave
            };
        }
    }

    internal sealed class MetadataEntry : BatchFile
    {
        internal readonly string SelectionError, Extension;

        internal MetadataEntry(string path) : base(path)
        {
            try
            {
                Source = Path.GetFullPath(path);
                Name = Path.GetFileName(Source);
                Extension = MetadataCleaner.Extension(Source);
            }
            catch (Exception ex) { SelectionError = ex.Message; }
            Reset();
        }

        internal void Reset()
        {
            Set(BatchFileState.Waiting, "En attente", "");
            if (SelectionError != null) Set(BatchFileState.Failed, "Refusé", SelectionError);
        }
    }

    internal sealed class MetadataPage
    {
        readonly Window window;
        readonly Button choose, save, cancel, batchFolder;
        readonly TextBlock name, status, summary, saved, note, outputHint;
        readonly Border result, batch, drop;
        readonly ListBox items;
        readonly Action<string> openFolder;
        List<MetadataEntry> selection = new List<MetadataEntry>();
        string savedDirectory;
        CancellationTokenSource operation;
        bool closeAfterOperation, leaving;

        internal MetadataPage(Window window, Action<string> openFolder = null)
        {
            this.window = window;
            this.openFolder = openFolder ?? delegate(string directory) {
                using (Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true, Verb = "open" })) { }
            };
            choose = (Button)window.FindName("MetadataChoose");
            save = (Button)window.FindName("MetadataSave");
            cancel = (Button)window.FindName("MetadataCancel");
            name = (TextBlock)window.FindName("MetadataName");
            status = (TextBlock)window.FindName("MetadataStatus");
            summary = (TextBlock)window.FindName("MetadataSummary");
            saved = (TextBlock)window.FindName("MetadataSaved");
            note = (TextBlock)window.FindName("MetadataNote");
            outputHint = (TextBlock)window.FindName("MetadataOutputHint");
            result = (Border)window.FindName("MetadataResult");
            batch = (Border)window.FindName("MetadataBatch");
            items = (ListBox)window.FindName("MetadataItems");
            batchFolder = (Button)window.FindName("MetadataBatchFolder");
            FileWorkspace.BindLayout(window, "Metadata", items);
            choose.Click += delegate {
                var dialog = new OpenFileDialog { Title = "Choisir des photos et des documents", Filter = MetadataCleaner.Filter, Multiselect = true };
                if (dialog.ShowDialog(window) == true) SelectFiles(dialog.FileNames);
            };
            save.Click += async delegate { await ChooseDestinationAsync(); };
            cancel.Click += delegate {
                if (operation == null) return;
                operation.Cancel();
                cancel.IsEnabled = false;
            };
            drop = (Border)window.FindName("MetadataDrop");
            drop.PreviewDragOver += delegate(object sender, DragEventArgs e) {
                bool accepts = operation == null && (e.AllowedEffects & DragDropEffects.Copy) != 0 && e.Data.GetDataPresent(DataFormats.FileDrop);
                drop.Tag = accepts ? "Drag" : null;
                e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            drop.DragLeave += delegate { drop.Tag = null; };
            drop.Drop += delegate(object sender, DragEventArgs e) {
                drop.Tag = null;
                e.Handled = true;
                if (operation == null) SelectFiles(e.Data.GetData(DataFormats.FileDrop) as string[]);
            };
            ((Button)window.FindName("MetadataFolder")).Click += OpenSavedFolder;
            batchFolder.Click += OpenSavedFolder;
            window.Closing += delegate(object sender, CancelEventArgs e) {
                if (operation == null || e.Cancel) return;
                e.Cancel = true;
                closeAfterOperation = true;
                Leave();
            };
            Refresh();
        }

        void OpenSavedFolder(object sender, RoutedEventArgs args)
        {
            if (savedDirectory == null) return;
            try { openFolder(savedDirectory); }
            catch (Win32Exception) { status.Text = "Windows n’a pas pu ouvrir le dossier des copies."; }
        }

        internal void SelectFile(string path) { SelectFiles(new[] { path }); }

        internal void SelectFiles(IEnumerable<string> paths)
        {
            if (operation != null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<MetadataEntry>();
            foreach (string path in paths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var entry = new MetadataEntry(path);
                if (seen.Add(entry.Source)) entries.Add(entry);
            }
            BatchFile.DistinguishNames(entries);

            selection = entries;
            items.ItemsSource = selection;
            savedDirectory = null;
            summary.Text = saved.Text = "";
            status.Text = entries.Count == 0 ? "Déposez un ou plusieurs fichiers." : "";
            if (entries.Count == 1 && entries[0].SelectionError != null) status.Text = entries[0].SelectionError;
            Refresh();
        }

        async Task ChooseDestinationAsync()
        {
            if (operation != null || !save.IsEnabled) return;
            if (selection.Count > 1)
            {
                string folder = ConversionBatch.ChooseFolder(window, Path.GetDirectoryName(selection.First(entry => entry.SelectionError == null).Source),
                    "Choisissez le dossier où enregistrer les copies nettoyées.");
                if (folder != null) await SaveBatchToAsync(folder);
                return;
            }
            string source = selection[0].Source;
            string ext = Path.GetExtension(source);
            var single = new SaveFileDialog {
                Title = "Enregistrer la copie nettoyée", DefaultExt = ext, Filter = "Même format (*" + ext + ")|*" + ext,
                FileName = MetadataCleaner.CopyName(source),
                InitialDirectory = Path.GetDirectoryName(source), OverwritePrompt = false
            };
            if (single.ShowDialog(window) != true) return;
            string path = single.FileName;
            if (string.Equals(source, path, StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "Choisissez un autre nom pour conserver le fichier d’origine.";
                return;
            }
            bool replace = File.Exists(path);
            if (replace && MessageBox.Show(window, "Remplacer le fichier « " + Path.GetFileName(path) + " » ?",
                "Enregistrer la copie", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await SaveToAsync(path, replace);
        }

        internal Task SaveToAsync(string path, bool overwrite)
        {
            return selection.Count == 1 ? RunAsync(path, overwrite, false) : Task.CompletedTask;
        }

        internal Task SaveBatchToAsync(string folder)
        {
            return selection.Count > 1 ? RunAsync(folder, false, true) : Task.CompletedTask;
        }

        async Task RunAsync(string destination, bool overwrite, bool multiple)
        {
            if (operation != null || !selection.Any(entry => entry.SelectionError == null)) return;
            savedDirectory = null;
            leaving = false;
            summary.Text = saved.Text = status.Text = "";
            foreach (var entry in selection) entry.Reset();
            using (var running = new CancellationTokenSource())
            {
                operation = running;
                Refresh();
                try
                {
                    await Task.Run(() => ProcessSelection(destination, overwrite, multiple, running.Token));
                    if (multiple)
                    {
                        foreach (var entry in selection.Where(entry => entry.State == BatchFileState.Waiting))
                            entry.Set(BatchFileState.Skipped, "Non traité", "Lot annulé avant ce fichier.");
                        int copies = selection.Count(entry => entry.State == BatchFileState.Saved);
                        int failures = selection.Count(entry => entry.State == BatchFileState.Failed);
                        int canceled = selection.Count - copies - failures;
                        status.Text = (canceled > 0 ? "Lot annulé. " : "Lot terminé. ") +
                            copies + (copies == 1 ? " copie enregistrée" : " copies enregistrées") +
                            " · " + failures + " refus" +
                            (canceled > 0 ? " · " + canceled + (canceled == 1 ? " fichier non traité" : " fichiers non traités") : "");
                    }
                    else if (running.IsCancellationRequested && selection[0].State == BatchFileState.Waiting)
                        status.Text = "Nettoyage annulé.";
                }
                finally
                {
                    operation = null;
                    if (leaving) Clear();
                    Refresh();
                    if (closeAfterOperation) window.Close();
                }
            }
            if (!leaving && multiple) batch.BringIntoView();
            else if (savedDirectory != null) result.BringIntoView();
        }

        void ProcessSelection(string destination, bool overwrite, bool multiple, CancellationToken token)
        {
            var originals = new HashSet<string>(selection.Select(entry => entry.Source), StringComparer.OrdinalIgnoreCase);
            using (PhotoMetadata.BeginBatch())
            {
                for (int i = 0; i < selection.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    var entry = selection[i];
                    if (entry.SelectionError != null) continue;
                    int index = i + 1;
                    window.Dispatcher.Invoke(new Action(delegate {
                        entry.Set(BatchFileState.Running, "En cours…", "");
                        status.Text = multiple ? string.Format("Fichier {0} sur {1} · {2}", index, selection.Count, Path.GetFileName(entry.Source))
                            : "Nettoyage et vérification de la copie en cours…";
                    }));
                    try
                    {
                        if (!File.Exists(entry.Source))
                            throw new IOException(Directory.Exists(entry.Source) ? "Sélectionnez les fichiers contenus dans ce dossier."
                                : "Ce fichier est introuvable ou inaccessible.");
                        Action<FileProgress> progress = update => window.Dispatcher.Invoke(new Action(delegate {
                            entry.Report(update);
                            if (!multiple) status.Text = update.Caption + " · " + update.Status;
                        }));
                        var report = multiple ? MetadataCleaner.SaveToFolder(entry.Source, destination, originals, token, progress)
                            : MetadataCleaner.Save(entry.Source, destination, overwrite, token, progress);
                        window.Dispatcher.Invoke(new Action(delegate {
                            string details = report.Summary();
                            savedDirectory = Path.GetDirectoryName(report.Destination);
                            string outcome = !report.Changed ? "Déjà propre" : report.HasReportedRemoval ? "Copie nettoyée" : "Copie vérifiée";
                            entry.Set(BatchFileState.Saved, outcome,
                                details + Environment.NewLine + "Copie enregistrée · " + Path.GetFileName(report.Destination));
                            if (!multiple) { summary.Text = details; saved.Text = report.Destination; status.Text = ""; }
                        }));
                    }
                    catch (OperationCanceledException)
                    {
                        window.Dispatcher.Invoke(new Action(delegate {
                            entry.Set(BatchFileState.Canceled, "Annulé", "Aucune copie enregistrée pour ce fichier.");
                            if (!multiple) status.Text = "Nettoyage annulé.";
                        }));
                        break;
                    }
                    catch (Exception ex)
                    {
                        window.Dispatcher.Invoke(new Action(delegate {
                            entry.Set(BatchFileState.Failed, "Refusé", ex.Message);
                            if (!multiple) status.Text = ex.Message;
                        }));
                    }
                }
            }
        }

        void Refresh()
        {
            bool multiple = selection.Count > 1;
            choose.IsEnabled = operation == null;
            choose.Content = selection.Count == 0 ? "Choisir des fichiers…" : "Changer de sélection…";
            choose.SetResourceReference(FrameworkElement.StyleProperty, selection.Count == 0 ? "PrimaryAction" : "Action");
            save.IsEnabled = operation == null && selection.Any(entry => entry.SelectionError == null);
            save.Content = multiple ? "Enregistrer les copies nettoyées…" : "Enregistrer une copie nettoyée…";
            name.Text = multiple ? selection.Count + " fichiers sélectionnés"
                : selection.Count == 1 ? selection[0].Name : "Déposez vos photos et documents ici";
            outputHint.Text = "";
            if (operation == null && savedDirectory == null && selection.Any(entry => entry.SelectionError == null))
                outputHint.Text = multiple ? "Les copies seront enregistrées dans le dossier de votre choix."
                    : "Nom proposé · " + MetadataCleaner.CopyName(selection[0].Source);
            note.Visibility = selection.Any(e => e.Extension == ".docx" || e.Extension == ".xlsx" || e.Extension == ".pptx" || e.Extension == ".pdf")
                ? Visibility.Visible : Visibility.Collapsed;
            cancel.Visibility = operation == null ? Visibility.Collapsed : Visibility.Visible;
            cancel.IsEnabled = operation != null && !operation.IsCancellationRequested;
            result.Visibility = savedDirectory != null && !multiple ? Visibility.Visible : Visibility.Collapsed;
            batch.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
            batchFolder.Visibility = multiple && savedDirectory != null && operation == null ? Visibility.Visible : Visibility.Collapsed;
        }

        void Clear()
        {
            selection = new List<MetadataEntry>();
            items.ItemsSource = selection;
            savedDirectory = null;
            summary.Text = saved.Text = status.Text = "";
        }

        internal void Leave()
        {
            drop.Tag = null;
            leaving = true;
            if (operation != null) { operation.Cancel(); return; }
            Clear();
            Refresh();
        }
    }
}
