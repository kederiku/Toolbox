using System;
using System.Collections.Generic;
using System.Linq;

namespace AtelierPC
{
    internal sealed class RouterInventory
    {
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<RouterClient> Clients;
        internal readonly int OfflineCount;

        internal RouterInventory(List<RouterClient> entries)
        {
            var unique = entries.GroupBy(c => new { c.Mac, c.Address, c.Name, c.Active }).Select(group => group.First()).ToList();
            Clients = unique.Where(c => c.Active != false).ToList();
            OfflineCount = unique.Count - Clients.Count;
            foreach (var group in Clients.Where(c => !string.IsNullOrEmpty(c.Mac) && !string.IsNullOrWhiteSpace(c.Name)).GroupBy(c => c.Mac, StringComparer.OrdinalIgnoreCase))
            {
                var candidates = group.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                if (candidates.Count == 1) names.Add(group.Key, candidates[0]);
            }
        }

        internal string NameFor(LocalDevice device)
        {
            string name;
            return device.HardwareAddress != null && names.TryGetValue(device.MacAddress, out name) ? name : null;
        }
    }
}
