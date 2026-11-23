using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace AtelierPC
{
    internal enum CleanupResult { Removed, Changed, Denied, PendingRestart, Unverified, Failed, Cancelled }

    internal static class UsbCleanup
    {
        public const string Command = "--retirer-ancienne-entree-usb";

        public static UsbChange FindReviewed(string fingerprint, IEnumerable<Device> live, IEnumerable<Device> history)
        {
            if (!Regex.IsMatch(fingerprint ?? "", @"\A[A-F0-9]{64}\z")) return null;
            return new UsbTracker().Update(live, history).SingleOrDefault(c => c.CanClean && c.Fingerprint == fingerprint);
        }

        public static int Run(string fingerprint)
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return (int)CleanupResult.Denied;
                var match = FindReviewed(fingerprint, DeviceReader.Scan(), DeviceReader.DisconnectedUsb());
                return (int)(match == null ? CleanupResult.Changed : DeviceReader.RemoveDisconnected(match));
            }
            catch (Exception) { return (int)CleanupResult.Failed; }
        }

        public static CleanupResult Request(UsbChange change, IntPtr owner)
        {
            if (!change.CanClean) return CleanupResult.Changed;
            var start = new ProcessStartInfo {
                FileName = Assembly.GetExecutingAssembly().Location, UseShellExecute = true, Verb = "runas",
                Arguments = Command + " " + change.Fingerprint, WorkingDirectory = Environment.SystemDirectory,
                ErrorDialogParentHandle = owner
            };
            try
            {
                using (var child = Process.Start(start))
                {
                    child.WaitForExit();
                    return Enum.IsDefined(typeof(CleanupResult), child.ExitCode) ? (CleanupResult)child.ExitCode : CleanupResult.Failed;
                }
            }
            catch (Win32Exception ex) { return ex.NativeErrorCode == 1223 ? CleanupResult.Cancelled : CleanupResult.Failed; }
            catch (InvalidOperationException) { return CleanupResult.Failed; }
        }
    }

    internal static partial class DeviceReader
    {
        [DllImport("newdev.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool DiUninstallDevice(IntPtr owner, DeviceSet set, ref DeviceInfo info, uint flags, out bool needReboot);

        internal static CleanupResult RemoveDisconnected(UsbChange change)
        {
            if (!change.CanClean) return CleanupResult.Changed;
            string oldId = change.Before.Device.Id, currentId = change.Now.Device.Id;
            if (UsbTracker.Equal(oldId, currentId)) return CleanupResult.Changed;
            using (var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, AllClasses))
            {
                if (set.IsInvalid) return CleanupResult.Failed;
                var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    if (!UsbTracker.Equal(InstanceId(set, ref info), oldId)) continue;
                    uint oldNode, currentNode, flags, problem;
                    if (CM_Locate_DevNode(out currentNode, currentId, 0) != 0
                        || CM_Get_DevNode_Status(out flags, out problem, currentNode, 0) != 0 || (flags & HasProblem) != 0
                        || CM_Locate_DevNode(out oldNode, oldId, 1) != 0 || oldNode != info.DevInst
                        || CM_Locate_DevNode(out oldNode, oldId, 0) != NoDevnode) return CleanupResult.Changed;
                    bool reboot;
                    if (!DiUninstallDevice(IntPtr.Zero, set, ref info, 0, out reboot)) return CleanupResult.Failed;
                    if (reboot) return CleanupResult.PendingRestart;
                    return CM_Locate_DevNode(out oldNode, oldId, 1) == NoDevnode && CM_Locate_DevNode(out currentNode, currentId, 0) == 0
                        ? CleanupResult.Removed : CleanupResult.Unverified;
                }
            }
            return CleanupResult.Changed;
        }
    }
}
