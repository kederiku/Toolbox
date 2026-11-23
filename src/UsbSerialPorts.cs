using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AtelierPC
{
    internal sealed class UsbSerialPorts
    {
        static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
        const int MaxComPort = 4096; // COMDB_MAX_PORTS_ARBITRATED in the Windows SDK.
        static readonly Regex PortName = new Regex(@"\ACOM[1-9][0-9]{0,3}\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        readonly Dictionary<string, List<string>> ports = new Dictionary<string, List<string>>(Ids);
        readonly HashSet<string> shared = new HashSet<string>(Ids);

        public static UsbSerialPorts Read(IEnumerable<Device> snapshot)
        {
            var result = new UsbSerialPorts();
            var byId = snapshot.Where(d => d != null && !string.IsNullOrEmpty(d.Id)).ToLookup(d => d.Id, Ids);
            var assigned = new HashSet<string>(Ids);
            foreach (var group in byId)
            {
                if (group.Count() != 1) continue;
                var device = group.First();
                string port = Normalize(device.ComPort);
                if (port == null) continue;
                var owner = FindOwner(device, byId);
                if (owner == null) continue;
                List<string> list;
                if (!result.ports.TryGetValue(owner.Id, out list))
                    result.ports.Add(owner.Id, list = new List<string>());
                if (list.Contains(port)) continue;
                list.Add(port);
                if (!assigned.Add(port)) result.shared.Add(port);
            }
            foreach (var list in result.ports.Values) list.Sort(ComparePorts);
            return result;
        }

        public string Describe(Device device)
        {
            List<string> list;
            if (device == null || device.Id == null || !ports.TryGetValue(device.Id, out list)) return "";
            string result = list.Count == 1
                ? "Windows indique le port " + list[0] + ". Vérifiez ce réglage dans votre logiciel série."
                : "Windows indique les ports " + string.Join(", ", list)
                    + ". Dans votre logiciel série, sélectionnez le port de l’interface que vous utilisez.";
            var duplicates = list.Where(shared.Contains).ToArray();
            if (duplicates.Length > 0)
                result += " Windows indique aussi " + string.Join(", ", duplicates)
                    + " pour un autre appareil présent. Vérifiez ces affectations dans le Gestionnaire de périphériques.";
            return result;
        }

        public string SharedAssignments()
        {
            var groups = ports.Values.Select(list => string.Join(", ", list)).OrderBy(value => value, StringComparer.Ordinal);
            string result = string.Join(Environment.NewLine, groups.Select(value => "• " + value));
            if (shared.Count > 0)
                result += Environment.NewLine + "Affectation présente sur plusieurs appareils : "
                    + string.Join(", ", shared.OrderBy(value => value, StringComparer.Ordinal)) + ".";
            return result;
        }

        internal static Device FindOwner(Device port, ILookup<string, Device> byId)
        {
            string functionModel = null;
            foreach (var entry in Ancestors(port.Id, byId))
            {
                if (!ValidInstanceId(entry.Id)) break;
                // Remote Bluetooth services can sit below a USB radio in the PnP tree.
                if (entry.Id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)
                    || entry.Id.StartsWith(@"BTHLEDEVICE\", StringComparison.OrdinalIgnoreCase)) break;
                if (UsbEvidence.CompositeInterface(entry.Id))
                {
                    string model = UsbEvidence.ProductIds(entry.Id);
                    if (model == null || (functionModel != null && !Ids.Equals(functionModel, model))) break;
                    functionModel = model;
                    continue;
                }
                if (entry.Kind == UsbKind.None) continue;
                if ((entry.Kind == UsbKind.Device || entry.Kind == UsbKind.Hub) && UsbEvidence.PhysicalEntry(entry)
                    && (functionModel == null || Ids.Equals(functionModel, UsbEvidence.ProductIds(entry.Id))))
                    return entry;
                break;
            }
            return null;
        }

        internal static IEnumerable<Device> Ancestors(string id, ILookup<string, Device> byId)
        {
            var visited = new HashSet<string>(Ids);
            while (!string.IsNullOrEmpty(id) && visited.Add(id))
            {
                var matches = byId[id];
                if (matches.Count() != 1) yield break;
                var current = matches.First();
                yield return current;
                id = current.ParentId;
            }
        }

        static bool ValidInstanceId(string id)
        {
            if (string.IsNullOrEmpty(id) || !char.IsLetterOrDigit(id[0]) || id.Any(char.IsControl)
                || id.IndexOfAny(new[] { '"', '*', '?', '/' }) >= 0) return false;
            var parts = id.Split('\\');
            return parts.Length == 3 && parts.All(part => !string.IsNullOrWhiteSpace(part));
        }

        internal static string Normalize(string port)
        {
            if (port == null || !PortName.IsMatch(port)) return null;
            return int.Parse(port.Substring(3), CultureInfo.InvariantCulture) <= MaxComPort ? port.ToUpperInvariant() : null;
        }

        internal static int ComparePorts(string a, string b)
        {
            int length = a.Length.CompareTo(b.Length);
            return length != 0 ? length : string.CompareOrdinal(a, b);
        }
    }
}
