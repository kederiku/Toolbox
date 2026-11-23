using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AtelierPC
{
    internal enum UsbFailure { None, Reset, Description, Address, Identification }

    internal sealed class UsbEvidence
    {
        static readonly Regex PathPort = new Regex(@"#USB\(([0-9]+)\)", RegexOptions.IgnoreCase);
        static readonly Regex HubPort = new Regex(@"^Port_#0*(\d+)\.Hub_#0*(\d+)$", RegexOptions.IgnoreCase);
        static readonly Regex VendorProduct = new Regex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&|\\|$)", RegexOptions.IgnoreCase);
        readonly string[] hardwareIds;
        readonly string hubPort;
        public readonly string PortPath;
        public readonly string PortLabel, Product;
        public readonly int? PortNumber;
        public readonly bool UniqueInstance;
        public readonly bool IdentityChecked;
        public readonly List<string> PriorModels = new List<string>();

        public UsbEvidence(string[] hardwareIds, string path, string location, string product, bool? uniqueInstance = null)
        {
            this.hardwareIds = hardwareIds ?? new string[0];
            UniqueInstance = uniqueInstance == true;
            IdentityChecked = uniqueInstance.HasValue;
            PortPath = IsPortPath(path) ? path : null;
            Product = string.IsNullOrWhiteSpace(product) ? null : product.Trim();
            var match = HubPort.Match(location ?? "");
            string number = null;
            if (match.Success)
            {
                hubPort = match.Value;
                number = match.Groups[1].Value;
                PortLabel = "Port USB " + match.Groups[1].Value + " du concentrateur " + match.Groups[2].Value;
            }
            else if (PortPath != null)
            {
                var ports = PathPort.Matches(path).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                number = ports.Last();
                PortLabel = "Chemin des ports USB : " + string.Join(" → ", ports);
            }
            int value;
            if (int.TryParse(number, out value) && value > 0) PortNumber = value;
        }

        public void Remember(Device previous)
        {
            string ids = ProductIds(previous.Id);
            if (PortPath == null || previous.Usb == null || ids == null || !PhysicalEntry(previous)
                || !string.Equals(PortPath, previous.Usb.PortPath, StringComparison.OrdinalIgnoreCase)) return;
            string name = previous.Usb.Product ?? previous.Model;
            string label = string.IsNullOrWhiteSpace(name) ? ids : name + " (" + ids + ")";
            if (!PriorModels.Contains(label, StringComparer.CurrentCultureIgnoreCase)) PriorModels.Add(label);
        }

        public static bool IsPortPath(string value)
        {
            return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"#USB\(\d+\)$", RegexOptions.IgnoreCase);
        }

        public static string ProductIds(string id)
        {
            var match = VendorProduct.Match(id ?? "");
            if (!match.Success || match.Groups[1].Value == "0000") return null;
            return "VID " + match.Groups[1].Value.ToUpperInvariant() + " · PID " + match.Groups[2].Value.ToUpperInvariant();
        }

        public static UsbFailure ForDevice(Device d)
        {
            if (d.Code != 43) return UsbFailure.None;
            if (!string.IsNullOrEmpty(d.Id) && !d.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                return UsbFailure.None;
            var ids = d.Usb == null ? new string[0] : d.Usb.hardwareIds;
            if (ids.Contains(@"USB\RESET_FAILURE", StringComparer.OrdinalIgnoreCase)) return UsbFailure.Reset;
            if (ids.Contains(@"USB\SET_ADDRESS_FAILURE", StringComparer.OrdinalIgnoreCase)) return UsbFailure.Address;
            if (ids.Any(x => x.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                && x.IndexOf("DESCRIPTOR", StringComparison.OrdinalIgnoreCase) >= 0
                && x.EndsWith("FAILURE", StringComparison.OrdinalIgnoreCase))) return UsbFailure.Description;

            string label = (d.Name + " " + d.Model).ToLowerInvariant();
            if (label.Contains("usb") && (label.Contains("réinitialisation de port") || label.Contains("port reset failed")))
                return UsbFailure.Reset;
            if (label.Contains("usb") && (label.Contains("descripteur") || label.Contains("descriptor failed") || label.Contains("descriptor request failed")))
                return UsbFailure.Description;
            if (ids.Contains(@"USB\UNKNOWN", StringComparer.OrdinalIgnoreCase)
                || (d.Id ?? "").StartsWith(@"USB\VID_0000&PID_", StringComparison.OrdinalIgnoreCase)
                || label.Contains("unknown usb device") || label.Contains("périphérique usb inconnu"))
                return UsbFailure.Identification;
            return UsbFailure.None;
        }

        public static bool PhysicalEntry(Device d)
        {
            var parts = (d.Id ?? "").Split('\\');
            return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
                && !parts[1].StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase)
                && parts[1].IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public static bool CompositeInterface(string id)
        {
            var parts = (id ?? "").Split('\\');
            return parts.Length == 3 && parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase)
                && parts[1].IndexOf("&MI_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool? SamePort(Device a, Device b)
        {
            if (a.Usb != null && b.Usb != null)
            {
                if (a.Usb.PortPath != null && b.Usb.PortPath != null)
                    return string.Equals(a.Usb.PortPath, b.Usb.PortPath, StringComparison.OrdinalIgnoreCase);
                if (a.Usb.hubPort != null && b.Usb.hubPort != null)
                    return string.Equals(a.Usb.hubPort, b.Usb.hubPort, StringComparison.OrdinalIgnoreCase);
            }
            // A USB instance with a serial number can retain its ID when moved to another port.
            return null;
        }
    }
}
