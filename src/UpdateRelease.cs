using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace AtelierPC
{
    internal sealed class UpdateRelease
    {
        const string Latest = "https://api.github.com/repos/kederiku/Toolbox/releases/latest";
        const string AssetBase = "https://github.com/kederiku/Toolbox/releases/download/";
        const int MetadataLimit = 512 * 1024;
        const long AssetLimit = 1024L * 1024 * 1024;
        readonly Uri assetUri;

        internal Version Version { get; private set; }
        internal long Size { get; private set; }
        internal string Sha256 { get; private set; }

        UpdateRelease(Version version, long size, string digest, string url)
        {
            Version = version;
            Size = size;
            Sha256 = digest;
            assetUri = new Uri(url);
        }

        internal static UpdateRelease Check(Version current, CancellationToken token)
        {
            UpdateRelease result = null;
            Fetch(new Uri(Latest), false, token, delegate(HttpWebResponse response, CancellationToken active)
            {
                if (response.ContentLength > MetadataLimit) throw InvalidRelease();
                using (var body = new MemoryStream())
                using (var input = response.GetResponseStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        active.ThrowIfCancellationRequested();
                        if (body.Length + read > MetadataLimit) throw InvalidRelease();
                        body.Write(buffer, 0, read);
                    }
                    result = Parse(new UTF8Encoding(false, true).GetString(body.ToArray()), current);
                }
            });
            return result;
        }

        internal static UpdateRelease Parse(string json, Version current)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (String.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MetadataLimit) throw InvalidRelease();
            var document = new XmlDocument { XmlResolver = null };
            var limits = new XmlDictionaryReaderQuotas { MaxDepth = 32, MaxStringContentLength = MetadataLimit };
            try
            {
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), limits))
                    document.Load(reader);
            }
            catch (XmlException ex) { throw new InvalidDataException("Réponse de GitHub illisible.", ex); }
            var root = document.DocumentElement;
            if (root == null || root.GetAttribute("type") != "object") throw InvalidRelease();
            if (Field(root, "draft", "boolean").InnerText == "true" ||
                Field(root, "prerelease", "boolean").InnerText == "true") return null;
            string tag = Field(root, "tag_name", "string").InnerText;
            Version version;
            if (tag.Length > 18 || !tag.StartsWith("v", StringComparison.Ordinal) ||
                !Version.TryParse(tag.Substring(1), out version) || version.Build < 0 || version.Revision >= 0 ||
                version.Major >= 65535 || version.Minor >= 65535 || version.Build >= 65535 ||
                "v" + version.ToString() != tag) return null;
            var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (version <= installed) return null;

            string url = AssetBase + tag + "/Toolbox.exe";
            UpdateRelease release = null;
            foreach (XmlElement asset in Field(root, "assets", "array").ChildNodes)
            {
                if (asset.GetAttribute("type") != "object") throw InvalidRelease();
                if (Field(asset, "name", "string").InnerText != "Toolbox.exe") continue;
                string digest = Field(asset, "digest", "string").InnerText;
                long size;
                if (release != null || Field(asset, "state", "string").InnerText != "uploaded" ||
                    Field(asset, "browser_download_url", "string").InnerText != url ||
                    !Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") ||
                    !Int64.TryParse(Field(asset, "size", "number").InnerText, NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                    size <= 0 || size > AssetLimit) throw InvalidRelease();
                release = new UpdateRelease(version, size, digest.Substring(7).ToLowerInvariant(), url);
            }
            if (release == null) throw InvalidRelease();
            return release;
        }

        static XmlElement Field(XmlElement owner, string key, string type)
        {
            var fields = owner.SelectNodes(key);
            if (fields.Count != 1) throw InvalidRelease();
            var value = (XmlElement)fields[0];
            if (value.GetAttribute("type") != type) throw InvalidRelease();
            return value;
        }

        static InvalidDataException InvalidRelease()
        {
            return new InvalidDataException("La publication GitHub ne contient pas les informations attendues pour Toolbox.exe.");
        }

        internal static bool IsAssetRedirect(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
                (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
        }

        static void Fetch(Uri uri, bool asset, CancellationToken token, Action<HttpWebResponse, CancellationToken> consume)
        {
            token.ThrowIfCancellationRequested();
            if ((int)ServicePointManager.SecurityProtocol != 0)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(asset ? TimeSpan.FromMinutes(45) : TimeSpan.FromSeconds(15));
                try
                {
                    for (int redirects = 0; ; redirects++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var request = WebRequest.CreateHttp(uri);
                        request.UserAgent = "Toolbox-Windows-Updater";
                        request.Accept = asset ? "application/octet-stream" : "application/vnd.github+json";
                        request.AllowAutoRedirect = false;
                        request.UseDefaultCredentials = false;
                        request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
                        request.Timeout = request.ReadWriteTimeout = asset ? 30000 : 15000;
                        using (deadline.Token.Register(request.Abort))
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status == 200)
                            {
                                consume(response, deadline.Token);
                                return;
                            }
                            Uri next;
                            if (!asset || redirects >= 3 ||
                                (status != 301 && status != 302 && status != 303 && status != 307 && status != 308) ||
                                !Uri.TryCreate(response.Headers[HttpResponseHeader.Location], UriKind.Absolute, out next) ||
                                !IsAssetRedirect(next)) throw InvalidRelease();
                            uri = next;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var web = ex as WebException;
                    using (var failedResponse = web == null ? null : web.Response as HttpWebResponse)
                    {
                        token.ThrowIfCancellationRequested();
                        if (deadline.IsCancellationRequested) throw new TimeoutException("GitHub n’a pas répondu dans le délai prévu.", ex);
                        if (!asset && failedResponse != null && failedResponse.StatusCode == HttpStatusCode.NotFound) return;
                        throw;
                    }
                }
            }
        }

        internal void Download(string stagedPath, CancellationToken token, Action<int> progress)
        {
            Fetch(assetUri, true, token, delegate(HttpWebResponse response, CancellationToken active)
            {
                if (response.ContentLength >= 0 && response.ContentLength != Size)
                    throw new InvalidDataException("La taille annoncée par GitHub a changé.");
                using (var input = response.GetResponseStream()) Stage(input, stagedPath, active, progress);
            });
        }

        internal void Stage(Stream input, string stagedPath, CancellationToken token, Action<int> progress)
        {
            token.ThrowIfCancellationRequested();
            string partial = stagedPath + "." + Guid.NewGuid().ToString("N") + ".part";
            bool ownsPartial = false;
            try
            {
                using (var sha = SHA256.Create())
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    ownsPartial = true;
                    var buffer = new byte[81920];
                    long received = 0;
                    int previous = -1;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        received += read;
                        if (received > Size) throw new InvalidDataException("Le fichier téléchargé dépasse la taille attendue.");
                        output.Write(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                        int percent = (int)Math.Min(99, received * 100 / Size);
                        if (percent != previous && progress != null) progress(percent);
                        previous = percent;
                    }
                    token.ThrowIfCancellationRequested();
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                    if (received != Size || !String.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("La taille ou l’empreinte SHA-256 du téléchargement ne correspond pas à la publication.");
                    output.Flush();
                }
                AssemblyName identity;
                try { identity = AssemblyName.GetAssemblyName(partial); }
                catch (BadImageFormatException ex) { throw new InvalidDataException("Le fichier reçu est illisible.", ex); }
                if (identity.Name != "Toolbox" || identity.Version != new Version(Version.Major, Version.Minor, Version.Build, 0))
                    throw new InvalidDataException("Le nom ou la version du programme reçu ne correspond pas à la publication.");
                token.ThrowIfCancellationRequested();
                File.Move(partial, stagedPath);
                ownsPartial = false;
                if (progress != null) progress(100);
            }
            finally
            {
                if (ownsPartial) File.Delete(partial);
            }
        }
    }
}
