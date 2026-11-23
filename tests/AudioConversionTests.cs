using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckAudioNavigation()
        {
            var window = new Shell(() => new List<Device>()).Window;
            try
            {
                ShowNavigationWindow(window);
                var catalog = (FrameworkElement)window.FindName("OtherPage");
                ClickVisible(FindAction(catalog, "Convertir un fichier audio"));
                var view = (StackPanel)window.FindName("AudioView");
                Check(view.IsVisible, "Le catalogue initial ouvre le convertisseur audio");
                ((Button)window.FindName("HomeNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBox)window.FindName("CatalogSearch")).Text = "flac";
                var entry = FindAction((StackPanel)window.FindName("OtherPage"), "Convertir un fichier audio");
                Check(entry != null, "La recherche FLAC retrouve le convertisseur audio");
                entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((Button)window.FindName("StartNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                OpenFilesToolFromMenu(window, "AudioNav");
                Check(view.Visibility == Visibility.Visible && !((Button)window.FindName("AudioSave")).IsEnabled,
                    "Le menu ouvre la page audio en attente d’un fichier");
            }
            finally { window.Close(); }
        }

        static void CheckAudioConversions()
        {
            var token = CancellationToken.None;
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-tests-audio-" + Guid.NewGuid().ToString("N")));
            try
            {
                using (var tools = new VideoEngine(token))
                {
                    string source = Path.Combine(folder.FullName, "été [1] %d.inconnu");
                    tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "sine=f=440:r=44100:d=1",
                        "-af", "volume=0.812345:precision=double", "-ac", "2", "-c:a", "pcm_s24le",
                        "-metadata", "title=Essai", "-f", "wav", source }, token);
                    var expected = new Dictionary<string, string> {
                        { "MP3", "mp3" }, { "WAV", "pcm_s24le" }, { "FLAC", "flac" }, { "M4A", "aac" },
                        { "ALAC", "alac" }, { "OGG", "vorbis" }, { "Opus", "opus" }, { "AAC", "aac" },
                        { "WMA", "wmav2" }, { "AIFF", "pcm_s24be" }, { "WavPack", "wavpack" }, { "TTA", "tta" },
                        { "M4B", "aac" }, { "CAF", "pcm_s24le" }, { "AU", "pcm_s24be" }, { "W64", "pcm_s24le" },
                        { "MKA", "flac" }, { "WebM", "opus" }, { "MP2", "mp2" }, { "AC3", "ac3" }, { "EAC3", "eac3" },
                        { "Speex", "speex" }, { "AMR", "amr_nb" }, { "AMR-WB", "amr_wb" }, { "OGA", "flac" },
                        { "ADX", "adpcm_adx" }, { "VOC", "pcm_s16le" }, { "IRCAM", "pcm_s24le" },
                        { "GSM", "gsm" }, { "LOAS", "aac_latm" }, { "SBC", "sbc" }, { "SoX", "pcm_s32le" }
                    };
                    using (var audio = AudioConversion.Open(source, token))
                    {
                        var track = audio.DefaultTrack;
                        Check(track.SampleRate == 44100 && track.Channels == 2 && audio.Format == "WAV"
                            && Math.Abs(audio.Duration - 1) < 0.01, "Le contenu d’un fichier renommé identifie son format et sa piste");
                        var lossless = new[] { "WAV", "FLAC", "ALAC", "AIFF", "WavPack", "TTA", "MKA", "CAF", "AU", "W64", "OGA", "IRCAM" };
                        string reference = PcmHash(tools, source);
                        Check(expected.Count == AudioOutput.Formats.Length, "Chaque profil audio possède une conversion de contrôle");
                        foreach (var profile in AudioOutput.Formats)
                        {
                            string destination = Path.Combine(folder.FullName, profile.Code + "." + profile.Extension);
                            var progress = new List<double>();
                            audio.Save(destination, profile, track, ConversionQuality.Balanced, false, token, progress.Add);
                            var info = ProbeVideo(tools, destination);
                            Check(info.SelectNodes("//stream").Count == 1 &&
                                info.SelectSingleNode("//stream/@codec_name").Value == expected[profile.Code],
                                "Flux audio exporté et relu pour " + profile.Code);
                            Check(progress.Last() == 100 && progress.SequenceEqual(progress.OrderBy(v => v)),
                                "Progression terminée pour " + profile.Code);
                            using (var reopened = AudioConversion.Open(destination, token))
                                Check(reopened.Tracks.Length == 1, "Le convertisseur rouvre le fichier " + profile.Code);
                            string hash = PcmHash(tools, destination);
                            if (lossless.Contains(profile.Code))
                                Check(hash == reference, "Les échantillons 24 bits sont conservés en " + profile.Code);
                        }
                        var mp3 = AudioOutput.Formats.First(f => f.Code == "MP3");
                        string dest = Path.Combine(folder.FullName, "copie.mp3");
                        File.WriteAllText(dest, "à garder");
                        bool refused = false;
                        try { audio.Save(dest, mp3, track, ConversionQuality.Balanced, false, token, null); }
                        catch (IOException) { refused = true; }
                        Check(refused && File.ReadAllText(dest) == "à garder", "Un fichier existant exige la confirmation");
                        audio.Save(dest, mp3, track, ConversionQuality.Smaller, true, token, null);
                        long smaller = new FileInfo(dest).Length;
                        audio.Save(dest, mp3, track, ConversionQuality.Higher, true, token, null);
                        Check(new FileInfo(dest).Length > smaller, "Le niveau de qualité agit sur le MP3 produit");
                        using (var copy = AudioConversion.Open(dest, token))
                        {
                            refused = false;
                            try { copy.Save(dest, mp3, copy.DefaultTrack, ConversionQuality.Balanced, true, token, null); }
                            catch (IOException) { refused = true; }
                            Check(refused, "L’enregistrement sur l’original est refusé");
                        }
                    }
                    CheckAudioInputs(tools, source, folder.FullName);
                    CheckAudioPageOperations(tools, source, folder.FullName);
                }
            }
            finally { folder.Delete(true); }
        }

        static string PcmHash(VideoEngine tools, string file)
        {
            return tools.Run("ffmpeg", new[] { "-v", "error", "-xerror", "-i", file, "-map", "0:a:0",
                "-c:a", "pcm_s24le", "-f", "hash", "-" }, CancellationToken.None).Trim();
        }

        static void CheckAudioInputs(VideoEngine tools, string source, string folder)
        {
            var token = CancellationToken.None;
            string surround = Path.Combine(folder, "huit canaux.wav");
            tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=7.1:d=0.25", surround }, token);
            using (var audio = AudioConversion.Open(surround, token))
            {
                string dest = Path.Combine(folder, "huit canaux.m4a");
                audio.Save(dest, AudioOutput.Formats.Single(f => f.Code == "ALAC"), audio.DefaultTrack,
                    ConversionQuality.Balanced, false, token, null);
                Check(ProbeVideo(tools, dest).SelectSingleNode("//stream/@channels").Value == "8",
                    "Apple Lossless conserve huit canaux lors de l’adaptation de la disposition 7.1");
            }
            string voice = Path.Combine(folder, "téléphone.gsm");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-ac", "1", "-ar", "8000", "-c:a", "libgsm", voice }, token);
            using (var audio = AudioConversion.Open(voice, token))
            {
                Check(audio.DefaultTrack.SampleRate == 8000, "La voix GSM est reconnue en entrée");
                var mp2 = AudioOutput.Formats.Single(f => f.Code == "MP2");
                string dest = Path.Combine(folder, "téléphone.mp2");
                audio.Save(dest, mp2, audio.DefaultTrack, ConversionQuality.Higher, false, token, null);
                Check(ProbeVideo(tools, dest).SelectSingleNode("//stream/@codec_name").Value == "mp2",
                    "Un enregistrement téléphonique à 8 kHz se convertit en MP2");
            }
            string silent = Path.Combine(folder, "sans son.mp4");
            tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=48x32:d=0.2", "-c:v", "libx264", silent }, token);
            bool refused = false;
            try { using (AudioConversion.Open(silent, token)) { } } catch (InvalidDataException) { refused = true; }
            Check(refused, "Une vidéo muette ne peut pas être convertie en audio");
            string video = Path.Combine(folder, "avec son.mov");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", silent, "-i", source, "-map", "0:v", "-map", "1:a", "-c", "copy", video }, token);
            using (var audio = AudioConversion.Open(video, token))
            {
                Check(audio.HasOtherStreams && audio.Tracks.Length == 1, "La vidéo et sa piste audio sont distinguées");
                string dest = Path.Combine(folder, "piste extraite.flac");
                audio.Save(dest, AudioOutput.Formats.Single(f => f.Code == "FLAC"), audio.DefaultTrack,
                    ConversionQuality.Balanced, false, token, null);
                Check(PcmHash(tools, source) == PcmHash(tools, dest), "La piste extraite conserve le son du MOV");
            }
            string playlist = Path.Combine(folder, "liste.mp3");
            File.WriteAllText(playlist, "ffconcat version 1.0\nfile '" + source.Replace('\\', '/') + "'\n");
            refused = false;
            try { using (AudioConversion.Open(playlist, token)) { } } catch (InvalidDataException) { refused = true; }
            Check(refused, "La conversion audio accepte uniquement des fichiers multimédias autonomes");
        }

        static void CheckAudioPageOperations(VideoEngine tools, string source, string folder)
        {
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                string opened = null;
                var page = new AudioConversionPage(window, dir => opened = dir);
                var quality = (ComboBox)window.FindName("AudioQuality");
                var formats = (ComboBox)window.FindName("AudioFormat");
                var save = (Button)window.FindName("AudioSave");
                string multi = Path.Combine(folder, "deux pistes.mka");
                tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-f", "lavfi", "-i", "sine=f=880:r=32000:d=1",
                    "-map", "0:a", "-map", "1:a", "-c:a", "flac", "-disposition:a:0", "0", "-disposition:a:1", "default", multi }, CancellationToken.None);
                var reading = page.LoadAsync(multi);
                WaitForVideoPage(window, () => reading.IsCompleted);
                var tracks = (ComboBox)window.FindName("AudioTrack");
                Check(save.IsEnabled && tracks.Items.Count == 2 && tracks.SelectedIndex == 1 && quality.IsEnabled,
                    "La page propose MP3 avec qualité réglable et présélectionne la piste par défaut");
                tracks.SelectedIndex = 0;
                formats.SelectedItem = AudioOutput.Formats.Single(f => f.Code == "FLAC");
                Check(!quality.IsEnabled, "La qualité est fixe pour FLAC");
                string dest = Path.Combine(folder, "sélection.flac");
                var saving = page.SaveToAsync(dest, false);
                WaitForVideoPage(window, () => saving.IsCompleted);
                Check(ProbeVideo(tools, dest).SelectSingleNode("//stream/@sample_rate").Value == "44100",
                    "La copie contient la piste choisie");
                ((Button)window.FindName("AudioOpenFolder")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(opened == folder, "Le dossier ouvert est celui de la copie audio");
                var before = File.ReadAllBytes(dest);
                var cancelled = page.SaveToAsync(dest, true);
                ((Button)window.FindName("AudioCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitForVideoPage(window, () => cancelled.IsCompleted);
                Check(before.SequenceEqual(File.ReadAllBytes(dest)) && save.IsEnabled && !Directory.GetFiles(folder, ".AtelierPC-*.tmp").Any(),
                    "L’annulation conserve la copie et nettoie le temporaire");
                string invalid = Path.Combine(folder, "faux.flac");
                File.WriteAllText(invalid, "contenu incompatible");
                reading = page.LoadAsync(invalid);
                WaitForVideoPage(window, () => reading.IsCompleted);
                Check(!save.IsEnabled && ((TextBlock)window.FindName("AudioStatus")).Text.Length > 0,
                    "Un fichier illisible efface l’ancienne sélection et affiche une erreur");
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
}
