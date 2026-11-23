using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    [DataContract]
    internal sealed class SpeedConnection
    {
        [DataMember(Name = "clientIp", EmitDefaultValue = false)] string oldAddress;
        [DataMember(EmitDefaultValue = false)] internal string PublicIPv4;
        [DataMember(EmitDefaultValue = false)] internal string PublicIPv6;
        [DataMember(Name = "asOrganization")] internal string Provider;
        [DataMember] internal string Link;
        [DataMember(EmitDefaultValue = false)] internal string WifiName;

        [OnDeserialized]
        void RestorePreviousAddress(StreamingContext context)
        {
            PublicIPv4 = PublicIPv4 ?? AddressFor(oldAddress, AddressFamily.InterNetwork);
            PublicIPv6 = PublicIPv6 ?? AddressFor(oldAddress, AddressFamily.InterNetworkV6);
            oldAddress = null;
        }

        internal static string AddressFor(string value, AddressFamily family)
        {
            IPAddress address;
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (!IPAddress.TryParse(value, out address)) return null;
            if (address.AddressFamily == AddressFamily.InterNetwork && address.ToString() != value) return null;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return address.AddressFamily == family ? address.ToString() : null;
        }

        internal static SpeedConnection Read(CancellationToken token)
        {
            var request = InternetSpeedTest.CreateRequest("meta?nonce=" + Guid.NewGuid().ToString("N"));
            request.Referer = "https://speed.cloudflare.com/";
            request.Headers["Origin"] = "https://speed.cloudflare.com";
            request.Accept = "application/json";
            request.UserAgent = "Toolbox";
            request.Timeout = request.ReadWriteTimeout = 3000;
            var info = new SpeedConnection();
            bool hasResponse = false;
            using (token.Register(request.Abort))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var input = response.GetResponseStream())
                    {
                        hasResponse = true;
                        if (response.StatusCode == HttpStatusCode.OK) info = Parse(input);
                    }
                }
                catch (WebException ex) { if (ex.Response != null) ex.Response.Close(); }
                catch (IOException) { }
                catch (SerializationException) { }
            }
            if (token.IsCancellationRequested) return info;
            // Identify missing families before timing the transfers. Keep each successful lookup.
            var v4 = LookupMissingAddress(info.PublicIPv4, AddressFamily.InterNetwork, token);
            var v6 = LookupMissingAddress(info.PublicIPv6, AddressFamily.InterNetworkV6, token);
            info.ReadRoute(request, hasResponse, token);
            info.PublicIPv4 = v4.GetAwaiter().GetResult();
            info.PublicIPv6 = v6.GetAwaiter().GetResult();
            return info;
        }

        static Task<string> LookupMissingAddress(string known, AddressFamily family, CancellationToken token)
        {
            if (known != null || token.IsCancellationRequested) return Task.FromResult(known);
            return Task.Run(() => {
                if (token.IsCancellationRequested) return null;
                string host = family == AddressFamily.InterNetwork ? "ipv4.icanhazip.com" : "ipv6.icanhazip.com";
                var uri = new Uri("https://" + host + "/?nonce=" + Guid.NewGuid().ToString("N"));
                var lookup = InternetSpeedTest.CreateRequest(uri);
                lookup.Timeout = lookup.ReadWriteTimeout = 2500;
                try
                {
                    using (token.Register(lookup.Abort))
                    using (var response = (HttpWebResponse)lookup.GetResponse())
                    using (var input = response.GetResponseStream())
                    {
                        if (response.StatusCode != HttpStatusCode.OK) return null;
                        var bytes = new byte[64];
                        int used = 0, read;
                        while (used < bytes.Length && (read = input.Read(bytes, used, bytes.Length - used)) > 0)
                            used += read;
                        return used < bytes.Length ? AddressFor(Encoding.ASCII.GetString(bytes, 0, used), family) : null;
                    }
                }
                catch (WebException ex) { if (ex.Response != null) ex.Response.Close(); }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }
                return null;
            });
        }

        [DataContract]
        sealed class ServerMetadata
        {
            [DataMember(Name = "clientIp")] public string Address { get; set; }
            [DataMember(Name = "asOrganization")] public string Organization { get; set; }
        }

        internal static SpeedConnection Parse(Stream input)
        {
            var buffer = new byte[16385];
            int length = 0, read;
            while (length < buffer.Length && (read = input.Read(buffer, length, buffer.Length - length)) > 0)
                length += read;
            if (length == buffer.Length) return new SpeedConnection();
            using (var json = new MemoryStream(buffer, 0, length))
            {
                var meta = (ServerMetadata)new DataContractJsonSerializer(typeof(ServerMetadata)).ReadObject(json);
                if (meta == null) return new SpeedConnection();
                return new SpeedConnection {
                    PublicIPv4 = AddressFor(meta.Address, AddressFamily.InterNetwork),
                    PublicIPv6 = AddressFor(meta.Address, AddressFamily.InterNetworkV6),
                    Provider = string.IsNullOrWhiteSpace(meta.Organization) || meta.Organization.Length > 256 ||
                        meta.Organization.Any(char.IsControl) ? null : meta.Organization.Trim()
                };
            }
        }

        void ReadRoute(HttpWebRequest request, bool hasResponse, CancellationToken token)
        {
            try
            {
                // ServicePoint identifies the next hop, including an HTTP proxy when one is used.
                Uri peer = request.ServicePoint.Address;
                var lookup = Dns.GetHostAddressesAsync(peer.DnsSafeHost);
                if (!lookup.Wait(1500, token)) return;
                var localAddresses = new List<IPAddress>();
                var peers = lookup.Result;
                if (hasResponse && request.ServicePoint.CurrentConnections > 0)
                {
                    try
                    {
                        // Use the established TCP path before considering other possible IPv4/IPv6 routes.
                        localAddresses.AddRange(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                            .Where(c => c.State == TcpState.Established && c.RemoteEndPoint.Port == peer.Port &&
                                peers.Contains(c.RemoteEndPoint.Address)).Select(c => c.LocalEndPoint.Address));
                    }
                    catch (NetworkInformationException) { }
                }
                if (localAddresses.Count == 0)
                {
                    foreach (var remote in peers)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            // Ask Windows for its source address without sending a UDP packet.
                            using (var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                            {
                                socket.Connect(new IPEndPoint(remote, peer.Port));
                                localAddresses.Add(((IPEndPoint)socket.LocalEndPoint).Address);
                            }
                        }
                        catch (SocketException) { }
                    }
                }
                var routes = new List<NetworkInterface>();
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    if (adapter.GetIPProperties().UnicastAddresses.Any(a => localAddresses.Contains(a.Address)))
                        routes.Add(adapter);
                }
                Link = CommonLink(routes.Select(a => LinkName(a.NetworkInterfaceType)));
                Guid adapterId;
                if (Link == "Wi-Fi" && routes.Count == 1 && Guid.TryParse(routes[0].Id, out adapterId))
                    WifiName = WifiNetwork.ConnectedName(adapterId);
            }
            catch (NetworkInformationException) { }
            catch (AggregateException) { }
            catch (OperationCanceledException) { }
            catch (NotSupportedException) { }
            catch (System.Security.SecurityException) { }
            catch (WebException) { }
        }

        internal static string CommonLink(IEnumerable<string> routes)
        {
            var names = routes.Distinct().ToArray();
            return names.Length == 1 ? names[0] : null;
        }

        internal static string LinkName(NetworkInterfaceType type)
        {
            switch (type)
            {
                case NetworkInterfaceType.Wireless80211: return "Wi-Fi";
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.Ethernet3Megabit:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.GigabitEthernet: return "Ethernet";
                case NetworkInterfaceType.Ppp:
                case NetworkInterfaceType.Tunnel: return "Tunnel / PPP";
                case NetworkInterfaceType.Wwanpp:
                case NetworkInterfaceType.Wwanpp2: return "Réseau mobile";
                case NetworkInterfaceType.Unknown:
                case NetworkInterfaceType.Loopback: return null;
                default: return "Autre";
            }
        }
    }
}
