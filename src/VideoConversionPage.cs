using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        NavigationPage CreateVideoPage()
        {
            var page = new VideoConversionPage(Window);
            return new NavigationPage("VideoNav", Get<StackPanel>("VideoView"), "Convertir une vidéo", "\uE714",
                "Enregistrez une copie en MP4, MKV, WebM, MOV, AVI ou WMV avec l’encodage et la qualité de votre choix.") {
                Category = "Vidéos", OpenLabel = "Convertir une vidéo", PrimaryAction = Get<Button>("VideoChoose"),
                OnLeave = page.Leave
            };
        }
    }

    internal sealed class VideoConversionPage : MediaConversionPage<VideoConversion>
    {
        readonly ComboBox codecs;

        internal VideoConversionPage(Window owner, Action<string> showFolder = null)
            : base(owner, "Video", "vidéo", "des vidéos",
                "Fichiers vidéo|*.mp4;*.m4v;*.mkv;*.webm;*.mov;*.avi;*.wmv;*.mpg;*.mpeg;*.ts;*.mts;*.m2ts;*.flv;*.3gp;*.ogv|Tous les fichiers|*.*", showFolder)
        {
            codecs = (ComboBox)owner.FindName("VideoCodec");
            formats.SelectionChanged += delegate { UpdateCodecs(); };
            formats.ItemsSource = VideoOutput.Formats;
            formats.SelectedIndex = 0;
        }

        void UpdateCodecs()
        {
            var format = (VideoOutput)formats.SelectedItem;
            var previous = (VideoCodec)codecs.SelectedItem;
            codecs.ItemsSource = format == null ? null : format.Codecs;
            if (format != null)
            {
                codecs.SelectedItem = previous;
                if (codecs.SelectedIndex < 0) codecs.SelectedIndex = 0;
            }
            Refresh();
        }

        protected override VideoConversion Open(string path, CancellationToken token) { return VideoConversion.Open(path, token); }
        protected override string Details { get { return string.Format("{0} · {1} · {2} × {3} pixels",
            selected.Format, selected.Codec, selected.Width, selected.Height); } }
        protected override string Warning { get { return selected.Warning; } }
        protected override double Duration { get { return selected.Duration; } }
        protected override string BatchWarning { get { return "Chaque copie conservera la vidéo principale et les pistes audio. Les sous-titres, pochettes et autres pistes seront omis."; } }
        protected override void RefreshChoices(bool idle) { codecs.IsEnabled = idle; }

        protected override Task ConvertBatchAsync(ConversionBatch batch)
        {
            var format = (VideoOutput)formats.SelectedItem;
            var codec = (VideoCodec)codecs.SelectedItem;
            var quality = (ConversionQuality)qualities.SelectedValue;
            return batch.ConvertAsync(() => new VideoEngine(batch.Token), (engine, source, destination, report) => {
                using (var video = VideoConversion.OpenWithEngine(source, batch.Token, engine))
                {
                    report.Report(FileProgress.Activity("Conversion"));
                    video.Save(destination, format, codec, quality, false, batch.Token,
                        value => report.Report(FileProgress.Conversion(value)));
                }
            });
        }

        protected override Encoder GetEncoder()
        {
            var video = selected;
            var format = (VideoOutput)formats.SelectedItem;
            var codec = (VideoCodec)codecs.SelectedItem;
            if (video == null || format == null || codec == null || qualities.SelectedValue == null) return null;
            var quality = (ConversionQuality)qualities.SelectedValue;
            return (path, overwrite, token, report) => video.Save(path, format, codec, quality, overwrite, token, report);
        }
    }
}
