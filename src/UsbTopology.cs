using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AtelierPC
{
    internal enum UsbKind { None, Controller, RootHub, Hub, Device }

    internal sealed class UsbNode
    {
        public readonly Device Device;
        public readonly List<UsbNode> Children = new List<UsbNode>();
        public readonly List<string> Functions = new List<string>();
        public readonly List<Device> FunctionDevices = new List<Device>();
        public int ControllerNumber { get; internal set; }
        public UsbNode Parent { get; private set; }
        public UsbNode(Device device) { Device = device; }
        public int? Port { get { return Device.Usb == null ? null : Device.Usb.PortNumber; } }

        public static string FunctionName(Device function)
        {
            if (!string.IsNullOrWhiteSpace(function.Name) &&
                !string.Equals(function.Name.Trim(), function.Id, StringComparison.OrdinalIgnoreCase)) return function.Name;
            var number = Regex.Match(function.Id ?? "", @"^[^\\]+\\[^\\]*&MI_([0-9a-f]{2})(?:&|\\)", RegexOptions.IgnoreCase);
            return number.Success ? "Fonction USB " + Convert.ToInt32(number.Groups[1].Value, 16) + " sans nom" : "Fonction USB sans nom";
        }

        public bool NeedsAttention { get { return Device.Code != 0 || FunctionDevices.Any(f => f.Code != 0); } }

        string FunctionSummary
        {
            get
            {
                int failed = FunctionDevices.Count(f => f.HasProblem);
                int unknown = FunctionDevices.Count(f => f.Code == Device.Unreadable);
                var parts = new List<string>();
                if (failed > 0) parts.Add(failed + (failed == 1 ? " fonction en erreur" : " fonctions en erreur"));
                if (unknown > 0) parts.Add(unknown + (unknown == 1 ? " fonction à l’état indéterminé" : " fonctions à l’état indéterminé"));
                return string.Join(" · ", parts);
            }
        }

        public string RowIssue
        {
            get
            {
                string own = Device.Code > 0 ? "Code Windows " + Device.Code : Device.Code == 0 ? "" : Advice.For(Device).Title;
                string functions = FunctionSummary;
                return own.Length == 0 || functions.Length == 0 ? own + functions : own + " · " + functions;
            }
        }

        public string StateText
        {
            get
            {
                string functions = FunctionSummary;
                if (Device.Code == 0 && functions.Length > 0)
                    return functions + ". Windows détecte le périphérique parent.";
                string own = Device.Code == 0 ? "Windows ne signale aucun problème sur cet appareil."
                    : Advice.For(Device).Title + (Device.Code > 0 ? " (code Windows " + Device.Code + ")." : ".");
                return functions.Length == 0 ? own : own + " " + functions + ".";
            }
        }

        public string KindLabel
        {
            get
            {
                switch (Device.Kind)
                {
                    case UsbKind.Controller: return ControllerNumber == 0 ? "Contrôleur USB" : "Contrôleur USB " + ControllerNumber;
                    case UsbKind.RootHub: return "Hub racine";
                    case UsbKind.Hub: return "Hub USB";
                    default: return "Périphérique USB";
                }
            }
        }

        public string Title
        {
            get
            {
                if (Device.Usb != null && !string.IsNullOrWhiteSpace(Device.Usb.Product)) return Device.Usb.Product;
                if (!string.IsNullOrWhiteSpace(Device.Name) && !string.Equals(Device.Name.Trim(), Device.Id, StringComparison.OrdinalIgnoreCase)) return Device.Name;
                return KindLabel;
            }
        }

        public string PortLabel
        {
            get
            {
                if (Device.Kind != UsbKind.Hub && Device.Kind != UsbKind.Device) return "";
                return Port.HasValue ? "Port " + Port.Value : "Port inconnu";
            }
        }

        public bool AttachTo(UsbNode parent)
        {
            for (var ancestor = parent; ancestor != null; ancestor = ancestor.Parent)
                if (ancestor == this) return false;
            Parent = parent;
            parent.Children.Add(this);
            return true;
        }
    }

    internal sealed class UsbTopology
    {
        public readonly List<UsbNode> Controllers = new List<UsbNode>();
        public readonly List<UsbNode> Unattached = new List<UsbNode>();
        public int HubCount { get; private set; }
        public int DeviceCount { get; private set; }

        public static UsbTopology Build(IEnumerable<Device> snapshot)
        {
            var map = new UsbTopology();
            var devices = snapshot.ToList();
            var byId = devices.Where(d => !string.IsNullOrEmpty(d.Id)).ToLookup(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var nodes = devices.Where(d => d.Kind != UsbKind.None).ToDictionary(d => d, d => new UsbNode(d));
            foreach (var function in devices.Where(d => UsbEvidence.CompositeInterface(d.Id)))
            {
                var owner = FunctionOwner(function, byId);
                if (owner != null) nodes[owner].FunctionDevices.Add(function);
            }
            foreach (var node in nodes.Values)
            {
                if (node.Device.Kind == UsbKind.Controller)
                {
                    map.Controllers.Add(node);
                    continue;
                }
                if (node.Device.Kind == UsbKind.Device)
                {
                    map.DeviceCount++;
                    node.FunctionDevices.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
                    var labels = node.FunctionDevices.Select(d => d.Code == 0 ? UsbNode.FunctionName(d)
                        : UsbNode.FunctionName(d) + " — " + Advice.For(d).Title);
                    node.Functions.AddRange(labels.Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase));
                }
                else map.HubCount++;

                var parent = FindParent(node.Device, byId, nodes);
                if (parent == null || !node.AttachTo(parent)) map.Unattached.Add(node);
            }
            foreach (var node in nodes.Values) node.Children.Sort(CompareNodes);
            map.Controllers.Sort(CompareNodes);
            if (map.Controllers.Count > 1)
                for (int i = 0; i < map.Controllers.Count; i++) map.Controllers[i].ControllerNumber = i + 1;
            map.Unattached.Sort(CompareNodes);
            return map;
        }

        public static Device FunctionOwner(Device function, IEnumerable<Device> snapshot)
        {
            if (function.Kind != UsbKind.None || !UsbEvidence.CompositeInterface(function.Id)) return null;
            return FunctionOwner(function, snapshot.ToLookup(d => d.Id, StringComparer.OrdinalIgnoreCase));
        }

        static Device FunctionOwner(Device function, ILookup<string, Device> byId)
        {
            if (function.Kind != UsbKind.None) return null;
            var ancestor = UsbSerialPorts.FindOwner(function, byId);
            return ancestor != null && ancestor.Kind == UsbKind.Device ? ancestor : null;
        }

        static UsbNode FindParent(Device child, ILookup<string, Device> byId, Dictionary<Device, UsbNode> nodes)
        {
            var parent = UsbSerialPorts.Ancestors(child.ParentId, byId).FirstOrDefault(d => d.Kind != UsbKind.None);
            if (parent == null) return null;
            bool valid = child.Kind == UsbKind.RootHub ? parent.Kind == UsbKind.Controller
                : parent.Kind == UsbKind.RootHub || parent.Kind == UsbKind.Hub;
            return valid ? nodes[parent] : null;
        }

        static int CompareNodes(UsbNode left, UsbNode right)
        {
            int port = (left.Port ?? int.MaxValue).CompareTo(right.Port ?? int.MaxValue);
            if (port != 0) return port;
            int title = StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
            return title != 0 ? title : StringComparer.OrdinalIgnoreCase.Compare(left.Device.Id, right.Device.Id);
        }
    }
}
