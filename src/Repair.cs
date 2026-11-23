using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AtelierPC
{
    internal enum RepairState { Healthy, Problem, Unverified }

    internal static class Repair
    {
        public static bool CanEnable(Device device, int build)
        {
            if (device == null || device.Code != 22 || build < 19041 || string.IsNullOrEmpty(device.Id)) return false;
            string id = device.Id;
            string[] parts = id.Split('\\');
            return parts.Length == 3 && parts.All(p => !string.IsNullOrWhiteSpace(p))
                && char.IsLetterOrDigit(id[0]) && !id.Any(char.IsControl)
                && id.IndexOfAny(new[] { '"', '*', '?', '/' }) < 0;
        }

        static Device Find(string id, IEnumerable<Device> devices)
        {
            return devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static RepairState Verify(string id, IEnumerable<Device> devices)
        {
            Device current = Find(id, devices);
            if (current == null || current.Code == Device.Unreadable) return RepairState.Unverified;
            return current.Code == 0 ? RepairState.Healthy : RepairState.Problem;
        }

        // Elevate only the Windows utility. It receives one freshly matched instance ID.
        public static string Enable(Device selected, int build, IntPtr owner)
        {
            Device current = Find(selected.Id, DeviceReader.Scan());
            if (!CanEnable(current, build))
                return "L’état de cet appareil a changé. Consultez les résultats actualisés.";
            var start = new ProcessStartInfo {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                Arguments = "/enable-device \"" + current.Id + "\"",
                UseShellExecute = true, Verb = "runas", ErrorDialogParentHandle = owner,
                WorkingDirectory = Environment.SystemDirectory, WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                using (var process = Process.Start(start))
                {
                    if (!process.WaitForExit(60000)) return "Windows traite encore la demande. Relancez l’analyse dans un instant.";
                    if (process.ExitCode == 3010) return "Windows demande un redémarrage. Enregistrez votre travail et redémarrez le PC.";
                    if (process.ExitCode != 0) return "Windows a refusé la réactivation (code " + process.ExitCode + "). Consultez les étapes manuelles.";
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return "Réactivation annulée lors de la demande d’autorisation Windows.";
                throw;
            }
            // A successful command can precede the actual Plug and Play state change.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (Verify(current.Id, DeviceReader.Scan()) == RepairState.Healthy) break;
                Thread.Sleep(500);
            }
            return null;
        }
    }
}
