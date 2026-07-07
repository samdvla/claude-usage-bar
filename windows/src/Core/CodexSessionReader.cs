using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeUsage.Core;

// Reads OpenAI Codex usage from its local session logs. Codex CLI appends a
// rate_limits snapshot to ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl on
// every turn; the newest snapshot is normalized into RateUsage. A window whose
// resets_at is already past reports 0.0 (snapshot predates the current
// window) and drops its reset.
public sealed class CodexSessionReader
{
    private const int TailBytes = 64 * 1024;
    private const int MaxFiles = 3;
    private readonly string _root;

    public CodexSessionReader(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "sessions");

    public RateUsage Read(long nowEpoch)
    {
        foreach (var file in SessionFiles())
        {
            var parsed = LastRateLimits(file);
            if (parsed is null) continue;
            var (rl, ts) = parsed.Value;

            (double? util, long? reset) Window(JsonElement rlEl, string key)
            {
                if (!rlEl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
                    return (null, null);
                double? u = w.TryGetProperty("used_percent", out var up) &&
                            up.ValueKind == JsonValueKind.Number &&
                            up.TryGetDouble(out var ud) ? ud : null;
                // resets_at is normally a whole-second epoch, but tolerate a
                // fractional value (e.g. "1783300000.5") the same way the
                // Python reader's dynamic typing does implicitly.
                long? r = null;
                if (w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                {
                    if (ra.TryGetInt64(out var l)) r = l;
                    else if (ra.TryGetDouble(out var dbl)) r = (long)Math.Floor(dbl);
                }
                if (r is not null && nowEpoch > r) { u = 0.0; r = null; }
                return (u is null ? null : Math.Round(u.Value / 100.0, 4), r);
            }

            var (u5, r5) = Window(rl, "primary");
            var (u7, r7) = Window(rl, "secondary");
            string? plan = rl.TryGetProperty("plan_type", out var p) &&
                           p.ValueKind == JsonValueKind.String && p.GetString()!.Length > 0
                ? char.ToUpper(p.GetString()![0]) + p.GetString()![1..] : null;
            long? asOf = ParseIso(ts);
            return new RateUsage(UsageState.Ok, u5, u7, r5, r7, plan,
                                 Provider.Codex, asOf);
        }
        return RateUsage.NoData(Provider.Codex);
    }

    // Directory.GetDirectories/GetFiles throw on a permission-denied or
    // vanished-mid-walk entry; since SessionFiles is an iterator (can't
    // yield inside a try/catch), enumeration failures are isolated here and
    // degrade to "nothing under this node" instead of aborting the whole
    // walk (parity with the Python reader's per-level `except OSError`).
    private static string[] SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        { return Array.Empty<string>(); }
    }

    private static string[] SafeFiles(string path, string pattern)
    {
        try { return Directory.GetFiles(path, pattern); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        { return Array.Empty<string>(); }
    }

    private static bool IsAllDigits(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 0 && name.All(char.IsDigit);
    }

    private IEnumerable<string> SessionFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        int yielded = 0;
        // Top-level dirs must be all-digit (year) names — parity with the
        // Python reader's `d.isdigit()` filter — so a stray non-year entry
        // under the sessions root can't be walked as if it were one.
        foreach (var y in SafeDirs(_root).Where(IsAllDigits).OrderDescending())
        foreach (var m in SafeDirs(y).OrderDescending())
        foreach (var d in SafeDirs(m).OrderDescending())
        {
            var files = SafeFiles(d, "rollout-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (var f in files)
            {
                yield return f;
                if (++yielded >= MaxFiles) yield break;
            }
        }
    }

    private static (JsonElement, string?)? LastRateLimits(string path)
    {
        string chunk;
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, fs.Length - TailBytes), SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            chunk = sr.ReadToEnd();
        }
        catch (IOException) { return null; }

        var lines = chunk.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("\"rate_limits\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var rootEl = doc.RootElement;
                // ValueKind guards precede every TryGetProperty on a
                // non-guaranteed-object element: calling TryGetProperty on a
                // non-object JsonElement (e.g. a line whose "payload" is a
                // string or number) throws InvalidOperationException, which
                // the catch below (JsonException-only) does not catch.
                if (rootEl.ValueKind != JsonValueKind.Object ||
                    !rootEl.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("rate_limits", out var rl) ||
                    rl.ValueKind != JsonValueKind.Object ||
                    !rl.TryGetProperty("primary", out _))
                    continue;
                string? ts = rootEl.TryGetProperty("timestamp", out var t) &&
                             t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                return (rl.Clone(), ts);
            }
            catch (JsonException) { continue; }
        }
        return null;
    }

    private static long? ParseIso(string? ts)
    {
        if (ts is null) return null;
        return DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToUnixTimeSeconds() : null;
    }
}
