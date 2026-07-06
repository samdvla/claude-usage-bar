namespace ClaudeUsage.Core;

public enum Provider { Claude, Codex }

public record RateUsage(
    UsageState State,
    double? Util5h = null,
    double? Util7d = null,
    long? Reset5h = null,
    long? Reset7d = null,
    string? Plan = null,
    Provider Provider = Provider.Claude,
    long? AsOf = null)
{
    public static RateUsage NoLogin() => new(UsageState.NoLogin);
    public static RateUsage Expired() => new(UsageState.Expired);
    public static RateUsage Transient() => new(UsageState.Transient);
    public static RateUsage NoData(Provider p) => new(UsageState.NoData, Provider: p);
}
