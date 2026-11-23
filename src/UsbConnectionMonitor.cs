using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AtelierPC
{
    internal sealed class UsbConnectionEvent
    {
        public string LocalText { get; private set; }
        public string SharedText { get; private set; }

        public UsbConnectionEvent(string localText, string sharedText)
        {
            LocalText = localText;
            SharedText = sharedText;
        }
    }

    internal sealed class UsbConnectionMonitor
    {
        enum Presence { Uncertain, Present, Disappeared }
        enum Observation { Appearance, Loss, Return }

        sealed class Entry
        {
            public Device Device;
            public readonly int Number;
            public Presence Status;
            public int Losses, Returns;

            public Entry(Device device, int number) { Device = device; Number = number; }

            public string Describe(Observation observation, bool local)
            {
                var usb = Device.Usb;
                string label = "Appareil USB n°" + Number;
                if (local)
                {
                    string name = (usb == null ? null : usb.Product) ?? Device.Name;
                    if (!string.IsNullOrWhiteSpace(name) && !Ids.Equals(name, Device.Id) && !name.Any(char.IsControl))
                        label += " · " + name;
                    if (usb != null && usb.PortLabel != null) label += " · " + usb.PortLabel;
                }
                else if (usb != null && usb.PortNumber.HasValue)
                    label += " · port USB " + usb.PortNumber.Value;

                if (observation == Observation.Appearance) return label + " · apparition observée.";
                if (observation == Observation.Return)
                    return label + " · " + Returns + (Returns == 1 ? " retour observé." : " retours observés.");
                return label + " · " + Losses + (Losses == 1 ? " disparition observée." : " disparitions observées.");
            }
        }

        static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(Ids);
        HashSet<string> previousIds;

        public void ResetBaseline()
        {
            previousIds = null;
            foreach (var entry in entries.Values) entry.Status = Presence.Uncertain;
        }

        static bool PhysicalDevice(Device device)
        {
            return (device.Kind == UsbKind.Device || device.Kind == UsbKind.Hub) && UsbEvidence.PhysicalEntry(device);
        }

        public List<UsbConnectionEvent> Observe(IEnumerable<Device> devices, DateTime time)
        {
            var events = new List<UsbConnectionEvent>();
            if (devices == null) { ResetBaseline(); return events; }
            var snapshot = devices.ToLookup(d => d == null ? "" : d.Id ?? "", Ids);
            bool hasUnknownId = snapshot.Any(g => string.IsNullOrWhiteSpace(g.Key));
            foreach (var group in snapshot)
            {
                if (string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1 || entries.ContainsKey(group.Key)) continue;
                var device = group.First();
                if (PhysicalDevice(device)) entries.Add(group.Key, new Entry(device, entries.Count + 1));
            }

            var losses = new Dictionary<string, Entry>(Ids);
            var returns = new Dictionary<string, Entry>(Ids);
            var arrivals = new Dictionary<string, Entry>(Ids);
            foreach (var entry in entries.Values)
            {
                var rows = snapshot[entry.Device.Id];
                int count = rows.Count();
                if (count == 1 && PhysicalDevice(rows.First()))
                {
                    entry.Device = rows.First();
                    if (entry.Status == Presence.Disappeared)
                    {
                        entry.Returns++;
                        returns.Add(entry.Device.Id, entry);
                    }
                    else if (previousIds != null && !previousIds.Contains(entry.Device.Id))
                        arrivals.Add(entry.Device.Id, entry);
                    entry.Status = Presence.Present;
                }
                else if (count != 0 || hasUnknownId)
                    entry.Status = Presence.Uncertain;
                else if (entry.Status == Presence.Present)
                {
                    entry.Losses++;
                    losses.Add(entry.Device.Id, entry);
                    entry.Status = Presence.Disappeared;
                }
            }
            // A row with no identity might already be the newly readable device.
            previousIds = hasUnknownId ? null : new HashSet<string>(snapshot.Select(g => g.Key), Ids);
            AppendEvents(events, losses, time, Observation.Loss);
            AppendEvents(events, returns, time, Observation.Return);
            AppendEvents(events, arrivals, time, Observation.Appearance);
            return events;
        }

        // Only group changes seen on the same reading and linked by an explicit hub parent.
        static Entry ChangedHub(Entry entry, Dictionary<string, Entry> changes)
        {
            var ancestor = entry;
            var seen = new HashSet<string>(Ids);
            Entry parent;
            while (!string.IsNullOrEmpty(ancestor.Device.ParentId) && changes.TryGetValue(ancestor.Device.ParentId, out parent)
                && parent.Device.Kind == UsbKind.Hub)
            {
                if (!seen.Add(parent.Device.Id)) return entry;
                ancestor = parent;
            }
            return ancestor;
        }

        static void AppendEvents(List<UsbConnectionEvent> events, Dictionary<string, Entry> changes, DateTime time, Observation observation)
        {
            foreach (var group in changes.Values.GroupBy(entry => ChangedHub(entry, changes)).OrderBy(g => g.Key.Number))
            {
                string heading = time.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " · ";
                if (group.Count() > 1) heading += "Changement simultané du hub et de ses appareils." + Environment.NewLine;
                var ordered = group.OrderBy(e => e == group.Key ? 0 : e.Number).ToList();
                string local = string.Join(Environment.NewLine, ordered.Select(e => e.Describe(observation, true)));
                string shared = string.Join(Environment.NewLine, ordered.Select(e => e.Describe(observation, false)));
                string next = observation == Observation.Loss && ordered.Any(e => e.Losses > 1)
                    ? Environment.NewLine + "Pour un accessoire externe, essayez une autre prise. Gardez le même câble si vous en utilisez un." : "";
                events.Add(new UsbConnectionEvent(heading + local + next, heading + shared + next));
            }
        }
    }
}
