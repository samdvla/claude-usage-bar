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

    public TrayController()
    {
        _tray.Visible = true;
        _tray.Text = "Claude usage";
        ApplyIcon(null);

        _autostart = new ToolStripMenuItem("Auto-start at login", null, (_, _) => ToggleAutostart())
        { Checked = IsAutostartEnabled() };
        _menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => Refresh()));
        _menu.Items.Add(_autostart);
        _menu.Items.Add(new ToolStripMenuItem("Open in Terminal", null, (_, _) => OpenTerminal()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()));

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleFlyout();
            else if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        };

        _timer.Interval = RefreshMs;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
        PromoteToTaskbar();
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
                _tray.Visible = true;
            }
        }
        catch { /* best effort */ }
    }

    private void ToggleFlyout()
    {
        _theme.Refresh();
        if (_flyout is { Visible: true }) { _flyout.Hide(); return; }
        _flyout ??= BuildFlyout();
        _flyout.Render(_last);
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

    private async void Refresh()
    {
        RateUsage u;
        try { u = await _probe.FetchAsync(); }
        catch { u = RateUsage.Transient(); }
        _last = u;
        ApplyIcon(u);
        if (_flyout is { Visible: true }) _flyout.Render(u);
    }

    private void ApplyIcon(RateUsage? u)
    {
        double? util = u?.State == UsageState.Ok ? u.Util5h : null;
        var newIcon = IconRenderer.Render(util, _theme.Dark);
        _tray.Icon = newIcon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
        _tray.Text = TooltipFor(u);
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

    public void Dispose()
    {
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        _flyout?.Dispose();
        _menu.Dispose();
    }
}
