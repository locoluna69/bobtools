using System.Drawing;
using System.Windows;
using System.Windows.Media;

namespace BobTools.Utils;

internal static class WpfDpiHelper
{
    public static Rect PixelsToDips(Visual visual, Rectangle pixelBounds)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new Rect(
            pixelBounds.X / dpi.DpiScaleX,
            pixelBounds.Y / dpi.DpiScaleY,
            pixelBounds.Width / dpi.DpiScaleX,
            pixelBounds.Height / dpi.DpiScaleY);
    }

    public static System.Windows.Point PixelsToDips(Visual visual, int x, int y)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new System.Windows.Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    public static System.Drawing.Point DipsToPixels(Visual visual, System.Windows.Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new System.Drawing.Point(
            (int)Math.Round(point.X * dpi.DpiScaleX),
            (int)Math.Round(point.Y * dpi.DpiScaleY));
    }

    public static System.Windows.Size PixelsToDipsSize(Visual visual, int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new System.Windows.Size(width / dpi.DpiScaleX, height / dpi.DpiScaleY);
    }
}