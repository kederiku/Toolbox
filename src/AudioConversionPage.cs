using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        NavigationPage CreateAudioPage()
        {
            var page = new AudioConversionPage(Window);
            return new NavigationPage("AudioNav", Get<StackPanel>("AudioView"), "Convertir un fichier audio", "\uE8D6",
                "Convertissez un enregistrement en MP3, WAV, FLAC, M4A, Ogg, Opus ou dans un autre format audio.") {
                Category = "Audio", OpenLabel = "Convertir un fichier audio", PrimaryAction = Get<Button>("AudioChoose"),
                OnLeave = page.Leave
            };
        }
    }

    internal sealed class AudioConversionPage : MediaConversionPage<AudioConversion>
    {
        readonly ComboBox tracks;
        readonly FrameworkElement trackGroup, qualityGroup;

        internal AudioConversionPage(Window owner, Action<string> showFolder = null)
            : base(owner, "Audio", "audio", "des fichiers audio", AudioConversion.InputFilter, showFolder)
        {
            tracks = (ComboBox)owner.FindName("AudioTrack");
            trackGroup = (FrameworkElement)owner.FindName("AudioTrackGroup");
            qualityGroup = (FrameworkElement)owner.FindName("AudioQualityGroup");
            tracks.SelectionChanged += delegate { Refresh(); };
            formats.SelectionChanged += delegate { Refresh(); };
            formats.ItemsSource = AudioOutput.Formats;
            formats.SelectedIndex = 0;
        }

        protected override AudioConversion Open(string path, CancellationToken token) { return AudioConversion.Open(path, token); }
        AudioTrack Track { get { return (AudioTrack)tracks.SelectedItem; } }
        AudioOutput Format { get { return (AudioOutput)formats.SelectedItem; } }

        protected override void SourceChanged()
        {
            tracks.ItemsSource = selected == null ? null : selected.Tracks;
            tracks.SelectedItem = selected == null ? null : selected.DefaultTrack;
        }

        protected override void RefreshChoices(bool idle)
        {
            tracks.IsEnabled = idle;
            trackGroup.Visibility = selected != null && selected.Tracks.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            bool adjustable = Format != null && Format.HasQuality;
            qualityGroup.Visibility = adjustable ? Visibility.Visible : Visibility.Collapsed;
            qualities.IsEnabled = idle && adjustable;
        }

        protected override string Details
        {
            get
            {
                var track = Track;
                if (track == null) return "";
                string channels = track.Channels == 1 ? "Mono" : track.Channels == 2 ? "Stéréo" : track.Channels + " canaux";
                string format = selected.Format == track.Codec ? selected.Format : selected.Format + " · " + track.Codec;
                return string.Format("{0} · {1:N0} Hz · {2}", format, track.SampleRate, channels);
            }
        }

        protected override double Duration { get { return Track == null ? 0 : selected.DurationFor(Track); } }
        protected override string BatchWarning { get { return "Chaque copie contiendra la piste audio par défaut du fichier, ou sa première piste audio. Le nombre et la disposition des canaux seront adaptés au format choisi si nécessaire."; } }

        protected override Task ConvertBatchAsync(ConversionBatch batch)
        {
            var format = Format;
            var quality = (ConversionQuality)qualities.SelectedValue;
            return batch.ConvertAsync(() => new VideoEngine(batch.Token), (engine, source, destination, report) => {
                using (var audio = AudioConversion.OpenWithEngine(source, batch.Token, engine))
                {
                    report.Report(FileProgress.Activity("Conversion"));
                    audio.Save(destination, format, audio.DefaultTrack, quality, false, batch.Token,
                        value => report.Report(FileProgress.Conversion(value)));
                }
            });
        }
        protected override string Warning
        {
            get
            {
                var notes = new List<string>();
                if (selected.HasOtherStreams) notes.Add("La copie contiendra uniquement le son de la piste choisie.");
                if (Track != null && Format != null && Format.ChannelsFor(Track) < Track.Channels)
                {
                    int channels = Format.ChannelsFor(Track);
                    string mix = channels == 1 ? "mono" : channels == 2 ? "stéréo" : channels + " canaux";
                    notes.Add("La copie sera enregistrée en " + mix + ".");
                }
                else if (Track != null && Format != null)
                {
                    string layout = Format.LayoutFor(Track);
                    if (layout != null && layout != Track.Layout)
                        notes.Add("La disposition des enceintes sera adaptée au format de sortie (" + layout + ").");
                }
                return string.Join(" ", notes);
            }
        }

        protected override Encoder GetEncoder()
        {
            var audio = selected;
            var format = Format;
            var track = Track;
            if (audio == null || format == null || track == null || qualities.SelectedValue == null) return null;
            var quality = (ConversionQuality)qualities.SelectedValue;
            return (path, overwrite, token, report) => audio.Save(path, format, track, quality, overwrite, token, report);
        }
    }
}
