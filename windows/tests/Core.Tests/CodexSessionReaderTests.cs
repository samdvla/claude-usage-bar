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
}
