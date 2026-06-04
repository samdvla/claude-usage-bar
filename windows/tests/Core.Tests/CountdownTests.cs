using ClaudeUsage.Core;

namespace Core.Tests;

public class CountdownTests
{
    // now is injected so the test is deterministic.
    private const long Now = 1_000_000;

    [Fact]
    public void Null_RendersDash() => Assert.Equal("—", Countdown.Format(null, Now));

    [Fact]
    public void Past_RendersNow() => Assert.Equal("now", Countdown.Format(Now - 10, Now));

    [Fact]
    public void UnderADay_RendersHoursMinutes()
    {
        // 2h 39m = 9540s
        Assert.Equal("2h 39m", Countdown.Format(Now + 9540, Now));
    }

    [Fact]
    public void OverADay_RendersDaysHours()
    {
        // 5d 3h = 5*86400 + 3*3600 = 442800s
        Assert.Equal("5d 3h", Countdown.Format(Now + 442800, Now));
    }
}
