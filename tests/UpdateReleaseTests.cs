using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckUpdateRelease()
        {
            const string tag = "v1.10.0";
            const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string assetUrl = "https://github.com/kederiku/Toolbox/releases/download/v1.10.0/Toolbox.exe";
            const string asset = "{\"name\":\"Toolbox.exe\",\"size\":123,\"digest\":\"sha256:" + digest +
                "\",\"state\":\"uploaded\",\"browser_download_url\":\"" + assetUrl + "\"}";
            const string metadata = "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"assets\":[" + asset + "]}";
            var current = new Version(1, 9, 9, 0);
            var update = UpdateRelease.Parse(metadata, current);
            Check(update.Version.Equals(new Version(1, 10, 0)) && update.Size == 123 && update.Sha256 == digest,
                "Mise à jour stable plus récente selon les composantes numériques");
            Check(UpdateRelease.Parse(metadata, new Version(1, 10, 0, 0)) == null &&
                UpdateRelease.Parse(metadata, new Version(2, 0, 0, 0)) == null, "Une version identique ou antérieure est ignorée");
            foreach (string invalid in new[] { "1.10.0", "v01.10.0", "v1.10", "v1.10.0.0", "v1.10.0-beta.1", "v1.10.0+build", "v65536.0.0" })
                Check(UpdateRelease.Parse(metadata.Replace(tag, invalid), current) == null, "Tag non stable ou non canonique ignoré");
            Check(UpdateRelease.Parse(metadata.Replace("\"draft\":false", "\"draft\":true"), current) == null &&
                UpdateRelease.Parse(metadata.Replace("\"prerelease\":false", "\"prerelease\":true"), current) == null,
                "Brouillons et préversions ignorés");
            foreach (string invalid in new[] {
                "{invalid", metadata.Replace("\"draft\":false", "\"draft\":false,\"draft\":false"), metadata.Replace("\"draft\":false,", ""), metadata.Replace("\"prerelease\":false", "\"prerelease\":\"false\""),
                metadata.Replace(asset, asset + "," + asset), metadata.Replace("Toolbox.exe", "toolbox.exe"),
                metadata.Replace("uploaded", "new"), metadata.Replace("123", "0"), metadata.Replace("123", "4294967296"),
                metadata.Replace("123", "\"123\""), metadata.Replace("sha256:", "sha1:"), metadata.Replace(digest, "01"),
                metadata.Replace(assetUrl, assetUrl.Replace("kederiku", "another")), metadata.Replace(assetUrl, assetUrl + "?test"),
                metadata.Replace(assetUrl, assetUrl.Replace("v1.10.0", "latest")), metadata.Replace(assetUrl, assetUrl.Replace("https:", "http:")) })
                ExpectReleaseRejection(delegate { UpdateRelease.Parse(invalid, current); });
            Check(UpdateRelease.IsAssetRedirect(new Uri("https://release-assets.githubusercontent.com/id?sig=abc")),
                "La redirection HTTPS vers le service de fichiers GitHub est acceptée");
            foreach (string address in new[] { "http://release-assets.githubusercontent.com/asset", "https://github.com/owner/project",
                "https://release-assets.githubusercontent.com.evil.example/x", "https://evil.example@release-assets.githubusercontent.com/x",
                "https://release-assets.githubusercontent.com:8443/x", "file:///C:/Toolbox.exe" })
                Check(!UpdateRelease.IsAssetRedirect(new Uri(address)), "Redirection étrangère ou ambiguë refusée");
            bool cancelled = false;
            try { UpdateRelease.Check(current, new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Une vérification annulée ne lance aucune requête");

            string dir = Path.Combine(Path.GetTempPath(), "Toolbox-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                const string name = "Toolbox";
                var identity = new AssemblyName(name) { Version = new Version(1, 10, 0, 0) };
                var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(identity, AssemblyBuilderAccess.Save, dir);
                var module = assembly.DefineDynamicModule(name, "Toolbox.exe");
                var type = module.DefineType("Program", TypeAttributes.Public);
                var main = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
                main.GetILGenerator().Emit(OpCodes.Ret);
                type.CreateType();
                assembly.SetEntryPoint(main);
                assembly.Save("Toolbox.exe");

                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "Toolbox.exe"));
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                string validMetadata = metadata.Replace("123", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace(digest, hash);
                var verified = UpdateRelease.Parse(validMetadata, current);
                string stage = Path.Combine(dir, "staged.exe");
                int last = -1;
                verified.Stage(new MemoryStream(bytes), stage, CancellationToken.None, delegate(int value) { last = value; });
                Check(last == 100 && File.ReadAllBytes(stage).Length == bytes.Length, "Fichier vérifié et publié dans le dossier temporaire");
                bool existingProtected = false;
                try { verified.Stage(new MemoryStream(bytes), stage, CancellationToken.None, null); }
                catch (IOException) { existingProtected = true; }
                Check(existingProtected && File.Exists(stage), "Un fichier existant est préservé");
                File.Delete(stage);
                foreach (string invalid in new[] { validMetadata.Replace(hash, digest),
                    validMetadata.Replace(tag, "v1.10.1"), validMetadata.Replace("\"size\":" + bytes.Length, "\"size\":" + (bytes.Length - 1)),
                    validMetadata.Replace("\"size\":" + bytes.Length, "\"size\":" + (bytes.Length + 1)) })
                {
                    var corrupt = UpdateRelease.Parse(invalid, current);
                    ExpectReleaseRejection(delegate { corrupt.Stage(new MemoryStream(bytes), stage, CancellationToken.None, null); });
                    Check(Directory.GetFiles(dir).Length == 1, "Échec de vérification sans fichier partiel");
                }
                byte[] unrelated = File.ReadAllBytes(typeof(Uri).Assembly.Location);
                string unrelatedHash;
                using (var sha = SHA256.Create()) unrelatedHash = BitConverter.ToString(sha.ComputeHash(unrelated)).Replace("-", "").ToLowerInvariant();
                var unrelatedVersion = typeof(Uri).Assembly.GetName().Version;
                string unrelatedTag = "v" + unrelatedVersion.ToString(3);
                var other = UpdateRelease.Parse(metadata.Replace(tag, unrelatedTag).Replace("123", unrelated.Length.ToString()).Replace(digest, unrelatedHash), new Version(0, 0, 0));
                ExpectReleaseRejection(delegate { other.Stage(new MemoryStream(unrelated), stage, CancellationToken.None, null); });
                var invalidImage = UpdateRelease.Parse(metadata.Replace("123", "3").Replace(digest,
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"), current);
                ExpectReleaseRejection(delegate { invalidImage.Stage(new MemoryStream(new byte[] { 97, 98, 99 }), stage, CancellationToken.None, null); });
                using (var stop = new CancellationTokenSource())
                {
                    cancelled = false;
                    try { verified.Stage(new MemoryStream(bytes), stage, stop.Token, delegate { stop.Cancel(); }); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && Directory.GetFiles(dir).Length == 1, "Annulation sans publication ni fichier partiel");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        static void ExpectReleaseRejection(Action action)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "La publication non vérifiable est refusée");
        }
    }
}
