using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static void WaitForMetadataBatch(Window window, Task work)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            timer.Tick += delegate { frame.Continue = false; };
            work.ContinueWith(t => window.Dispatcher.BeginInvoke(new Action(delegate { frame.Continue = false; })));
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Check(work.IsCompleted, "Le lot se termine et rend la main à la fenêtre");
            work.GetAwaiter().GetResult();
        }

        static void CheckMetadataBatch()
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AtelierPC-lot-" + Guid.NewGuid().ToString("N")));
            var previous = SynchronizationContext.Current;
            Window window;
            using (var xaml = typeof(Shell).Assembly.GetManifestResourceStream("AtelierPC.Fenetre.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(xaml);
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
                string first = Path.Combine(root.CreateSubdirectory("A").FullName, "photo.jpg");
                string other = Path.Combine(root.CreateSubdirectory("B").FullName, "photo.jpg");
                byte[] original = MetadataTestJpeg("PRIVATE-LOT");
                File.WriteAllBytes(first, original);
                File.WriteAllBytes(other, original);
                string document = Path.Combine(root.FullName, "courrier.docx");
                OfficeFixture(document, ".docx", "");
                string broken = Path.Combine(root.FullName, "illisible.pdf");
                File.WriteAllText(broken, "PDF invalide");
                string unsupported = Path.Combine(root.FullName, "note.txt");
                File.WriteAllText(unsupported, "document texte");
                string dest = root.CreateSubdirectory("copies").FullName;
                string preexisting = Path.Combine(dest, "photo-nettoye.jpg");
                File.WriteAllText(preexisting, "copie à garder");

                string opened = null;
                var page = new MetadataPage(window, directory => opened = directory);
                var drop = (Border)window.FindName("MetadataDrop");
                var files = new DataObject(DataFormats.FileDrop, new[] { first, unsupported, broken, other, document, first });
                // WPF construit ces arguments en interne lorsqu’un dépôt arrive depuis l’Explorateur.
                var dropped = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    new object[] { files, DragDropKeyStates.None, DragDropEffects.Copy, drop, new Point() }, null);
                dropped.RoutedEvent = DragDrop.DropEvent;
                drop.RaiseEvent(dropped);
                var list = (ListBox)window.FindName("MetadataItems");
                var rows = list.Items.Cast<MetadataEntry>().ToArray();
                Check(rows.Length == 5 && rows[0].Name != rows[3].Name, "Les doublons sont ignorés et les homonymes identifiables");
                Check(rows[0].State == BatchFileState.Waiting && rows[1].State == BatchFileState.Failed && rows.All(row => row.Progress == 0),
                    "Dès le dépôt, chaque fichier possède une ligne avec son état initial");
                var steps = new List<string>();
                rows[0].PropertyChanged += delegate {
                    if (rows[0].State == BatchFileState.Running && rows[0].ProgressLabel.Length > 0)
                        steps.Add(rows[0].Status + " " + rows[0].ProgressLabel);
                };
                WaitForMetadataBatch(window, page.SaveBatchToAsync(dest));
                Check(steps.SequenceEqual(new[] { "Nettoyage Étape 1/3", "Vérification Étape 2/3", "Enregistrement Étape 3/3" }),
                    "La ligne de la photo signale chacune des étapes exécutées");
                Check(rows.Where(row => row.State == BatchFileState.Saved).All(row => row.Progress == 100 && row.ProgressLabel == "100 %") &&
                    rows.Where(row => row.State == BatchFileState.Failed).All(row => row.Progress < 100 && !row.IsIndeterminate),
                    "Seules les copies enregistrées atteignent 100 %");
                Check(rows[0].State == BatchFileState.Saved && rows[3].State == BatchFileState.Saved && rows[4].State == BatchFileState.Saved,
                    "Le lot mixte continue après des refus");
                Check(rows[1].State == BatchFileState.Failed && rows[2].State == BatchFileState.Failed &&
                    rows[1].Details.Length > 0 && rows[2].Details.Length > 0, "Chaque refus donne son motif");
                Check(rows[0].Details.Contains("Coordonnées GPS") && rows[0].Details.Contains("Modèle d’appareil") && rows[0].Details.Contains("Auteur") &&
                    rows[4].Details.Contains("Dernier éditeur") && !rows[4].Details.Contains("Coordonnées GPS") &&
                    rows.All(row => !row.Details.Contains("PRIVATE-")), "Chaque ligne indique ses champs, sans divulguer de valeur privée");
                Check(File.ReadAllText(preexisting) == "copie à garder" && File.Exists(Path.Combine(dest, "photo-nettoye (2).jpg")) &&
                    File.Exists(Path.Combine(dest, "photo-nettoye (3).jpg")) && File.Exists(Path.Combine(dest, "courrier-nettoye.docx")) &&
                    Directory.GetFiles(dest).Length == 4, "Noms numérotés sans écrasement ni sortie partielle");
                Check(rows[0].Details.Contains("photo-nettoye (2).jpg") && rows[3].Details.Contains("photo-nettoye (3).jpg") &&
                    File.ReadAllBytes(first).SequenceEqual(original) && File.ReadAllBytes(other).SequenceEqual(original),
                    "Chaque bilan identifie la bonne copie et les deux originaux restent intacts");
                ((Button)window.FindName("MetadataBatchFolder")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(opened == dest && ((TextBlock)window.FindName("MetadataStatus")).Text.Contains("2 refus"), "Dossier et bilan final du lot");

                string clean = Path.Combine(dest, "photo-nettoye (2).jpg");
                page.SelectFiles(new[] { clean, other, document });
                rows = list.Items.Cast<MetadataEntry>().ToArray();
                rows[1].PropertyChanged += delegate {
                    if (rows[1].State == BatchFileState.Running)
                        ((Button)window.FindName("MetadataCancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                string partial = root.CreateSubdirectory("lot annulé").FullName;
                WaitForMetadataBatch(window, page.SaveBatchToAsync(partial));
                Check(rows[0].Details.Contains("déjà") && !rows[0].Details.Contains("Coordonnées GPS") &&
                    rows[1].State == BatchFileState.Canceled && rows[2].State == BatchFileState.Skipped &&
                    Directory.GetFiles(partial).Length == 1, "Annuler garde la copie terminée et identifie les fichiers non traités");
                Check(rows[0].Progress == 100 && rows[1].Progress < 100 && !rows[1].IsIndeterminate &&
                    rows[2].Progress == 0, "Le suivi de chaque ligne se fige à l’annulation");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                window.Close();
                root.Delete(true);
            }
        }
    }
}
