using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingBrush = System.Drawing.SolidBrush;
using DrawingColor = System.Drawing.Color;
using DrawingFormat = System.Drawing.Imaging.PixelFormat;

namespace AtelierPC
{
    internal enum WatermarkPosition { Center, TopLeft, TopRight, BottomLeft, BottomRight }

    internal sealed class WatermarkOptions
    {
        internal readonly string Text;
        internal readonly bool Repeated;
        internal readonly WatermarkPosition Position;
        internal readonly double Size, Opacity;

        internal WatermarkOptions(string text, bool repeated, WatermarkPosition position, double size, double opacity)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new ArgumentException("Saisissez le texte du filigrane.");
            if (!(size >= 0 && size <= 1 && opacity >= 0 && opacity <= 1))
                throw new ArgumentOutOfRangeException("size", "La taille et l’opacité doivent être comprises entre 0 et 100 %.");
            Text = text;
            Repeated = repeated;
            Position = position;
            Size = size;
            Opacity = opacity;
        }
    }

    internal static class WatermarkArt
    {
        // Lay out in the same coordinates at every resolution, including the live preview.
        static Bitmap Draw(int width, int height, WatermarkOptions options)
        {
            var bitmap = new Bitmap(width, height, DrawingFormat.Format32bppPArgb);
            try
            {
                using (var canvas = Graphics.FromImage(bitmap))
                using (var font = new Font("Segoe UI", 100, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new DrawingBrush(DrawingColor.FromArgb((int)Math.Round(options.Opacity * 255), 92, 92, 92)))
                using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    canvas.PageUnit = GraphicsUnit.Pixel;
                    canvas.TextRenderingHint = TextRenderingHint.AntiAlias;
                    canvas.SmoothingMode = SmoothingMode.AntiAlias;
                    format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                    SizeF bounds = canvas.MeasureString(options.Text, font, PointF.Empty, format);
                    float shortSide = Math.Min(width, height);
                    bool diagonal = options.Repeated || options.Position == WatermarkPosition.Center;
                    double angle = diagonal ? -30 : 0;
                    double radians = Math.Abs(angle) * Math.PI / 180;
                    double bw = bounds.Width * Math.Cos(radians) + bounds.Height * Math.Sin(radians);
                    double bh = bounds.Height * Math.Cos(radians) + bounds.Width * Math.Sin(radians);
                    int columns = options.Repeated ? Math.Min(12, Math.Max(2, (int)Math.Round(width / (double)shortSide * 2))) : 1;
                    int rows = options.Repeated ? Math.Min(16, Math.Max(4, (int)Math.Round(height / (double)shortSide * 4))) : 1;
                    float cellWidth = (float)width / columns, cellHeight = (float)height / rows;
                    double fontSize = shortSide * (options.Repeated ? .025 + .09 * options.Size : .035 + .18 * options.Size);
                    float scale = (float)Math.Min(fontSize / 100, Math.Min(cellWidth * .88 / Math.Max(1, bw), cellHeight * .85 / Math.Max(1, bh)));
                    if (options.Repeated)
                    {
                        for (int row = 0; row < rows; row++)
                            for (int column = 0; column < columns + row % 2; column++)
                                Stamp(canvas, font, brush, format, options.Text, bounds, (column + (row % 2 == 0 ? .5f : 0)) * cellWidth,
                                    (row + .5f) * cellHeight, scale, (float)angle);
                    }
                    else
                    {
                        float margin = shortSide * .045f;
                        float x = width / 2f, y = height / 2f;
                        if (!diagonal)
                        {
                            bool left = options.Position == WatermarkPosition.TopLeft || options.Position == WatermarkPosition.BottomLeft;
                            bool top = options.Position == WatermarkPosition.TopLeft || options.Position == WatermarkPosition.TopRight;
                            float dx = margin + bounds.Width * scale / 2, dy = margin + bounds.Height * scale / 2;
                            x = left ? dx : width - dx;
                            y = top ? dy : height - dy;
                        }
                        Stamp(canvas, font, brush, format, options.Text, bounds, x, y, scale, (float)angle);
                    }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        static void Stamp(Graphics canvas, Font font, DrawingBrush brush, StringFormat format, string text, SizeF bounds,
            float x, float y, float scale, float angle)
        {
            var state = canvas.Save();
            canvas.TranslateTransform(x, y);
            canvas.RotateTransform(angle);
            canvas.ScaleTransform(scale, scale);
            canvas.DrawString(text, font, brush, -bounds.Width / 2, -bounds.Height / 2, format);
            canvas.Restore(state);
        }

        internal static BitmapSource Render(int width, int height, WatermarkOptions options)
        {
            using (var bitmap = Draw(width, height, options))
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, DrawingFormat.Format32bppPArgb);
                try
                {
                    var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, checked(data.Stride * height), data.Stride);
                    source.Freeze();
                    return source;
                }
                finally { bitmap.UnlockBits(data); }
            }
        }

        internal static void WritePng(string path, int width, int height, WatermarkOptions options)
        {
            using (var bitmap = Draw(width, height, options))
                bitmap.Save(path, ImageFormat.Png);
        }
    }

    internal abstract class WatermarkSource : IDisposable
    {
        internal double Width, Height;
        internal BitmapSource Preview;
        internal string Extension, Notice;
        internal abstract void WriteCopy(string temporary, WatermarkOptions options, CancellationToken token);
        public virtual void Dispose() { }

        internal void Save(string path, string destination, bool overwrite, WatermarkOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            destination = Path.GetFullPath(destination);
            if (String.Equals(path, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choisissez un autre nom pour conserver le fichier d’origine.");
            if (!String.Equals(Extension, Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Utilisez l’extension " + Extension + " pour cette copie.");
            using (var original = File.OpenRead(path))
            {
                if (!overwrite && File.Exists(destination)) throw new IOException("Un fichier existe déjà à cet emplacement.");
                MetadataCleaner.CheckDistinctFiles(original, destination);
                string temp = Path.Combine(Path.GetDirectoryName(destination), ".AtelierPC-" + Guid.NewGuid().ToString("N") + Extension);
                try
                {
                    WriteCopy(temp, options, token);
                    token.ThrowIfCancellationRequested();
                    if (new FileInfo(temp).Length == 0) throw new InvalidDataException("La copie produite est vide.");
                    if (overwrite && File.Exists(destination))
                    {
                        MetadataCleaner.CheckDistinctFiles(original, destination);
                        File.Replace(temp, destination, null);
                    }
                    else File.Move(temp, destination);
                }
                finally { File.Delete(temp); }
            }
        }
    }
}
