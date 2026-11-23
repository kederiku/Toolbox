using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        NavigationPage CreateWatermarkPage()
        {
            var page = new WatermarkPage(Window);
            return new NavigationPage("WatermarkNav", Get<StackPanel>("WatermarkView"), "Ajouter un filigrane", "\uE8D2",
                "Inscrivez votre texte sur un PDF, un document Word ou une image et enregistrez une copie.") {
                Category = "Fichiers", OpenLabel = "Ajouter un filigrane", PrimaryAction = Get<Button>("WatermarkChoose"), OnLeave = page.Leave
            };
        }
    }

    internal sealed class WatermarkPage
    {
        readonly Window window;
        readonly TextBox text;
        readonly ComboBox mode, position;
        readonly Slider size, opacity;
        readonly TextBlock status, name, notice;
        readonly Button choose, save, cancel, folder;
        readonly DispatcherTimer previewTimer;
        WatermarkSource source;
        FileStream original;
        CancellationTokenSource operation;
        string savedDirectory;
        bool leaving, closeAfterOperation;

        T Get<T>(string suffix) where T : FrameworkElement { return (T)window.FindName("Watermark" + suffix); }

        internal WatermarkPage(Window owner)
        {
            window = owner;
            text = Get<TextBox>("Text"); mode = Get<ComboBox>("Mode"); position = Get<ComboBox>("Position");
            size = Get<Slider>("Size"); opacity = Get<Slider>("Opacity");
            status = Get<TextBlock>("Status"); name = Get<TextBlock>("Name"); notice = Get<TextBlock>("Notice");
            choose = Get<Button>("Choose"); save = Get<Button>("Save"); cancel = Get<Button>("Cancel"); folder = Get<Button>("Folder");
            previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
            previewTimer.Tick += delegate { previewTimer.Stop(); DrawPreview(); };
            text.TextChanged += delegate { SettingsChanged(); };
            mode.SelectionChanged += delegate { SettingsChanged(); };
            position.SelectionChanged += delegate { SettingsChanged(); };
            size.ValueChanged += delegate { SettingsChanged(); };
            opacity.ValueChanged += delegate { SettingsChanged(); };
            choose.Click += async delegate {
                var dialog = new OpenFileDialog { Title = "Choisir un document ou une image", Filter = "Documents et images|*.*|PDF et Word|*.pdf;*.docx|Images courantes|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.tif;*.tiff;*.heic;*.heif;*.avif;*.bmp" };
                if (dialog.ShowDialog(window) == true) await LoadAsync(dialog.FileName);
            };
            var drop = Get<Border>("Drop");
            drop.PreviewDragOver += delegate(object sender, DragEventArgs e) {
                e.Effects = operation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            drop.Drop += async delegate(object sender, DragEventArgs e) {
                e.Handled = true;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length != 1) { status.Text = "Déposez un seul fichier à la fois."; return; }
                await LoadAsync(files[0]);
            };
            save.Click += async delegate {
                if (!save.IsEnabled) return;
                string ext = source.Extension;
                var dialog = new SaveFileDialog {
                    Title = "Enregistrer la copie avec filigrane", DefaultExt = ext, Filter = "Copie (" + ext + ")|*" + ext,
                    FileName = Path.GetFileNameWithoutExtension(original.Name) + "-filigrane" + ext,
                    InitialDirectory = Path.GetDirectoryName(original.Name), OverwritePrompt = true
                };
                if (dialog.ShowDialog(window) == true) await SaveAsync(dialog.FileName, File.Exists(dialog.FileName));
            };
            cancel.Click += delegate { if (operation != null) { operation.Cancel(); cancel.IsEnabled = false; } };
            folder.Click += delegate {
                if (savedDirectory == null) return;
                try { using (Process.Start(new ProcessStartInfo(savedDirectory) { UseShellExecute = true, Verb = "open" })) { } }
                catch (Win32Exception ex) { status.Text = ex.Message; }
            };
            var workspace = Get<Grid>("Workspace");
            workspace.SizeChanged += delegate(object sender, SizeChangedEventArgs e) {
                if (!e.WidthChanged || e.NewSize.Width <= 0) return;
                bool compact = e.NewSize.Width < 610;
                var pane = Get<Border>("PreviewPane");
                Grid.SetColumn(pane, compact ? 0 : 1);
                Grid.SetRow(pane, compact ? 1 : 0);
                workspace.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(6, GridUnitType.Star);
                Get<Border>("SettingsPane").Margin = compact ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 12, 0);
            };
            UpdateNotice.WaitForTool(window, () => operation != null);
            window.Closing += delegate(object sender, CancelEventArgs e) {
                if (e.Cancel || operation == null) return;
                e.Cancel = true;
                closeAfterOperation = true;
                Leave();
            };
            window.Closed += delegate { Leave(); };
            SettingsChanged();
        }

        internal async Task LoadAsync(string path)
        {
            if (operation != null) return;
            ClearSource();
            Begin("Lecture du fichier…");
            var token = operation.Token;
            try
            {
                original = File.OpenRead(path);
                source = await Task.Run<WatermarkSource>(delegate {
                    token.ThrowIfCancellationRequested();
                    switch (Path.GetExtension(path).ToLowerInvariant())
                    {
                        case ".pdf": return new PdfWatermark(path, token);
                        case ".docx": return new WordWatermark(path, token);
                        case ".doc": throw new NotSupportedException("Enregistrez d’abord cet ancien document Word au format .docx.");
                        default: return new ImageWatermark(path, token);
                    }
                }, token);
                token.ThrowIfCancellationRequested();
                name.Text = Path.GetFileName(path);
                notice.Text = source.Notice;
                Get<TextBlock>("Caption").Text = source is WordWatermark ? "Simulation du placement" : source is PdfWatermark ? "Aperçu · première page" : "Aperçu";
                Get<Image>("Background").Source = source.Preview;
                status.Text = "";
                DrawPreview();
            }
            catch (OperationCanceledException) { ClearSource(); status.Text = "Lecture annulée."; }
            catch (Exception ex) { ClearSource(); status.Text = ex.Message; }
            finally { End(); }
        }

        internal async Task SaveAsync(string destination, bool overwrite)
        {
            if (!save.IsEnabled) return;
            var options = Options();
            Begin("Enregistrement de la copie…");
            try
            {
                var token = operation.Token;
                string path = original.Name;
                await Task.Run(() => source.Save(path, destination, overwrite, options, token), token);
                savedDirectory = Path.GetDirectoryName(destination);
                status.Text = "Copie enregistrée · " + destination;
            }
            catch (OperationCanceledException) { status.Text = "Enregistrement annulé."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { End(); }
        }

        WatermarkOptions Options()
        {
            return new WatermarkOptions(text.Text, mode.SelectedIndex == 1, (WatermarkPosition)position.SelectedIndex, size.Value / 100, opacity.Value / 100);
        }

        void SettingsChanged()
        {
            Get<Label>("SizeLabel").Content = "Taille · " + size.Value.ToString("0") + " %";
            Get<Label>("OpacityLabel").Content = "Opacité · " + opacity.Value.ToString("0") + " %";
            Get<StackPanel>("PositionRow").Visibility = mode.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
            if (savedDirectory != null) { savedDirectory = null; status.Text = ""; }
            Refresh();
            if (source == null) return;
            previewTimer.Stop();
            previewTimer.Start();
        }

        void DrawPreview()
        {
            if (source == null) return;
            double scale = Math.Min(720 / source.Width, 520 / source.Height);
            var paper = Get<Grid>("Paper");
            int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
            paper.Width = w;
            paper.Height = h;
            var overlay = Get<Image>("Overlay");
            overlay.Source = null;
            if (!String.IsNullOrWhiteSpace(text.Text))
            {
                try { overlay.Source = WatermarkArt.Render(w, h, Options()); }
                catch (Exception ex) { status.Text = "Aperçu indisponible. " + ex.Message; }
            }
        }

        void Begin(string message)
        {
            operation = new CancellationTokenSource();
            leaving = false;
            savedDirectory = null;
            status.Text = message;
            Refresh();
        }

        void End()
        {
            operation.Dispose();
            operation = null;
            if (leaving) { ClearSource(); status.Text = ""; }
            Refresh();
            if (closeAfterOperation) window.Dispatcher.BeginInvoke(new Action(window.Close));
        }

        void Refresh()
        {
            choose.IsEnabled = Get<StackPanel>("Settings").IsEnabled = operation == null;
            save.IsEnabled = operation == null && source != null && !String.IsNullOrWhiteSpace(text.Text);
            cancel.Visibility = operation == null ? Visibility.Collapsed : Visibility.Visible;
            cancel.IsEnabled = operation != null && !operation.IsCancellationRequested;
            folder.Visibility = savedDirectory == null ? Visibility.Collapsed : Visibility.Visible;
            Get<Viewbox>("Sheet").Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
            Get<TextBlock>("Placeholder").Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        }

        void ClearSource()
        {
            previewTimer.Stop();
            if (source != null) { source.Dispose(); source = null; }
            if (original != null) { original.Dispose(); original = null; }
            savedDirectory = null;
            Get<Image>("Overlay").Source = Get<Image>("Background").Source = null;
            Get<TextBlock>("Caption").Text = "Aperçu";
            name.Text = "Ou déposez ici un PDF, un document Word (.docx) ou une image.";
            notice.Text = "";
        }

        internal void Leave()
        {
            leaving = true;
            previewTimer.Stop();
            if (operation != null) { operation.Cancel(); return; }
            ClearSource();
            status.Text = "";
            Refresh();
        }
    }
}
