using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        void ShowUsbGuidance(UsbNode node)
        {
            var panel = Get<StackPanel>("MapGuidance");
            panel.Children.Clear();
            panel.Visibility = node != null && node.NeedsAttention ? Visibility.Visible : Visibility.Collapsed;
            if (panel.Visibility != Visibility.Visible) return;

            if (node.Device.Code != 0)
                AddUsbAdvice(panel, node.Device, "Dépanner cet appareil");
            foreach (var function in node.FunctionDevices.Where(f => f.Code != 0))
                AddUsbAdvice(panel, function, UsbNode.FunctionName(function) + " — " + Advice.For(function).Title +
                    (function.Code > 0 ? " (code Windows " + function.Code + ")" : ""));
        }

        void AddUsbAdvice(StackPanel panel, Device device, string heading)
        {
            var title = Text(heading, 15, true, "TextBrush");
            title.Margin = new Thickness(0, 10, 0, 3);
            panel.Children.Add(title);
            var advice = Advice.For(device);
            panel.Children.Add(Text(advice.Cause, 14, false, "MutedBrush"));
            bool guided = UsbEvidence.ForDevice(device) != UsbFailure.None && UsbEvidence.PhysicalEntry(device);
            if (guided)
            {
                panel.Children.Add(ActionButton("Retrouver cet appareil", async delegate {
                    await OpenPageAsync(diagnosticPage);
                    await StartUsbAsync(device);
                }));
            }
            var steps = new StackPanel { Margin = new Thickness(5, 4, 0, 0) };
            for (int i = 0; i < advice.Steps.Length; i++)
            {
                var step = Text((i + 1) + ".  " + advice.Steps[i], 14, false, "MutedBrush");
                step.Margin = new Thickness(0, 2, 0, 3);
                steps.Children.Add(step);
            }
            panel.Children.Add(new Expander { Header = "Étapes de résolution", Content = steps,
                Margin = new Thickness(0, 5, 0, 0), IsExpanded = !guided });
        }
    }
}
