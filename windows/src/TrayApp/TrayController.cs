using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

public sealed class TrayController : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeUsageBar";
    private const int RefreshMs = 120_000;

    private readonly NotifyIcon _tray = new();
    private readonly Theme _theme = new();
    private readonly Probe _probe = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _autostart;
    private FlyoutForm? _flyout;
    private Icon? _currentIcon;
    private RateUsage _last = RateUsage.Transient();

    private const string SettingsKey = @"Software\ClaudeUsageBar";
    private NotifyIcon? _trayCodex;
    private readonly CodexSessionReader _codexReader = new();
    private RateUsage _lastCodex = RateUsage.NoData(Provider.Codex);
    private Icon? _currentCodexIcon;
    private ToolStripMenuItem _showClaude = null!;
    private ToolStripMenuItem _showCodex = null!;

    public TrayController()
    {
        _tray.Visible = GetShow("ShowClaude");
        _tray.Text = "Claude usage";
        ApplyIcon(null);

        _showClaude = new ToolStripMenuItem("Show Claude icon", null, (_, _) => ToggleShow(Provider.Claude))
        { Checked = GetShow("ShowClaude") };
        _showCodex = new ToolStripMenuItem("Show Codex icon", null, (_, _) => ToggleShow(Provider.Codex))
        { Checked = GetShow("ShowCodex") };

        _autostart = new ToolStripMenuItem("Auto-start at login", null, (_, _) => ToggleAutostart())
        { Checked = IsAutostartEnabled() };
        _menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => Refresh()));
        _menu.Items.Add(_autostart);
        _menu.Items.Add(_showClaude);
        _menu.Items.Add(_showCodex);
        _menu.Items.Add(new ToolStripMenuItem("Open in Terminal", null, (_, _) => OpenTerminal()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()));

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleFlyout();
            else if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        };

        _trayCodex = new NotifyIcon { Visible = GetShow("ShowCodex"), Text = "Codex usage" };
        _trayCodex.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleFlyout();
            else if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        };
        ApplyCodexIcon(RateUsage.NoData(Provider.Codex));

        _timer.Interval = RefreshMs;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        // Defer the first probe + taskbar promotion until the message loop is
        // running, so the async Refresh continuation resumes on the WinForms UI
        // thread (no SynchronizationContext exists yet during construction).
        var init = new System.Windows.Forms.Timer { Interval = 1 };
        init.Tick += (_, _) =>
        {
            init.Stop();
            init.Dispose();
            PromoteToTaskbar();
            Refresh();
        };
        init.Start();
    }

    // Windows 11 keeps newly-added tray icons in the "^" overflow until the user
    // promotes them. We set IsPromoted=1 on our own NotifyIconSettings entry so the
    // icon sits on the taskbar like the macOS menu bar. The entry exists once the
    // icon has been added; re-toggling Visible makes the shell re-read it.
    private void PromoteToTaskbar()
    {
        try
        {
            var me = Environment.ProcessPath;
            if (me is null) return;
            using var root = Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", writable: true);
            if (root is null) return;

            bool changed = false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, writable: true);
                if (k?.GetValue("ExecutablePath") is string exe &&
                    string.Equals(exe, me, StringComparison.OrdinalIgnoreCase))
                {
                    if (k.GetValue("IsPromoted") as int? != 1)
                    {
                        k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                // Force the shell to re-read the promotion state.
                _tray.Visible = false;
                _tray.Visible = GetShow("ShowClaude");
                if (_trayCodex is not null)
                {
                    _trayCodex.Visible = false;
                    _trayCodex.Visible = GetShow("ShowCodex");
                }
            }
        }
        catch { /* best effort */ }
    }

    private void ToggleFlyout()
    {
        _theme.Refresh();
        if (_flyout is { Visible: true }) { _flyout.Hide(); return; }
        _flyout ??= BuildFlyout();
        RenderFlyout();
        _flyout.ShowAt(Cursor.Position);
        Refresh(); // probe fresh on open, like the macOS menuWillOpen_
    }

    private FlyoutForm BuildFlyout()
    {
        var f = new FlyoutForm(_theme);
        f.RefreshRequested += Refresh;
        f.OpenTerminalRequested += OpenTerminal;
        return f;
    }

    private bool _refreshing;

    private async void Refresh()
    {
        // All Refresh calls happen on the UI thread, so this guard is enough to
        // stop overlapping probes (timer + manual + flyout-open) from piling up.
        if (_refreshing) return;
        _refreshing = true;
        RateUsage u;
        try { u = await _probe.FetchAsync(); }
        catch { u = RateUsage.Transient(); }
        finally { _refreshing = false; }
        _last = u;
        try { _lastCodex = _codexReader.Read(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
        catch { _lastCodex = RateUsage.NoData(Provider.Codex); }
        ApplyIcon(u);
        ApplyCodexIcon(_lastCodex);
        if (_flyout is { Visible: true }) RenderFlyout();
    }

    private void RenderFlyout() =>
        _flyout!.Render(_last, GetShow("ShowCodex") ? _lastCodex : null);

    private void ApplyIcon(RateUsage? u)
    {
        double? util = u?.State == UsageState.Ok ? u.Util5h : null;
        var newIcon = IconRenderer.Render(util, _theme.Dark);
        _tray.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
        _tray.Text = TooltipFor(u);
    }

    private void ApplyCodexIcon(RateUsage u)
    {
        if (_trayCodex is null) return;
        double? util = u.State == UsageState.Ok ? u.Util5h : null;
        var icon = IconRenderer.Render(util, _theme.Dark, Provider.Codex);
        _trayCodex.Icon = icon;
        _currentCodexIcon?.Dispose();
        _currentCodexIcon = icon;
        _trayCodex.Text = u.State == UsageState.Ok
            ? $"Codex usage{(string.IsNullOrEmpty(u.Plan) ? "" : " · " + u.Plan)} — 5h {Pct(u.Util5h)} · 7d {Pct(u.Util7d)}"
            : "Codex usage — no session data";
    }

    private static string TooltipFor(RateUsage? u) => u?.State switch
    {
        UsageState.Ok => $"Claude usage{Plan(u)} — 5h {Pct(u.Util5h)} · 7d {Pct(u.Util7d)}",
        UsageState.Expired => "Claude usage — login expired",
        UsageState.NoLogin => "Claude usage — no login",
        _ => "Claude usage",
    };

    private static string Plan(RateUsage u) =>
        string.IsNullOrEmpty(u.Plan) ? "" : " · " + char.ToUpper(u.Plan![0]) + u.Plan[1..];
    private static string Pct(double? v) => v is null ? "?" : $"{(int)Math.Round(v.Value * 100)}%";

    private void OpenTerminal()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ccc.exe");
            var args = File.Exists(exe) ? $"/k \"{exe}\" --watch" : "/k echo ccc.exe not found";
            Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private static bool IsAutostartEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is not null;
    }

    private void ToggleAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k is null) return;
        if (IsAutostartEnabled()) k.DeleteValue(RunValue, throwOnMissingValue: false);
        else k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        _autostart.Checked = IsAutostartEnabled();
    }

    private static bool GetShow(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return (k?.GetValue(name) as int?) != 0;   // absent → 1 (on)
    }

    private static void SetShow(string name, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
        k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
    }

    private void ToggleShow(Provider p)
    {
        bool claude = GetShow("ShowClaude"), codex = GetShow("ShowCodex");
        if (p == Provider.Claude)
        {
            if (claude && !codex) return;          // would hide the last icon
            claude = !claude; SetShow("ShowClaude", claude);
        }
        else
        {
            if (codex && !claude) return;
            codex = !codex; SetShow("ShowCodex", codex);
        }
        _showClaude.Checked = claude; _showCodex.Checked = codex;
        _tray.Visible = claude;
        if (_trayCodex is not null) _trayCodex.Visible = codex;
        Refresh();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        _trayCodex?.Dispose();
        _currentCodexIcon?.Dispose();
        _flyout?.Dispose();
        _menu.Dispose();
        _probe.Dispose();
    }
}
