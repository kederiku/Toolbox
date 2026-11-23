using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        readonly WindowsIntegrity integrity;
        StackPanel integrityChoices, integrityLogArea;
        Border integrityFeedback;
        TextBlock integrityTitle, integrityDetail;
        bool integrityWindowClosed;

        NavigationPage CreateIntegrityPage()
        {
            var view = Get<StackPanel>("IntegrityView");
            integrityTitle = Text("", 16, true, "TextBrush");
            integrityDetail = Text("", 14, false, "MutedBrush");
            integrityDetail.Margin = new Thickness(0, 4, 0, 0);
            var feedback = new StackPanel();
            feedback.Children.Add(integrityTitle);
            feedback.Children.Add(integrityDetail);
            integrityLogArea = new StackPanel();
            feedback.Children.Add(integrityLogArea);
            integrityFeedback = Card(feedback);
            integrityFeedback.Background = Ink("AccentSoftBrush");
            integrityFeedback.Margin = new Thickness(0, 0, 0, 10);
            integrityFeedback.Visibility = Visibility.Collapsed;
            view.Children.Add(integrityFeedback);

            integrityChoices = new StackPanel();
            view.Children.Add(integrityChoices);
            var verifyFiles = IntegrityButton("IntegrityVerifyFiles", IntegrityCommand.VerifyFiles);
            AddIntegrityCard("Fichiers système protégés (SFC)",
                "Vérifiez les fichiers utilisés par Windows. Si le contrôle signale des fichiers endommagés, SFC peut tenter de les remplacer.",
                verifyFiles, IntegrityButton("IntegrityRepairFiles", IntegrityCommand.RepairFiles));
            AddIntegrityCard("Magasin de composants (DISM)",
                "Vérifiez la réserve de composants utilisée par Windows pour ses mises à jour et ses réparations. DISM peut la restaurer si elle est endommagée.",
                IntegrityButton("IntegrityVerifyStore", IntegrityCommand.VerifyStore), IntegrityButton("IntegrityRepairStore", IntegrityCommand.RepairStore));

            Window.Closing += (sender, e) => {
                if (e.Cancel || !integrity.IsRunning) return;
                e.Cancel = MessageBox.Show(Window,
                    "La commande Windows a sa propre console. Quitter Toolbox avant de connaître le résultat ?",
                    "Quitter Toolbox", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes;
            };
            Window.Closed += delegate { integrityWindowClosed = true; };
            return new NavigationPage("IntegrityNav", view, "Intégrité de Windows", "\uE83D",
                "Choisissez une vérification. Windows ouvre une console pour afficher la progression et peut demander les droits administrateur.") {
                Category = "Système", OpenLabel = "Ouvrir les vérifications", PrimaryAction = verifyFiles
            };
        }

        void AddIntegrityCard(string title, string description, Button verify, Button repair)
        {
            var contents = new StackPanel();
            contents.Children.Add(Text(title, 19, true, "TextBrush"));
            var explanation = Text(description, 14, false, "MutedBrush");
            explanation.Margin = new Thickness(0, 5, 0, 0);
            contents.Children.Add(explanation);
            var actions = new WrapPanel();
            verify.Style = (Style)Window.FindResource("PrimaryAction");
            actions.Children.Add(verify);
            actions.Children.Add(repair);
            contents.Children.Add(actions);
            var card = Card(contents);
            card.Margin = new Thickness(0, 0, 0, 10);
            integrityChoices.Children.Add(card);
        }

        Button IntegrityButton(string name, IntegrityCommand command)
        {
            var button = ActionButton(command.Label, async delegate {
                if (integrity.IsRunning || integrityWindowClosed) return;
                integrityChoices.IsEnabled = false;
                integrityTitle.Text = command.Title;
                integrityDetail.Text = "En attente du lancement de la commande Windows…";
                integrityLogArea.Children.Clear();
                integrityFeedback.Visibility = Visibility.Visible;
                try
                {
                    var outcome = await integrity.RunAsync(command, new WindowInteropHelper(Window).Handle, choice =>
                        MessageBox.Show(Window, choice.Confirmation, choice.Title, MessageBoxButton.YesNo,
                            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes, detail => {
                            if (integrityWindowClosed) return;
                            integrityDetail.Text = detail;
                            RecordIntervention(command.Title, detail);
                        });
                    if (!integrityWindowClosed && outcome != IntegrityOutcome.NotStarted) AddIntegrityLog(command);
                }
                finally { if (!integrityWindowClosed) integrityChoices.IsEnabled = true; }
            });
            button.Name = name;
            Window.RegisterName(name, button);
            AutomationProperties.SetAutomationId(button, name);
            AutomationProperties.SetName(button, command.Label);
            return button;
        }

        void AddIntegrityLog(IntegrityCommand command)
        {
            var explanation = Text("Ce journal peut couvrir plusieurs opérations. Les dernières lignes s’affichent ici en lecture seule et restent hors du rapport d’intervention.", 13, false, "MutedBrush");
            explanation.Margin = new Thickness(0, 8, 0, 0);
            var excerpt = new TextBox {
                IsReadOnly = true, Visibility = Visibility.Collapsed, Height = 162, Margin = new Thickness(0, 8, 0, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            AutomationProperties.SetName(excerpt, "Fin du journal " + command.LogName);
            var button = ActionButton("Lire le journal " + command.LogName, async (sender, e) => {
                var clicked = (Button)sender;
                clicked.IsEnabled = false;
                excerpt.Text = "Lecture du journal…";
                excerpt.Visibility = Visibility.Visible;
                string detail = await Task.Run(() => integrity.ReadLog(command));
                if (integrityWindowClosed) return;
                excerpt.Text = detail;
                excerpt.ScrollToEnd();
                clicked.IsEnabled = true;
            });
            integrityLogArea.Children.Add(explanation);
            integrityLogArea.Children.Add(button);
            integrityLogArea.Children.Add(excerpt);
        }
    }
}
