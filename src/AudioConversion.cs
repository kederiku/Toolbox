using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal sealed class AudioTrack
    {
        internal int Index, Channels, SampleRate;
        internal double Duration;
        internal bool IsDefault;
        internal string Codec, Language, Title, PcmPrecision, Layout;

        public override string ToString()
        {
            return string.Join(" · ", new[] { "Piste " + (Index + 1), Language, Title, Codec }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    internal sealed class AudioConversion : MediaConversion
    {
        // Fichiers autonomes seulement, y compris les conteneurs multimédias avec une piste audio.
        const string Demuxers = "aac,ac3,ac4,adx,aiff,amr,ape,asf,au,avi,caf,dsf,dss,dts,dtshd,eac3,flac,flv," +
            "iff,ircam,libgme,libopenmpt,loas,matroska,mlp,mmf,mov,mp3,mpc,mpc8,mpeg,mpegts,mxf,nistsphere," +
            "ogg,oma,qcp,qoa,rm,shn,sox,spdif,tak,truehd,tta,voc,vqf,w64,wav,wsd,wv,xwma," +
            "aa,aea,aix,apac,ast,avr,bfstm,binka,bonk,brstm,codec2,daud,derf,fsb,hca,hcom,ilbc,laf," +
            "mca,osq,paf,pvf,rka,rso,sbc,sds,sdx,sol,svag,vag,wavarc,wve,xa,xvag,act,afc,gsm";
        const string ExtraExtensions = "aif aifc rf64 bwf wave snd alac ogv " +
            "ac4 dts dtshd dtsma thd mlp dsf dff wsd ape mac mpc mpp mp+ tak shn oma aa " +
            "at3 at9 aa3 ra rm rmvb aob adts adt latm loas mp1 mpa mpeg mpg mp4 m4v 3gp 3g2 mov mkv avi wmv " +
            "flv ts m2ts mts mxf vob sox ircam nist sph qcp qoa vqf dss gsm binka " +
            "mod xm s3m it mptm mo3 mtm stm med 669 amf ams dbm dtm far gdm mdl okt ptm umx " +
            "nsf nsfe spc gbs gym hes kss sap vgm vgz ast brstm bfstm fsb hca ilbc mmf sbc " +
            "xwma xa vag xvag paf pvf avr rso wve bonk rka act afc";

        internal static readonly string InputFilter = "Fichiers audio et multimédias|" +
            string.Join(";", AudioOutput.Formats.Select(f => f.Extension)
                .Concat(ExtraExtensions.Split(' ')).Distinct().Select(ext => "*." + ext)) + "|Tous les fichiers|*.*";

        internal AudioTrack[] Tracks { get; private set; }
        internal AudioTrack DefaultTrack { get; private set; }
        internal string Format { get; private set; }
        internal bool HasOtherStreams { get; private set; }

        AudioConversion(string path, CancellationToken token, VideoEngine sharedEngine) : base(path, token, sharedEngine) { }

        internal static AudioConversion Open(string path, CancellationToken token)
        {
            return OpenWithEngine(path, token, null);
        }

        internal static AudioConversion OpenWithEngine(string path, CancellationToken token, VideoEngine sharedEngine)
        {
            var audio = new AudioConversion(path, token, sharedEngine);
            try
            {
                const string fields = "format=format_name,duration:stream=index,codec_type,codec_name,sample_rate,channels,channel_layout," +
                    "bits_per_raw_sample,bits_per_sample,sample_fmt,duration:stream_tags=title,language:stream_disposition=default";
                string xml;
                try
                {
                    xml = audio.engine.Run("ffprobe", new[] { "-v", "error", "-protocol_whitelist", "file",
                        "-format_whitelist", Demuxers, "-show_entries", fields, "-of", "xml", audio.Source }, token);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException("Impossible de lire ce fichier audio. Son format est incompatible ou le fichier est endommagé.", ex);
                }
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xml);
                var streams = doc.SelectNodes("/ffprobe/streams/stream").Cast<XmlElement>().ToArray();
                audio.Tracks = streams.Where(s => s.GetAttribute("codec_type") == "audio" &&
                    Number(s, "sample_rate") > 0 && Number(s, "channels") > 0 && s.GetAttribute("codec_name").Length > 0)
                    .Select(ReadTrack).ToArray();
                var container = (XmlElement)doc.SelectSingleNode("/ffprobe/format");
                if (container == null || audio.Tracks.Length == 0)
                    throw new InvalidDataException("Ce fichier ne contient aucune piste audio lisible.");
                audio.DefaultTrack = audio.Tracks.FirstOrDefault(t => t.IsDefault) ?? audio.Tracks[0];
                audio.HasOtherStreams = streams.Length != audio.Tracks.Length;
                audio.Duration = Seconds(container);
                string format = container.GetAttribute("format_name").Split(',')[0];
                switch (format)
                {
                    case "mov": audio.Format = "MP4 / M4A / MOV"; break;
                    case "matroska": audio.Format = "Matroska / WebM"; break;
                    case "asf": audio.Format = "ASF / WMA"; break;
                    case "mp3": audio.Format = audio.DefaultTrack.Codec; break;
                    default: audio.Format = format.ToUpperInvariant(); break;
                }
                token.ThrowIfCancellationRequested();
                return audio;
            }
            catch { audio.Dispose(); throw; }
        }

        static AudioTrack ReadTrack(XmlElement stream)
        {
            var language = stream.SelectSingleNode("tag[@key='language']/@value");
            var title = stream.SelectSingleNode("tag[@key='title']/@value");
            string sample = stream.GetAttribute("sample_fmt").TrimEnd('p');
            int bits = Math.Max(Number(stream, "bits_per_raw_sample"), Number(stream, "bits_per_sample"));
            string precision;
            if (sample == "flt" || sample == "dbl") precision = sample == "flt" ? "f32" : "f64";
            else if (bits > 24 || (bits == 0 && sample == "s32")) precision = "s32";
            else precision = bits > 16 ? "s24" : "s16";
            return new AudioTrack {
                Index = Number(stream, "index"), Channels = Number(stream, "channels"), SampleRate = Number(stream, "sample_rate"),
                Codec = stream.GetAttribute("codec_name").ToUpperInvariant(), Language = language == null ? "" : language.Value,
                Title = title == null ? "" : title.Value, Duration = Seconds(stream), IsDefault = Flag(stream, "default"),
                PcmPrecision = precision, Layout = stream.GetAttribute("channel_layout")
            };
        }

        internal double DurationFor(AudioTrack track) { return track.Duration > 0 ? track.Duration : Duration; }

        internal void Save(string destination, AudioOutput format, AudioTrack track, ConversionQuality quality, bool overwrite,
            CancellationToken token, Action<double> progress)
        {
            if (format == null || !Tracks.Contains(track)) throw new ArgumentException("Choisissez un format et une piste audio.");
            SaveCopy(destination, format.Extension, format.Code, DurationFor(track), overwrite, token, progress, temp => {
                var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-abort_on", "empty_output_stream", "-y",
                    "-stats_period", "0.25", "-nostats", "-progress", "pipe:1", "-protocol_whitelist", "file", "-format_whitelist",
                    Demuxers, "-i", Source, "-map", "0:" + track.Index.ToString(Invariant), "-vn", "-sn", "-dn", "-map_metadata", "0" };
                format.AddOptions(args, track, quality);
                args.AddRange(new[] { "-f", format.Muxer, temp });
                return args;
            });
        }
    }
}
