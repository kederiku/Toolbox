using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Threading;

namespace AtelierPC
{
    internal enum SpeedPhase { Latency, Download, Upload }

    internal sealed class InternetSpeedTest
    {
        // Including one possible repeat of the first sample: at most 49,128,000 bytes per direction.
        static readonly int[] TransferSizes = { 64000, 1000000, 8000000, 40000000 };
        readonly Func<SpeedPhase, int, CancellationToken, TimeSpan> exchange;
        readonly Func<CancellationToken, SpeedConnection> connection;

        internal InternetSpeedTest(Func<SpeedPhase, int, CancellationToken, TimeSpan> exchange = null,
            Func<CancellationToken, SpeedConnection> connection = null)
        {
            this.exchange = exchange ?? Exchange;
            this.connection = connection ?? SpeedConnection.Read;
        }

        internal SpeedConnection ReadConnection(CancellationToken token)
        {
            return connection(token);
        }

        internal double Measure(SpeedPhase phase, CancellationToken token, double latencyMilliseconds = 0,
            SpeedResult result = null)
        {
            token.ThrowIfCancellationRequested();
            if (phase == SpeedPhase.Latency)
            {
                exchange(phase, 0, token); // Open the HTTPS connection before timing its round trips.
                var samples = new double[5];
                for (int i = 0; i < samples.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    samples[i] = exchange(phase, 0, token).TotalMilliseconds;
                }
                if (result != null)
                {
                    double differences = 0;
                    for (int i = 1; i < samples.Length; i++)
                        differences += Math.Abs(samples[i] - samples[i - 1]);
                    result.Jitter = differences / (samples.Length - 1);
                }
                Array.Sort(samples);
                return samples[samples.Length / 2];
            }

            double rate = 0;
            long bytes = 0;
            var targetDuration = TimeSpan.FromMilliseconds(1000 + latencyMilliseconds);
            foreach (int size in TransferSizes)
            {
                token.ThrowIfCancellationRequested();
                var elapsed = exchange(phase, size, token);
                bytes += size;
                if (size == TransferSizes[0] && elapsed >= targetDuration)
                {
                    // Confirm a slow first sample before ending the test on a single small transfer.
                    token.ThrowIfCancellationRequested();
                    var confirmation = exchange(phase, size, token);
                    bytes += size;
                    if (confirmation < elapsed) elapsed = confirmation;
                }
                if (elapsed <= TimeSpan.Zero) throw new IOException("Durée du transfert indisponible.");
                rate = size * 8.0 / 1000000 / elapsed.TotalSeconds;
                if (elapsed >= targetDuration) break;
            }
            if (result != null)
            {
                if (phase == SpeedPhase.Download) result.ReceivedBytes = bytes;
                else result.SentBytes = bytes;
            }
            return rate;
        }

        static TimeSpan Exchange(SpeedPhase phase, int size, CancellationToken token)
        {
            bool upload = phase == SpeedPhase.Upload;
            string path = upload ? "__up?" : "__down?bytes=" + size.ToString(CultureInfo.InvariantCulture) + "&";
            var request = CreateRequest(path + "nonce=" + Guid.NewGuid().ToString("N"));
            request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            var buffer = new byte[65536];
            if (upload)
            {
                request.Method = "POST";
                request.ContentType = "application/octet-stream";
                request.ContentLength = size;
                request.AllowWriteStreamBuffering = false;
                request.ServicePoint.Expect100Continue = false;
                new Random().NextBytes(buffer);
            }

            using (token.Register(request.Abort))
            {
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                try
                {
                    if (upload)
                    {
                        using (var output = request.GetRequestStream())
                        {
                            for (int sent = 0; sent < size; )
                            {
                                token.ThrowIfCancellationRequested();
                                int length = Math.Min(size - sent, buffer.Length);
                                output.Write(buffer, 0, length);
                                sent += length;
                            }
                        }
                    }
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode != HttpStatusCode.OK ||
                            response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Réponse inattendue du serveur de mesure.");
                        if (!upload)
                        {
                            if (response.ContentLength >= 0 && response.ContentLength != size)
                                throw new IOException("Taille de réponse inattendue.");
                            using (var input = response.GetResponseStream())
                                ReadDownload(input, size, buffer, token);
                        }
                        clock.Stop();
                    }
                }
                catch (WebException ex)
                {
                    if (ex.Response != null) ex.Response.Close();
                    token.ThrowIfCancellationRequested();
                    throw;
                }
                token.ThrowIfCancellationRequested();
                return clock.Elapsed;
            }
        }

        internal static HttpWebRequest CreateRequest(string path)
        {
            return CreateRequest(new Uri("https://speed.cloudflare.com/" + path));
        }

        internal static HttpWebRequest CreateRequest(Uri uri)
        {
            if ((int)ServicePointManager.SecurityProtocol != 0)
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = WebRequest.CreateHttp(uri);
            request.AllowAutoRedirect = false;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            return request;
        }

        internal static void ReadDownload(Stream input, int size, byte[] buffer, CancellationToken token)
        {
            for (int remaining = size; remaining > 0; )
            {
                token.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, Math.Min(remaining, buffer.Length));
                if (read == 0) throw new EndOfStreamException("Réception interrompue.");
                remaining -= read;
            }
            if (input.ReadByte() != -1) throw new IOException("Volume reçu inattendu.");
        }
    }
}
