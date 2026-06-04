using System.Diagnostics;
using ClaudeUsage.Core;

// ccc — Claude usage readout in the terminal (Windows).
//   ccc            one-shot
//   ccc --watch    live (default 5s; -w N to change)
//   ccc --json     raw rate headers
//   ccc open       launch the tray app

EnableAnsi();

bool watch = false, json = false; int interval = 5;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--watch" or "-W": watch = true; break;
        case "-w": if (i + 1 < args.Length) int.TryParse(args[++i], out interval); break;
        case "--json" or "--raw": json = true; break;
        case "open":
            var exe = Path.Combine(AppContext.BaseDirectory, "ClaudeUsageBar.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else Console.Error.WriteLine("ccc: Claude Usage app not found");
            return 0;
        case "-h" or "--help":
            Console.WriteLine("ccc [--watch [-w N]] [--json] [open]");
            return 0;
    }
}

var probe = new Probe();

if (watch && !json)
{
    Console.CancelKeyPress += (_, e) => { Console.CursorVisible = true; };
    Console.CursorVisible = false;
    while (true)
    {
        Console.Clear();
        await RenderOnce(probe, json);
        Console.WriteLine($"\n  \x1b[2mrefreshing every {interval}s · ctrl-c to quit\x1b[0m");
        await Task.Delay(interval * 1000);
    }
}

return await RenderOnce(probe, json);

static async Task<int> RenderOnce(Probe probe, bool json)
{
    var u = await probe.FetchAsync();
    if (u.State == UsageState.NoLogin)
    { Console.Error.WriteLine("\x1b[31mccc: no Claude Code login found\x1b[0m — open Claude Code"); return 1; }
    if (u.State == UsageState.Expired)
    { Console.Error.WriteLine("\x1b[31mccc: login expired\x1b[0m — open Claude Code"); return 1; }
    if (u.State == UsageState.Transient)
    { Console.Error.WriteLine("\x1b[31mccc: couldn't read usage\x1b[0m — try again shortly"); return 1; }

    if (json)
    {
        Console.WriteLine($"{RateHeaders.Util5h}: {u.Util5h}");
        Console.WriteLine($"{RateHeaders.Util7d}: {u.Util7d}");
        Console.WriteLine($"{RateHeaders.Reset5h}: {u.Reset5h}");
        Console.WriteLine($"{RateHeaders.Reset7d}: {u.Reset7d}");
        return 0;
    }

    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string plan = string.IsNullOrEmpty(u.Plan) ? "" : $"  \x1b[2m· {u.Plan}\x1b[0m";
    Console.WriteLine($"\n  \x1b[1;36mclaude usage\x1b[0m{plan}\n");
    Console.WriteLine("  5h  " + Bar(u.Util5h) + "  " + Pct(u.Util5h));
    Console.WriteLine("  7d  " + Bar(u.Util7d) + "  " + Pct(u.Util7d));
    Console.WriteLine($"\n  \x1b[2m5h resets {Countdown.Format(u.Reset5h, now)}\x1b[0m\n");
    return 0;
}

static string Bar(double? util)
{
    double u = util ?? 0;
    int width = 14, filled = (int)Math.Round(Math.Clamp(u, 0, 1) * width);
    string color = Health.Level(u) switch
    { HealthLevel.Red => "\x1b[31m", HealthLevel.Orange => "\x1b[33m", _ => "\x1b[32m" };
    return color + new string('█', filled) + "\x1b[2m" + new string('░', width - filled) + "\x1b[0m";
}

static string Pct(double? util)
{
    double u = util ?? 0;
    string color = Health.Level(u) switch
    { HealthLevel.Red => "\x1b[31m", HealthLevel.Orange => "\x1b[33m", _ => "\x1b[32m" };
    return $"{color}{(int)Math.Round(u * 100),3}%\x1b[0m";
}

static void EnableAnsi()
{
    try
    {
        var h = GetStdHandle(-11);
        if (GetConsoleMode(h, out uint m)) SetConsoleMode(h, m | 0x0004);
    }
    catch { }
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern IntPtr GetStdHandle(int n);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool GetConsoleMode(IntPtr h, out uint mode);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool SetConsoleMode(IntPtr h, uint mode);
