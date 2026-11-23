using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal sealed class VideoConversion : MediaConversion
    {
        // Seuls les démultiplexeurs de fichiers vidéo autonomes peuvent ouvrir la source.
        const string Demuxers = "mov,matroska,avi,asf,mpegts,mpeg,flv,ogg,mxf,h264,hevc";
        const string ProbeFields = "format=format_name,duration:format_tags=major_brand:" +
            "stream=index,codec_type,codec_name,width,height,channels,sample_rate,duration,r_frame_rate,time_base:stream_tags=language:" +
            "stream_disposition:stream_side_data=rotation";
        int videoIndex;
        XmlElement[] audio;
        string nominalRate, sourceTimeBase, aviTimeBase;

        internal string Format { get; private set; }
        internal string Codec { get; private set; }
        internal string Warning { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal bool HasAudio { get { return audio.Length != 0; } }

        VideoConversion(string source, CancellationToken token, VideoEngine sharedEngine) : base(source, token, sharedEngine) { }

        internal static VideoConversion Open(string path, CancellationToken token)
        {
            return OpenWithEngine(path, token, null);
        }

        internal static VideoConversion OpenWithEngine(string path, CancellationToken token, VideoEngine sharedEngine)
        {
            token.ThrowIfCancellationRequested();
            var video = new VideoConversion(path, token, sharedEngine);
            try
            {
                string xml;
                try
                {
                    xml = video.engine.Run("ffprobe", new[] { "-v", "error", "-protocol_whitelist", "file", "-format_whitelist",
                        Demuxers, "-show_entries", ProbeFields, "-of", "xml", video.Source }, token);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException("Impossible de lire cette vidéo. Son format ou sa compression est incompatible, ou le fichier est endommagé.", ex);
                }
                video.ReadMetadata(xml);
                token.ThrowIfCancellationRequested();
                return video;
            }
            catch { video.Dispose(); throw; }
        }

        void ReadMetadata(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            var container = (XmlElement)doc.SelectSingleNode("/ffprobe/format");
            var streams = doc.SelectNodes("/ffprobe/streams/stream").Cast<XmlElement>().ToArray();
            var videos = streams.Where(s => s.GetAttribute("codec_type") == "video").ToArray();
            var primary = videos.Where(s => !Flag(s, "attached_pic") && !Flag(s, "timed_thumbnails") && !Flag(s, "still_image") &&
                Number(s, "width") > 0 && Number(s, "height") > 0)
                .OrderByDescending(s => Flag(s, "default"))
                .ThenByDescending(s => (long)Number(s, "width") * Number(s, "height")).FirstOrDefault();
            if (container == null || primary == null) throw new InvalidDataException("Le fichier ne contient aucune piste vidéo lisible.");
            Format = FormatName(container);
            videoIndex = Number(primary, "index");
            nominalRate = primary.GetAttribute("r_frame_rate");
            sourceTimeBase = primary.GetAttribute("time_base");
            string code = primary.GetAttribute("codec_name");
            var known = VideoOutput.Formats.SelectMany(f => f.Codecs).FirstOrDefault(c => c.Code == code);
            Codec = known == null ? code.ToUpperInvariant() : known.ToString();
            Width = Number(primary, "width");
            Height = Number(primary, "height");
            var rotation = primary.SelectSingleNode(".//side_datum[@key='rotation']/@value");
            int angle;
            if (rotation != null && int.TryParse(rotation.Value, NumberStyles.Integer, Invariant, out angle) && Math.Abs(angle % 180) == 90)
            {
                int width = Width;
                Width = Height;
                Height = width;
            }
            audio = streams.Where(s => s.GetAttribute("codec_type") == "audio").ToArray();
            Duration = Seconds(container);
            if (Duration == 0) Duration = Math.Max(Seconds(primary), audio.Select(Seconds).DefaultIfEmpty().Max());
            var omitted = new List<string>();
            if (videos.Length > 1) omitted.Add("les pistes vidéo supplémentaires ou les pochettes");
            if (streams.Any(s => s.GetAttribute("codec_type") == "subtitle")) omitted.Add("les sous-titres");
            if (streams.Any(s => s.GetAttribute("codec_type") == "attachment")) omitted.Add("les pièces jointes");
            if (streams.Any(s => !new[] { "audio", "video", "subtitle", "attachment" }.Contains(s.GetAttribute("codec_type"))))
                omitted.Add("les pistes de données");
            Warning = omitted.Count == 0 ? "" : "La copie omettra " + string.Join(", ", omitted) + ".";
        }

        string FormatName(XmlElement container)
        {
            string name = container.GetAttribute("format_name").Split(',')[0];
            switch (name)
            {
                case "mov":
                    var brand = container.SelectSingleNode(".//tag[@key='major_brand']/@value");
                    string code = brand == null ? "" : brand.Value.Trim();
                    if (code == "avif" || code == "avis" || code == "mif1" || code == "msf1" || code == "heic" || code == "heix")
                        throw new InvalidDataException("Choisissez une vidéo. Ce fichier contient une image.");
                    if (code == "qt" || brand == null) return "MOV";
                    if (code.StartsWith("3g", StringComparison.OrdinalIgnoreCase)) return "3GP";
                    return "MP4";
                case "matroska": return ReadEbmlDocType() == "webm" ? "WEBM" : "MKV";
                case "asf": return "WMV";
                default: return name.ToUpperInvariant();
            }
        }

        // FFprobe regroupe MKV et WebM. Le type de document EBML identifie le conteneur sans utiliser son extension.
        string ReadEbmlDocType()
        {
            sourceFile.Position = 0;
            if (ReadEbmlInteger(false) != 0x1a45dfa3) return "";
            long length = ReadEbmlInteger(true);
            long end = sourceFile.Position + Math.Min(length, 65536);
            while (sourceFile.Position < end)
            {
                long id = ReadEbmlInteger(false);
                long size = ReadEbmlInteger(true);
                if (id < 0 || size < 0 || size > end - sourceFile.Position) break;
                if (id == 0x4282 && size <= 32)
                {
                    var bytes = new byte[(int)size];
                    return sourceFile.Read(bytes, 0, bytes.Length) == size ? Encoding.ASCII.GetString(bytes) : "";
                }
                sourceFile.Position += size;
            }
            return "";
        }

        long ReadEbmlInteger(bool removeMarker)
        {
            int first = sourceFile.ReadByte();
            if (first <= 0) return -1;
            int mask = 0x80, length = 1;
            while ((first & mask) == 0) { mask >>= 1; length++; }
            long value = removeMarker ? first ^ mask : first;
            for (int i = 1; i < length; i++)
            {
                int next = sourceFile.ReadByte();
                if (next < 0) return -1;
                value = (value << 8) | (byte)next;
            }
            return value;
        }

        internal void Save(string destination, VideoOutput format, VideoCodec codec, ConversionQuality quality, bool overwrite,
            CancellationToken token, Action<double> progress)
        {
            if (format == null || !format.Codecs.Contains(codec)) throw new ArgumentException("Choisissez un encodage proposé pour ce format.");
            SaveCopy(destination, format.Extension, format.Code + " avec " + codec, Duration, overwrite, token, progress,
                temp => EncodingArguments(format, codec, quality, temp, token));
        }

        static bool PositiveRatio(string ratio, out int numerator, out int denominator)
        {
            numerator = denominator = 0;
            string[] parts = ratio.Split('/');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, Invariant, out numerator) && numerator > 0 &&
                int.TryParse(parts[1], NumberStyles.None, Invariant, out denominator) && denominator > 0;
        }

        string AviTimeBase(CancellationToken token)
        {
            if (aviTimeBase != null) return aviTimeBase;
            int rate, scale, tick, frequency;
            if (!PositiveRatio(nominalRate, out rate, out scale) || rate > 65535) return "1/1000";
            double interval = (double)scale / rate;
            double precision = PositiveRatio(sourceTimeBase, out tick, out frequency) ? (double)tick / frequency : 0;
            double tolerance = Math.Min(interval / 4, precision + 0.000002);
            double? first = null;
            bool variable = false;
            // AVI stocke un emplacement d’index par tick. Utiliser des millisecondes pour un film à
            // cadence fixe gonflerait cet index et lui attribuerait une cadence nominale de 1 000 i/s.
            using (var scan = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try
                {
                    engine.Run("ffprobe", new[] { "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", Demuxers,
                        "-select_streams", videoIndex.ToString(Invariant), "-show_entries", "packet=pts_time", "-of", "csv=p=0", Source },
                        scan.Token, line => {
                            double pts;
                            if (!double.TryParse(line.Split(',')[0], NumberStyles.Float, Invariant, out pts)) return;
                            if (!first.HasValue) first = pts;
                            double elapsed = pts - first.Value;
                            if (Math.Abs(elapsed - Math.Round(elapsed / interval) * interval) > tolerance)
                            {
                                variable = true;
                                scan.Cancel();
                            }
                        });
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    if (!variable) throw;
                }
            }
            token.ThrowIfCancellationRequested();
            aviTimeBase = variable ? "1/1000" : scale.ToString(Invariant) + "/" + rate.ToString(Invariant);
            return aviTimeBase;
        }

        List<string> EncodingArguments(VideoOutput format, VideoCodec codec, ConversionQuality quality, string temp, CancellationToken token)
        {
            var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-abort_on", "empty_output_stream", "-y",
                "-stats_period", "0.25", "-nostats", "-progress", "pipe:1", "-protocol_whitelist", "file",
                "-format_whitelist", Demuxers, "-i", Source, "-map", "0:" + videoIndex.ToString(Invariant) };
            foreach (var track in audio) args.AddRange(new[] { "-map", "0:" + track.GetAttribute("index") });
            codec.AddEncodingOptions(args, quality);
            args.AddRange(new[] { "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-fps_mode:v", "passthrough" });
            string timeBase = format.Code == "AVI" ? AviTimeBase(token) : format.Code == "WMV" ? "1/1000" : "demux";
            args.AddRange(new[] { "-enc_time_base:v", timeBase });
            if (format.Code == "MP4" || format.Code == "MOV")
            {
                args.AddRange(new[] { "-movflags", "+faststart" });
                if (codec == VideoCodec.Hevc) args.AddRange(new[] { "-tag:v", "hvc1" });
            }
            if (audio.Length > 0) args.AddRange(new[] { "-c:a", format.AudioEncoder, "-b:a", "128k" });
            for (int i = 0; i < audio.Length; i++)
            {
                string index = ":a:" + i.ToString(Invariant);
                var track = audio[i];
                // FFmpeg copie les métadonnées de chaque piste. Rendre les dispositions explicites empêche
                // l’activation automatique de la première piste lorsqu'aucune n'était marquée par défaut.
                var flags = track["disposition"].Attributes.Cast<XmlAttribute>().Where(a => a.Value == "1").Select(a => a.Name).ToArray();
                args.AddRange(new[] { "-disposition" + index, flags.Length == 0 ? "0" : string.Join("+", flags) });
                int channels = Number(track, "channels");
                if ((format.Code == "AVI" || format.Code == "WMV") && channels > 2)
                    args.AddRange(new[] { "-ac" + index, "2" });
                if (format.Code == "WMV" && Number(track, "sample_rate") > 48000) args.AddRange(new[] { "-ar" + index, "48000" });
                if (format.Code == "WEBM" && channels > 2)
                {
                    // Opus utilise les positions Vorbis, notamment les enceintes arrière pour le 5.1.
                    string[] layouts = { "3.0", "quad", "5.0", "5.1", "6.1", "7.1" };
                    if (channels <= 8) args.AddRange(new[] { "-channel_layout" + index, layouts[channels - 3] });
                    else args.AddRange(new[] { "-mapping_family" + index, "255" });
                }
            }
            args.AddRange(new[] { "-f", format.Muxer, temp });
            return args;
        }

    }
}
