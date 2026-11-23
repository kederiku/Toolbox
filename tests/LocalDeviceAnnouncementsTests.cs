using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void CheckLocalDeviceAnnouncements()
        {
            var network = new LocalNetwork("Ethernet", 7, IPAddress.Parse("192.168.5.2"), IPAddress.Parse("255.255.255.0"), new IPAddress[0]);
            var sender = IPAddress.Parse("192.168.5.42");
            const string prefix = "HTTP/1.1 200 OK\r\nsT: upnp:rootdevice\r\nLocation: ";
            const string url = "http://192.168.5.42:49152/root.xml";
            Func<IPAddress, string, Uri> location = (ip, reply) =>
                LocalDeviceAnnouncements.DescriptionLocation(network, ip, reply + "\r\n\r\n");
            var accepted = location(sender, prefix + url);
            Check(accepted != null && accepted.Port == 49152 && accepted.AbsolutePath == "/root.xml",
                "UPnP lit le port et le chemin annoncés par l’appareil local");
            foreach (string rejected in new[] { "http://192.168.5.41/root", "http://device.local/root",
                "https://192.168.5.42/root", "http://alice@192.168.5.42/root", "http://192.168.5.42:0/root" })
                Check(location(sender, prefix + rejected) == null, "UPnP écarte la destination " + rejected);
            Check(location(IPAddress.Parse("192.168.6.2"), prefix + "http://192.168.6.2/root") == null &&
                location(sender, prefix + url + "\r\nLocation: " + url) == null &&
                location(sender, prefix.Replace("200 OK", "301 Moved") + url) == null &&
                location(sender, prefix.Replace("upnp:rootdevice", "ssdp:all") + url) == null,
                "UPnP vérifie le sous-réseau, la réponse et le type de recherche");

            const string xml = "<root xmlns='urn:schemas-upnp-org:device-1-0'><device>" +
                "<friendlyName>Salon &amp; TV</friendlyName><modelName> Modèle 42 </modelName><manufacturer>Atelier</manufacturer>" +
                "<deviceList><device><friendlyName>Lecteur intégré</friendlyName></device></deviceList></device></root>";
            Func<string, string, LocalDeviceAnnouncement> response = (head, body) => {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(head + "\r\n\r\n" + body)))
                    return LocalDeviceAnnouncements.ReadResponse(sender, 49152, stream);
            };
            const string ok = "HTTP/1.1 200 OK";
            var details = response(ok + "\r\nContent-Length: " + Encoding.UTF8.GetByteCount(xml), xml);
            Check(details != null && details.Address.Equals(sender) && details.Port == 49152 &&
                details.Name == "Salon & TV" && details.Model == "Modèle 42" && details.Manufacturer == "Atelier",
                "La description enrichit l’appareil racine et confirme le port qui a répondu");
            details = response("HTTP/1.0 200 OK", xml.Replace("modelName", "modelNumber"));
            Check(details != null && details.Model == "Modèle 42", "Un appareil qui publie son numéro de modèle reste identifiable");
            Check(response("HTTP/1.1 302 Found\r\nLocation: http://192.168.6.2/root", xml) == null,
                "Une redirection ne confirme aucune description");
            details = response(ok, "<!DOCTYPE root [<!ENTITY a 'autre'>]>" + xml.Replace("Salon &amp; TV", "&a;"));
            Check(details != null && details.Port == 49152 && details.Name == null, "Une DTD ne fournit aucun nom");
            details = response(ok + "\r\nContent-Length: 999999", xml);
            Check(details != null && details.Name == null, "Une description trop volumineuse conserve seulement le port");
            using (var interrupted = new InterruptedDescription(Encoding.ASCII.GetBytes(ok + "\r\nContent-Length: 200\r\n\r\n<root>")))
            {
                details = LocalDeviceAnnouncements.ReadResponse(sender, 49152, interrupted);
                Check(details != null && details.Port == 49152 && details.Name == null,
                    "Une coupure après la réponse HTTP conserve le port confirmé");
            }
            details = response(ok, "<root><device><friendlyName>autre</friendlyName></device></root>");
            Check(details != null && details.Name == null, "Un autre format XML ne fournit pas de nom UPnP");
            bool cancelled = false;
            try { LocalDeviceAnnouncements.Read(network, reply => { throw new Exception("Annonce tardive"); }, new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Une découverte UPnP déjà annulée s’arrête avant la requête");
        }

        sealed class InterruptedDescription : MemoryStream
        {
            internal InterruptedDescription(byte[] bytes) : base(bytes) { }
            public override int Read(byte[] bytes, int offset, int count)
            {
                if (Position == Length) throw new IOException("Lecture interrompue");
                return base.Read(bytes, offset, count);
            }
        }
    }
}
