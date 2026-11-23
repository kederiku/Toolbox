using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckSpeedDetails()
        {
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("{\"clientIp\":\"2001:db8::1\"}")))
            {
                var oldResult = new SpeedResult { Connection = SpeedConnection.Parse(source) };
                Check(oldResult.NetworkDetail().Contains("IPv6 publique : 2001:db8::1") &&
                    oldResult.NetworkDetail().Contains("IPv4 publique : indisponible"),
                    "Chaque famille d’adresse publique possède son propre libellé");
            }
            var times = new[] { 900, 20, 5, 40, 10, 20 };
            int index = 0;
            var result = new SpeedResult();
            var test = new InternetSpeedTest((phase, size, token) => TimeSpan.FromMilliseconds(times[index++]));
            Check(test.Measure(SpeedPhase.Latency, CancellationToken.None, 0, result) == 20 && result.Jitter == 22.5,
                "La gigue utilise les écarts successifs avant tri et exclut le préchauffage");
            test = new InternetSpeedTest((phase, size, token) => TimeSpan.FromSeconds(2));
            test.Measure(SpeedPhase.Upload, CancellationToken.None, 0, result);
            Check(result.SentBytes == 128000 && result.ReceivedBytes == 0, "Le volume d’envoi comprend la confirmation");
            Check(SpeedResult.Rate(80) == "80 Mbit/s · 10 Mo/s" && SpeedResult.ByteRate(0.079) == "< 0,01 Mo/s",
                "Conversion des bits en octets avant arrondi");
            Check(SpeedConnection.LinkName(NetworkInterfaceType.Wireless80211) == "Wi-Fi" &&
                SpeedConnection.LinkName(NetworkInterfaceType.GigabitEthernet) == "Ethernet" &&
                SpeedConnection.CommonLink(new[] { "Ethernet", "Wi-Fi" }) == null &&
                SpeedConnection.CommonLink(new[] { "Wi-Fi", "Wi-Fi" }) == "Wi-Fi",
                "Liaison Wi-Fi ou Ethernet du chemin réseau avec repli si les routes divergent");
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("{\"clientIp\":\"2001:db8::1\",\"asOrganization\":\"Réseau témoin\"}")))
            {
                var network = SpeedConnection.Parse(source);
                Check(network.PublicIPv6 == "2001:db8::1" && network.PublicIPv4 == null && network.Provider == "Réseau témoin",
                    "Identité Cloudflare IPv6 et FAI");
            }
            using (var source = new MemoryStream(Encoding.UTF8.GetBytes("{\"clientIp\":\"inconnue\"}")))
            {
                var network = SpeedConnection.Parse(source);
                Check(network.PublicIPv4 == null && network.PublicIPv6 == null && network.Provider == null,
                    "Champs indisponibles sans identification inventée");
            }
            Check(SpeedConnection.AddressFor("203.0.113.8\n", AddressFamily.InterNetwork) == "203.0.113.8" &&
                SpeedConnection.AddressFor("203.0.113.8", AddressFamily.InterNetworkV6) == null &&
                SpeedConnection.AddressFor("::ffff:203.0.113.8", AddressFamily.InterNetwork) == "203.0.113.8" &&
                SpeedConnection.AddressFor("403", AddressFamily.InterNetwork) == null,
                "Une réponse ne peut renseigner que sa famille IP et doit contenir une adresse complète");
            string path = Path.Combine(Path.GetTempPath(), "AtelierPC-debit-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                long epoch = DateTime.UtcNow.Ticks;
                string legacy = "[{\"CompletedUtcTicks\":1,\"Latency\":20,\"Jitter\":0,\"Download\":80,\"Upload\":40," +
                    "\"Seconds\":2,\"ReceivedBytes\":100,\"SentBytes\":50,\"Connection\":{\"Link\":\"Wi-Fi\"," +
                    "\"asOrganization\":\"FAI témoin\",\"clientIp\":\"{IP}\"}}]";
                foreach (string address in new[] { "2001:db8::1", "203.0.113.8" })
                {
                    File.WriteAllText(path, legacy.Replace("{IP}", address));
                    var old = new SpeedHistory(path).Load().Single().Connection;
                    bool v4 = address.Contains(".");
                    Check(old.PublicIPv4 == (v4 ? address : null) && old.PublicIPv6 == (v4 ? null : address) &&
                        old.WifiName == null && old.Provider == "FAI témoin", "Relecture de l’ancien historique " + address);
                }
                Parallel.For(0, 12, i => new SpeedHistory(path).Add(new SpeedResult { CompletedUtcTicks = epoch + i,
                    Download = i, Upload = i / 2.0, Connection = new SpeedConnection { Link = i % 2 == 0 ? "Wi-Fi" : "Ethernet" } }));
                var stored = new SpeedHistory(path).Load();
                Check(stored.Count == 10 && stored.Select(s => s.Download).SequenceEqual(Enumerable.Range(2, 10).Reverse().Select(i => (double)i)) &&
                    stored.First().Connection.Link == "Ethernet" && stored.Last().Connection.Link == "Wi-Fi",
                    "Relecture des dix derniers tests datés avec leurs propres liaisons après des écritures concurrentes");
                File.WriteAllText(path, "[{\"CompletedUtcTicks\":");
                new SpeedHistory(path).Add(new SpeedResult { CompletedUtcTicks = epoch + 12, Download = 80 });
                Check(new SpeedHistory(path).Load().Single().Download == 80 &&
                    File.ReadAllText(path + ".illisible") == "[{\"CompletedUtcTicks\":",
                    "Un historique tronqué est conservé à part et le résultat suivant reste enregistrable");
            }
            finally { File.Delete(path); File.Delete(path + ".lock"); File.Delete(path + ".illisible"); }
        }
    }
}
