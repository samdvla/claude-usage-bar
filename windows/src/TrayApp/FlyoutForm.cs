using System.Drawing;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

// Borderless popup anchored above the tray. Hosts the header, two BarControls,
// reset lines, and footer text-buttons. Closes on deactivate / Esc.
public sealed class FlyoutForm : Form
{
    private readonly Theme _theme;
    private readonly Label _header = new();
    private readonly BarControl _bar5;
    private readonly BarControl _bar7;
    private readonly Label _reset5 = new();
    private readonly Label _reset7 = new();
    private readonly Label _headerCodex = new();
    private readonly BarControl _bar5x;
    private readonly BarControl _bar7x;
    private readonly Label _resetX = new();
    private readonly Label _asOfX = new();
    private readonly LinkLabel _refresh = new();
    private readonly LinkLabel _terminal = new();

    private readonly List<Font> _fonts = new();

    public event Action? RefreshRequested;
    public event Action? OpenTerminalRequested;

    // Tracks fonts so they can be disposed with the form (Control.Font does not
    // take ownership of assigned Font instances).
    private Font F(string family, float size, FontStyle style = FontStyle.Regular)
    {
        var f = new Font(family, size, style);
        _fonts.Add(f);
        return f;
    }

    public FlyoutForm(Theme theme)
    {
        _theme = theme;
        _bar5 = new BarControl(theme) { BarLabel = "5h" };
        _bar7 = new BarControl(theme) { BarLabel = "7d" };
        _bar5x = new BarControl(theme) { BarLabel = "5h" };
        _bar7x = new BarControl(theme) { BarLabel = "7d" };

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 280;
        Height = 320;
        BackColor = theme.Panel;
        Padding = new Padding(16, 14, 16, 12);

        _header.AutoSize = false;
        _header.Dock = DockStyle.Top;
        _header.Height = 22;
        _header.Font = F("Segoe UI", 11f, FontStyle.Bold);
        _header.ForeColor = theme.Primary;

        foreach (var b in new[] { _bar5, _bar7, _bar5x, _bar7x }) b.Dock = DockStyle.Top;

        foreach (var l in new[] { _reset5, _reset7, _resetX, _asOfX })
        {
            l.Dock = DockStyle.Top;
            l.Height = 18;
            l.Font = F("Segoe UI", 9f);
            l.ForeColor = theme.Secondary;
        }

        _headerCodex.AutoSize = false;
        _headerCodex.Dock = DockStyle.Top;
        _headerCodex.Height = 30;                 // extra top gap between sections
        _headerCodex.TextAlign = ContentAlignment.BottomLeft;
        _headerCodex.Font = F("Segoe UI", 11f, FontStyle.Bold);
        _headerCodex.ForeColor = theme.Primary;

        _refresh.Text = "Refresh";
        _terminal.Text = "Open in Terminal";
        foreach (var lk in new[] { _refresh, _terminal })
        {
            lk.AutoSize = true;
            lk.Font = F("Segoe UI", 9f);
            lk.LinkColor = theme.Secondary;
            lk.ActiveLinkColor = theme.Primary;
        }
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        _terminal.Click += (_, _) => OpenTerminalRequested?.Invoke();

        var footer = new FlowLayoutPanel
        { Dock = DockStyle.Bottom, Height = 24, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0) };
        footer.Controls.Add(_refresh);
        footer.Controls.Add(_terminal);

        // Add in reverse for DockStyle.Top stacking order.
        Controls.Add(_asOfX);
        Controls.Add(_resetX);
        Controls.Add(_bar7x);
        Controls.Add(_bar5x);
        Controls.Add(_headerCodex);
        Controls.Add(_reset7);
        Controls.Add(_reset5);
        Controls.Add(_bar7);
        Controls.Add(_bar5);
        Controls.Add(_header);
        Controls.Add(footer);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.ApplyFlyoutChrome(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void Render(RateUsage claude, RateUsage? codex)
    {
        _header.Text = "Claude usage" + PlanSuffix(claude);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (claude.State)
        {
            case UsageState.NoLogin:
                ClaudeMessage("No Claude Code login found — open Claude Code."); break;
            case UsageState.Expired:
                ClaudeMessage("Login expired — open Claude Code to refresh."); break;
            case UsageState.Transient:
                ClaudeMessage("Couldn't read usage — try again shortly."); break;
            default:
                _bar5.Visible = _bar7.Visible = _reset5.Visible = _reset7.Visible = true;
                _bar5.Util = claude.Util5h; _bar7.Util = claude.Util7d;
                _bar5.Invalidate(); _bar7.Invalidate();
                _reset5.Text = $"5h resets in {Countdown.Format(claude.Reset5h, now)}";
                _reset7.Text = $"7d resets in {Countdown.Format(claude.Reset7d, now)}";
                break;
        }

        bool showCodex = codex is not null;
        _headerCodex.Visible = _bar5x.Visible = _bar7x.Visible =
            _resetX.Visible = _asOfX.Visible = showCodex;
        Height = showCodex ? 320 : 168;
        if (!showCodex) return;

        _headerCodex.Text = "Codex usage" + PlanSuffix(codex!);
        if (codex!.State == UsageState.NoData)
        {
            _bar5x.Visible = _bar7x.Visible = _asOfX.Visible = false;
            _resetX.Text = "No Codex sessions found — run codex once.";
            return;
        }
        _bar5x.Util = codex.Util5h; _bar7x.Util = codex.Util7d;
        _bar5x.Invalidate(); _bar7x.Invalidate();
        _resetX.Text = $"5h resets in {Countdown.Format(codex.Reset5h, now)}";
        long age = codex.AsOf is null ? 0 : now - codex.AsOf.Value;
        _asOfX.Text = age > 120 ? $"as of {age / 60}m ago" : "";
    }

    private void ClaudeMessage(string msg)
    {
        _bar5.Visible = _bar7.Visible = _reset7.Visible = false;
        _reset5.Visible = true;
        _reset5.Text = msg;
    }

    private static string PlanSuffix(RateUsage u) =>
        string.IsNullOrEmpty(u.Plan) ? "" : "  ·  " + Capitalize(u.Plan!);

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public void ShowAt(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Min(anchor.X - Width, wa.Right - Width - 8);
        int y = anchor.Y - Height - 8;
        if (y < wa.Top) y = wa.Top + 8;
        Location = new Point(Math.Max(wa.Left + 8, x), y);
        Show();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var f in _fonts) f.Dispose();
        base.Dispose(disposing);
    }
}
