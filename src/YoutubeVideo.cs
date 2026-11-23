using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AtelierPC
{
    internal sealed class YoutubeResolution
    {
        internal readonly int Width, Height;
        internal readonly string Selector;
        internal readonly bool HasAudio;

        internal YoutubeResolution(int width, int height, string selector, bool hasAudio)
        {
            Width = width;
            Height = height;
            Selector = selector;
            HasAudio = hasAudio;
        }

        public override string ToString()
        {
            return string.Format("{0}p ({1} × {2})", Math.Min(Width, Height), Width, Height);
        }
    }

    internal sealed class YoutubeVideo
    {
        static readonly Regex VideoId = new Regex(@"\A[A-Za-z0-9_-]{11}\z");
        static readonly Regex FormatId = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9_-]*\z");
        // Ces mots demandent une sélection dynamique à yt-dlp, même si une piste porte le même identifiant.
        static readonly Regex FormatKeyword = new Regex(@"\A(?:(?:best|worst|b|w)(?:video|audio|v|a)?|all|mergeall|" +
            @"avi|flv|mkv|mov|mp4|webm|3gp|aiff|alac|flac|m4a|mka|mp3|ogg|opus|wav|mhtml)\z");
        static readonly Regex DeviceName = new Regex(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³]) *(?:\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal readonly string Id, Title, FileName, DownloadInfo, AudioSelector, AudioFileName;
        internal readonly YoutubeResolution[] Resolutions;

        YoutubeVideo(string id, string title, string json, YoutubeResolution[] resolutions, string audioSelector)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? id : title;
            FileName = SuggestedName(Title, id, ".mp4");
            AudioFileName = SuggestedName(Title, id, ".mp3");
            AudioSelector = audioSelector;
            DownloadInfo = json;
            Resolutions = resolutions;
        }

        internal static string ParseId(string input)
        {
            string text = (input ?? "").Trim();
            if (VideoId.IsMatch(text)) return text;
            if (!text.Contains("://")) text = "https://" + text;
            Uri uri;
            if (Uri.TryCreate(text, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http") &&
                uri.UserInfo.Length == 0 && uri.IsDefaultPort)
            {
                string path = uri.AbsolutePath.TrimEnd('/');
                string candidate = "";
                switch (uri.Host.ToLowerInvariant())
                {
                    case "youtu.be":
                    case "www.youtu.be":
                        candidate = path.TrimStart('/');
                        break;
                    case "youtube.com":
                    case "www.youtube.com":
                    case "m.youtube.com":
                    case "music.youtube.com":
                        if (path == "/watch")
                        {
                            var values = uri.Query.TrimStart('?').Split('&').Where(p => p.StartsWith("v=", StringComparison.Ordinal)).ToArray();
                            if (values.Length == 1) candidate = Uri.UnescapeDataString(values[0].Substring(2));
                        }
                        else if (path.StartsWith("/shorts/", StringComparison.Ordinal) || path.StartsWith("/embed/", StringComparison.Ordinal) ||
                            path.StartsWith("/live/", StringComparison.Ordinal))
                            candidate = path.Substring(path.IndexOf('/', 1) + 1);
                        break;
                }
                if (VideoId.IsMatch(candidate)) return candidate;
            }
            throw new InvalidDataException("Saisissez un identifiant YouTube de 11 caractères ou le lien d’une vidéo YouTube.");
        }

        internal static YoutubeVideo FromJson(string id, string json)
        {
            id = ParseId(id);
            Dictionary<string, object> info;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                info = serializer.DeserializeObject(json ?? "") as Dictionary<string, object>;
            }
            catch (ArgumentException ex) { throw new InvalidDataException("La réponse de YouTube est illisible.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("La réponse de YouTube est illisible.", ex); }
            if (info == null || Text(info, "id") != id)
                throw new InvalidDataException("Les informations reçues ne correspondent pas à cette vidéo YouTube.");
            // yt-dlp reporte aussi ici le statut « maybe » du seul flux HLS présélectionné.
            if (Flag(info, "has_drm") && !Text(info, "has_drm").Equals("maybe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cette vidéo est protégée par DRM.");
            switch (Text(info, "availability"))
            {
                case "private": throw new InvalidDataException("Cette vidéo YouTube est privée.");
                case "needs_auth": throw new InvalidDataException("YouTube exige une connexion pour accéder à cette vidéo.");
                case "subscriber_only":
                case "premium_only": throw new InvalidDataException("Cette vidéo YouTube est réservée aux abonnés.");
            }
            string live = Text(info, "live_status");
            if (Flag(info, "is_live") || live == "is_live" || live == "is_upcoming" || live == "post_live")
                throw new InvalidDataException("La rediffusion de ce direct YouTube n’est pas encore disponible.");

            object formats;
            if (!info.TryGetValue("formats", out formats) || !(formats is object[]))
                throw new InvalidDataException("YouTube n’a fourni aucun format audio ou vidéo pour ce lien.");
            var all = ((object[])formats).OfType<Dictionary<string, object>>().Select(f => new Format(f)).ToArray();
            var usable = all.Where(f => f.Available).ToArray();
            // Le moteur assemble l’image et le son explicitement. Une piste progressive peut ainsi fournir
            // uniquement son audio si aucune piste audio seule de la langue d’origine n’est disponible.
            var bestAudio = usable.Where(f => f.HasAudio && (f.AudioOnly || f.HasVideo))
                .OrderByDescending(f => f.LanguagePreference).ThenByDescending(f => f.AudioOnly)
                .ThenByDescending(f => f.Aac).ThenByDescending(f => f.AudioBitrate)
                .ThenBy(f => f.VideoBitrate).ThenBy(f => (long)f.Width * f.Height).FirstOrDefault();
            bool hasSound = all.Any(f => f.HasAudio);
            var choices = usable.Where(f => f.Width > 0 && f.Height > 0 && f.HasVideo)
                .Select(v => new { Video = v, Audio = v.HasAudio && v.LanguagePreference >= bestAudio.LanguagePreference ? v : bestAudio })
                .Where(pair => pair.Audio != null || !hasSound)
                .GroupBy(pair => new { pair.Video.Width, pair.Video.Height })
                .Select(group => group.OrderByDescending(pair => pair.Audio == null ? 0 : pair.Audio.LanguagePreference)
                    .ThenByDescending(pair => pair.Video.VideoCompatibility)
                    .ThenByDescending(pair => pair.Audio != null && pair.Audio.Aac)
                    .ThenByDescending(pair => pair.Video.Fps)
                    .ThenByDescending(pair => pair.Video.VideoBitrate)
                    .ThenByDescending(pair => pair.Audio == null ? 0 : pair.Audio.AudioBitrate)
                    .ThenByDescending(pair => pair.Video == pair.Audio).First())
                .Select(pair => new YoutubeResolution(pair.Video.Width, pair.Video.Height,
                    pair.Video.Id + (pair.Audio != null && pair.Audio != pair.Video ? "+" + pair.Audio.Id : ""), pair.Audio != null))
                .OrderByDescending(r => Math.Min(r.Width, r.Height)).ThenByDescending(r => r.Width).ThenByDescending(r => r.Height)
                .ToArray();
            if (choices.Length == 0 && bestAudio == null)
            {
                if (all.Any(f => f.Protected)) throw new InvalidDataException("Les flux nécessaires à cette vidéo sont protégés par DRM.");
                throw new InvalidDataException("YouTube ne propose aucun flux audio ou vidéo téléchargeable pour ce lien.");
            }
            return new YoutubeVideo(id, Text(info, "title"), json, choices, bestAudio == null ? null : bestAudio.Id);
        }

        static string SuggestedName(string title, string id, string extension)
        {
            var invalid = Path.GetInvalidFileNameChars();
            string stem = new string(title.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            if (stem.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                stem = Path.GetFileNameWithoutExtension(stem);
            if (stem.Length > 160)
                stem = stem.Substring(0, char.IsHighSurrogate(stem[159]) ? 159 : 160);
            stem = stem.TrimEnd(' ', '.');
            if (stem.Length == 0) stem = id;
            if (DeviceName.IsMatch(stem)) stem = "_" + stem;
            return stem + extension;
        }

        static string Text(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        }

        static double Number(Dictionary<string, object> data, string key, double fallback)
        {
            double number;
            return double.TryParse(Text(data, key), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                !double.IsNaN(number) && !double.IsInfinity(number) ? number : fallback;
        }

        static bool Flag(Dictionary<string, object> data, string key)
        {
            string value = Text(data, key);
            return value != "" && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        sealed class Format
        {
            internal readonly string Id;
            internal readonly int Width, Height, VideoCompatibility;
            internal readonly bool Available, Protected, HasAudio, HasVideo, AudioOnly, Aac;
            internal readonly double LanguagePreference, AudioBitrate, VideoBitrate, Fps;

            internal Format(Dictionary<string, object> data)
            {
                Id = Text(data, "format_id");
                string video = Text(data, "vcodec").ToLowerInvariant(), audio = Text(data, "acodec").ToLowerInvariant();
                string note = Text(data, "format_note").ToLowerInvariant();
                HasVideo = video != "" && video != "none" && video != "images" && video != "unknown";
                HasAudio = audio != "" && audio != "none" && audio != "unknown";
                AudioOnly = video == "none" && HasAudio;
                Protected = Flag(data, "has_drm");
                string protocol = Text(data, "protocol");
                Uri url;
                bool supported = protocol == "" || protocol == "http" || protocol == "https" ||
                    protocol == "http_dash_segments" || protocol == "m3u8" || protocol == "m3u8_native";
                Available = FormatId.IsMatch(Id) && !FormatKeyword.IsMatch(Id) && !Protected && supported && Text(data, "ext") != "mhtml" &&
                    !note.Contains("storyboard") && !note.Contains("missing pot") && !note.Contains("damaged") &&
                    Uri.TryCreate(Text(data, "url"), UriKind.Absolute, out url) && (url.Scheme == "https" || url.Scheme == "http") &&
                    Number(data, "available_at", 0) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Width = (int)Number(data, "width", 0);
                Height = (int)Number(data, "height", 0);
                LanguagePreference = Number(data, "language_preference", note.Contains("descriptive") ? -10 :
                    note.Contains("original") ? 10 : note.Contains("default") ? 5 : note.Contains("dubbed") ? -1 : 0);
                VideoCompatibility = video.StartsWith("avc", StringComparison.Ordinal) || video == "h264" ? 2 :
                    Text(data, "ext") == "mp4" ? 1 : 0;
                Aac = audio.StartsWith("mp4a", StringComparison.Ordinal) || audio == "aac";
                AudioBitrate = Number(data, "abr", AudioOnly ? Number(data, "tbr", 0) : 0);
                VideoBitrate = Number(data, "vbr", Number(data, "tbr", 0));
                Fps = Number(data, "fps", 0);
            }
        }
    }
}
