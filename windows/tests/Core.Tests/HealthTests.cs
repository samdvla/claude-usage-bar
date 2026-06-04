using ClaudeUsage.Core;

namespace Core.Tests;

public class HealthTests
{
    [Theory]
    [InlineData(0.0, HealthLevel.Green)]
    [InlineData(0.49, HealthLevel.Green)]
    [InlineData(0.50, HealthLevel.Orange)]
    [InlineData(0.79, HealthLevel.Orange)]
    [InlineData(0.80, HealthLevel.Red)]
    [InlineData(1.0, HealthLevel.Red)]
    public void Level_MatchesMacThresholds(double util, HealthLevel expected)
    {
        Assert.Equal(expected, Health.Level(util));
    }
}
