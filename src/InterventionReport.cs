using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;

namespace AtelierPC
{
    internal sealed partial class Shell
    {
        readonly List<string> interventionEntries = new List<string>();

        void RecordIntervention(string title, string detail)
        {
            interventionEntries.Add(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") + "  ·  " + title
                + Environment.NewLine + detail);
            if (currentPage == reportPage) ShowInterventionReport();
        }

        string InterventionText()
        {
            return "Toolbox " + typeof(Shell).Assembly.GetName().Version.ToString(3) + " — Rapport d’intervention"
                + Environment.NewLine + DiagnosticReport.WindowsName(build) + " (build " + build + ")"
                + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, interventionEntries);
        }

        void ShowInterventionReport()
        {
            bool hasEntries = interventionEntries.Count > 0;
            Get<Button>("CopyReport").IsEnabled = hasEntries;
            Get<TextBlock>("ReportFeedback").Text = "";
            Get<TextBox>("ReportText").Text = hasEntries ? InterventionText()
                : "Aucun contrôle effectué dans cette session. Choisissez un symptôme sur l’accueil, puis lancez la vérification.";
        }

        void CopyInterventionReport()
        {
            if (interventionEntries.Count == 0) return;
            var feedback = Get<TextBlock>("ReportFeedback");
            try
            {
                copyText(InterventionText());
                feedback.Text = "Rapport copié. Vous pouvez le coller dans un document ou un message.";
            }
            catch (ExternalException)
            {
                feedback.Text = "Le presse-papiers est indisponible. Patientez un instant, puis réessayez.";
            }
        }
    }
}
