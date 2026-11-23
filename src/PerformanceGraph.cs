using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AtelierPC
{
    internal sealed class PerformanceGraph : FrameworkElement
    {
        const double WindowSeconds = 60;
        struct Reading { internal double Seconds; internal double? Percent; }
        readonly List<Reading> readings = new List<Reading>();
        readonly Pen grid, trace;
        readonly Brush fill;

        internal PerformanceGraph(Brush accent, Brush gridBrush)
        {
            grid = new Pen(gridBrush, 0.65);
            trace = new Pen(accent, 1.8) { LineJoin = PenLineJoin.Round };
            fill = accent.Clone();
            fill.Opacity = 0.12;
            fill.Freeze();
            ClipToBounds = true;
            IsHitTestVisible = false;
        }

        internal void Clear()
        {
            readings.Clear();
            InvalidateVisual();
        }

        internal void Append(double seconds, double? percent)
        {
            readings.RemoveAll(p => p.Seconds < seconds - WindowSeconds);
            readings.Add(new Reading { Seconds = seconds, Percent = percent });
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            const double inset = 2.5;
            double width = ActualWidth - 2 * inset, height = ActualHeight - 2 * inset;
            if (width <= 0 || height <= 0) return;
            double bottom = inset + height;
            for (int i = 0; i <= 6; i++)
            {
                double x = inset + width * i / 6;
                drawing.DrawLine(grid, new Point(x, inset), new Point(x, bottom));
            }
            for (int i = 0; i <= 4; i++)
            {
                double y = inset + height * i / 4;
                drawing.DrawLine(grid, new Point(inset, y), new Point(inset + width, y));
            }
            if (readings.Count == 0) return;
            double end = readings[readings.Count - 1].Seconds;
            var area = new StreamGeometry();
            Point? previous = null;
            using (var path = area.Open())
            {
                for (int i = 0; i < readings.Count; i++)
                {
                    var sample = readings[i];
                    if (previous.HasValue && (!sample.Percent.HasValue || sample.Seconds - readings[i - 1].Seconds > 2.5))
                    {
                        path.LineTo(new Point(previous.Value.X, bottom), false, false);
                        previous = null;
                    }
                    if (!sample.Percent.HasValue) continue;
                    var point = new Point(inset + width * (1 - (end - sample.Seconds) / WindowSeconds),
                        bottom - height * sample.Percent.Value / 100);
                    if (!previous.HasValue) path.BeginFigure(new Point(point.X, bottom), true, false);
                    path.LineTo(point, previous.HasValue, false);
                    previous = point;
                }
                if (previous.HasValue) path.LineTo(new Point(previous.Value.X, bottom), false, false);
            }
            drawing.DrawGeometry(fill, trace, area);
            if (previous.HasValue) drawing.DrawEllipse(trace.Brush, null, previous.Value, 2.4, 2.4);
        }
    }
}
