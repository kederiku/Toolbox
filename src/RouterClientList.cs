using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AtelierPC
{
    internal sealed class RouterClient
    {
        public string Name { get; private set; }
        public string Mac { get; private set; }
        public string Address { get; private set; }
        public bool? Active { get; private set; }
        public string StateLabel { get { return Active == true ? "Connecté" : Active == false ? "Hors ligne" : "Non précisé"; } }
        public string AddressSortKey { get; private set; }

        internal RouterClient(string name, string mac, string address, bool? active)
        {
            Mac = string.IsNullOrWhiteSpace(mac) ? "" : RouterClientList.NormalizeMac(mac);
            if (Mac == null) throw new ArgumentException("Adresse MAC unicast attendue.", "mac");
            Name = RouterClientList.CleanName(name);
            var ip = RouterClientList.ReadAddress(address);
            Address = ip == null ? "" : ip.ToString();
            AddressSortKey = ip == null ? "9" : (ip.AddressFamily == AddressFamily.InterNetwork ? "4" : "6") +
                BitConverter.ToString(ip.GetAddressBytes()).Replace("-", "");
            Active = active;
        }
    }

    internal static class RouterClientList
    {
        const int MaxChars = 1024 * 1024, MaxRowChars = 16384, MaxClients = 4096;
        enum Field { Other, Name, Mac, Address, Status }
        static readonly Regex macFormat = new Regex(@"\A(?:[0-9a-f]{12}|[0-9a-f]{2}([:-])(?:[0-9a-f]{2}\1){4}[0-9a-f]{2}|[0-9a-f]{4}(?:\.[0-9a-f]{4}){2})\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex spaces = new Regex(@"\s+");
        static readonly Regex gutters = new Regex(@" {2,}");

        internal static string NormalizeMac(string text)
        {
            if (text == null || text.Length > 64) return null;
            string value = text.Trim();
            if (!macFormat.IsMatch(value)) return null;
            value = value.Replace("-", "").Replace(":", "").Replace(".", "").ToUpperInvariant();
            if (value == "000000000000" || (Convert.ToByte(value.Substring(0, 2), 16) & 1) != 0) return null;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => value.Substring(i * 2, 2)));
        }

        internal static IPAddress ReadAddress(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return null;
            string value = text.Trim();
            IPAddress ip;
            if (value.Contains(":"))
            {
                if (value.StartsWith("[", StringComparison.Ordinal) && !value.EndsWith("]", StringComparison.Ordinal)) return null;
                return IPAddress.TryParse(value, out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                    !ip.Equals(IPAddress.IPv6Any) && !ip.IsIPv6Multicast && !IPAddress.IsLoopback(ip) ? ip : null;
            }
            var parts = value.Split('.');
            if (parts.Length != 4) return null;
            var bytes = new byte[4];
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 3 || !byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return null;
            return bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224 ? null : new IPAddress(bytes);
        }

        // Match whole field labels, so "Gateway IP" or "Last online" cannot describe the client.
        static string Key(string value)
        {
            if (value.Length > 160) return "";
            try { value = value.Normalize(NormalizationForm.FormD); }
            catch (ArgumentException) { return ""; }
            var key = new StringBuilder(value.Length);
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
            return key.ToString();
        }

        static Field Label(string text)
        {
            switch (Key(text))
            {
                case "name": case "nom": case "hostname": case "nomdhote": case "nomhote": case "host":
                case "devicename": case "clientname": case "friendlyname": case "alias": case "device": case "appareil":
                case "equipement": case "client": case "peripherique": case "nomdequipement":
                case "nomdelappareil": case "nomappareil": case "nomdelequipement":
                case "nomduperipherique": case "nomduclient": case "nomdelhote": case "nomperipherique": return Field.Name;
                case "mac": case "macaddress": case "addressmac": case "adressemac": case "clientmac":
                case "hardwareaddress": case "physicaladdress": case "adressephysique": case "adressematerielle": return Field.Mac;
                case "ip": case "ipv4": case "ipv6": case "ipaddress": case "ipv4address": case "ipv6address":
                case "adresseip": case "adresseipv4": case "adresseipv6": case "clientip":
                case "adresseinternet": case "internetaddress": return Field.Address;
                case "status": case "state": case "etat": case "statut": case "online": case "enligne":
                case "connected": case "connecte": case "active": case "actif": case "connectionstatus":
                case "connection": case "connexion": case "connectionstate": case "etatdeconnexion": case "statutdeconnexion": return Field.Status;
                default: return Field.Other;
            }
        }

        static bool? Status(string text, bool labeled)
        {
            switch (Key(text))
            {
                case "online": case "enligne": case "active": case "actif": case "connected": case "connecte":
                case "connectee": case "associe": case "associee": case "associated": return true;
                case "offline": case "horsligne": case "inactive": case "inactif": case "disconnected":
                case "deconnecte": case "deconnectee": case "nonconnecte": case "nonconnectee": case "notconnected": return false;
                case "yes": case "oui": case "true": case "1": if (labeled) return true; break;
                case "no": case "non": case "false": case "0": if (labeled) return false; break;
            }
            return null;
        }

        static Field LabeledValue(string text, out string value)
        {
            value = "";
            int colon = text.IndexOfAny(new[] { ':', '=' });
            if (colon < 1 || colon > 80) return Field.Other;
            var field = Label(text.Substring(0, colon));
            if (field != Field.Other) value = text.Substring(colon + 1).Trim();
            return field;
        }

        static bool OtherLabeledText(string text)
        {
            int at = text.IndexOfAny(new[] { ':', '=' });
            return at > 0 && char.IsLetter(text[0]) && (at + 1 == text.Length ||
                text[at] == '=' || char.IsWhiteSpace(text[at - 1]) || char.IsWhiteSpace(text[at + 1]));
        }

        static bool HasLabeledValue(string text)
        {
            string value;
            return LabeledValue(text, out value) != Field.Other && value.Length != 0;
        }

        static bool FitsLabel(Field field, string value)
        {
            switch (field)
            {
                case Field.Name: return Label(value) == Field.Other && NormalizeMac(value) == null &&
                    ReadAddress(value) == null && !Status(value, true).HasValue;
                case Field.Mac: return NormalizeMac(value) != null;
                case Field.Address: return ReadAddress(value) != null;
                case Field.Status: return Status(value, true).HasValue;
                default: return false;
            }
        }

        internal static string CleanName(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 160) return "";
            string name = spaces.Replace(text.Trim(), " ");
            if (!name.Any(char.IsLetterOrDigit) || name.Any(c => char.IsControl(c) ||
                char.GetUnicodeCategory(c) == UnicodeCategory.Format) || ReadAddress(name) != null ||
                macFormat.IsMatch(name) || Status(name, true).HasValue || Label(name) != Field.Other) return "";
            string key = Key(name);
            switch (key)
            {
                case "unknown": case "unknowndevice": case "inconnu": case "inconnue": case "appareilinconnu":
                case "nominconnu": case "sansnom": case "unnamed": case "none": case "null": case "na":
                case "indisponible": case "nonrenseigne": case "nonprecise": case "ethernet":
                case "wlan": case "lan": case "wireless": case "wired": case "filaire": case "sansfil": return "";
            }
            if (key.StartsWith("wifi", StringComparison.Ordinal) &&
                Regex.IsMatch(key, @"^wifi(?:[24567](?:[.0-9]*ghz)?|24ghz|[abgn]+)?$")) return "";
            return name;
        }

        // Keep duplicate rows as separate observations. The caller owns reconciliation after preview.
        internal static List<RouterClient> Parse(string input)
        {
            var clients = new List<RouterClient>();
            if (input == null || input.Length > MaxChars || string.IsNullOrWhiteSpace(input)) return clients;
            var lines = input.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF').Split('\n');
            var block = new List<KeyValuePair<Field, string>>();
            var pending = Field.Other;
            Field[] header = null;
            char tableSeparator = '\0';
            for (int i = 0; i < lines.Length && clients.Count < MaxClients; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxRowChars)
                {
                    AddBlock(clients, block);
                    pending = Field.Other;
                    header = null;
                    continue;
                }
                string trimmed = line.Trim();
                string value;
                Field prefix = LabeledValue(trimmed, out value);
                char separator = header == null ? Separator(line) : tableSeparator;
                bool tabular = line.IndexOf('\t') >= 0 || (separator != '\0' && line.IndexOf(separator) >= 0 &&
                    (header != null || line.Split(separator).Skip(1).Any(HasLabeledValue)));
                if (prefix != Field.Other && !tabular)
                {
                    if (value.Length > 0) block.Add(new KeyValuePair<Field, string>(prefix, value));
                    pending = value.Length == 0 ? prefix : Field.Other;
                    header = null;
                    continue;
                }
                if (header == null && !tabular && OtherLabeledText(trimmed))
                {
                    // Metadata for an unknown field is never a substitute for a client's name or MAC.
                    pending = Field.Other;
                    continue;
                }
                if (pending != Field.Other && separator == '\0' && (FitsLabel(pending, trimmed) || Label(trimmed) == Field.Other))
                {
                    block.Add(new KeyValuePair<Field, string>(pending, trimmed));
                    pending = Field.Other;
                    continue;
                }
                pending = Field.Other;
                var cells = Cells(lines, ref i, separator);
                if (cells == null) { AddBlock(clients, block); continue; }
                var labels = cells.Select(Label).ToArray();
                if (cells.Length == 1 && labels[0] != Field.Other)
                {
                    pending = labels[0];
                    header = null;
                    continue;
                }
                if (header == null && cells.Length == 2 && FitsLabel(labels[0], cells[1]))
                {
                    block.Add(new KeyValuePair<Field, string>(labels[0], cells[1]));
                    continue;
                }
                AddBlock(clients, block);
                if (cells.Length > 1 && (labels.Contains(Field.Mac) || labels.Distinct().Count(f => f != Field.Other) >= 2) &&
                    cells.All(c => NormalizeMac(c) == null && ReadAddress(c) == null && !HasLabeledValue(c)))
                {
                    header = labels;
                    tableSeparator = separator;
                    continue;
                }
                if (header != null && header.Length == cells.Length)
                    AddRow(clients, header, cells, true);
                else
                    AddLoose(clients, cells, header == null);
            }
            AddBlock(clients, block);
            return clients;
        }

        static char Separator(string line)
        {
            bool quoted = false, comma = false, semicolon = false;
            foreach (char c in line)
            {
                if (c == '"') quoted = !quoted;
                else if (!quoted && c == '\t') return '\t';
                else if (!quoted && c == ';') semicolon = true;
                else if (!quoted && c == ',') comma = true;
            }
            return semicolon ? ';' : comma ? ',' : '\0';
        }

        static string[] Cells(string[] lines, ref int index, char separator)
        {
            if (separator == '\0') return gutters.Split(lines[index].Trim());
            var cells = new List<string>();
            var value = new StringBuilder();
            bool quoted = false, closed = false;
            int length = 0;
            for (int i = index; i < lines.Length && i - index <= 16; i++)
            {
                string line = lines[i];
                length += line.Length + 1;
                if (length > MaxRowChars) return null;
                foreach (char c in line)
                {
                    if (c == '"' && (quoted || closed || value.Length == 0))
                    {
                        if (closed) value.Append('"');
                        closed = quoted;
                        quoted = !quoted;
                    }
                    else if (c == separator && !quoted)
                    {
                        if (cells.Count >= 128) return null;
                        cells.Add(value.ToString().Trim());
                        value.Clear();
                        closed = false;
                    }
                    else if (closed && !char.IsWhiteSpace(c)) return null;
                    else value.Append(c);
                }
                if (!quoted)
                {
                    index = i;
                    cells.Add(value.ToString().Trim());
                    return cells.ToArray();
                }
                value.Append('\n');
            }
            return null;
        }

        static void AddLoose(List<RouterClient> clients, string[] cells, bool infer)
        {
            if (cells.Length == 1 && cells[0].Contains(" ")) cells = spaces.Split(cells[0]);
            var labels = new Field[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                string value;
                var prefix = LabeledValue(cells[i], out value);
                if (prefix != Field.Other && (infer || prefix == Field.Mac))
                {
                    labels[i] = prefix;
                    cells[i] = value;
                }
                else if (NormalizeMac(cells[i]) != null) labels[i] = Field.Mac;
                else if (!infer) continue;
                else if (ReadAddress(cells[i]) != null) labels[i] = Field.Address;
                else if (Status(cells[i], false).HasValue) labels[i] = Field.Status;
                else if (!OtherLabeledText(cells[i])) labels[i] = Field.Name;
            }
            AddRow(clients, labels, cells);
        }

        static void AddRow(List<RouterClient> clients, Field[] labels, string[] cells, bool headed = false)
        {
            if (clients.Count >= MaxClients) return;
            var macs = new HashSet<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new HashSet<IPAddress>();
            var states = new HashSet<bool>();
            bool hasMacValue = false;
            for (int i = 0; i < cells.Length; i++)
            {
                switch (labels[i])
                {
                    case Field.Mac:
                        hasMacValue |= !string.IsNullOrWhiteSpace(cells[i]);
                        string mac = NormalizeMac(cells[i]);
                        if (mac != null) macs.Add(mac);
                        break;
                    case Field.Name:
                        string name = CleanName(cells[i]);
                        if (name != "") names.Add(name);
                        break;
                    case Field.Address:
                        var ip = ReadAddress(cells[i]);
                        if (ip != null) addresses.Add(ip);
                        break;
                    case Field.Status:
                        bool? active = Status(cells[i], true);
                        if (active.HasValue) states.Add(active.Value);
                        break;
                }
            }
            if (macs.Count > 1 || (macs.Count == 0 && (!headed || hasMacValue || names.Count != 1))) return;
            // Prefer a unique IPv4 when the router reports both families for the same client.
            var ipv4 = addresses.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToArray();
            var address = ipv4.Length == 1 ? ipv4[0] : addresses.Count == 1 ? addresses.First() : null;
            if (macs.Count == 0 && address == null) return;
            clients.Add(new RouterClient(names.Count == 1 ? names.First() : "", macs.FirstOrDefault(),
                address == null ? "" : address.ToString(), states.Count == 1 ? (bool?)states.First() : null));
        }

        static void AddBlock(List<RouterClient> clients, List<KeyValuePair<Field, string>> block)
        {
            if (block.Count == 0) return;
            var labels = block.Select(pair => pair.Key).ToArray();
            var values = block.Select(pair => pair.Value).ToArray();
            block.Clear();
            if (labels.Count(f => f == Field.Mac) <= 1) { AddRow(clients, labels, values); return; }
            // Several cards may have lost their blank lines. Only a repeating schema establishes their boundaries.
            int period = Array.IndexOf(labels, labels[0], 1);
            bool repeated = period > 0 && labels.Length % period == 0 &&
                labels.Take(period).Count(f => f == Field.Mac) == 1;
            for (int i = period; repeated && i < labels.Length; i++) repeated = labels[i] == labels[i % period];
            if (repeated)
            {
                var schema = labels.Take(period).ToArray();
                var row = new string[period];
                for (int i = 0; i < values.Length && clients.Count < MaxClients; i += period)
                {
                    Array.Copy(values, i, row, 0, period);
                    AddRow(clients, schema, row);
                }
            }
            else
                for (int i = 0; i < labels.Length && clients.Count < MaxClients; i++)
                    if (labels[i] == Field.Mac) AddRow(clients, new[] { Field.Mac }, new[] { values[i] });
        }
    }
}
