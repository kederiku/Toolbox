using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AtelierPC
{
    internal sealed class LocalDevice : INotifyPropertyChanged
    {
        internal readonly IPAddress Ip;
        internal string HostName { get; private set; }
        internal string RouterName { get; private set; }
        internal PhysicalAddress HardwareAddress { get; private set; }
        internal bool IsThisPc, IsGateway;
        internal LocalDeviceAnnouncement Announcement { get; private set; }
        internal readonly SortedSet<int> DescriptionPorts = new SortedSet<int>();
        public string Name
        {
            get
            {
                if (IsThisPc) return Environment.MachineName + " (ce PC)";
                if (Announcement != null && !string.IsNullOrWhiteSpace(Announcement.Name)) return Announcement.Name;
                return HostName ?? RouterName ?? (IsGateway ? "Passerelle du réseau" : "Nom inconnu");
            }
        }
        public string MacAddress
        {
            get { return HardwareAddress == null ? "Indisponible" : BitConverter.ToString(HardwareAddress.GetAddressBytes()).Replace('-', ':'); }
        }
        public string Address { get; private set; }
        public uint AddressNumber { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        internal bool UsesRouterName
        {
            get { return RouterName != null && !IsThisPc && HostName == null && (Announcement == null || string.IsNullOrWhiteSpace(Announcement.Name)); }
        }

        internal void SetRouterName(string name)
        {
            if (RouterName == name) return;
            RouterName = name;
            var changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs("Name"));
        }

        internal LocalDevice(IPAddress address, string name, PhysicalAddress mac = null, LocalDeviceAnnouncement announcement = null)
        {
            Ip = address;
            Address = address.ToString();
            AddressNumber = LocalNetwork.Number(address);
            HostName = string.IsNullOrWhiteSpace(name) || name == Address ? null : name;
            HardwareAddress = mac == null || mac.Equals(PhysicalAddress.None) ? null : mac;
            Announcement = announcement;
            if (announcement != null && announcement.Port > 0) DescriptionPorts.Add(announcement.Port);
        }

        internal void Merge(LocalDevice update)
        {
            HostName = update.HostName ?? HostName;
            HardwareAddress = update.HardwareAddress ?? HardwareAddress;
            IsThisPc |= update.IsThisPc;
            IsGateway |= update.IsGateway;
            var entry = update.Announcement;
            if (entry != null)
            {
                DescriptionPorts.UnionWith(update.DescriptionPorts);
                if (Announcement == null) Announcement = entry;
                else if (entry.Port > 0)
                    Announcement = new LocalDeviceAnnouncement(Ip, Announcement.Name ?? entry.Name, Announcement.Model ?? entry.Model,
                        Announcement.Manufacturer ?? entry.Manufacturer, entry.Port);
            }
            var changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    internal sealed class LocalNetwork
    {
        internal readonly string Name;
        internal readonly int Index;
        internal readonly IPAddress Source;
        internal readonly PhysicalAddress HardwareAddress;
        internal readonly IPAddress[] Gateways;
        readonly Guid networkId;
        readonly uint mask;

        internal LocalNetwork(string name, int index, IPAddress source, IPAddress subnetMask,
            IPAddress[] gateways, PhysicalAddress hardwareAddress = null, Guid networkId = default(Guid))
        {
            Name = name;
            Index = index;
            Source = source;
            mask = Number(subnetMask);
            Gateways = gateways;
            HardwareAddress = hardwareAddress;
            this.networkId = networkId;
        }

        internal bool Contains(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
            uint ip = Number(address);
            uint network = Number(Source) & mask;
            if ((ip & mask) != network || ip == 0 || (ip >> 28) >= 14) return false;
            return ~mask <= 1 || (ip != network && ip != (network | ~mask));
        }

        internal static uint Number(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        }

        static IPAddress FromNumber(long value)
        {
            return new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }

        internal bool SameConnection(LocalNetwork other)
        {
            return other != null && other.Index == Index && other.networkId == networkId && other.Source.Equals(Source) && other.mask == mask &&
                new HashSet<IPAddress>(Gateways).SetEquals(other.Gateways);
        }

        internal IEnumerable<IPAddress> Hosts()
        {
            uint local = Number(Source);
            long first = local & mask;
            long last = first + ~mask;
            if (last - first > 1) { first++; last--; }
            // On a large LAN, start in the PC's own /24 before continuing around the subnet.
            long start = Math.Max(first, local & 0xffffff00u);
            for (long i = start; i <= last; i++) yield return FromNumber(i);
            for (long i = first; i < start; i++) yield return FromNumber(i);
        }

        public override string ToString()
        {
            int prefix = 0;
            for (uint bits = mask; bits != 0; bits <<= 1) prefix++;
            return Name + " · " + Source + "/" + prefix;
        }
    }

    internal sealed class LocalNetworkDiscovery
    {
        const int ProbeLimit = 24;
        static readonly SemaphoreSlim probeSlots = new SemaphoreSlim(ProbeLimit);
        static readonly SemaphoreSlim nameSlots = new SemaphoreSlim(8);
        readonly Func<LocalNetwork, IPAddress, PhysicalAddress> resolveMac;
        readonly Func<IPAddress, string> resolveName;
        readonly Action<LocalNetwork, Action<LocalDeviceAnnouncement>, CancellationToken> readAnnouncements;

        internal LocalNetworkDiscovery() : this(ResolveNeighbor, LookupName, LocalDeviceAnnouncements.Read) { }

        internal LocalNetworkDiscovery(Func<LocalNetwork, IPAddress, PhysicalAddress> resolveMac, Func<IPAddress, string> resolveName,
            Action<LocalNetwork, Action<LocalDeviceAnnouncement>, CancellationToken> readAnnouncements = null)
        {
            this.resolveMac = resolveMac;
            this.resolveName = resolveName;
            this.readAnnouncements = readAnnouncements;
        }

        // Isolate WinRT so a runtime without that projection can still enumerate adapters.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void ReadNetworkIds(Dictionary<Guid, Guid> ids)
        {
            foreach (var profile in Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
            {
                var adapter = profile.NetworkAdapter;
                if (adapter == null || profile.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.None) continue;
                var item = adapter.NetworkItem;
                if (item != null) ids[adapter.NetworkAdapterId] = item.NetworkId;
            }
        }

        static Dictionary<Guid, Guid> ActiveNetworkIds()
        {
            var ids = new Dictionary<Guid, Guid>();
            try { ReadNetworkIds(ids); }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            return ids;
        }

        internal static List<LocalNetwork> Connections()
        {
            var result = new List<LocalNetwork>();
            var ids = ActiveNetworkIds();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    string type = SpeedConnection.LinkName(adapter.NetworkInterfaceType);
                    if (adapter.OperationalStatus != OperationalStatus.Up || (type != "Ethernet" && type != "Wi-Fi") ||
                        !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                    var properties = adapter.GetIPProperties();
                    int index = properties.GetIPv4Properties().Index;
                    var gateways = properties.GatewayAddresses.Select(g => g.Address)
                        .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any)).ToArray();
                    var mac = adapter.GetPhysicalAddress();
                    Guid adapterId, networkId = Guid.Empty;
                    if (Guid.TryParse(adapter.Id, out adapterId)) ids.TryGetValue(adapterId, out networkId);
                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address) ||
                            unicast.Address.Equals(IPAddress.Any) || unicast.IPv4Mask == null || unicast.IPv4Mask.Equals(IPAddress.Any) ||
                            (unicast.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Preferred &&
                             unicast.DuplicateAddressDetectionState != DuplicateAddressDetectionState.Deprecated)) continue;
                        result.Add(new LocalNetwork(adapter.Name, index, unicast.Address, unicast.IPv4Mask, gateways, mac, networkId));
                    }
                }
                catch (NetworkInformationException) { } // The adapter may have disappeared during enumeration.
            }
            return result.OrderByDescending(link => link.Gateways.Length > 0).ThenBy(link => link.Name).ToList();
        }

        internal void Scan(LocalNetwork network, Action<LocalDevice> found, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            found(new LocalDevice(network.Source, null, network.HardwareAddress) { IsThisPc = true });
            var options = new ParallelOptions { MaxDegreeOfParallelism = ProbeLimit, CancellationToken = token };
            var hosts = Partitioner.Create(network.Hosts(), EnumerablePartitionerOptions.NoBuffering);
            var names = new ConcurrentBag<Task>();
            if (readAnnouncements != null)
                names.Add(Task.Run(() => readAnnouncements(network, entry => {
                    token.ThrowIfCancellationRequested();
                    if (network.Contains(entry.Address))
                        found(new LocalDevice(entry.Address, null, null, entry) { IsGateway = network.Gateways.Contains(entry.Address) });
                }, token), token));
            try
            {
                Parallel.ForEach(hosts, options, address => {
                    if (address.Equals(network.Source)) return;
                    probeSlots.Wait(token);
                    PhysicalAddress mac;
                    try { mac = resolveMac(network, address); }
                    finally { probeSlots.Release(); }
                    token.ThrowIfCancellationRequested();
                    if (mac == null) return;
                    found(new LocalDevice(address, null, mac) { IsGateway = network.Gateways.Contains(address) });
                    names.Add(PublishNameAsync(address, found, token));
                });
            }
            finally { Task.WhenAll(names).GetAwaiter().GetResult(); }
        }

        async Task PublishNameAsync(IPAddress address, Action<LocalDevice> found, CancellationToken token)
        {
            await nameSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string name = await Task.Run(() => resolveName(address), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(name) && name != address.ToString()) found(new LocalDevice(address, name));
            }
            finally { nameSlots.Release(); }
        }

        static string LookupName(IPAddress ip)
        {
            try { return Dns.GetHostEntry(ip).HostName; }
            catch (SocketException) { return null; }
        }

        // SOCKADDR_INET and MIB_IPNET_ROW2 from netioapi.h have the same layout on x86 and x64.
        [StructLayout(LayoutKind.Explicit, Size = 28)]
        struct SocketAddressV4
        {
            [FieldOffset(0)] internal ushort Family;
            [FieldOffset(4)] internal uint Address;

            internal SocketAddressV4(IPAddress ip) : this()
            {
                Family = (ushort)AddressFamily.InterNetwork;
                Address = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 88)]
        struct NeighborRow
        {
            [FieldOffset(0)] internal SocketAddressV4 Destination;
            [FieldOffset(28)] internal uint InterfaceIndex;
            [FieldOffset(40), MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PhysicalAddressBytes;
            [FieldOffset(72)] internal uint PhysicalAddressLength;
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern uint ResolveIpNetEntry2(ref NeighborRow row, ref SocketAddressV4 source);

        static PhysicalAddress ResolveNeighbor(LocalNetwork network, IPAddress ip)
        {
            var row = new NeighborRow { Destination = new SocketAddressV4(ip), InterfaceIndex = (uint)network.Index,
                PhysicalAddressBytes = new byte[32] };
            var source = new SocketAddressV4(network.Source);
            uint error = ResolveIpNetEntry2(ref row, ref source);
            if (error == 67) return null; // ERROR_BAD_NET_NAME: this neighbor did not respond.
            if (error != 0) throw new Win32Exception((int)error);
            if (row.PhysicalAddressLength == 0 || row.PhysicalAddressLength > 32) return null;
            return new PhysicalAddress(row.PhysicalAddressBytes.Take((int)row.PhysicalAddressLength).ToArray());
        }
    }
}
