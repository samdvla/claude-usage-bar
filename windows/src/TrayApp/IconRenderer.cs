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

    // Renders the integer percent (e.g. "61") tinted by health onto a transparent
    // square icon. Caller must Dispose the returned Icon (which also destroys the
    // underlying HICON via DestroyHandle).
    public static Icon Render(double? util, bool dark, int size = 32)
    {
        int pct = util is null ? 0 : (int)Math.Round(util.Value * 100);
        string text = util is null ? "–" : pct.ToString();
        Color color = util is null
            ? (dark ? Color.Gainsboro : Color.DimGray)
            : Theme.Health(Health.Level(util.Value));

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Fit font to the number length so 2-3 digits stay readable.
            float fontSize = text.Length >= 3 ? size * 0.42f : size * 0.62f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), fmt);
        }

        IntPtr hicon = bmp.GetHicon();
        try
        {
            // Clone into a managed Icon so we can destroy the native handle now.
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }
}
