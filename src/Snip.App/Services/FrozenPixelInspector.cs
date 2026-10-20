using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Services;

public sealed record PixelReading(PixelPoint Position, string Hex);

/// <summary>Physical desktop coordinates and original, unshaded frozen pixels.</summary>
public sealed class FrozenPixelInspector(DesktopFrame frame)
{
    private readonly byte[] pixel = new byte[4];
    public PixelReading? Read(PixelPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !frame.Displays.Any(d => point.X >= d.Bounds.X && point.X < d.Bounds.Right && point.Y >= d.Bounds.Y && point.Y < d.Bounds.Bottom)) return null;
        int x = (int)Math.Floor(point.X - frame.Bounds.X), y = (int)Math.Floor(point.Y - frame.Bounds.Y);
        if (x < 0 || y < 0 || x >= frame.Image.PixelWidth || y >= frame.Image.PixelHeight) return null;
        var rect = new Int32Rect(x, y, 1, 1);
        if (frame.Image.Format == PixelFormats.Bgr32 || frame.Image.Format == PixelFormats.Bgra32 || frame.Image.Format == PixelFormats.Bgr24)
            frame.Image.CopyPixels(rect, pixel, 4, 0);
        else
            new FormatConvertedBitmap(new CroppedBitmap(frame.Image, rect), PixelFormats.Bgra32, null, 0).CopyPixels(pixel, 4, 0);
        return new(new(frame.Bounds.X + x, frame.Bounds.Y + y), $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}");
    }
}
