using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal sealed class Device
    {
        public const int Unreadable = -1;
        public const int UnspecifiedProblem = -2;
        public readonly string Id;
        public readonly string Name;
        public readonly string Model;
        public readonly UsbEvidence Usb;
        public readonly string ParentId;
        public readonly UsbKind Kind;
        public readonly string ComPort;
        public readonly int Code;
        public bool HasProblem { get { return Code > 0 || Code == UnspecifiedProblem; } }
        public Device(string id, string name, int code, string model = null, UsbEvidence usb = null,
            string parentId = null, UsbKind kind = UsbKind.None, string comPort = null)
        {
            Id = id;
            Name = name;
            Model = model;
            Code = code;
            ParentId = parentId;
            ComPort = comPort;
            Usb = usb;
            Kind = kind;
            if ((id ?? "").StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
                Kind = UsbKind.RootHub;
            else if (kind == UsbKind.None && (UsbEvidence.PhysicalEntry(this)
                || (string.IsNullOrEmpty(id) && UsbEvidence.ForDevice(this) != UsbFailure.None)))
                Kind = UsbKind.Device;
        }
    }

    internal static partial class DeviceReader
    {
        const uint Present = 0x2, AllClasses = 0x4, HasProblem = 0x400;
        const int NoMoreItems = 259;
        const uint Description = 0, HardwareIds = 1, CompatibleIds = 2, FriendlyName = 12, LocationInfo = 13, LocationPaths = 35;

        [StructLayout(LayoutKind.Sequential)]
        struct PropertyKey { public Guid Format; public uint Index; }

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInfo
        {
            public int Size;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        sealed class DeviceSet : SafeHandleZeroOrMinusOneIsInvalid
        {
            public DeviceSet() : base(true) { }
            protected override bool ReleaseHandle() { return SetupDiDestroyDeviceInfoList(handle); }
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern DeviceSet SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern DeviceSet SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(DeviceSet set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInstanceId(DeviceSet set, ref DeviceInfo info, StringBuilder id, int capacity, out int required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryProperty(DeviceSet set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, int length, out int required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceProperty(DeviceSet set, ref DeviceInfo info, ref PropertyKey key, out uint type, byte[] buffer, int size, out int used, uint flags);
        [DllImport("cfgmgr32.dll")]
        static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Locate_DevNode(out uint devInst, string instanceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_ID(uint devInst, StringBuilder buffer, int capacity, uint flags);

        static string ParentInstanceId(uint devInst)
        {
            uint parent;
            if (CM_Get_Parent(out parent, devInst, 0) != 0) return null;
            var buffer = new StringBuilder(256);
            return CM_Get_Device_ID(parent, buffer, buffer.Capacity, 0) == 0 ? buffer.ToString() : null;
        }

        static HashSet<string> InterfaceDevices(Guid interfaceClass)
        {
            const uint DeviceInterface = 0x10;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var set = SetupDiGetClassDevs(ref interfaceClass, null, IntPtr.Zero, Present | DeviceInterface))
            {
                if (set.IsInvalid) return ids;
                var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    string id = InstanceId(set, ref info);
                    if (id.Length > 0) ids.Add(id);
                }
            }
            return ids;
        }

        static string InstanceId(DeviceSet set, ref DeviceInfo info)
        {
            var buffer = new StringBuilder(256);
            int required;
            return SetupDiGetDeviceInstanceId(set, ref info, buffer, buffer.Capacity, out required) ? buffer.ToString() : "";
        }

        static string RegistryText(DeviceSet set, ref DeviceInfo info, uint property, uint expectedType = 1)
        {
            uint type;
            int length;
            byte[] bytes = new byte[512];
            if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, bytes, bytes.Length, out length))
            {
                if (Marshal.GetLastWin32Error() != 122 || length <= 0 || length > 65536) return null;
                bytes = new byte[length];
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, bytes, bytes.Length, out length)) return null;
            }
            return type == expectedType && length <= bytes.Length ? Encoding.Unicode.GetString(bytes, 0, length).TrimEnd('\0') : null;
        }

        static string[] StringList(DeviceSet set, ref DeviceInfo info, uint property)
        {
            string value = RegistryText(set, ref info, property, 7);
            return (value ?? "").Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static UsbEvidence ReadUsb(DeviceSet set, ref DeviceInfo info)
        {
            var key = new PropertyKey { Format = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), Index = 4 };
            string product = PropertyText(set, ref info, key);
            var path = StringList(set, ref info, LocationPaths).FirstOrDefault(UsbEvidence.IsPortPath);
            uint type;
            int used;
            byte[] bits = new byte[4];
            bool? unique = null;
            if (SetupDiGetDeviceRegistryProperty(set, ref info, 15, out type, bits, bits.Length, out used)
                && type == 4 && used == 4) unique = (BitConverter.ToUInt32(bits, 0) & 0x10) != 0;
            return new UsbEvidence(StringList(set, ref info, HardwareIds), path,
                RegistryText(set, ref info, LocationInfo), product, unique);
        }

        static string PropertyText(DeviceSet set, ref DeviceInfo info, PropertyKey key)
        {
            uint type;
            int used;
            byte[] bytes = new byte[2048];
            if (SetupDiGetDeviceProperty(set, ref info, ref key, out type, bytes, bytes.Length, out used, 0)
                && type == 0x12 && used <= bytes.Length)
                return Encoding.Unicode.GetString(bytes, 0, used).TrimEnd('\0');
            return null;
        }

        public static int InterpretStatus(uint result, uint flags, uint code)
        {
            if (result != 0) return Device.Unreadable;
            if ((flags & HasProblem) == 0) return 0;
            return code == 0 ? Device.UnspecifiedProblem : (int)code;
        }

        // Disconnected devices sometimes retain their USB port path and product description.
        // History is optional; a missing property must never invalidate the live scan.
        static void ReadPortHistory(List<Device> devices)
        {
            var targets = devices.Where(d => d.Usb != null && d.Usb.PortPath != null && UsbEvidence.ForDevice(d) != UsbFailure.None)
                .ToLookup(d => d.Usb.PortPath, StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0) return;
            var present = new HashSet<string>(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            using (var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, AllClasses))
            {
                if (set.IsInvalid) return;
                var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    string id = InstanceId(set, ref info);
                    if (present.Contains(id) || UsbEvidence.ProductIds(id) == null) continue;
                    var usb = ReadUsb(set, ref info);
                    if (usb.PortPath == null || !targets.Contains(usb.PortPath)) continue;
                    uint devInst;
                    const uint NoSuchDevnode = 0x0D;
                    if (CM_Locate_DevNode(out devInst, id, 0) != NoSuchDevnode) continue;
                    string model = RegistryText(set, ref info, Description);
                    var previous = new Device(id, model, Device.Unreadable, model, usb);
                    foreach (var target in targets[usb.PortPath]) target.Usb.Remember(previous);
                }
            }
        }

        public static List<Device> Scan()
        {
            var devices = new List<Device>();
            var controllers = InterfaceDevices(new Guid("3abf6f2d-71c4-462a-8a92-1e6861e6af27"));
            var hubs = InterfaceDevices(new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8"));
            using (var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, Present | AllClasses))
            {
                if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                for (uint i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInfo(set, i, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == NoMoreItems) break;
                        throw new Win32Exception(error);
                    }
                    string id = InstanceId(set, ref info);
                    string model = RegistryText(set, ref info, Description);
                    string name = RegistryText(set, ref info, FriendlyName) ?? model;
                    if (string.IsNullOrWhiteSpace(name)) name = id.Length == 0 ? "Périphérique sans nom" : id;
                    uint flags, problem;
                    uint result = CM_Get_DevNode_Status(out flags, out problem, info.DevInst, 0);
                    UsbEvidence usb = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || (id.Length == 0 && problem == 43)
                        ? ReadUsb(set, ref info) : null;
                    string parentId = ParentInstanceId(info.DevInst);
                    UsbKind kind = UsbKind.None;
                    if (controllers.Contains(id)) kind = UsbKind.Controller;
                    else if (hubs.Contains(id)) kind = controllers.Contains(parentId) ? UsbKind.RootHub : UsbKind.Hub;
                    else if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && StringList(set, ref info, CompatibleIds)
                        .Any(v => v.StartsWith(@"PCI\CC_0C03", StringComparison.OrdinalIgnoreCase))) kind = UsbKind.Controller;
                    else if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && StringList(set, ref info, CompatibleIds)
                        .Any(v => v.Equals(@"USB\Class_09", StringComparison.OrdinalIgnoreCase)
                            || v.StartsWith(@"USB\Class_09&", StringComparison.OrdinalIgnoreCase))
                        && !UsbEvidence.CompositeInterface(id)) kind = UsbKind.Hub;
                    devices.Add(new Device(id, name, InterpretStatus(result, flags, problem), model, usb,
                        parentId, kind, ReadComPort(set, ref info, name)));
                }
            }
            if (devices.Count == 0) throw new InvalidOperationException("Windows a renvoyé une liste vide.");
            ReadPortHistory(devices);
            devices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            return devices;
        }
    }
}
