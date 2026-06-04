using ClaudeUsage.Core;

namespace Core.Tests;

public class RateHeaderParserTests
{
    private static Dictionary<string, string> Headers(string u5, string u7, string r5, string r7) => new()
    {
        [RateHeaders.Util5h] = u5,
        [RateHeaders.Util7d] = u7,
        [RateHeaders.Reset5h] = r5,
        [RateHeaders.Reset7d] = r7,
    };

    [Fact]
    public void Parse_ReadsUtilisationAndReset()
    {
        var u = RateHeaderParser.Parse(Headers("0.61", "0.81", "1700000000", "1700500000"), plan: "max");
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.61, u.Util5h!.Value, 3);
        Assert.Equal(0.81, u.Util7d!.Value, 3);
        Assert.Equal(1700000000, u.Reset5h);
        Assert.Equal(1700500000, u.Reset7d);
        Assert.Equal("max", u.Plan);
    }

    [Fact]
    public void Parse_IsCaseInsensitiveOnKeys()
    {
        var raw = new Dictionary<string, string>
        {
            ["ANTHROPIC-RATELIMIT-UNIFIED-5H-UTILIZATION"] = "0.5",
            ["Anthropic-Ratelimit-Unified-7d-Utilization"] = "0.2",
        };
        var u = RateHeaderParser.Parse(raw, plan: null);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.5, u.Util5h!.Value, 3);
    }

    [Fact]
    public void Parse_MissingBothUtilHeaders_IsTransient()
    {
        var u = RateHeaderParser.Parse(new Dictionary<string, string>(), plan: null);
        Assert.Equal(UsageState.Transient, u.State);
    }
}
