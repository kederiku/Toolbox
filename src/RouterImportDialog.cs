using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AtelierPC
{
    internal sealed class RouterImportDialog
    {
        readonly Window dialog;
        readonly FrameworkElement form;
        readonly DataGrid preview;
        readonly TextBox text;
        readonly TextBlock message;
        readonly Button accept;
        List<RouterClient> entries;

        internal RouterImportDialog(Window owner, LocalNetwork network)
        {
            form = (FrameworkElement)((DataTemplate)owner.FindResource("RouterImportForm")).LoadContent();
            dialog = new Window { Title = "Lire depuis la box", Content = form, FontFamily = owner.FontFamily,
                FontSize = owner.FontSize, Background = owner.Background, Foreground = owner.Foreground,
                Width = Math.Min(790, SystemParameters.WorkArea.Width), Height = Math.Min(570, SystemParameters.WorkArea.Height),
                MinWidth = 390, MinHeight = 395, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            dialog.Resources.MergedDictionaries.Add(owner.Resources);
            if (owner.IsVisible) dialog.Owner = owner;
            preview = (DataGrid)form.FindName("BoxPreview");
            AddColumns(preview);
            text = (TextBox)form.FindName("BoxText");
            message = (TextBlock)form.FindName("BoxMessage");
            accept = (Button)form.FindName("AcceptBoxImport");
            var read = (Button)form.FindName("ReadBox");
            var paste = (Expander)form.FindName("PasteBox");
            message.Text = network.Gateways.Length == 0
                ? "Aucune passerelle indiquée. Vous pouvez saisir l’adresse locale de la box à l’ouverture ou coller sa liste."
                : "Aperçu de la liste à importer.";
            read.Click += delegate {
                try
                {
                    string captured = RouterBrowser.Capture(dialog, network);
                    if (captured != null)
                    {
                        text.Clear();
                        paste.IsExpanded = false;
                        Preview(captured);
                    }
                }
                catch (Exception)
                {
                    paste.IsExpanded = true;
                    message.Text = "L’interface de la box n’a pas pu s’ouvrir. Vous pouvez coller sa liste de clients ci-dessus.";
                }
            };
            text.TextChanged += delegate {
                entries = null;
                preview.ItemsSource = null;
                accept.IsEnabled = false;
                message.Text = "Cliquez sur « Afficher l’aperçu » pour relire les appareils reconnus.";
            };
            ((Button)form.FindName("PreviewBoxText")).Click += delegate { Preview(text.Text); };
            accept.Click += delegate { if (entries != null && entries.Count > 0) dialog.DialogResult = true; };
        }

        internal static List<RouterClient> Read(Window owner, LocalNetwork network)
        {
            var importer = new RouterImportDialog(owner, network);
            return importer.dialog.ShowDialog() == true ? importer.entries : null;
        }

        void Preview(string source)
        {
            entries = RouterClientList.Parse(source);
            preview.ItemsSource = entries;
            accept.IsEnabled = entries.Count > 0;
            if (entries.Count == 0)
            {
                message.Text = "Aucun appareil reconnu. Affichez la liste des clients avec leurs adresses ou collez un tableau avec ses en-têtes.";
                return;
            }
            int offline = entries.Count(c => c.Active == false);
            message.Text = entries.Count + (entries.Count == 1 ? " ligne reconnue." : " lignes reconnues.") +
                (offline == 0 ? "" : " Lignes hors ligne écartées à l’importation : " + offline + ".") +
                " L’importation remplacera le précédent relevé de la box.";
        }

        internal static void AddColumns(DataGrid grid)
        {
            AddColumn(grid, "Nom dans la box", "Name", "Name", 145);
            AddColumn(grid, "Adresse IP", "Address", "AddressSortKey", 115);
            AddColumn(grid, "Adresse MAC", "Mac", "Mac", 152);
            AddColumn(grid, "État dans la box", "StateLabel", "StateLabel", 115);
        }

        static void AddColumn(DataGrid grid, string title, string field, string sort, int minimum)
        {
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(field) { Mode = BindingMode.OneWay },
                SortMemberPath = sort, Width = new DataGridLength(field == "Name" ? 1.4 : 1, DataGridLengthUnitType.Star), MinWidth = minimum });
        }
    }
}
