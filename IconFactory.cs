using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RIR_PluginManager
{
    // Иконка рисуется в коде, чтобы не возиться с файлами ресурсов.
    internal static class IconFactory
    {
        public static ImageSource MakePlay(int size)
        {
            double s = size;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, s, s), s * 0.18, s * 0.18);
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(s * 0.36, s * 0.24), true, true);
                    g.LineTo(new Point(s * 0.36, s * 0.76), true, false);
                    g.LineTo(new Point(s * 0.78, s * 0.50), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(Brushes.White, null, tri);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        public static ImageSource Make(int size)
        {
            double s = size;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, s, s), s * 0.18, s * 0.18);

                var pen = new Pen(Brushes.White, Math.Max(1.0, s / 12.0));
                for (int i = 0; i < 3; i++)
                {
                    double y = s * (0.28 + i * 0.22);
                    dc.DrawRectangle(Brushes.White, null, new Rect(s * 0.16, y - s * 0.06, s * 0.12, s * 0.12));
                    dc.DrawLine(pen, new Point(s * 0.38, y), new Point(s * 0.84, y));
                }
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
