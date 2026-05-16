using System.Drawing;
using System.Drawing.Drawing2D;
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

    /// <summary>
    /// Read a PNG from disk, downscale so the longest side is at most
    /// <paramref name="maxEdge"/> px, return the re-encoded bytes. 4K windows
    /// otherwise produce ~1-2 MB base64 payloads, which slow vision LLMs down
    /// and can push them past timeouts. 1280 is a safe default for vision.
    /// </summary>
    public static byte[] ReadAndDownscalePng(string path, int maxEdge = 1280)
    {
        if (maxEdge <= 0)
            return File.ReadAllBytes(path);

        using var src = new Bitmap(path);
        var longest = Math.Max(src.Width, src.Height);
        if (longest <= maxEdge)
            return File.ReadAllBytes(path);

        var scale = (double)maxEdge / longest;
        var newW = Math.Max(1, (int)Math.Round(src.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(src.Height * scale));

        using var dst = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, newW, newH);
        }
        using var ms = new MemoryStream();
        dst.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
