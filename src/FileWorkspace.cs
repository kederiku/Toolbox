using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AtelierPC
{
    internal static class FileWorkspace
    {
        internal static void BindLayout(Window owner, string prefix, ListBox files)
        {
            var workspace = (Grid)owner.FindName(prefix + "Workspace");
            var source = (Border)owner.FindName(prefix + "SourcePane");
            var settings = (Border)owner.FindName(prefix + "Settings");
            var art = (Grid)owner.FindName(prefix + "Artwork");
            files.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) {
                var inner = files.Template.FindName("PART_ScrollViewer", files) as ScrollViewer;
                if (inner == null || e.Delta == 0) return;
                if (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight) return;
                var parent = (UIElement)files.Parent;
                e.Handled = true;
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) {
                    RoutedEvent = Mouse.MouseWheelEvent
                });
            };
            workspace.SizeChanged += delegate(object sender, SizeChangedEventArgs e) {
                if (!e.WidthChanged || e.NewSize.Width <= 0) return;
                bool compact = e.NewSize.Width < 650;
                workspace.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(settings, compact ? 0 : 1);
                Grid.SetRow(settings, compact ? 1 : 0);
                source.Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 14, 0);
                art.Tag = compact ? "Compact" : null;
            };
        }
    }
}
