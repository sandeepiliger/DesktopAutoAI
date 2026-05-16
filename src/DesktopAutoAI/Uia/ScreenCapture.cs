using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace DesktopAutoAI.Uia;

[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    public static void CapturePng(Rectangle bounds, string outPath)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException(
                $"Invalid capture bounds: {bounds.Width}x{bounds.Height}", nameof(bounds));

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(
                sourceX: bounds.Left,
                sourceY: bounds.Top,
                destinationX: 0,
                destinationY: 0,
                blockRegionSize: bounds.Size,
                copyPixelOperation: CopyPixelOperation.SourceCopy);
        }
        bmp.Save(outPath, ImageFormat.Png);
    }
}
