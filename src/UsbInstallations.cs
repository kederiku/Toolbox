using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AtelierPC
{
    internal static partial class DeviceReader
    {
        static readonly Guid PortsClass = new Guid("4d36e978-e325-11ce-bfc1-08002be10318");
        static readonly PropertyKey ParentProperty = new PropertyKey {
            Format = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), Index = 8 };
        const uint NoDevnode = 0x0D;

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern SafeRegistryHandle SetupDiOpenDevRegKey(DeviceSet set, ref DeviceInfo info, uint scope, uint profile, uint kind, uint access);

        static string ReadComPort(DeviceSet set, ref DeviceInfo info, string name)
        {
            if (info.ClassGuid != PortsClass) return null;
            using (var handle = SetupDiOpenDevRegKey(set, ref info, 1, 0, 1, 1))
            {
                if (!handle.IsInvalid)
                {
                    using (var key = RegistryKey.FromHandle(handle))
                    {
                        string value = null;
                        try { value = key.GetValue("PortName") as string; }
                        catch (IOException) { /* The port can disappear during a scan. */ }
                        catch (SecurityException) { /* The device name may still expose its COM port. */ }
                        catch (UnauthorizedAccessException) { }
                        if (value != null && Regex.IsMatch(value, @"^COM[1-9][0-9]*\z", RegexOptions.IgnoreCase))
                            return value.ToUpperInvariant();
                    }
                }
            }
            var suffix = Regex.Match(name ?? "", @"\((COM[1-9][0-9]*)\)\s*\z", RegexOptions.IgnoreCase);
            return suffix.Success ? suffix.Groups[1].Value.ToUpperInvariant() : null;
        }

        public static List<Device> DisconnectedUsb()
        {
            var history = new List<Device>();
            using (var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, AllClasses))
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
                    bool usb = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
                    if (!usb && info.ClassGuid != PortsClass && !id.StartsWith(@"FTDIBUS\", StringComparison.OrdinalIgnoreCase)) continue;
                    uint devInst;
                    if (CM_Locate_DevNode(out devInst, id, 0) != NoDevnode) continue;
                    var evidence = usb ? ReadUsb(set, ref info) : null;
                    string model = RegistryText(set, ref info, Description);
                    string name = RegistryText(set, ref info, FriendlyName) ?? model;
                    bool hub = usb && StringList(set, ref info, CompatibleIds).Any(v => v.Equals(@"USB\Class_09", StringComparison.OrdinalIgnoreCase)
                        || v.StartsWith(@"USB\Class_09&", StringComparison.OrdinalIgnoreCase));
                    string parent = PropertyText(set, ref info, ParentProperty);
                    history.Add(new Device(id, name, Device.Unreadable, model, evidence, parent,
                        hub ? UsbKind.Hub : UsbKind.None, ReadComPort(set, ref info, name)));
                }
            }
            return history;
        }
    }
}
