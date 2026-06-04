namespace ClaudeUsage.Core;

public static class Countdown
{
    // Mirrors the macOS countdown(): minutes zero-padded, days+hours past 24h.
    public static string Format(long? resetEpoch, long nowEpoch)
    {
        if (resetEpoch is null) return "—";
        long d = resetEpoch.Value - nowEpoch;
        if (d < 0) return "now";
        if (d >= 86400) return $"{d / 86400}d {(d % 86400) / 3600}h";
        return $"{d / 3600}h {(d % 3600) / 60:D2}m";
    }
}
