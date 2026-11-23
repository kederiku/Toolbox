using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckConversionBatches()
        {
            string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-lots-" + Guid.NewGuid().ToString("N"))).FullName;
            var previous = SynchronizationContext.Current;
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                string opened = null;
                var images = new ImageConversionPage(window, folder => opened = folder);
                string a = Path.Combine(dir, "été.ppm");
                string other = Directory.CreateDirectory(Path.Combine(dir, "autres")).FullName;
                string b = Path.Combine(other, "été.bmp");
                File.WriteAllText(a, "P3\n2 1\n255\n255 0 0 0 255 0\n", Encoding.ASCII);
                var bmp = new BmpBitmapEncoder();
                bmp.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[] { 255, 0, 0 }, 3)));
                using (var stream = File.Create(b)) bmp.Save(stream);
                string broken = Path.Combine(dir, "été.jpg");
                File.WriteAllText(broken, "pas une image");
                string existing = Path.Combine(dir, "été.png");
                File.WriteAllText(existing, "copie existante");
                var drop = new DataObject(DataFormats.FileDrop, new[] { a, broken, b });
                WaitBatch(window, images.LoadDroppedAsync(drop));
                var list = (ListBox)window.FindName("ImageBatchFiles");
                Check(list.Items.Count == 3 && ((Button)window.FindName("ImageSave")).IsEnabled, "Un dépôt multiple prépare le lot d’images");
                WaitBatch(window, images.SaveBatchToAsync(dir));
                var first = Path.Combine(dir, "été (2).png");
                var second = Path.Combine(dir, "été (3).png");
                Check(File.ReadAllText(existing) == "copie existante" && File.ReadAllBytes(first)[1] == 80 && File.ReadAllBytes(second)[1] == 80,
                    "Le lot écrit deux PNG distincts malgré un nom occupé et un fichier intermédiaire illisible");
                Check(((TextBlock)window.FindName("ImageSavedName")).Text.Contains("2 sur 3") &&
                    ((TextBlock)window.FindName("ImageResultGlyph")).Text != "\uE73E" &&
                    ((BatchFile)list.Items[1]).Status.Contains("Échec"), "Le bilan distingue une erreur des copies terminées");
                var results = list.Items.Cast<BatchFile>().ToArray();
                Check(results[0].Progress == 100 && results[1].Progress < 100 && results[2].Progress == 100 &&
                    results.All(row => !row.IsIndeterminate), "Les barres distinguent les deux images enregistrées de l’image illisible");
                ((Button)window.FindName("ImageOpenFolder")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(opened == dir, "Le résultat du lot ouvre le dossier choisi");
                WaitBatch(window, images.LoadFilesAsync(new[] { first, second }));
                var cancelled = images.SaveBatchToAsync(dir);
                ((Button)window.FindName("ImageCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitBatch(window, cancelled);
                Check(((TextBlock)window.FindName("ImageResultGlyph")).Text != "\uE73E" && !((Button)window.FindName("ImageOpenFolder")).IsEnabled,
                    "Un lot entièrement annulé affiche un bilan neutre");
                Check(Directory.GetFiles(dir, "*.png").Length == 3 && !Directory.GetFiles(dir, ".AtelierPC-*.tmp").Any(),
                    "L’annulation du lot protège les originaux de même extension et ne laisse pas de fichier incomplet");
                var finished = (BatchFile)list.Items[0];
                finished.PropertyChanged += delegate {
                    if (finished.Status.StartsWith("Enregistré")) ((Button)window.FindName("ImageCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                WaitBatch(window, images.SaveBatchToAsync(dir));
                Check(File.Exists(Path.Combine(dir, "été (2) (2).png")) && Directory.GetFiles(dir, "*.png").Length == 4 &&
                    ((TextBlock)window.FindName("ImageSavedName")).Text.Contains("1 sur 2") &&
                    ((Button)window.FindName("ImageOpenFolder")).IsEnabled, "Un lot interrompu conserve et signale la première copie terminée");
                WaitBatch(window, images.LoadFilesAsync(new[] { first }));
                Check(list.Visibility == Visibility.Collapsed && ((Image)window.FindName("ImagePreview")).Source != null,
                    "Une nouvelle sélection unique retrouve l’aperçu habituel");
                var hint = window.FindName("ImageOutputHint") as TextBlock;
                Check(hint != null && hint.Visibility == Visibility.Visible && hint.Text.Contains("été (2) - convertie.png"),
                    "Le nom proposé à l’enregistrement correspond à la source et au format PNG");
                var formats = (ComboBox)window.FindName("ImageFormat");
                formats.SelectedItem = ImageOutput.Formats.Single(f => f.Code == "JPEG");
                Check(hint.Text.Contains("été (2) - convertie.jpg") && !hint.Text.Contains("été (2) - convertie.png"),
                    "Le passage au JPEG actualise le nom proposé avec l’extension .jpg");
                WaitBatch(window, images.LoadFilesAsync(new[] { first, second }));
                Check(hint.Visibility != Visibility.Visible || !hint.Text.Contains("été (2) - convertie"),
                    "Le passage au lot retire le nom de sortie du fichier individuel");
                images.Leave();

                using (var engine = new VideoEngine(CancellationToken.None))
                {
                    string clip = Path.Combine(dir, "film 1.mkv");
                    string clip2 = Path.Combine(dir, "film 2.mp4");
                    engine.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=64x48:r=5:d=0.4",
                        "-f", "lavfi", "-i", "sine=f=440:d=0.4", "-f", "lavfi", "-i", "sine=f=880:r=32000:d=0.4",
                        "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-c:a", "flac",
                        "-disposition:a:0", "0", "-disposition:a:1", "default", clip }, CancellationToken.None);
                    engine.Run("ffmpeg", new[] { "-v", "error", "-i", clip, "-map", "0:v", "-map", "0:a:0",
                        "-c:v", "copy", "-c:a", "aac", clip2 }, CancellationToken.None);
                    var files = new[] { clip, clip2 };
                    var audio = new AudioConversionPage(window);
                    WaitBatch(window, audio.LoadFilesAsync(files));
                    ((ComboBox)window.FindName("AudioFormat")).SelectedItem = AudioOutput.Formats.Single(f => f.Code == "FLAC");
                    WaitBatch(window, audio.SaveBatchToAsync(dir));
                    var video = new VideoConversionPage(window);
                    WaitBatch(window, video.LoadDroppedAsync(new DataObject(DataFormats.FileDrop, files)));
                    ((ComboBox)window.FindName("VideoFormat")).SelectedItem = VideoOutput.Formats.Single(f => f.Code == "WEBM");
                    ((ComboBox)window.FindName("VideoCodec")).SelectedItem = VideoCodec.Vp9;
                    WaitBatch(window, video.SaveBatchToAsync(dir));
                    Check(new[] { "VideoBatchFiles", "AudioBatchFiles" }.All(id =>
                        ((ListBox)window.FindName(id)).Items.Cast<BatchFile>().All(row =>
                            row.State == BatchFileState.Saved && row.Progress == 100 && row.ProgressLabel == "100 %")),
                        "Les résultats audio et vidéo confirment séparément les copies enregistrées");
                    foreach (string file in files)
                    {
                        var sound = ProbeVideo(engine, Path.ChangeExtension(file, ".flac"));
                        string rate = file == clip ? "32000" : "44100";
                        Check(sound.SelectSingleNode("//stream[@codec_name='flac']/@sample_rate").Value == rate,
                            "Le lot audio utilise le profil FLAC et la piste par défaut de " + Path.GetFileName(file));
                        var movie = ProbeVideo(engine, Path.ChangeExtension(file, ".webm"));
                        int audioTracks = file == clip ? 2 : 1;
                        Check(movie.SelectSingleNode("//stream[@codec_name='vp9']") != null && movie.SelectNodes("//stream[@codec_type='audio']").Count == audioTracks,
                            "Le lot vidéo applique WebM/VP9 et conserve les pistes audio de " + Path.GetFileName(file));
                    }
                }
            }
            finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(dir, true); }
        }

        static void WaitBatch(Window window, Task task)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            Check(task.IsCompleted, "Le traitement du lot se termine");
            task.GetAwaiter().GetResult();
        }
    }
}
