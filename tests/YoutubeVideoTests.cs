using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace AtelierPC
{
    internal static partial class Tests
    {
        const string YoutubeFixtureId = "Abc_12-XyZ0";

        static void CheckYoutubeVideo()
        {
            foreach (var input in new[] { "  " + YoutubeFixtureId + "  ",
                "https://www.youtube.com/watch?list=abc&v=" + YoutubeFixtureId + "&t=24",
                "https://youtu.be/" + YoutubeFixtureId + "?si=partage",
                "https://m.youtube.com/shorts/" + YoutubeFixtureId + "#t=4",
                "https://www.youtube.com/live/" + YoutubeFixtureId + "?si=partage",
                "youtube.com/embed/" + YoutubeFixtureId })
                Check(YoutubeVideo.ParseId(input) == YoutubeFixtureId, "Reconnaissance du lien ou de l’identifiant YouTube " + input);

            foreach (var input in new[] { "", "abc", YoutubeFixtureId + "X", "0123456789é", "--version",
                "https://youtu.be.example.org/" + YoutubeFixtureId, "https://youtu.be/" + YoutubeFixtureId + "/other",
                "https://user@youtube.com/watch?v=" + YoutubeFixtureId, "https://www.youtube.com/playlist?list=" + YoutubeFixtureId,
                "https://youtube.com/watch?other=v=" + YoutubeFixtureId, "ftp://youtube.com/watch?v=" + YoutubeFixtureId })
                CheckYoutubeInvalid(delegate { YoutubeVideo.ParseId(input); }, "Entrée YouTube refusée " + input);

            var low = YoutubeStream("18", 640, 360, "avc1.42001E", "mp4a.40.2");
            low["language_preference"] = 10;
            var high = YoutubeStream("136", 1280, 720, "avc1.4d401f", "none");
            var vp9 = YoutubeStream("247", 1280, 720, "vp9", "none");
            var original = YoutubeStream("140-2", 0, 0, "none", "mp4a.40.2");
            original["language_preference"] = 10;
            original["abr"] = 128;
            var dub = YoutubeStream("140-1", 0, 0, "none", "mp4a.40.2");
            dub["language_preference"] = -1;
            dub["abr"] = 256;
            var descriptive = YoutubeStream("251", 0, 0, "none", "opus");
            descriptive["language_preference"] = -10;
            descriptive["abr"] = 320;

            var drm = YoutubeStream("701", 3840, 2160, "av01", "none");
            drm["has_drm"] = "maybe";
            var missing = YoutubeStream("702", 1920, 1080, "avc1", "mp4a");
            missing.Remove("url");
            var board = YoutubeStream("sb0", 160, 90, "images", "none");
            board["protocol"] = "mhtml";
            var upcoming = YoutubeStream("703", 3840, 2160, "avc1", "none");
            upcoming["available_at"] = 7258118400L;
            string json = YoutubeInfo("Été : les essais ?", low, high, vp9, original, dub, descriptive, drm, missing,
                board, upcoming, YoutubeStream("136/best", 1920, 1080, "avc1", "mp4a"),
                YoutubeStream("best", 1920, 1080, "avc1", "mp4a"));
            var video = YoutubeVideo.FromJson(YoutubeFixtureId, json);
            Check(video.Id == YoutubeFixtureId && video.DownloadInfo == json && video.Title == "Été : les essais ?",
                "Le résultat conserve le titre et les informations des flux analysés");
            Check(video.Resolutions.Select(r => r.Selector).SequenceEqual(new[] { "136+140-2", "18" }),
                "Deux dimensions réelles, en MP4 avec le son d’origine, par résolution décroissante");
            Check(video.Resolutions.All(r => r.HasAudio) && video.Resolutions[0].Width == 1280 && video.Resolutions[0].Height == 720,
                "La liste conserve le son des formats assemblés et des formats progressifs");
            Check(video.FileName.Contains("Été") && video.FileName.EndsWith(".mp4") &&
                video.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "Nom proposé utilisable dans Enregistrer sous");
            Check(video.AudioSelector == "140-2" && video.AudioFileName == Path.ChangeExtension(video.FileName, ".mp3"),
                "Le MP3 utilise la piste audio seule d’origine et le nom proposé est sûr sous Windows");

            var vertical = YoutubeStream("399", 720, 1280, "av01", "none");
            var silent = YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Court métrage", vertical));
            var resolution = silent.Resolutions.Single();
            Check(!resolution.HasAudio && resolution.Selector == "399" && resolution.Width == 720 && resolution.Height == 1280 &&
                resolution.ToString().Contains("720p") && resolution.ToString().Contains("1280"),
                "Un Short muet reste en portrait avec sa véritable résolution");
            Check(silent.AudioSelector == null, "Un Short véritablement muet ne propose aucun flux pour le MP3");
            var withDonor = YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Sans piste audio séparée", low, vertical));
            Check(withDonor.Resolutions.Length == 2 && withDonor.Resolutions.All(r => r.HasAudio) &&
                withDonor.Resolutions[0].Selector == "399+18", "Le son d’une vidéo progressive accompagne aussi la résolution supérieure");
            Check(withDonor.AudioSelector == "18", "Le MP3 peut extraire le son du seul flux progressif disponible");
            var originalMuxed = YoutubeStream("22", 1280, 720, "avc1", "mp4a");
            originalMuxed["language_preference"] = 10;
            var dubbedLow = new Dictionary<string, object>(low);
            dubbedLow["language_preference"] = -1;
            var muxed = YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Pistes", dubbedLow, originalMuxed, original, vertical));
            Check(muxed.Resolutions.Single(r => r.Width == 720).Selector == "399+140-2" &&
                muxed.Resolutions.Single(r => r.Width == 640).Selector == "18+140-2" &&
                muxed.Resolutions.Single(r => r.Width == 1280).Selector == "22",
                "Le son d’origine est réutilisé s’il est intégré ou pris dans la piste audio seule de même langue");
            var manifest = YoutubeStream("hls-2170", 1920, 1080, "avc1", "mp4a");
            manifest["protocol"] = "m3u8_native";
            var unavailable = YoutubeStream("999", 3840, 2160, "av01", "none");
            unavailable["protocol"] = "unknown";
            Check(YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Manifeste", manifest, unavailable))
                .Resolutions.Single().Selector == "hls-2170", "Le format de manifeste sélectionné possède un protocole téléchargeable");
            var lockedAudio = YoutubeStream("140", 0, 0, "none", "mp4a");
            lockedAudio["has_drm"] = true;
            var unavailableAudio = YoutubeStream("141", 0, 0, "none", "mp4a");
            unavailableAudio.Remove("url");
            var audioOnly = YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Concert.mp3", lockedAudio, unavailableAudio, dub, original));
            Check(audioOnly.Resolutions.Length == 0 && audioOnly.AudioSelector == "140-2" && audioOnly.AudioFileName == "Concert.mp3",
                "Une source exclusivement sonore propose sa piste originale et un seul suffixe MP3");
            CheckYoutubeInvalid(delegate { YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Pistes indisponibles", lockedAudio, unavailableAudio)); },
                "Des pistes uniquement protégées ou indisponibles ne permettent aucun MP3");
            CheckYoutubeInvalid(delegate { YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo("Son protégé", vertical, lockedAudio)); },
                "Un son accessible uniquement sous DRM ne donne pas de résultat silencieux");
            var protectedHls = YoutubeStream("hls-720", 1280, 720, "avc1", "mp4a");
            protectedHls["has_drm"] = "maybe";
            protectedHls["protocol"] = "m3u8_native";
            var clear360 = new Dictionary<string, object>(low);
            clear360["has_drm"] = false;
            string mixedDrm = YoutubeInfo("Autre résolution disponible", protectedHls, clear360)
                .Insert(1, "\"format_id\":\"hls-720\",\"has_drm\":\"maybe\",");
            Check(YoutubeVideo.FromJson(YoutubeFixtureId, mixedDrm).Resolutions.Single().Selector == "18",
                "La protection incertaine du flux HLS sélectionné laisse disponible le 360p sans DRM");

            foreach (string title in new[] { "NUL", "con .export", "LPT1.", "   ", new string('x', 270) })
            {
                var filename = YoutubeVideo.FromJson(YoutubeFixtureId, YoutubeInfo(title, low)).FileName;
                string name = Path.GetFileNameWithoutExtension(filename);
                Check(filename.EndsWith(".mp4") && filename.Length <= 255 && name.Trim(' ', '.').Length > 0 &&
                    !new[] { "NUL", "CON", "LPT1" }.Contains(name.Split('.')[0].TrimEnd().ToUpperInvariant()),
                    "Nom Windows sûr même avec un titre vide, réservé ou long");
            }
            foreach (string extra in new[] { "\"live_status\":\"is_live\"", "\"is_live\":true", "\"live_status\":\"is_upcoming\"",
                "\"availability\":\"private\"", "\"has_drm\":true" })
            {
                string info = YoutubeInfo("Indisponible", low).Insert(1, extra + ",");
                CheckYoutubeInvalid(delegate { YoutubeVideo.FromJson(YoutubeFixtureId, info); }, "Vidéo indisponible " + extra);
            }
            foreach (string info in new[] { "{}", "null", "invalid", YoutubeInfo("Sans flux"), YoutubeInfo("Images", board),
                YoutubeInfo("Sans URL", missing), YoutubeInfo("Protégé", drm), YoutubeInfo("Autre vidéo", low).Replace(YoutubeFixtureId, "0123456789a") })
                CheckYoutubeInvalid(delegate { YoutubeVideo.FromJson(YoutubeFixtureId, info); }, "Réponse sans média utilisable rejetée");
        }

        static void CheckYoutubeInvalid(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }

        static string YoutubeInfo(string title, params Dictionary<string, object>[] formats)
        {
            return new JavaScriptSerializer().Serialize(new { id = YoutubeFixtureId, title = title, formats = formats });
        }

        static Dictionary<string, object> YoutubeStream(string id, int width, int height, string videoCodec, string audioCodec)
        {
            return new Dictionary<string, object> {
                { "url", "https://example.org/media" }, { "format_id", id }, { "width", width }, { "height", height },
                { "vcodec", videoCodec }, { "acodec", audioCodec }, { "ext", videoCodec == "vp9" || audioCodec == "opus" ? "webm" : "mp4" }
            };
        }
    }
}
