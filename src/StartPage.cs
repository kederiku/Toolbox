using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        void BuildStartView(NavigationPage[] tools, NavigationPage catalog)
        {
            AddSymptom("StartPerformance", "Mon PC est lent", "Processeur, mémoire et espace disque", tools.Single(t => t.NavName == "PerformanceNav"));
            AddSymptom("StartNetwork", "Internet ne fonctionne plus", "Connexion à la box et accès Internet", tools.Single(t => t.NavName == "NetworkRepairNav"));
            AddSymptom("StartDevices", "Un appareil ne fonctionne plus", "Périphériques et erreurs de pilote", diagnosticPage);
            AddSymptom("StartSpeed", "Ma connexion est lente", "Latence et débits en réception et en envoi", tools.Single(t => t.NavName == "SpeedNav"));
            Get<TextBlock>("AccessLevel").Text = WindowsIntegrity.IsElevated()
                ? "Mode administrateur" : "Mode standard";
            Get<Button>("StartCatalog").Click += async delegate { await OpenPageAsync(catalog); };
            Get<Button>("StartIntegrity").Click += async delegate { await OpenPageAsync(tools.Single(t => t.NavName == "IntegrityNav")); };
            Get<Button>("StartReport").Click += async delegate { await OpenPageAsync(reportPage); };
            Get<Button>("CopyReport").Click += delegate { CopyInterventionReport(); };
            reportPage.OnOpen = delegate { ShowInterventionReport(); return Task.CompletedTask; };
        }

        void AddSymptom(string name, string symptom, string checks, NavigationPage destination)
        {
            var row = new DockPanel();
            row.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = Ink("AccentSoftBrush"), Child = Glyph(destination.Icon, 17, "AccentBrush") });
            var lines = new StackPanel();
            lines.Children.Add(Text(symptom, 16, true, "TextBrush"));
            var caption = Text(checks, 14, false, "MutedBrush");
            caption.Margin = new Thickness(0, 3, 0, 0);
            lines.Children.Add(caption);
            row.Children.Add(lines);
            var button = new Button {
                Name = name, Content = row, Style = (Style)Window.FindResource("Action"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(16, 13, 16, 13), Margin = new Thickness(0, 0, 7, 7)
            };
            AutomationProperties.SetName(button, symptom);
            AutomationProperties.SetHelpText(button, checks);
            button.Click += async delegate {
                await OpenPageAsync(destination);
                if (currentPage == destination && destination.PrimaryAction != null) destination.PrimaryAction.Focus();
            };
            Window.RegisterName(name, button);
            Get<UniformGrid>("SymptomLinks").Children.Add(button);
        }
    }
}
