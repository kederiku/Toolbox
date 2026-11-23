using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AtelierPC
{
    internal static partial class Tests
    {
        static ProgressBar RowProgress(ListBoxItem item)
        {
            var border = (Border)VisualTreeHelper.GetChild(item, 0);
            var presenter = (ContentPresenter)border.Child;
            return (ProgressBar)presenter.ContentTemplate.FindName("FileProgress", presenter);
        }

        static void CheckConverterLayouts()
        {
            var window = new Shell(() => new List<Device>()).Window;
            try
            {
                ShowNavigationWindow(window);
                var catalog = (FrameworkElement)window.FindName("OtherPage");
                foreach (var tool in new[] {
                    new { Prefix = "Image", Action = "Convertir une image" },
                    new { Prefix = "Video", Action = "Convertir une vidéo" },
                    new { Prefix = "Audio", Action = "Convertir un fichier audio" },
                    new { Prefix = "Metadata", Action = "Ouvrir le nettoyeur" }
                })
                {
                    window.Width = 1100;
                    UpdateNavigationLayout(window);
                    ClickVisible(FindAction(catalog, tool.Action));
                    var workspace = window.FindName(tool.Prefix + "Workspace") as Grid;
                    var source = window.FindName(tool.Prefix + "SourcePane") as Border;
                    var settings = window.FindName(tool.Prefix + "Settings") as Border;
                    Check(workspace != null && source != null && settings != null, tool.Action + " présente une source et des réglages");
                    bool metadata = tool.Prefix == "Metadata";
                    var drop = (Border)window.FindName(tool.Prefix + (metadata ? "Drop" : "DropZone"));
                    var files = new DataObject(DataFormats.FileDrop, new[] { @"C:\Photos\Jardin.jpg", @"C:\Photos\Terrasse.jpg" });
                    var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                        new object[] { files, DragDropKeyStates.None, DragDropEffects.Copy, drop, new Point() }, null);
                    args.RoutedEvent = DragDrop.DropEvent;
                    drop.RaiseEvent(args);
                    var list = (ListBox)window.FindName(tool.Prefix + (metadata ? "Items" : "BatchFiles"));
                    var view = (StackPanel)window.FindName(tool.Prefix + "View");
                    foreach (int width in new[] { 1100, 415 })
                    {
                        window.Width = width;
                        UpdateNavigationLayout(window);
                        var input = source.TransformToAncestor(workspace).TransformBounds(new Rect(source.RenderSize));
                        var options = settings.TransformToAncestor(workspace).TransformBounds(new Rect(settings.RenderSize));
                        bool wide = width == 1100;
                        Check(source.IsVisible && settings.IsVisible && (wide ? workspace.ActualWidth >= 650 : workspace.ActualWidth < 650),
                            tool.Action + " dispose de ses deux panneaux à " + width);
                        bool sideBySide = options.Left >= input.Right - 1 && options.Top < input.Bottom && input.Top < options.Bottom;
                        bool stacked = options.Top >= input.Bottom - 1 && options.Left < input.Right && input.Left < options.Right;
                        Check(wide ? sideBySide : stacked,
                            tool.Action + " adapte la disposition de ses réglages à la largeur disponible");
                        Check(options.Left >= -1 && options.Right <= workspace.ActualWidth + 1,
                            tool.Action + " contient les réglages dans la largeur de la page");
                        var formats = (ComboBox)window.FindName(tool.Prefix + "Format");
                        var save = (Button)window.FindName(tool.Prefix + "Save");
                        var controls = Descendants(settings).OfType<Control>().Where(control =>
                            control.IsVisible && (control is ComboBox || control is Button)).ToArray();
                        Check((metadata || controls.Contains(formats)) && controls.Contains(save) && controls.All(control => {
                            var bounds = control.TransformToAncestor(settings).TransformBounds(new Rect(control.RenderSize));
                            return bounds.Width > 0 && bounds.Left >= -1 && bounds.Right <= settings.ActualWidth + 1;
                        }), tool.Action + " conserve les champs et l’action de conversion dans le panneau de réglages");
                        var queue = list.TransformToAncestor(view).TransformBounds(new Rect(list.RenderSize));
                        var panes = workspace.TransformToAncestor(view).TransformBounds(new Rect(workspace.RenderSize));
                        Check(list.IsVisible && list.Items.Count == 2 && queue.Top >= panes.Bottom - 1 &&
                            Math.Abs(queue.Width - panes.Width) < 2, tool.Action + " aligne le lot sous les panneaux sur toute la largeur");
                        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                        var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                        Check(first != null && second != null && second.TranslatePoint(new Point(), list).Y >=
                            first.TranslatePoint(new Point(0, first.ActualHeight), list).Y - 1 &&
                            RowProgress(first) != null && RowProgress(second) != null,
                            tool.Action + " empile les fichiers avec une barre chacun");
                        if (wide)
                        {
                            var row = (BatchFile)list.Items[0];
                            row.Set(BatchFileState.Running, "Lecture", "");
                            row.Report(FileProgress.Conversion(42.7));
                            row.Report(FileProgress.Conversion(41));
                            UpdateNavigationLayout(window);
                            Check(RowProgress(first).Value == 42 && row.ProgressLabel == "42 %" && RowProgress(second).Value == 0,
                                tool.Action + " actualise uniquement la barre du fichier actif");
                        }
                    }
                    ClickVisible((Button)window.FindName("CrumbHome"));
                }
            }
            finally { window.Close(); }
        }
    }
}
