using System.Drawing;
using System.Drawing.Drawing2D;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

// A single "5h ████░░ 61%" row drawn by hand: label, rounded track, rounded
// health-colored fill, right-aligned percent. Not a stock ProgressBar.
public sealed class BarControl : Control
{
    private readonly Theme _theme;
    public string BarLabel { get; set; } = "5h";
    public double? Util { get; set; }

    public BarControl(Theme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 26;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        double u = Util ?? 0.0;
        var health = Theme.Health(Health.Level(u));

        using var labelFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        using var pctFont = new Font("Cascadia Mono", 9.5f, FontStyle.Bold);
        using var primary = new SolidBrush(_theme.Primary);
        using var healthBrush = new SolidBrush(health);

        int labelW = 30, pctW = 42, gap = 8;
        int trackX = labelW + gap;
        int trackW = Width - trackX - pctW - gap;
        int trackH = 8;
        int trackY = (Height - trackH) / 2;

        // Label
        g.DrawString(BarLabel, labelFont, primary, 0, trackY - 4);

        // Track
        using (var track = new SolidBrush(_theme.Track))
            FillRounded(g, track, new Rectangle(trackX, trackY, trackW, trackH), trackH / 2);

        // Fill
        int fillW = (int)Math.Round(Math.Clamp(u, 0, 1) * trackW);
        if (fillW > 0)
            FillRounded(g, healthBrush, new Rectangle(trackX, trackY, fillW, trackH), trackH / 2);

        // Percent (right-aligned)
        string pct = Util is null ? "?" : $"{(int)Math.Round(u * 100)}%";
        using var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString(pct, pctFont, healthBrush,
            new RectangleF(Width - pctW, 0, pctW, Height), fmt);
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        if (r.Width <= 0) return;
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { g.FillRectangle(brush, r); return; }
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
