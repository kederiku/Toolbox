using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace AtelierPC
{
    internal static class YoutubeDownloader
    {
        internal static YoutubeVideo Inspect(string input, CancellationToken token)
        {
            string id = YoutubeVideo.ParseId(input);
            using (var engine = new VideoEngine(token))
            {
                string json = Run(engine, new[] { "--dump-single-json", "--format", "best*", "--",
                    "https://www.youtube.com/watch?v=" + id }, token, null);
                return YoutubeVideo.FromJson(id, json);
            }
        }

        internal static void Save(YoutubeVideo video, YoutubeResolution resolution, string destination, bool overwrite,
            CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested();
            if (video == null || resolution == null || !video.Resolutions.Contains(resolution))
                throw new InvalidDataException("Choisissez une résolution proposée pour cette vidéo.");
            SaveMedia(video, resolution, destination, overwrite, token, progress);
        }

        internal static void SaveAudio(YoutubeVideo video, string destination, bool overwrite,
            CancellationToken token, Action<string> progress)
        {
            token.ThrowIfCancellationRequested();
            if (video == null || string.IsNullOrEmpty(video.AudioSelector))
                throw new InvalidDataException("Aucune piste audio n’est disponible pour cette vidéo.");
            SaveMedia(video, null, destination, overwrite, token, progress);
        }

        static void SaveMedia(YoutubeVideo video, YoutubeResolution resolution, string destination, bool overwrite,
            CancellationToken token, Action<string> progress)
        {
            bool audioOnly = resolution == null;
            string extension = audioOnly ? ".mp3" : ".mp4";
            destination = MediaConversion.WindowsFilePath(destination);
            if (!string.Equals(Path.GetExtension(destination), extension, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choisissez un nom de fichier avec l’extension " + extension + ".");
            if (!overwrite && File.Exists(destination)) throw new IOException("Un fichier existe déjà à cet emplacement.");
            string staging = Path.Combine(Path.GetDirectoryName(destination), ".AtelierPC-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                using (var engine = new VideoEngine(token))
                {
                    var ids = audioOnly ? new[] { video.AudioSelector } : resolution.Selector.Split('+');
                    if (progress != null) progress("Préparation du téléchargement…");
                    DownloadTracks(engine, video, ids, audioOnly, staging, token, progress);
                    token.ThrowIfCancellationRequested();
                    string first = Path.Combine(staging, "piece-" + ids[0] + ".media");
                    string output = Path.Combine(staging, "final" + extension);
                    if (audioOnly) WriteMp3(engine, first, output, token, progress);
                    else
                    {
                        if (progress != null) progress("Enregistrement du MP4…");
                        string audio = ids.Length == 2 ? Path.Combine(staging, "piece-" + ids[1] + ".media") : first;
                        Mux(engine, first, audio, resolution, output, token);
                    }
                    token.ThrowIfCancellationRequested();
                    if (overwrite && File.Exists(destination)) File.Replace(output, destination, null);
                    else File.Move(output, destination);
                }
            }
            finally { VideoEngine.RemoveTemporaryDirectory(staging); }
        }

        static void DownloadTracks(VideoEngine engine, YoutubeVideo video, string[] ids, bool audioOnly, string staging,
            CancellationToken token, Action<string> progress)
        {
            string info = Path.Combine(engine.WorkingDirectory, "video.info.json");
            File.WriteAllText(info, video.DownloadInfo, new UTF8Encoding(false));
            engine.Executable("ffmpeg", token);
            Run(engine, new[] { "--load-info-json", info, "--format", string.Join(",", ids), "--no-simulate",
                "--paths", staging, "--output", "piece-%(format_id)s.media", "--fixup", "never",
                "--abort-on-unavailable-fragments", "--quiet", "--newline", "--progress", "--progress-delta", "0.3",
                "--progress-template", "download:ATELIER %(info.format_id)s %(progress._percent_str)s" },
                token, line => {
                    if (progress == null || !line.StartsWith("ATELIER ", StringComparison.Ordinal)) return;
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3) return;
                    bool sound = audioOnly || (ids.Length == 2 && parts[1] == ids[1]);
                    progress((sound ? "Téléchargement du son… " : "Téléchargement de la vidéo… ") + parts[2]);
                });
        }

        static void WriteMp3(VideoEngine engine, string source, string output, CancellationToken token, Action<string> progress)
        {
            if (!File.Exists(source)) throw new InvalidDataException("La piste audio n’est plus disponible. Relancez la recherche.");
            if (progress != null) progress("Enregistrement du MP3…");
            using (var audio = AudioConversion.OpenWithEngine(source, token, engine))
            {
                var mp3 = AudioOutput.Formats.Single(format => format.Code == "MP3");
                audio.Save(output, mp3, audio.DefaultTrack, ConversionQuality.Balanced, false, token, value => {
                    if (progress != null) progress("Enregistrement du MP3… " + value.ToString("0") + " %");
                });
            }
            using (var result = AudioConversion.OpenWithEngine(output, token, engine))
                if (result.HasOtherStreams || result.Tracks.Length != 1 || result.DefaultTrack.Codec != "MP3")
                    throw new InvalidDataException("Le fichier obtenu ne contient pas la piste MP3 attendue.");
        }

        static void Mux(VideoEngine engine, string picture, string sound, YoutubeResolution resolution, string output, CancellationToken token)
        {
            if (!File.Exists(picture) || (resolution.HasAudio && !File.Exists(sound)))
                throw new InvalidDataException("Un flux de la résolution choisie est indisponible. Affichez à nouveau les résolutions.");
            const string formats = "mov,matroska,mpegts,aac,mp3,ogg,flv";
            var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-abort_on", "empty_output_stream", "-n",
                "-protocol_whitelist", "file", "-format_whitelist", formats, "-i", picture };
            if (sound != picture) args.AddRange(new[] { "-protocol_whitelist", "file", "-format_whitelist", formats, "-i", sound });
            string codec = ProbeCodec(engine, picture, "v:0", formats, token);
            bool copy = new[] { "h264", "hevc", "av1", "vp9", "mpeg4" }.Contains(codec);
            args.AddRange(new[] { "-map", "0:v:0", "-c:v", copy ? "copy" : "libx264" });
            if (resolution.HasAudio)
            {
                args.AddRange(new[] { "-map", sound == picture ? "0:a:0" : "1:a:0" });
                string audioCodec = ProbeCodec(engine, sound, "a:0", formats, token);
                args.AddRange(audioCodec == "aac" ? new[] { "-c:a", "copy" } : new[] { "-c:a", "aac", "-b:a", "192k" });
            }
            args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", output });
            try { engine.Run("ffmpeg", args, token); }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("Impossible d’assembler le MP4. Vérifiez l’espace disponible, puis relancez le téléchargement.", ex);
            }
            using (var media = VideoConversion.OpenWithEngine(output, token, engine))
            {
                if (media.Width != resolution.Width || media.Height != resolution.Height)
                    throw new InvalidDataException("Les dimensions reçues ont changé. Affichez à nouveau les résolutions.");
                if (resolution.HasAudio && !media.HasAudio)
                    throw new InvalidDataException("La piste audio reçue est incomplète. Relancez le téléchargement.");
            }
        }

        static string ProbeCodec(VideoEngine engine, string file, string stream, string formats, CancellationToken token)
        {
            string codec = engine.Run("ffprobe", new[] { "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", formats,
                "-select_streams", stream, "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", file }, token).Trim();
            if (codec.Length == 0) throw new InvalidDataException("Une piste attendue est absente. Affichez à nouveau les résolutions.");
            return codec;
        }

        static string Run(VideoEngine engine, IEnumerable<string> action, CancellationToken token, Action<string> output)
        {
            string dir = engine.WorkingDirectory;
            ExtractTools("AtelierPC.YoutubeEngine.zip", dir, token);
            ExtractTools("AtelierPC.Deno.zip", dir, token);
            var args = new List<string> { "--ignore-config", "--no-plugin-dirs", "--no-remote-components", "--no-playlist",
                "--no-cache-dir", "--encoding", "utf-8", "--color", "never", "--socket-timeout", "20", "--retries", "2",
                "--extractor-retries", "2", "--fragment-retries", "2", "--ffmpeg-location", dir,
                "--js-runtimes", "deno:" + Path.Combine(dir, "deno.exe") };
            args.AddRange(action);
            return engine.RunProcess(Path.Combine(dir, "yt-dlp.exe"), args, token, output, true, ExplainError);
        }

        static Exception ExplainError(string detail)
        {
            string message;
            string error = detail.ToLowerInvariant();
            if (error.Contains("requested format") || error.Contains("403") || error.Contains("expired"))
                message = "YouTube ne fournit plus le flux choisi. Cliquez sur « Analyser le lien » et réessayez.";
            else if (error.Contains("private video") || error.Contains("members-only") || error.Contains("age-restricted"))
                message = "L’accès à cette vidéo est restreint par YouTube.";
            else if (error.Contains("sign in") || error.Contains("sign in to confirm") || error.Contains("429"))
                message = "YouTube demande une vérification pour cette connexion. Réessayez plus tard.";
            else if (error.Contains("video unavailable") || error.Contains("not available") || error.Contains("removed"))
                message = "Cette vidéo est indisponible sur YouTube.";
            else if (error.Contains("timed out") || error.Contains("unable to download") || error.Contains("connection") || error.Contains("socket"))
                message = "Impossible de joindre YouTube. Vérifiez la connexion Internet, puis réessayez.";
            else
            {
                string last = detail.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                message = "Le téléchargement YouTube a échoué." + (string.IsNullOrWhiteSpace(last) ? "" : " " + last);
            }
            return new InvalidDataException(message, new Exception(detail));
        }

        static void ExtractTools(string resource, string dir, CancellationToken token)
        {
            using (var data = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (data == null) throw new InvalidDataException("Le moteur YouTube manque dans cet exécutable.");
                using (var zip = new ZipArchive(data, ZipArchiveMode.Read))
                {
                    var buffer = new byte[81920];
                    foreach (var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.Name.Length == 0) continue;
                        string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
                        if (!target.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Chemin inattendu dans le moteur YouTube.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var source = entry.Open())
                        using (var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            int read;
                            while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                token.ThrowIfCancellationRequested();
                                dest.Write(buffer, 0, read);
                            }
                        }
                    }
                }
            }
        }
    }
}
