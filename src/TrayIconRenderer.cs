using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MouseBat;

public enum IconTone { Normal, Low, Critical, Charging, Stale }

/// <summary>Draws the percentage as the tray icon itself, so it's readable without hovering.</summary>
public static class TrayIconRenderer
{
    public static Icon Render(string text, IconTone tone, bool lightTaskbar, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var path = new GraphicsPath())
        using (var family = new FontFamily("Segoe UI"))
        using (var brush = new SolidBrush(ColorFor(tone, lightTaskbar)))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
            FitToSquare(path, size);
            graphics.FillPath(brush, path);
        }

        // Icon.FromHandle doesn't take ownership, so clone it and release the original handle.
        nint handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    // Uses the full icon height, and squeezes horizontally only if the text is too wide ("100").
    private static void FitToSquare(GraphicsPath path, int size)
    {
        var bounds = path.GetBounds();
        float scaleY = size / bounds.Height;
        float scaleX = Math.Min(scaleY, size / bounds.Width);

        using var transform = new Matrix();
        transform.Translate((size - bounds.Width * scaleX) / 2f, 0);
        transform.Scale(scaleX, scaleY);
        transform.Translate(-bounds.X, -bounds.Y);
        path.Transform(transform);
    }

    public static Color ColorFor(IconTone tone, bool lightTaskbar) => (tone, lightTaskbar) switch
    {
        (IconTone.Low, false) => Color.FromArgb(0xFF, 0xC8, 0x3D),
        (IconTone.Low, true) => Color.FromArgb(0xB3, 0x5C, 0x00),
        (IconTone.Critical, false) => Color.FromArgb(0xFF, 0x5A, 0x5A),
        (IconTone.Critical, true) => Color.FromArgb(0xC8, 0x10, 0x2E),
        (IconTone.Charging, false) => Color.FromArgb(0x6C, 0xE0, 0x6C),
        (IconTone.Charging, true) => Color.FromArgb(0x10, 0x7C, 0x10),
        (IconTone.Stale, false) => Color.FromArgb(0x9A, 0x9A, 0x9A),
        (IconTone.Stale, true) => Color.FromArgb(0x76, 0x76, 0x76),
        (_, false) => Color.White,
        (_, true) => Color.FromArgb(0x1A, 0x1A, 0x1A),
    };

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
