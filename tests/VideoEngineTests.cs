using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckVideoEngine()
        {
            string workDirectory;
            FileStream held;
            using (var engine = new VideoEngine(CancellationToken.None))
            {
                string encoders = engine.Run("ffmpeg", new[] { "-hide_banner", "-encoders" }, CancellationToken.None);
                foreach (string codec in new[] { "libx264", "libx265", "libaom-av1", "libvpx-vp9", "mpeg4", "mjpeg", "wmv1", "wmv2", "aac", "libmp3lame", "libopus", "wmav2" })
                    Check(encoders.Contains(" " + codec + " "), "Encodeur embarqué disponible : " + codec);
                Check(engine.Run("ffprobe", new[] { "-version" }, CancellationToken.None).StartsWith("ffprobe version"),
                    "Lecture des métadonnées avec le seul exécutable Toolbox");
                bool failed = false;
                try { engine.Run("ffmpeg", new[] { "-invalid_AtelierPC_option" }, CancellationToken.None); }
                catch (InvalidDataException) { failed = true; }
                Check(failed, "Une erreur du moteur ne peut pas être déclarée comme un succès");
                // Un lecteur externe peut encore garder le binaire ouvert quand Windows termine le processus.
                string marker = Guid.NewGuid().ToString("N") + ".bmp";
                engine.Run("ffmpeg", new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=16x16:d=0.1", "-frames:v", "1", "-update", "1", marker }, CancellationToken.None);
                workDirectory = Directory.EnumerateDirectories(Path.GetTempPath(), "AtelierPC-video-*")
                    .Single(dir => File.Exists(Path.Combine(dir, marker)));
                held = File.Open(Path.Combine(workDirectory, "ffmpeg.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            held.Dispose();
            Check(SpinWait.SpinUntil(() => !Directory.Exists(workDirectory), TimeSpan.FromSeconds(8)),
                "Un verrou temporaire sur le moteur ne laisse pas de copie dans le dossier temporaire");
        }
    }
}
