using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckYoutubeDownload()
        {
            const string id = "YtTestClip1";
            var token = CancellationToken.None;
            string dir = Path.Combine(Path.GetTempPath(), "AtelierPC-youtube-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var server = new TcpListener(IPAddress.Loopback, 0);
            Task serving = null;
            try
            {
                using (var ffmpeg = new VideoEngine(token))
                {
                    string video = Path.Combine(dir, "video.mp4"), audio = Path.Combine(dir, "audio.m4a");
                    ffmpeg.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=160x90:d=0.25", "-c:v", "libx264", video }, token);
                    ffmpeg.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "sine=duration=0.25", "-c:a", "aac", "-movflags", "+faststart", audio }, token);
                    server.Start();
                    string origin = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
                    var formats = new object[] {
                        new { format_id = "136", url = origin + "/small", ext = "mp4", width = 160, height = 90, vcodec = "avc1", acodec = "none" },
                        new { format_id = "137", url = origin + "/large", ext = "mp4", width = 320, height = 180, vcodec = "avc1", acodec = "none" },
                        new { format_id = "140", url = origin + "/sound", ext = "m4a", vcodec = "none", acodec = "mp4a", abr = 128 }
                    };
                    string json = new JavaScriptSerializer().Serialize(new { id = id, title = "Extrait", extractor = "youtube", extractor_key = "Youtube",
                        webpage_url = "https://www.youtube.com/watch?v=" + id, formats = formats, duration = 0.25 });
                    var source = YoutubeVideo.FromJson(id, json);
                    var selection = source.Resolutions.Single(r => r.Height == 90);
                    var requests = new List<string>();
                    string destination = Path.Combine(dir, "copie choisie.mp4");
                    serving = Task.Run(() => ServeYoutubeMedia(server, video, audio, requests));
                    var messages = new List<string>();
                    YoutubeDownloader.Save(source, selection, destination, false, token, messages.Add);
                    Check(requests.Contains("/sound") && requests.Contains("/small") && !requests.Contains("/large"),
                        "Seuls le son et la résolution choisie sont téléchargés");
                    Check(messages.Any(m => m.Contains("%")), "Le moteur communique la progression du transfert");
                    string streams = ffmpeg.Run("ffprobe", new[] { "-v", "error", "-show_entries", "stream=codec_type,width,height", "-of", "csv=p=0", destination }, token);
                    Check(streams.Contains("video,160,90") && streams.Contains("audio"), "Le MP4 enregistré contient la résolution choisie et le son");
                    string mp3 = Path.Combine(dir, "audio choisi.mp3");
                    requests.Clear();
                    YoutubeDownloader.SaveAudio(source, mp3, false, token, null);
                    string tracks = ffmpeg.Run("ffprobe", new[] { "-v", "error", "-show_entries", "stream=codec_name,codec_type",
                        "-of", "csv=p=0", mp3 }, token);
                    Check(tracks.Trim() == "mp3,audio", "Le mode audio seul produit un MP3 avec une unique piste sonore");
                    Check(requests.Count > 0 && requests.All(path => path == "/sound"), "Le MP3 récupère uniquement la piste audio disponible");
                    byte[] originalMp3 = File.ReadAllBytes(mp3);
                    bool audioDenied = false;
                    try { YoutubeDownloader.SaveAudio(source, mp3, false, token, null); }
                    catch (IOException) { audioDenied = true; }
                    Check(audioDenied && originalMp3.SequenceEqual(File.ReadAllBytes(mp3)), "Le MP3 existant reste protégé");
                    string combined = Path.Combine(dir, "combine.mp4");
                    ffmpeg.Run("ffmpeg", new[] { "-v", "error", "-i", video, "-i", audio, "-map", "0:v", "-map", "1:a", "-c", "copy", combined }, token);
                    string mixedJson = new JavaScriptSerializer().Serialize(new { id = id, title = "Flux combiné",
                        extractor = "youtube", extractor_key = "Youtube", webpage_url = "https://www.youtube.com/watch?v=" + id,
                        formats = new[] { new { format_id = "18", url = origin + "/combined", width = 160, height = 90,
                            ext = "mp4", vcodec = "avc1", acodec = "mp4a" } } });
                    requests.Clear();
                    YoutubeDownloader.SaveAudio(YoutubeVideo.FromJson(id, mixedJson), mp3, true, token, null);
                    tracks = ffmpeg.Run("ffprobe", new[] { "-v", "error", "-show_entries", "stream=codec_name,codec_type",
                        "-of", "csv=p=0", mp3 }, token);
                    Check(requests.Contains("/combined") && tracks.Trim() == "mp3,audio", "Un flux combiné permet aussi d’enregistrer le son seul");
                    byte[] original = File.ReadAllBytes(destination);
                    bool denied = false;
                    try { YoutubeDownloader.Save(source, selection, destination, false, token, null); }
                    catch (IOException) { denied = true; }
                    Check(denied && original.SequenceEqual(File.ReadAllBytes(destination)), "Un remplacement exige une confirmation");
                    using (var cancel = new CancellationTokenSource())
                    {
                        bool cancelled = false;
                        FileStream held = null;
                        try
                        {
                            YoutubeDownloader.Save(source, selection, destination, true, cancel.Token,
                                message => {
                                    if (!message.StartsWith("Enregistrement")) return;
                                    string file = Directory.GetFiles(dir, "piece-136.media", SearchOption.AllDirectories).Single();
                                    held = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                                    cancel.Cancel();
                                });
                        }
                        catch (OperationCanceledException) { cancelled = true; }
                        finally { if (held != null) held.Dispose(); }
                        Check(cancelled && original.SequenceEqual(File.ReadAllBytes(destination)), "Une annulation conserve le MP4 existant");
                        Check(SpinWait.SpinUntil(() => !Directory.GetDirectories(dir).Any(), TimeSpan.FromSeconds(8)),
                            "Le nettoyage reprend après la libération des fichiers temporaires");
                    }
                    bool failed = false;
                    try { YoutubeDownloader.Save(source, source.Resolutions.Single(r => r.Height == 180), destination, true, token, null); }
                    catch (InvalidDataException) { failed = true; }
                    Check(failed && original.SequenceEqual(File.ReadAllBytes(destination)) && !Directory.GetDirectories(dir).Any(),
                        "Un flux dont les dimensions ont changé conserve la destination et retire les fichiers temporaires");
                    byte[] sound = File.ReadAllBytes(audio);
                    int dataStart = Encoding.ASCII.GetString(sound).IndexOf("mdat", StringComparison.Ordinal);
                    Check(dataStart > 0, "L’extrait audio de test contient des paquets");
                    File.WriteAllBytes(audio, sound.Take(dataStart + 4).ToArray());
                    bool badAudio = false;
                    try { YoutubeDownloader.Save(source, selection, destination, true, token, null); }
                    catch (InvalidDataException) { badAudio = true; }
                    Check(badAudio && original.SequenceEqual(File.ReadAllBytes(destination)),
                        "Une piste audio tronquée ne peut pas remplacer le fichier par une vidéo muette");
                    byte[] savedAudio = File.ReadAllBytes(mp3);
                    bool incomplete = false;
                    try { YoutubeDownloader.SaveAudio(source, mp3, true, token, null); }
                    catch (InvalidDataException) { incomplete = true; }
                    Check(incomplete && savedAudio.SequenceEqual(File.ReadAllBytes(mp3)), "L’échec de l’extraction audio conserve le MP3 précédent");
                }
            }
            finally
            {
                server.Stop();
                if (serving != null) serving.GetAwaiter().GetResult();
                Directory.Delete(dir, true);
            }
        }

        static void ServeYoutubeMedia(TcpListener server, string video, string audio, List<string> requests)
        {
            while (true)
            {
                TcpClient client;
                try { client = server.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (InvalidOperationException) { return; }
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    var request = reader.ReadLine().Split(' ');
                    while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                    string path = request[1];
                    requests.Add(path);
                    byte[] data = path == "/combined" ? File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(video), "combine.mp4")) :
                        path == "/sound" ? File.ReadAllBytes(audio) :
                        path == "/small" || path == "/large" ? File.ReadAllBytes(video) : null;
                    string status = data == null ? "404 Not Found" : "200 OK";
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: " + (data == null ? 0 : data.Length) + "\r\nConnection: close\r\n\r\n");
                    stream.Write(reply, 0, reply.Length);
                    if (data != null && request[0] == "GET") stream.Write(data, 0, data.Length);
                }
            }
        }
    }
}
