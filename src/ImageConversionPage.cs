using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        NavigationPage CreateImagePage()
        {
            var view = Get<StackPanel>("ImageView");
            var page = new ImageConversionPage(Window);
            return new NavigationPage("ImageNav", view, "Convertir une image", "\uEB9F",
                "Enregistrez une copie de votre image en PNG, JPEG, WebP ou dans un autre format.") {
                Category = "Images", OpenLabel = "Convertir une image", PrimaryAction = Get<Button>("ImageChoose"),
                OnLeave = page.Leave
            };
        }
    }

    internal sealed class ImageConversionPage : MediaConversionPage<ImageConversion>
    {
        readonly Image preview;
        readonly FrameworkElement placeholder;

        internal ImageConversionPage(Window owner, Action<string> showFolder = null)
            : base(owner, "Image", "image", "des images", "Tous les fichiers|*.*", showFolder)
        {
            preview = (Image)owner.FindName("ImagePreview");
            placeholder = (FrameworkElement)owner.FindName("ImagePlaceholder");
            formats.SelectionChanged += delegate { Refresh(); };
            formats.ItemsSource = ImageOutput.Formats;
            formats.SelectedIndex = 0;
        }

        ImageOutput Format { get { return (ImageOutput)formats.SelectedItem; } }
        protected override ImageConversion Open(string path, CancellationToken token) { return ImageConversion.Open(path, token); }

        protected override async Task PrepareAsync(ImageConversion image, CancellationToken token)
        {
            var bytes = await Task.Run(() => image.ReadThumbnail(token), token);
            token.ThrowIfCancellationRequested();
            if (bytes != null)
                using (var input = new MemoryStream(bytes))
                    preview.Source = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        protected override void SourceChanged() { if (selected == null) preview.Source = null; }
        protected override void RefreshChoices(bool idle)
        {
            placeholder.Visibility = preview.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override string Details { get { return string.Format("{0} · {1} × {2} pixels · {3} image{4}",
            selected.Format, selected.Width, selected.Height, selected.FrameCount, selected.FrameCount == 1 ? "" : "s"); } }

        string IconWarning
        {
            get { return Format != null && (Format.Code == "ICO" || Format.Code == "CUR") ?
                "La taille maximale d’une icône est de 256 × 256 pixels." : ""; }
        }

        protected override bool ConfirmWarning { get { return Format != null && selected.LosesFrames(Format); } }

        protected override string Warning
        {
            get
            {
                string frames = ConfirmWarning ? "Ce fichier contient plusieurs images. Seule la première sera enregistrée en " + Format.Code + "." : "";
                return (frames + " " + IconWarning).Trim();
            }
        }

        protected override string BatchWarning
        {
            get
            {
                string frames = Format != null && !Format.Multiple ? "Pour les animations et les images multipages, seule la première image sera enregistrée." : "";
                return (frames + " " + IconWarning).Trim();
            }
        }

        protected override Encoder GetEncoder()
        {
            var image = selected;
            var format = Format;
            if (image == null || format == null) return null;
            return (path, overwrite, token, report) => image.Save(path, format, overwrite, token);
        }

        protected override Task ConvertBatchAsync(ConversionBatch batch)
        {
            var format = Format;
            return batch.ConvertAsync(() => new ImageEngine(), (engine, source, destination, report) => {
                report.Report(FileProgress.Step("Lecture", 1, 3));
                using (var image = ImageConversion.OpenWithEngine(source, batch.Token, engine))
                {
                    report.Report(FileProgress.Step("Conversion", 2, 3));
                    image.Save(destination, format, false, batch.Token,
                        delegate { report.Report(FileProgress.Step("Enregistrement", 3, 3)); });
                }
            });
        }
    }
}
