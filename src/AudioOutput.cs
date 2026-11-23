using System;
using System.Collections.Generic;
using System.Globalization;

namespace AtelierPC
{
    internal sealed class AudioOutput : MediaOutput
    {
        internal readonly string Muxer;
        readonly string label, encoder, qualityOption;
        readonly string[] levels;
        readonly int maxChannels, fixedRate;

        AudioOutput(string code, string label, string extension, string muxer, string encoder,
            int maxChannels = 0, int fixedRate = 0, string qualityOption = null,
            string smaller = null, string balanced = null, string higher = null)
            : base(code, extension)
        {
            this.label = label;
            Muxer = muxer;
            this.encoder = encoder;
            this.maxChannels = maxChannels;
            this.fixedRate = fixedRate;
            this.qualityOption = qualityOption;
            levels = new[] { smaller, balanced, higher };
        }

        static AudioOutput Bitrate(string code, string label, string extension, string muxer, string encoder,
            int channels, string smaller, string balanced, string higher, int rate = 0)
        {
            return new AudioOutput(code, label, extension, muxer, encoder, channels, rate, "-b:a", smaller, balanced, higher);
        }

        internal static readonly AudioOutput[] Formats = {
            Bitrate("MP3", "MP3", "mp3", "mp3", "libmp3lame", 2, "96k", "192k", "320k"),
            new AudioOutput("WAV", "WAV · PCM", "wav", "wav", "pcm"),
            new AudioOutput("FLAC", "FLAC", "flac", "flac", "flac", 8),
            Bitrate("M4A", "M4A · AAC", "m4a", "ipod", "aac", 8, "96k", "192k", "256k"),
            new AudioOutput("ALAC", "M4A · Apple Lossless", "m4a", "ipod", "alac", 8),
            new AudioOutput("OGG", "Ogg Vorbis", "ogg", "ogg", "libvorbis", 8, 0, "-q:a", "2", "5", "8"),
            Bitrate("Opus", "Ogg Opus", "opus", "opus", "libopus", 8, "64k", "128k", "192k"),
            Bitrate("AAC", "AAC · ADTS", "aac", "adts", "aac", 8, "96k", "192k", "256k"),
            Bitrate("WMA", "Windows Media Audio", "wma", "asf", "wmav2", 2, "64k", "128k", "192k"),
            new AudioOutput("AIFF", "AIFF · PCM", "aiff", "aiff", "pcm"),
            new AudioOutput("WavPack", "WavPack", "wv", "wv", "wavpack"),
            new AudioOutput("TTA", "True Audio", "tta", "tta", "tta"),
            new AudioOutput("MKA", "Matroska audio · FLAC", "mka", "matroska", "flac", 8),
            Bitrate("M4B", "Livre audio M4B · AAC", "m4b", "ipod", "aac", 8, "64k", "128k", "192k"),
            Bitrate("WebM", "WebM audio · Opus", "webm", "webm", "libopus", 8, "64k", "128k", "192k"),
            new AudioOutput("CAF", "Core Audio · PCM", "caf", "caf", "pcm"),
            new AudioOutput("AU", "Sun Audio · PCM", "au", "au", "pcm"),
            new AudioOutput("W64", "Sony Wave64 · PCM", "w64", "w64", "pcm"),
            Bitrate("MP2", "MPEG Audio Layer II", "mp2", "mp2", "mp2", 2, "96k", "192k", "256k"),
            Bitrate("AC3", "Dolby Digital", "ac3", "ac3", "ac3", 6, "192k", "384k", "640k"),
            Bitrate("EAC3", "Dolby Digital Plus", "eac3", "eac3", "eac3", 6, "192k", "384k", "640k"),
            new AudioOutput("Speex", "Ogg Speex", "spx", "spx", "libspeex", 2, 0, "-cbr_quality", "3", "7", "10"),
            Bitrate("AMR", "AMR · voix bande étroite", "amr", "amr", "libopencore_amrnb", 1, "5900", "7950", "12200", 8000),
            Bitrate("AMR-WB", "AMR-WB · voix large bande", "awb", "amr", "libvo_amrwbenc", 1, "8850", "15850", "23850", 16000),
            new AudioOutput("OGA", "Ogg FLAC", "oga", "oga", "flac", 8),
            new AudioOutput("ADX", "CRI ADX", "adx", "adx", "adpcm_adx", 2),
            new AudioOutput("VOC", "Creative Voice", "voc", "voc", "pcm_s16le", 2),
            new AudioOutput("IRCAM", "IRCAM · PCM", "sf", "ircam", "pcm"),
            new AudioOutput("GSM", "GSM · voix", "gsm", "gsm", "libgsm", 1, 8000),
            Bitrate("LOAS", "AAC · LOAS/LATM", "loas", "latm", "aac", 8, "96k", "192k", "256k"),
            new AudioOutput("SBC", "SBC", "sbc", "sbc", "sbc", 2),
            new AudioOutput("SoX", "SoX · PCM 32 bits", "sox", "sox", "pcm_s32le")
        };

        internal bool HasQuality { get { return qualityOption != null; } }

        internal int ChannelsFor(AudioTrack track)
        {
            if (maxChannels == 0 || track.Channels <= maxChannels) return track.Channels;
            return maxChannels;
        }

        // Ces encodeurs imposent une disposition précise pour chaque nombre de canaux.
        internal string LayoutFor(AudioTrack track)
        {
            int channels = ChannelsFor(track);
            if (channels < 3) return null;
            if (encoder == "alac") return AppleLayouts[channels - 3];
            if (encoder == "libopus") return OpusLayouts[channels - 3];
            return null;
        }

        static readonly string[] AppleLayouts = { "3.0", "4.0", "5.0", "5.1", "6.1(back)", "7.1(wide)" };
        static readonly string[] OpusLayouts = { "3.0", "quad", "5.0", "5.1", "6.1", "7.1" };

        internal void AddOptions(List<string> args, AudioTrack track, ConversionQuality quality)
        {
            string codec = encoder;
            if (codec == "pcm")
            {
                string endian = Code == "AIFF" || Code == "AU" ? "be" : "le";
                codec = "pcm_" + track.PcmPrecision + endian;
            }
            args.AddRange(new[] { "-c:a", codec });
            if (HasQuality)
            {
                int level;
                switch (quality)
                {
                    case ConversionQuality.Smaller: level = 0; break;
                    case ConversionQuality.Balanced: level = 1; break;
                    case ConversionQuality.Higher: level = 2; break;
                    default: throw new ArgumentOutOfRangeException("quality");
                }
                args.AddRange(new[] { qualityOption, levels[level] });
            }
            int channels = ChannelsFor(track);
            if (channels != track.Channels)
                args.AddRange(new[] { "-ac", channels.ToString(CultureInfo.InvariantCulture) });
            string layout = LayoutFor(track);
            if (layout != null) args.AddRange(new[] { "-channel_layout", layout });
            int rate = fixedRate;
            if (encoder == "wmav2" && (track.SampleRate < 32000 || track.SampleRate > 48000)) rate = 44100;
            // Les débits des profils MP2 équilibré et élevé demandent une fréquence MPEG-1.
            if (encoder == "mp2" && track.SampleRate < 32000 && quality != ConversionQuality.Smaller) rate = 32000;
            if (rate > 0) args.AddRange(new[] { "-ar", rate.ToString(CultureInfo.InvariantCulture) });
            if (Code == "WAV") args.AddRange(new[] { "-rf64", "auto" });
        }

        public override string ToString() { return label + "  (." + Extension + ")"; }
    }
}
