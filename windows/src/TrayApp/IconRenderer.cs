using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

public static class IconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    // Renders the 5h percent as a clean rounded "badge": a health-colored rounded
    // square with a subtle vertical gradient and a crisp white number. The color
    // conveys health at a glance; the number gives the exact value. Rendered at a
    // high resolution and downscaled by the shell so it stays sharp on any DPI.
    public static Icon Render(double? util, bool dark, int size = 64)
    {
        int pct = util is null ? 0 : (int)Math.Round(util.Value * 100);
        string text = util is null ? "—" : (pct >= 100 ? "99" : pct.ToString());
        bool maxed = pct >= 100;

        Color baseColor = util is null
            ? Color.FromArgb(120, 120, 128)                      // neutral grey when no login
            : Theme.Health(Health.Level(util.Value));

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            float pad = size * 0.06f;
            var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            float radius = size * 0.28f;

            // Soft vertical gradient (lighter top -> base) for a little depth.
            using (var path = Rounded(rect, radius))
            using (var fill = new LinearGradientBrush(rect,
                       Lighten(baseColor, 0.18f), baseColor, LinearGradientMode.Vertical))
            {
                g.FillPath(fill, path);
                // subtle inner highlight ring
                using var pen = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, size * 0.03f));
                g.DrawPath(pen, path);
            }

            // Number — white, bold, fit to digit count.
            float fontSize = text.Length >= 2 ? size * 0.50f : size * 0.66f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var textRect = new RectangleF(0, -size * 0.02f, size, size);
            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.DrawString(text, font, shadow,
                    new RectangleF(textRect.X, textRect.Y + size * 0.03f, textRect.Width, textRect.Height), fmt);
            using (var white = new SolidBrush(Color.White))
                g.DrawString(text, font, white, textRect, fmt);

            // A small "+" tick when maxed (>=100%) so 99 isn't mistaken for the cap.
            if (maxed)
            {
                using var plusFont = new Font("Segoe UI", size * 0.30f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var white = new SolidBrush(Color.White);
                g.DrawString("+", plusFont, white,
                    new RectangleF(size * 0.52f, size * 0.02f, size * 0.5f, size * 0.5f), fmt);
            }
        }

        IntPtr hicon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Lighten(Color c, float amount)
    {
        int R = (int)(c.R + (255 - c.R) * amount);
        int G = (int)(c.G + (255 - c.G) * amount);
        int B = (int)(c.B + (255 - c.B) * amount);
        return Color.FromArgb(c.A, Math.Min(255, R), Math.Min(255, G), Math.Min(255, B));
    }
}
