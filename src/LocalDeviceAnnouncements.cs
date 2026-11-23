using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace AtelierPC
{
    internal sealed class LocalDeviceAnnouncement
    {
        internal readonly IPAddress Address;
        internal readonly string Name, Model, Manufacturer;
        internal readonly int Port;

        internal LocalDeviceAnnouncement(IPAddress address, string name, string model, string manufacturer, int port)
        {
            Address = address;
            Name = name;
            Model = model;
            Manufacturer = manufacturer;
            Port = port;
        }
    }

    internal static class LocalDeviceAnnouncements
    {
        const int DescriptionLimit = 128 * 1024;
        const string DeviceNamespace = "urn:schemas-upnp-org:device-1-0";
        static readonly IPEndPoint group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        static readonly byte[] search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\nMX: 1\r\nST: upnp:rootdevice\r\n\r\n");

        internal static void Read(LocalNetwork network, Action<LocalDeviceAnnouncement> publish, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var locations = new HashSet<Uri>();
            try
            {
                using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    udp.Bind(new IPEndPoint(network.Source, 0));
                    udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, network.Source.GetAddressBytes());
                    udp.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    udp.SendTimeout = 250;
                    udp.SendTo(search, group);
                    var clock = Stopwatch.StartNew();
                    var buffer = new byte[8192];
                    int packets = 0;
                    while (clock.ElapsedMilliseconds < 1400 && locations.Count < 64 && packets < 256)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!udp.Poll(75000, SelectMode.SelectRead)) continue;
                        packets++;
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int count;
                        try { count = udp.ReceiveFrom(buffer, ref remote); }
                        catch (SocketException ex) { if (ex.SocketErrorCode == SocketError.MessageSize) continue; throw; }
                        var address = ((IPEndPoint)remote).Address;
                        var location = DescriptionLocation(network, address, Encoding.ASCII.GetString(buffer, 0, count));
                        if (location == null || !locations.Add(location)) continue;
                        token.ThrowIfCancellationRequested();
                        publish(new LocalDeviceAnnouncement(address, null, null, null, 0));
                    }
                }
            }
            catch (SocketException) { } // Multicast can be unavailable on an otherwise working interface.
            token.ThrowIfCancellationRequested();
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(4500);
                var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = budget.Token };
                try
                {
                    Parallel.ForEach(locations, options, location => {
                        var result = Fetch(network.Source, location, budget.Token);
                        budget.Token.ThrowIfCancellationRequested();
                        if (result != null) publish(result);
                    });
                }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
            }
        }

        static bool IsSuccess(string status)
        {
            var parts = status.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts[1] == "200" && (parts[0] == "HTTP/1.0" || parts[0] == "HTTP/1.1");
        }

        internal static Uri DescriptionLocation(LocalNetwork network, IPAddress sender, string reply)
        {
            if (!network.Contains(sender)) return null;
            var lines = reply.Split('\n');
            if (!IsSuccess(lines[0].TrimEnd('\r'))) return null;
            string target = null, address = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string header = line.Substring(0, colon).Trim();
                if (header.Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                {
                    if (address != null) return null;
                    address = line.Substring(colon + 1).Trim();
                }
                if (header.Equals("ST", StringComparison.OrdinalIgnoreCase))
                {
                    if (target != null) return null;
                    target = line.Substring(colon + 1).Trim();
                }
            }
            Uri location;
            if (!string.Equals(target, "upnp:rootdevice", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(address, UriKind.Absolute, out location) || !location.IsWellFormedOriginalString() ||
                location.Scheme != Uri.UriSchemeHttp || location.HostNameType != UriHostNameType.IPv4 ||
                location.Host != sender.ToString() || location.UserInfo.Length != 0 || location.Fragment.Length != 0 ||
                location.Port <= 0) return null;
            return location;
        }

        static LocalDeviceAnnouncement Fetch(IPAddress source, Uri location, CancellationToken token)
        {
            var address = IPAddress.Parse(location.Host);
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (timeout.Token.Register(socket.Dispose))
                {
                    timeout.CancelAfter(1400);
                    socket.Bind(new IPEndPoint(source, 0));
                    socket.Connect(new IPEndPoint(address, location.Port));
                    // A direct, source-bound connection keeps simultaneous scans on their own adapters.
                    using (var stream = new NetworkStream(socket))
                    {
                        byte[] request = Encoding.ASCII.GetBytes("GET " + location.PathAndQuery + " HTTP/1.0\r\nHost: " +
                            location.Authority + "\r\nConnection: close\r\n\r\n");
                        stream.Write(request, 0, request.Length);
                        return ReadResponse(address, location.Port, stream);
                    }
                }
            }
            catch (SocketException) { return null; }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
        }

        // HTTP/1.0 requests can use Content-Length or connection close for framing.
        internal static LocalDeviceAnnouncement ReadResponse(IPAddress address, int port, Stream source)
        {
            using (var input = new BufferedStream(source))
            {
                int headerBudget = 16384;
                string status = ReadLine(input, ref headerBudget);
                if (status == null || !IsSuccess(status)) return null;
                var confirmed = new LocalDeviceAnnouncement(address, null, null, null, port);
                try { return ReadDescription(input, headerBudget, confirmed); }
                catch (IOException) { return confirmed; }
                catch (ObjectDisposedException) { return confirmed; }
            }
        }

        static LocalDeviceAnnouncement ReadDescription(Stream input, int headerBudget, LocalDeviceAnnouncement confirmed)
        {
            int length = -1;
            string line;
            while ((line = ReadLine(input, ref headerBudget)) != "")
            {
                if (line == null) return confirmed;
                int colon = line.IndexOf(':');
                if (colon <= 0) return confirmed;
                string name = line.Substring(0, colon);
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return confirmed;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    (length >= 0 || !int.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out length) || length > DescriptionLimit)) return confirmed;
            }
            using (var body = new MemoryStream())
            {
                var buffer = new byte[4096];
                int cap = length < 0 ? DescriptionLimit + 1 : length;
                while (body.Length < cap)
                {
                    int count = input.Read(buffer, 0, Math.Min(buffer.Length, cap - (int)body.Length));
                    if (count == 0) break;
                    body.Write(buffer, 0, count);
                }
                if (body.Length > DescriptionLimit || (length >= 0 && body.Length != length)) return confirmed;
                body.Position = 0;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = DescriptionLimit };
                var document = new XmlDocument { XmlResolver = null };
                try { using (var reader = XmlReader.Create(body, settings)) document.Load(reader); }
                catch (XmlException) { return confirmed; }
                var root = document["root", DeviceNamespace];
                var device = root == null ? null : root["device", DeviceNamespace];
                if (device == null) return confirmed;
                string model = Metadata(device, "modelName") ?? Metadata(device, "modelNumber");
                return new LocalDeviceAnnouncement(confirmed.Address, Metadata(device, "friendlyName"),
                    model, Metadata(device, "manufacturer"), confirmed.Port);
            }
        }

        static string ReadLine(Stream input, ref int budget)
        {
            var line = new StringBuilder();
            while (budget-- > 0)
            {
                int value = input.ReadByte();
                if (value == -1) return null;
                if (value == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)value);
            }
            return null;
        }

        static string Metadata(XmlElement device, string name)
        {
            var field = device[name, DeviceNamespace];
            if (field == null) return null;
            foreach (XmlNode node in field.ChildNodes)
                if (node.NodeType == XmlNodeType.Element) return null;
            string text = string.Join(" ", field.InnerText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0) return null;
            return text.Length > 160 ? text.Substring(0, 160) : text;
        }
    }
}
