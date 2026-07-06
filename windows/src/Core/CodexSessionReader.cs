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
                            up.ValueKind == JsonValueKind.Number ? up.GetDouble() : null;
                long? r = w.TryGetProperty("resets_at", out var ra) &&
                          ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : null;
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

    private IEnumerable<string> SessionFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        int yielded = 0;
        foreach (var y in Directory.GetDirectories(_root).OrderDescending())
        foreach (var m in Directory.GetDirectories(y).OrderDescending())
        foreach (var d in Directory.GetDirectories(m).OrderDescending())
        {
            var files = Directory.GetFiles(d, "rollout-*.jsonl")
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
                if (!rootEl.TryGetProperty("payload", out var payload) ||
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
        return DateTimeOffset.TryParse(ts, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToUnixTimeSeconds() : null;
    }
}
