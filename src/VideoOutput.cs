using System;
using System.Collections.Generic;
using System.Globalization;

namespace AtelierPC
{
    internal sealed class VideoCodec
    {
        internal readonly string Code;
        readonly string label, encoder;
        readonly int balanced, smaller, higher;

        VideoCodec(string code, string label, string encoder, int balanced, int smaller, int higher)
        {
            Code = code;
            this.label = label;
            this.encoder = encoder;
            this.balanced = balanced;
            this.smaller = smaller;
            this.higher = higher;
        }

        internal static readonly VideoCodec H264 = new VideoCodec("h264", "H.264", "libx264", 23, 30, 17);
        internal static readonly VideoCodec Hevc = new VideoCodec("hevc", "H.265 (HEVC)", "libx265", 26, 33, 19);
        internal static readonly VideoCodec Av1 = new VideoCodec("av1", "AV1", "libaom-av1", 32, 42, 22);
        internal static readonly VideoCodec Vp9 = new VideoCodec("vp9", "VP9", "libvpx-vp9", 32, 42, 22);
        internal static readonly VideoCodec Mpeg4 = new VideoCodec("mpeg4", "MPEG-4 Part 2", "mpeg4", 6, 12, 2);
        internal static readonly VideoCodec Mjpeg = new VideoCodec("mjpeg", "Motion JPEG (MJPEG)", "mjpeg", 6, 12, 2);
        internal static readonly VideoCodec Wmv2 = new VideoCodec("wmv2", "Windows Media Video 8 (WMV2)", "wmv2", 6, 12, 2);
        internal static readonly VideoCodec Wmv1 = new VideoCodec("wmv1", "Windows Media Video 7 (WMV1)", "wmv1", 6, 12, 2);

        internal void AddEncodingOptions(List<string> args, ConversionQuality quality)
        {
            int level;
            switch (quality)
            {
                case ConversionQuality.Balanced: level = balanced; break;
                case ConversionQuality.Smaller: level = smaller; break;
                case ConversionQuality.Higher: level = higher; break;
                default: throw new ArgumentOutOfRangeException("quality");
            }
            bool crf = this == H264 || this == Hevc || this == Av1 || this == Vp9;
            args.AddRange(new[] { "-c:v", encoder, crf ? "-crf" : "-q:v", level.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", this == Mjpeg ? "yuvj420p" : "yuv420p" });
            if (this == H264 || this == Hevc) args.AddRange(new[] { "-preset", "fast" });
            if (this == Av1 || this == Vp9)
                args.AddRange(new[] { "-b:v", "0", "-cpu-used", this == Av1 ? "6" : "4", "-row-mt", "1" });
        }

        public override string ToString() { return label; }
    }

    internal sealed class VideoOutput : MediaOutput
    {
        internal readonly string Muxer, AudioEncoder;
        internal readonly VideoCodec[] Codecs;
        readonly string label;

        VideoOutput(string code, string name, string muxer, string audioEncoder, params VideoCodec[] codecs)
            : base(code, code.ToLowerInvariant())
        {
            Muxer = muxer;
            AudioEncoder = audioEncoder;
            Codecs = codecs;
            label = name + "  (." + Extension + ")";
        }

        internal static readonly VideoOutput[] Formats = {
            new VideoOutput("MP4", "MP4", "mp4", "aac", VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1),
            new VideoOutput("MKV", "Matroska", "matroska", "aac", VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1, VideoCodec.Vp9),
            new VideoOutput("WEBM", "WebM", "webm", "libopus", VideoCodec.Vp9, VideoCodec.Av1),
            new VideoOutput("MOV", "QuickTime", "mov", "aac", VideoCodec.H264, VideoCodec.Hevc),
            new VideoOutput("AVI", "AVI", "avi", "libmp3lame", VideoCodec.Mpeg4, VideoCodec.Mjpeg),
            new VideoOutput("WMV", "WMV", "asf", "wmav2", VideoCodec.Wmv2, VideoCodec.Wmv1)
        };

        public override string ToString() { return label; }
    }
}
