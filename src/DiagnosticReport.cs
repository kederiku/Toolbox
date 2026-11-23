using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AtelierPC
{
    internal static class DiagnosticReport
    {
        const string GenericName = "Périphérique";
        static readonly Regex Identifier = new Regex(
            @"\b(?:S/N|serial(?: number)?|numéro de série)\b|\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string WindowsName(int build)
        {
            return build >= 22000 ? "Windows 11" : "Windows 10";
        }

        static string DeviceLabel(Device device)
        {
            // Friendly names can be edited by a user; the local UI also falls back to the instance ID.
            string model = device.Model;
            if (string.IsNullOrWhiteSpace(model) || model.Contains("\\") || model.Any(char.IsControl)
                || model.IndexOf(Environment.MachineName, StringComparison.OrdinalIgnoreCase) >= 0
                || Identifier.IsMatch(model)) return GenericName;

            string id = device.Id ?? "";
            string instance = id.Substring(id.LastIndexOf('\\') + 1);
            if (instance.EndsWith("&0", StringComparison.Ordinal))
                instance = instance.Substring(0, instance.Length - 2);
            if (instance.Length > 0 && model.IndexOf(instance, StringComparison.OrdinalIgnoreCase) >= 0)
                return GenericName;
            return model.Trim();
        }

        static string Timestamp(DateTime date)
        {
            return date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        public static string Reactivation(DateTime date, Device device, string result)
        {
            return "Dernière demande de réactivation du " + Timestamp(date) + Environment.NewLine
                + DeviceLabel(device) + " — " + result;
        }

        public static string Findings(List<Device> devices)
        {
            if (devices == null) return "Analyse incomplète. Windows n’a pas fourni l’inventaire.";
            var lines = new List<string> { "Périphériques détectés : " + devices.Count + "." };
            foreach (var group in devices.Where(d => d.Code != 0).GroupBy(d => new { d.Code, Advice.For(d).Title }))
            {
                string code = group.Key.Code > 0 ? " (code " + group.Key.Code + ")" : "";
                lines.Add(group.Count() + " × " + group.Key.Title + code + ".");
            }
            if (lines.Count == 1) lines.Add("Aucun problème signalé par Windows.");
            return string.Join(Environment.NewLine, lines);
        }

        public static string Format(int build, DateTime date, List<Device> devices, string lastRepair, string lastUsbTest = null)
        {
            var text = new StringBuilder();
            var version = typeof(DiagnosticReport).Assembly.GetName().Version;
            text.AppendLine("Toolbox " + version.ToString(3) + " — Diagnostic des périphériques");
            text.AppendLine(WindowsName(build) + " (build " + build + ")");
            text.AppendLine("Analyse du " + Timestamp(date));
            text.AppendLine();
            if (devices == null)
                text.AppendLine("Analyse incomplète. Windows n’a pas fourni l’inventaire des périphériques.");
            else
            {
                text.AppendLine("Périphériques détectés : " + devices.Count);
                text.AppendLine("Avec un problème signalé : " + devices.Count(d => d.HasProblem));
                text.AppendLine("État illisible : " + devices.Count(d => d.Code == Device.Unreadable));
                if (devices.All(d => d.Code == 0)) text.AppendLine("Aucun problème signalé par Windows.");
                foreach (Device device in devices.Where(d => d.Code != 0))
                {
                    Advice advice = Advice.For(device);
                    string code = device.Code > 0 ? " (code " + device.Code + ")" : "";
                    text.AppendLine();
                    text.AppendLine(DeviceLabel(device) + " — " + advice.Title + code);
                    text.AppendLine(advice.Cause);
                    if (device.Usb != null && device.Usb.PortLabel != null) text.AppendLine(device.Usb.PortLabel);
                    if (UsbEvidence.ForDevice(device) != UsbFailure.None)
                        text.AppendLine("Vérification conseillée : repérer l’accessoire par débranchement, puis comparer la prise et le câble un à un.");
                }
            }
            text.AppendLine();
            text.Append(lastRepair ?? "Aucune réactivation demandée dans Toolbox pendant cette session.");
            if (!string.IsNullOrEmpty(lastUsbTest)) text.AppendLine().AppendLine().Append(lastUsbTest);
            return text.ToString();
        }
    }
}
