using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    internal static class NetworkTroubleshooter
    {
        internal static bool Usable(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return false;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] != 0 && b[0] < 224 && !(b[0] == 169 && b[1] == 254);
        }

        static bool TryRead<T>(Func<T> read, out T value)
        {
            try { value = read(); return true; }
            catch (Win32Exception) { }
            catch (WebException) { }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            value = default(T);
            return false;
        }

        static T ReadOr<T>(Func<T> read, T unknown)
        {
            T value;
            return TryRead(read, out value) ? value : unknown;
        }

        static NetworkLink[] ReadLinks(out bool complete)
        {
            var links = new List<NetworkLink>();
            NetworkInterface[] adapters;
            complete = TryRead(NetworkInterface.GetAllNetworkInterfaces, out adapters);
            if (!complete) return links.ToArray();
            foreach (var adapter in adapters)
            {
                bool active;
                if (!TryRead(() => adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    adapter.OperationalStatus == OperationalStatus.Up, out active)) { complete = false; continue; }
                if (!active) continue;
                var link = new NetworkLink();
                links.Add(link);
                TryRead(() => adapter.Id, out link.Id);
                TryRead(() => adapter.Name, out link.Name);
                IPInterfaceProperties ip;
                if (!TryRead(adapter.GetIPProperties, out ip) || ip == null)
                {
                    link.AddressUnknown = link.GatewayUnknown = true;
                    continue;
                }
                IPAddress[] addresses;
                link.AddressUnknown = !TryRead(() => ip.UnicastAddresses.Where(a =>
                    (a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred ||
                     a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Deprecated) && Usable(a.Address))
                    .Select(a => a.Address).ToArray(), out addresses);
                if (!link.AddressUnknown)
                {
                    link.HasIPv4 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork);
                    link.HasIPv6 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                }
                IPAddress gateway;
                link.GatewayUnknown = !TryRead(() => ip.GatewayAddresses.Select(g => g.Address)
                    .Where(g => !g.Equals(IPAddress.Any) && !g.Equals(IPAddress.IPv6Any))
                    .OrderByDescending(g => g.AddressFamily == AddressFamily.InterNetwork ? link.HasIPv4 : link.HasIPv6)
                    .FirstOrDefault(), out gateway);
                link.GatewayAddress = gateway;
                link.HasGateway = gateway != null;
                TryRead(() => (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet) &&
                    adapter.Supports(NetworkInterfaceComponent.IPv4) && ip.GetIPv4Properties().IsDhcpEnabled, out link.Dhcp);
            }
            return links.ToArray();
        }

        public static NetworkDiagnosis Diagnose()
        {
            bool complete;
            var links = ReadLinks(out complete);
            return Diagnose(links, complete, ResolveName, Probe, PingGateway);
        }

        internal static NetworkDiagnosis Diagnose(NetworkLink[] links, bool inventoryComplete, Func<string, bool?> resolve,
            Func<string, WebProbe> probeServer, Func<IPAddress, GatewayProbe> ping)
        {
            if (inventoryComplete && links.Length == 0)
                return NetworkDiagnosis.Assess(links, WebProbe.Omitted, DnsProbe.Omitted, GatewayProbe.Omitted, true);
            var gateway = links.Where(l => l.GatewayAddress != null).OrderByDescending(l => l.HasIPv4 || l.HasIPv6)
                .Select(l => l.GatewayAddress).FirstOrDefault();
            var gatewayResult = gateway == null ? GatewayProbe.Omitted : ReadOr(() => ping(gateway), GatewayProbe.Unknown);
            bool hasIPv6 = links.Any(l => l.HasIPv6);
            bool onlyIPv6 = hasIPv6 && !links.Any(l => l.HasIPv4);
            string host = onlyIPv6 ? "ipv6.msftconnecttest.com" : "www.msftconnecttest.com";
            bool? primary = ReadOr(() => resolve(host), (bool?)null);
            DnsProbe dns = DnsProbe.Resolved;
            if (primary != true)
            {
                bool? secondary = ReadOr(() => resolve("www.microsoft.com"), (bool?)null);
                dns = secondary == true ? DnsProbe.SecondNameResolved :
                    primary == false && secondary == false ? DnsProbe.TwoNamesFailed : DnsProbe.Unknown;
            }
            var probe = ReadOr(() => probeServer(host), WebProbe.Unknown);
            if (!onlyIPv6 && hasIPv6 && (probe == WebProbe.Unavailable || probe == WebProbe.DnsFailure || probe == WebProbe.Unknown))
            {
                var ipv6 = ReadOr(() => probeServer("ipv6.msftconnecttest.com"), WebProbe.Unknown);
                if (ipv6 == WebProbe.Healthy || ipv6 == WebProbe.Intercepted || ipv6 == WebProbe.Unavailable) probe = ipv6;
            }
            return NetworkDiagnosis.Assess(links, probe, dns, gatewayResult, inventoryComplete);
        }

        static GatewayProbe PingGateway(IPAddress address)
        {
            using (var ping = new Ping())
            {
                var status = ping.Send(address, 1000).Status;
                if (status == IPStatus.Success) return GatewayProbe.Responded;
                return status == IPStatus.TimedOut ? GatewayProbe.NoReply : GatewayProbe.Unknown;
            }
        }

        static bool? ResolveName(string host)
        {
            var task = ResolveNameAsync(host);
            return Task.WhenAny(task, Task.Delay(3000)).GetAwaiter().GetResult() == task ? task.GetAwaiter().GetResult() : null;
        }

        static async Task<bool?> ResolveNameAsync(string host)
        {
            try { return (await Dns.GetHostAddressesAsync(host).ConfigureAwait(false)).Length != 0; }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData) return false;
                return null;
            }
            catch (System.Security.SecurityException) { return null; }
        }

        internal static WebProbe InterpretResponse(int status, string content)
        {
            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308 || status == 511)
                return WebProbe.Intercepted;
            if (status != 200) return WebProbe.Unavailable;
            return content != null && content.Trim() == "Microsoft Connect Test" ? WebProbe.Healthy : WebProbe.Intercepted;
        }

        static WebProbe Probe(string host)
        {
            var request = WebRequest.CreateHttp("http://" + host + "/connecttest.txt");
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 5000;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
            using (timeout.Token.Register(request.Abort))
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status != 200) return InterpretResponse(status, null);
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        var buffer = new char[32];
                        int count = reader.ReadBlock(buffer, 0, buffer.Length);
                        return InterpretResponse(status, count < buffer.Length ? new string(buffer, 0, count) : null);
                    }
                }
            }
            catch (WebException ex)
            {
                using (var response = ex.Response as HttpWebResponse)
                {
                    if (response != null) return InterpretResponse((int)response.StatusCode, null);
                }
                if (ex.Status == WebExceptionStatus.NameResolutionFailure) return WebProbe.DnsFailure;
                return WebProbe.Unavailable;
            }
            catch (IOException) { return WebProbe.Unavailable; }
        }

        internal static string Arguments(NetworkDiagnosis issue, NetworkLink[] current)
        {
            if (issue.Fix == NetworkFix.FlushDns) return "/flushdns";
            if (issue.Fix != NetworkFix.RenewAddress) return null;
            var candidate = NetworkDiagnosis.Assess(current, WebProbe.Unavailable).Target;
            if (candidate == null || !string.Equals(candidate.Id, issue.Target.Id, StringComparison.OrdinalIgnoreCase) ||
                candidate.Name != issue.Target.Name) return null;
            return "/renew \"" + candidate.Name + "\"";
        }

        public static string Repair(NetworkDiagnosis issue, IntPtr owner)
        {
            bool complete;
            var links = ReadLinks(out complete);
            string arguments = issue.Fix == NetworkFix.RenewAddress && !complete ? null : Arguments(issue, links);
            if (arguments == null) return ConfigChanged;
            var start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), Arguments = arguments,
                UseShellExecute = true, Verb = "runas", ErrorDialogParentHandle = owner,
                WorkingDirectory = Environment.SystemDirectory, WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var child = Process.Start(start))
                {
                    child.WaitForExit();
                    if (child.ExitCode != 0) return FailedPrefix + child.ExitCode + ").";
                    return issue.Fix == NetworkFix.FlushDns ? Flushed : Renewed;
                }
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? Cancelled : LaunchFailed;
            }
        }

        const string ConfigChanged = "La configuration a changé. La demande de réparation a été écartée.";
        const string Flushed = "Windows a vidé le cache DNS.";
        const string Renewed = "Windows a traité la demande d’adresse pour la carte sélectionnée.";
        const string Cancelled = "Réparation annulée lors de la demande d’autorisation Windows.";
        const string LaunchFailed = "Windows n’a pas pu lancer la correction.";
        const string FailedPrefix = "Windows n’a pas terminé la correction (code ";
        internal const string Unconfirmed = "La correction n’a pas pu être confirmée.";

        internal static string SharedAction(string action)
        {
            if (action == ConfigChanged || action == Flushed || action == Renewed || action == Cancelled ||
                action == LaunchFailed) return action;
            int code;
            if (action != null && action.StartsWith(FailedPrefix, StringComparison.Ordinal) &&
                action.EndsWith(").", StringComparison.Ordinal) &&
                int.TryParse(action.Substring(FailedPrefix.Length, action.Length - FailedPrefix.Length - 2), out code))
                return FailedPrefix + code + ").";
            return Unconfirmed;
        }
    }
}
