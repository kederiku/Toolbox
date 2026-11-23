using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AtelierPC
{
    internal sealed class UsbPortRecord
    {
        public readonly Device Device;
        public readonly string Model, Location, Name;
        public readonly List<string> Ports = new List<string>();
        public bool PortsVerified { get; private set; }

        UsbPortRecord(Device device)
        {
            Device = device;
            PortsVerified = true;
            Model = UsbEvidence.ProductIds(device.Id);
            Name = device.Usb.Product ?? device.Name ?? "Appareil USB";
            string path = device.Usb.PortPath;
            if (path != null) Location = path;
            else if (device.Usb.PortNumber.HasValue && (device.ParentId ?? "").StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                Location = device.ParentId + "#PORT(" + device.Usb.PortNumber.Value + ")";
        }

        public string Label { get { return Device.Usb.PortLabel ?? "Prise indéterminée"; } }
        public string Com { get { return string.Join(", ", Ports); } }
        public bool DifferentLocation(UsbPortRecord other)
        {
            return Location != null && other.Location != null
                && (Device.Usb.PortPath == null) == (other.Device.Usb.PortPath == null)
                && !UsbTracker.Equal(Location, other.Location);
        }

        public static List<UsbPortRecord> Read(IEnumerable<Device> snapshot)
        {
            var devices = snapshot.Where(d => !string.IsNullOrEmpty(d.Id)).ToLookup(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var records = new Dictionary<string, UsbPortRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in devices)
            {
                if (group.Count() != 1) continue;
                var d = group.First();
                if ((d.Kind == UsbKind.Device || d.Kind == UsbKind.Hub) && d.Usb != null
                    && UsbEvidence.PhysicalEntry(d) && UsbEvidence.ProductIds(d.Id) != null)
                    records.Add(d.Id, new UsbPortRecord(d));
            }
            foreach (var port in devices.SelectMany(g => g).Where(d => d.ComPort != null))
            {
                var ancestor = UsbSerialPorts.Ancestors(port.Id, devices).FirstOrDefault(d => records.ContainsKey(d.Id));
                if (ancestor == null) continue;
                var record = records[ancestor.Id];
                string number = UsbSerialPorts.Normalize(port.ComPort);
                // An incomplete assignment must never make a removal eligible.
                if (number == null || ancestor != UsbSerialPorts.FindOwner(port, devices))
                    record.PortsVerified = false;
                else
                {
                    if (!record.Ports.Contains(number)) record.Ports.Add(number);
                    if (number != port.ComPort) record.PortsVerified = false;
                }
            }
            foreach (var entry in records.Values) entry.Ports.Sort(UsbSerialPorts.ComparePorts);
            return records.Values.ToList();
        }
    }

    internal sealed class UsbChange
    {
        public readonly UsbPortRecord Before, Now;
        public readonly bool Recognized, CanClean;
        public UsbChange(UsbPortRecord before, UsbPortRecord now, bool recognized, bool canClean)
        {
            Before = before; Now = now; Recognized = recognized; CanClean = canClean;
        }
        public string Title
        {
            get
            {
                if (!Recognized && UsbTracker.Equal(Before.Device.Id, Now.Device.Id) && Before.Com.Length > 0
                    && Now.Com.Length > 0 && Before.Com != Now.Com) return "Affectation COM modifiée pour la même entrée Windows";
                if (!Recognized) return "Même modèle USB, identité à confirmer";
                if (Before.DifferentLocation(Now)) return "Même appareil reconnu sur un autre port";
                if (Before.Com.Length > 0 && Now.Com.Length > 0 && Before.Com != Now.Com) return "Affectation COM modifiée";
                return "Même appareil reconnu";
            }
        }
        public string Settings
        {
            get
            {
                if (Before.Ports.Count == 0 && Now.Ports.Count == 0) return "";
                if (Before.Ports.Count == 0) return "Port COM actuel : " + Now.Com + ". Ancien réglage indisponible.";
                if (Now.Ports.Count == 0) return "Ancien port COM : " + Before.Com + ". Réglage actuel indisponible.";
                if (Before.Com == Now.Com) return "Même affectation COM : " + Now.Com + ".";
                string hint = Now.Ports.Count == 1 ? "Dans votre logiciel, sélectionnez " + Now.Com + " si c’est bien cet adaptateur que vous utilisez."
                    : "Vérifiez dans votre logiciel quelle interface série vous utilisez.";
                return "Ancien réglage : " + Before.Com + ". Réglage actuel : " + Now.Com + ". " + hint;
            }
        }
        public string Fingerprint
        {
            get
            {
                // Re-identify exactly the reviewed pair after UAC, including its ports and assignment.
                var fields = new[] { Before.Device.Id, Before.Location, Before.Com, Before.Device.Usb.UniqueInstance.ToString(),
                    Now.Device.Id, Now.Location, Now.Com, Now.Device.Usb.UniqueInstance.ToString() };
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\0", fields).ToUpperInvariant()))).Replace("-", "");
            }
        }
    }

    internal sealed class UsbTracker
    {
        static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
        readonly Dictionary<string, UsbPortRecord> seen = new Dictionary<string, UsbPortRecord>(Ids);
        readonly Dictionary<string, UsbChange> lastMove = new Dictionary<string, UsbChange>(Ids);
        readonly HashSet<string> seenTogether = new HashSet<string>(Ids);
        public static bool Equal(string a, string b) { return Ids.Equals(a, b); }

        static string Pair(string a, string b)
        {
            return Ids.Compare(a, b) < 0 ? a + "\0" + b : b + "\0" + a;
        }

        public List<UsbChange> Update(IEnumerable<Device> live, IEnumerable<Device> disconnected)
        {
            var snapshot = live.ToList();
            var activeIds = new HashSet<string>(snapshot.Select(d => d.Id), Ids);
            var active = UsbPortRecord.Read(snapshot);
            var installed = UsbPortRecord.Read(disconnected).Where(r => !activeIds.Contains(r.Device.Id)).ToDictionary(r => r.Device.Id, Ids);
            var previous = new Dictionary<string, UsbPortRecord>(seen, Ids);
            foreach (var record in installed.Values)
            {
                UsbPortRecord observed;
                // Offline nodes can lose properties when unplugged. Keep the live evidence for display.
                if (previous.TryGetValue(record.Device.Id, out observed) && observed.Location != null
                    && (record.Location == null || (record.Com.Length == 0 && observed.Com.Length > 0))) continue;
                previous[record.Device.Id] = record;
            }
            var changes = new List<UsbChange>();
            foreach (var model in active.GroupBy(r => r.Model, Ids))
            {
                foreach (var now in model)
                {
                    foreach (var peer in model.Where(p => p != now)) seenTogether.Add(Pair(now.Device.Id, peer.Device.Id));
                    UsbPortRecord prior;
                    if (seen.TryGetValue(now.Device.Id, out prior))
                    {
                        bool recognized = now.Device.Usb.UniqueInstance && prior.Device.Usb.UniqueInstance;
                        bool comChanged = prior.Ports.Count > 0 && now.Ports.Count > 0 && prior.Com != now.Com;
                        if (prior.DifferentLocation(now) || (comChanged && prior.PortsVerified && now.PortsVerified))
                            lastMove[now.Device.Id] = new UsbChange(prior, now, recognized, false);
                    }
                    UsbChange movement;
                    if (lastMove.TryGetValue(now.Device.Id, out movement))
                        changes.Add(new UsbChange(movement.Before, now, movement.Recognized && now.Device.Usb.UniqueInstance, false));

                    var candidates = previous.Values.Where(old => !activeIds.Contains(old.Device.Id) && Equal(old.Model, now.Model)
                        && !seenTogether.Contains(Pair(now.Device.Id, old.Device.Id))
                        && !(old.Device.Usb.UniqueInstance && now.Device.Usb.UniqueInstance)).ToList();
                    foreach (var old in candidates.Where(old => old.DifferentLocation(now)))
                    {
                        UsbPortRecord stored;
                        bool clean = candidates.Count == 1 && snapshot.Count(d => UsbEvidence.PhysicalEntry(d)
                            && Equal(UsbEvidence.ProductIds(d.Id), now.Model)) == 1 && installed.TryGetValue(old.Device.Id, out stored)
                            && Equal(stored.Location, old.Location) && stored.Com == old.Com
                            && old.PortsVerified && now.PortsVerified && stored.PortsVerified
                            && old.Device.Kind == UsbKind.Device && now.Device.Kind == UsbKind.Device && now.Device.Code == 0
                            && old.Device.Usb.IdentityChecked && now.Device.Usb.IdentityChecked
                            && !old.Device.Usb.UniqueInstance && !now.Device.Usb.UniqueInstance;
                        changes.Add(new UsbChange(old, now, false, clean));
                    }
                }
            }
            foreach (var record in active)
            {
                UsbPortRecord last;
                if (seen.TryGetValue(record.Device.Id, out last) && last.Location != null
                    && (record.Location == null || (Equal(record.Location, last.Location) && record.Com.Length == 0 && last.Com.Length > 0))) continue;
                seen[record.Device.Id] = record;
            }
            return changes;
        }
    }
}
