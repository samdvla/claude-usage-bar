using ClaudeUsage.Core;

namespace Core.Tests;

public class CodexSessionReaderTests : IDisposable
{
    private const long Now = 1783300000;
    private readonly string _root;

    public CodexSessionReaderTests()
        => _root = Directory.CreateTempSubdirectory("codex-sessions-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string rel, params string[] lines)
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllLines(p, lines);
        return p;
    }

    private static string Event(double u5 = 20.0, double u7 = 25.0,
        long r5 = Now + 9000, long r7 = Now + 200000,
        string plan = "plus", string ts = "2026-07-06T14:41:05.555Z") =>
        $"{{\"timestamp\":\"{ts}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{}}," +
        $"\"rate_limits\":{{\"limit_id\":\"codex\"," +
        $"\"primary\":{{\"used_percent\":{u5},\"window_minutes\":300,\"resets_at\":{r5}}}," +
        $"\"secondary\":{{\"used_percent\":{u7},\"window_minutes\":10080,\"resets_at\":{r7}}}," +
        $"\"plan_type\":\"{plan}\"}}}}}}";

    [Fact]
    public void HappyPath()
    {
        Write("2026/07/06/rollout-a.jsonl",
            "{\"timestamp\":\"x\",\"type\":\"event_msg\",\"payload\":{\"type\":\"other\"}}",
            Event());
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.20, u.Util5h!.Value, 3);
        Assert.Equal(0.25, u.Util7d!.Value, 3);
        Assert.Equal(Now + 9000, u.Reset5h);
        Assert.Equal("Plus", u.Plan);
        Assert.Equal(Provider.Codex, u.Provider);
        Assert.Equal(1783348865, u.AsOf);
    }

    [Fact]
    public void RolloverZeroing()
    {
        Write("2026/07/06/rollout-a.jsonl", Event(r5: Now - 100));
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(0.0, u.Util5h!.Value, 3);
        Assert.Null(u.Reset5h);
        Assert.Equal(0.25, u.Util7d!.Value, 3);
    }

    [Fact]
    public void MalformedLinesSkipped()
    {
        Write("2026/07/06/rollout-a.jsonl",
            Event(u5: 11.0), "{\"broken rate_limits", "not json rate_limits");
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.11, u.Util5h!.Value, 3);
    }

    [Fact]
    public void FallsBackToOlderFile()
    {
        var newer = Write("2026/07/06/rollout-new.jsonl",
            "{\"timestamp\":\"x\",\"type\":\"event_msg\",\"payload\":{}}");
        var older = Write("2026/07/05/rollout-old.jsonl", Event(u5: 33.0));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(0.33, u.Util5h!.Value, 3);
    }

    [Fact]
    public void NoDataWhenMissing()
    {
        Assert.Equal(UsageState.NoData,
            new CodexSessionReader(Path.Combine(_root, "nope")).Read(Now).State);
        Write("2026/07/06/rollout-a.jsonl", "{}");
        Assert.Equal(UsageState.NoData, new CodexSessionReader(_root).Read(Now).State);
    }

    // A line whose "payload" is not a JSON object (here a string, so the
    // "rate_limits" pre-filter still matches on substring) used to throw
    // InvalidOperationException from TryGetProperty on a non-object element,
    // uncaught by the JsonException-only catch. It must now be skipped like
    // any other malformed line, falling through to the next valid one.
    [Fact]
    public void NonObjectPayloadSkipped()
    {
        // LastRateLimits scans backward from the last line, so the bad line
        // must be written AFTER the good one to actually be evaluated (and
        // must not throw) before the walk falls back to the good line.
        Write("2026/07/06/rollout-a.jsonl",
            Event(u5: 44.0),
            "{\"timestamp\":\"x\",\"type\":\"event_msg\",\"payload\":\"rate_limits\"}");
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.44, u.Util5h!.Value, 3);
    }

    // resets_at is normally a whole-second epoch, but a fractional value
    // must not throw out of GetInt64() — it should floor to whole seconds.
    [Fact]
    public void FractionalResetsAtParsed()
    {
        var line = "{\"timestamp\":\"2026-07-06T14:41:05.555Z\",\"type\":\"event_msg\"," +
            "\"payload\":{\"rate_limits\":{\"primary\":{\"used_percent\":10.0,\"resets_at\":" +
            (Now + 9000) + ".7}," +
            "\"secondary\":{\"used_percent\":20.0,\"resets_at\":" + (Now + 200000) + "}}}}";
        Write("2026/07/06/rollout-a.jsonl", line);
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(Now + 9000, u.Reset5h);
    }

    // Top-level dirs under the sessions root must be all-digit (year) names —
    // parity with the Python reader's `d.isdigit()` filter — so a stray
    // non-year entry (e.g. a "latest" symlink/junction some tooling drops
    // next to the year dirs) is never walked as if it were one.
    [Fact]
    public void NonDigitTopLevelDirIgnored()
    {
        Write("latest/07/06/rollout-a.jsonl", Event(u5: 77.0));
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.NoData, u.State);
    }
}
