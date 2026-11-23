using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckVideoConversions()
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-vidéos-" + Guid.NewGuid().ToString("N")));
            var token = CancellationToken.None;
            try
            {
                using (var tools = new VideoEngine(token))
                {
                    string source = Path.Combine(folder.FullName, "été [1] %d.inconnu");
                    tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=96x64:r=12:d=1",
                        "-f", "lavfi", "-i", "sine=f=440:r=48000:d=1", "-c:v", "libx264", "-c:a", "aac", "-f", "mp4", source }, token);
                    using (var video = VideoConversion.Open(source, token))
                    {
                        Check(video.Format == "MP4" && video.Width == 96 && video.Height == 64 &&
                            Math.Abs(video.Duration - 1) < 0.12 && video.Warning == "", "Vidéo locale identifiée par son contenu");
                        CheckVideoFormats(tools, video, folder.FullName);
                        CheckVideoFileProtection(tools, video, folder.FullName);
                    }
                    CheckVideoStreams(tools, source, folder.FullName);
                    CheckVideoTiming(tools, source, folder.FullName);
                    CheckVideoEncodingFailure(tools, source, folder.FullName);
                    CheckInvalidVideos(tools, source, folder.FullName);
                }
            }
            finally { folder.Delete(true); }
        }

        static void CheckVideoFormats(VideoEngine tools, VideoConversion video, string folder)
        {
            var expected = new Dictionary<string, string[]> {
                { "MP4", new[] { "mov,mp4,m4a,3gp,3g2,mj2", "aac", "h264", "hevc", "av1" } },
                { "MKV", new[] { "matroska,webm", "aac", "h264", "hevc", "av1", "vp9" } },
                { "WEBM", new[] { "matroska,webm", "opus", "vp9", "av1" } },
                { "MOV", new[] { "mov,mp4,m4a,3gp,3g2,mj2", "aac", "h264", "hevc" } },
                { "AVI", new[] { "avi", "mp3", "mpeg4", "mjpeg" } },
                { "WMV", new[] { "asf", "wmav2", "wmv2", "wmv1" } }
            };
            var token = CancellationToken.None;
            Check(VideoOutput.Formats.Select(f => f.Code).SequenceEqual(expected.Keys), "Les six formats disponibles commencent par MP4");
            var mp4 = VideoOutput.Formats[0];
            var qualitiesChecked = new HashSet<string>();
            foreach (var format in VideoOutput.Formats)
            {
                var row = expected[format.Code];
                Check(format.Codecs.Length == row.Length - 2, "Choix de codecs pour " + format.Code);
                for (int i = 0; i < format.Codecs.Length; i++)
                {
                    var codec = format.Codecs[i];
                    string codecName = row[i + 2];
                    string output = Path.Combine(folder, "copie été [0] " + codecName + "." + format.Extension);
                    var progress = new List<double>();
                    video.Save(output, format, codec, ConversionQuality.Balanced, false, token, progress.Add);
                    Check(progress.Count > 0 && progress.Last() == 100 && progress.SequenceEqual(progress.OrderBy(n => n)) &&
                        progress.All(n => n >= 0 && n <= 100), "Progression de la conversion " + codecName);
                    var info = ProbeVideo(tools, output);
                    Check(info.SelectSingleNode("//format/@format_name").Value == row[0] &&
                        info.SelectSingleNode("//stream[@codec_type='video']/@codec_name").Value == codecName &&
                        info.SelectSingleNode("//stream[@codec_type='audio']/@codec_name").Value == row[1],
                        "Conteneur et deux codecs relus pour " + format.Code + " / " + codecName);
                    using (var saved = VideoConversion.Open(output, token))
                    {
                        Check(saved.Format == format.Code && saved.Width == 96 && saved.Height == 64 &&
                            Math.Abs(saved.Duration - 1) < 0.2, "Relecture de " + format.Code + " / " + codecName);
                        if (format.Code == "AVI" || format.Code == "WMV")
                        {
                            var frameRate = info.SelectSingleNode("//stream[@codec_type='video']/@r_frame_rate").Value;
                            Check(frameRate == "12/1", "La vidéo à cadence fixe reste à douze images par seconde dans " + format.Code);
                            string back = Path.Combine(folder, "retour " + codecName + ".mp4");
                            saved.Save(back, mp4, mp4.Codecs[0], ConversionQuality.Balanced, false, token, null);
                            var backInfo = ProbeVideo(tools, back);
                            Check(backInfo.SelectSingleNode("//stream[@codec_name='h264']") != null &&
                                backInfo.SelectSingleNode("//stream[@codec_name='aac']") != null, format.Code + " converti en MP4 sonorisé");
                        }
                    }
                    if (qualitiesChecked.Add(codecName))
                    {
                        long balanced = VideoPacketBytes(tools, output);
                        video.Save(output, format, codec, ConversionQuality.Smaller, true, token, null);
                        long smaller = VideoPacketBytes(tools, output);
                        video.Save(output, format, codec, ConversionQuality.Higher, true, token, null);
                        long higher = VideoPacketBytes(tools, output);
                        Check(smaller < balanced && balanced < higher, "Les trois qualités modifient le flux encodé pour " + codecName);
                    }
                }
            }
        }

        static long VideoPacketBytes(VideoEngine tools, string path)
        {
            var text = tools.Run("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=size",
                "-of", "csv=p=0", path }, CancellationToken.None);
            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Sum(s => long.Parse(s.TrimEnd(','), CultureInfo.InvariantCulture));
        }

        static void CheckVideoFileProtection(VideoEngine tools, VideoConversion video, string folder)
        {
            var token = CancellationToken.None;
            var mp4 = VideoOutput.Formats[0];
            var original = File.ReadAllBytes(video.Source);
            bool refused = false;
            try { video.Save(video.Source, mp4, mp4.Codecs[0], ConversionQuality.Balanced, true, token, null); }
            catch (IOException) { refused = true; }
            Check(refused && original.SequenceEqual(File.ReadAllBytes(video.Source)), "Protection de la vidéo d’origine");
            string dest = Path.Combine(folder, "copie à préserver.mp4");
            File.WriteAllText(dest, "copie existante");
            refused = false;
            try { video.Save(dest, mp4, mp4.Codecs[0], ConversionQuality.Balanced, false, token, null); }
            catch (IOException) { refused = true; }
            Check(refused && File.ReadAllText(dest) == "copie existante", "Le remplacement doit être confirmé");
            string longSource = Path.Combine(folder, "annulation.mp4");
            tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=12",
                "-c:v", "libx264", "-preset", "ultrafast", longSource }, token);
            var before = Directory.GetFiles(folder).OrderBy(s => s).ToArray();
            bool interrupted = false;
            using (var cancel = new CancellationTokenSource())
            using (var longer = VideoConversion.Open(longSource, token))
            {
                var av1 = mp4.Codecs.Single(c => c.Code == "av1");
                try { longer.Save(dest, mp4, av1, ConversionQuality.Balanced, true, cancel.Token,
                    percent => { if (percent > 0 && percent < 90) cancel.Cancel(); }); }
                catch (OperationCanceledException) { interrupted = true; }
            }
            Check(interrupted && File.ReadAllText(dest) == "copie existante" && before.SequenceEqual(Directory.GetFiles(folder).OrderBy(s => s)),
                "Une annulation pendant l’encodage préserve la copie et supprime le fichier temporaire");
            string concurrent = Path.Combine(folder, "copie concurrente.mp4");
            refused = false;
            try
            {
                video.Save(concurrent, mp4, mp4.Codecs[0], ConversionQuality.Balanced, false, token, percent => {
                    if (percent < 100 && !File.Exists(concurrent)) File.WriteAllText(concurrent, "créée pendant la conversion");
                });
            }
            catch (IOException) { refused = true; }
            Check(refused && File.ReadAllText(concurrent) == "créée pendant la conversion", "Une collision pendant l’encodage préserve la nouvelle destination");
            var wmv = VideoOutput.Formats.Single(f => f.Code == "WMV");
            refused = false;
            try { video.Save(dest, mp4, wmv.Codecs[0], ConversionQuality.Balanced, true, token, null); }
            catch (ArgumentException) { refused = true; }
            Check(refused && File.ReadAllText(dest) == "copie existante", "Un couple format/codec incompatible est rejeté");
        }

        static void CheckVideoStreams(VideoEngine tools, string source, string folder)
        {
            var token = CancellationToken.None;
            string cover = Path.Combine(folder, "pochette.mp4");
            string jpeg = Path.Combine(folder, "pochette.jpg");
            tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=48x32:d=0.1", "-frames:v", "1", "-update", "1", jpeg }, token);
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-i", jpeg, "-map", "0:v",
                "-map", "1:v", "-c", "copy", "-disposition:v:1", "attached_pic", cover }, token);
            using (var withCover = VideoConversion.Open(cover, token))
                Check(withCover.Width == 96 && withCover.Warning.Length > 0, "Une pochette est signalée et la vidéo principale est sélectionnée");

            string subtitles = Path.Combine(folder, "sous-titres.srt");
            File.WriteAllText(subtitles, "1\r\n00:00:00,100 --> 00:00:00,800\r\nBonjour\r\n");
            string tracks = Path.Combine(folder, "pistes.mkv");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-f", "lavfi", "-i", "testsrc2=s=48x32:r=12:d=1",
                "-i", subtitles, "-map", "1:v", "-map", "0:v", "-map", "0:a", "-map", "0:a", "-map", "2:s", "-c:v:0", "libx264",
                "-c:v:1", "copy", "-c:a", "copy", "-c:s", "srt", "-disposition:v:0", "0", "-disposition:v:1", "default",
                "-metadata:s:a:0", "language=fra", "-metadata:s:a:1", "language=eng", "-disposition:a:0", "0", "-disposition:a:1", "default",
                "-attach", subtitles, "-metadata:s:t:0", "mimetype=text/plain", tracks }, token);
            using (var video = VideoConversion.Open(tracks, token))
            {
                Check(video.Width == 96 && video.Warning.Contains("sous-titre") && video.Warning.Contains("vidéo") &&
                    video.Warning.Contains("jointe"), "Piste vidéo par défaut et avertissement des trois types de pertes");
                var mkv = VideoOutput.Formats.Single(f => f.Code == "MKV");
                string dest = Path.Combine(folder, "langues.mkv");
                video.Save(dest, mkv, mkv.Codecs[0], ConversionQuality.Balanced, false, token, null);
                var info = ProbeVideo(tools, dest);
                var streams = info.SelectNodes("//stream").Cast<XmlElement>().ToArray();
                var audio = streams.Where(n => n.GetAttribute("codec_type") == "audio").ToArray();
                Check(streams.Length == 3 && audio.Length == 2 && streams[0].GetAttribute("width") == "96", "Sortie composée de la vidéo principale et des deux sons");
                Check(audio[0].SelectSingleNode(".//tag[@key='language']/@value").Value == "fra" && audio[0].SelectSingleNode("disposition/@default").Value == "0" &&
                    audio[1].SelectSingleNode(".//tag[@key='language']/@value").Value == "eng" && audio[1].SelectSingleNode("disposition/@default").Value == "1",
                    "Langues et piste audio par défaut préservées");
            }
            string surround = Path.Combine(folder, "son multicanal.mov");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-f", "lavfi", "-i", "anullsrc=r=96000:cl=5.1(side):d=1",
                "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "pcm_s16le", surround }, token);
            using (var video = VideoConversion.Open(surround, token))
            {
                var webm = VideoOutput.Formats.Single(f => f.Code == "WEBM");
                string modern = Path.Combine(folder, "son 5.1.webm");
                video.Save(modern, webm, webm.Codecs[0], ConversionQuality.Balanced, false, token, null);
                Check(ProbeVideo(tools, modern).SelectSingleNode("//stream[@codec_name='opus' and @channels='6']") != null,
                    "Les six canaux du son 5.1 latéral sont présents dans le WebM");
                var wmv = VideoOutput.Formats.Single(f => f.Code == "WMV");
                string dest = Path.Combine(folder, "ancien format.wmv");
                video.Save(dest, wmv, wmv.Codecs[0], ConversionQuality.Balanced, false, token, null);
                var info = ProbeVideo(tools, dest);
                Check(info.SelectSingleNode("//stream[@codec_name='wmav2' and @channels='2' and @sample_rate='48000']") != null,
                    "Le WMV adapte le son multicanal à 96 kHz aux limites de WMA2");
            }
        }

        static void CheckVideoTiming(VideoEngine tools, string source, string folder)
        {
            var token = CancellationToken.None;
            string variable = Path.Combine(folder, "cadence.mp4");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-an", "-vf", "select='not(mod(n,2))+eq(n,5)'",
                "-fps_mode", "passthrough", "-c:v", "libx264", variable }, token);
            string portrait = Path.Combine(folder, "portrait.mp4");
            tools.Run("ffmpeg", new[] { "-v", "error", "-display_rotation:v:0", "90", "-i", variable, "-c", "copy", portrait }, token);
            using (var video = VideoConversion.Open(portrait, token))
            {
                Check(video.Width == 64 && video.Height == 96 && video.Warning.Length == 0, "Dimensions après rotation de la vidéo muette");
                var mp4 = VideoOutput.Formats[0];
                string dest = Path.Combine(folder, "portrait encodé.mp4");
                video.Save(dest, mp4, mp4.Codecs[0], ConversionQuality.Balanced, false, token, null);
                var info = ProbeVideo(tools, dest);
                Check(info.SelectNodes("//stream").Count == 1 && info.SelectSingleNode("//stream/@width").Value == "64" &&
                    info.SelectSingleNode("//stream/@height").Value == "96", "Vidéo muette réencodée en portrait");
                var inputTimes = VideoTimestamps(tools, portrait);
                var outputTimes = VideoTimestamps(tools, dest);
                Check(inputTimes.Length == 7 && inputTimes.Length == outputTimes.Length &&
                    inputTimes.Zip(outputTimes, (a, b) => Math.Abs(a - b)).All(diff => diff < 0.002), "Les instants de chaque image à cadence variable sont conservés");
            }
            string uneven = Path.Combine(folder, "intervalles irréguliers.mkv");
            tools.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=96x64:r=30:d=1", "-vf",
                "settb=1/1000,setpts='33*N+20*mod(N,2)'", "-fps_mode", "passthrough", "-enc_time_base:v", "1/1000", "-c:v", "libx264", uneven }, token);
            using (var video = VideoConversion.Open(uneven, token))
            {
                var inputTimes = VideoTimestamps(tools, uneven);
                foreach (var format in VideoOutput.Formats.Where(f => f.Code == "AVI" || f.Code == "WMV"))
                {
                    foreach (var codec in format.Codecs)
                    {
                        string dest = Path.Combine(folder, "intervalles " + codec.Code + "." + format.Extension);
                        video.Save(dest, format, codec, ConversionQuality.Balanced, false, token, null);
                        var outputTimes = VideoTimestamps(tools, dest);
                        Check(inputTimes.Length == 30 && outputTimes.Length == 30 &&
                            inputTimes.Zip(outputTimes, (a, b) => Math.Abs(a - b)).All(diff => diff < 0.002),
                            "Le " + format.Code + " en " + codec + " conserve trente images à intervalles irréguliers");
                    }
                }
            }
        }

        static double[] VideoTimestamps(VideoEngine tools, string path)
        {
            string text = tools.Run("ffprobe", new[] { "-v", "error", "-select_streams", "v:0",
                "-show_entries", "packet=pts_time", "-of", "csv=p=0", path }, CancellationToken.None);
            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s.TrimEnd(','), CultureInfo.InvariantCulture)).OrderBy(n => n).ToArray();
        }

        static void CheckVideoEncodingFailure(VideoEngine tools, string source, string folder)
        {
            var token = CancellationToken.None;
            var packets = new XmlDocument { XmlResolver = null };
            packets.LoadXml(tools.Run("ffprobe", new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
                "packet=pos,size", "-of", "xml", source }, token));
            var packet = (XmlElement)packets.SelectNodes("//packet")[2];
            string damaged = Path.Combine(folder, "paquet incomplet.mp4");
            byte[] data = File.ReadAllBytes(source);
            Array.Clear(data, int.Parse(packet.GetAttribute("pos"), CultureInfo.InvariantCulture),
                int.Parse(packet.GetAttribute("size"), CultureInfo.InvariantCulture));
            File.WriteAllBytes(damaged, data);
            string dest = Path.Combine(folder, "bonne copie.mp4");
            File.WriteAllText(dest, "à préserver");
            var before = Directory.GetFiles(folder).OrderBy(s => s).ToArray();
            using (var video = VideoConversion.Open(damaged, token))
            {
                bool failed = false;
                var mp4 = VideoOutput.Formats[0];
                try { video.Save(dest, mp4, mp4.Codecs[0], ConversionQuality.Balanced, true, token, null); }
                catch (InvalidDataException error) { failed = error.Message.Contains("MP4") && error.InnerException != null; }
                Check(failed && File.ReadAllText(dest) == "à préserver" && before.SequenceEqual(Directory.GetFiles(folder).OrderBy(s => s)),
                    "Un paquet illisible donne une explication, préserve la copie existante et nettoie la conversion");
            }
            string empty = Path.Combine(folder, "piste vidéo vide.avi");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-map", "0:v", "-map", "0:a", "-vf", "select=0",
                "-c:v", "rawvideo", "-c:a", "pcm_s16le", empty }, token);
            using (var video = VideoConversion.Open(empty, token))
            {
                var mp4 = VideoOutput.Formats[0];
                bool failed = false;
                try { video.Save(dest, mp4, mp4.Codecs[0], ConversionQuality.Balanced, true, token, null); }
                catch (InvalidDataException) { failed = true; }
                Check(failed && File.ReadAllText(dest) == "à préserver", "Une piste vidéo sans images ne peut pas remplacer une copie existante");
            }
        }

        static void CheckInvalidVideos(VideoEngine tools, string source, string folder)
        {
            var token = CancellationToken.None;
            string invalid = Path.Combine(folder, "cassé.mp4");
            File.WriteAllText(invalid, "contenu illisible");
            string list = Path.Combine(folder, "liste.mp4");
            File.WriteAllText(list, "ffconcat version 1.0\nfile '" + source.Replace('\\', '/') + "'\n");
            string image = Path.Combine(folder, "image.png");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-frames:v", "1", "-update", "1", image }, token);
            string onlyAudio = Path.Combine(folder, "audio.m4a");
            tools.Run("ffmpeg", new[] { "-v", "error", "-i", source, "-vn", "-c:a", "copy", onlyAudio }, token);
            foreach (string file in new[] { invalid, list, image, onlyAudio })
            {
                bool refused = false;
                try { using (VideoConversion.Open(file, token)) { } }
                catch (InvalidDataException error) { refused = error.Message.Contains("vidéo"); }
                Check(refused, "Entrée non vidéo refusée pour " + Path.GetFileName(file));
            }
            foreach (var forbidden in new[] { "https://example.invalid/sequence.mp4", @"\\.\NUL", @"\\localhost\pipe\video", "NUL.mp4", source + ":autre" })
            {
                bool refused = false;
                try { using (VideoConversion.Open(forbidden, token)) { } } catch (InvalidDataException) { refused = true; }
                Check(refused, "Entrée refusée avant lecture pour " + forbidden);
            }
        }

        static XmlDocument ProbeVideo(VideoEngine tools, string path)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(tools.Run("ffprobe", new[] { "-v", "error", "-show_streams", "-show_format", "-of", "xml", path }, CancellationToken.None));
            return doc;
        }
    }
}
