using System.Globalization;

namespace ClaudeUsage.Core;

public static class RateHeaderParser
{
    // headers: any case-mix of header names; values are raw strings.
    // A 429 response still carries the util headers, so "present" == data.
    public static RateUsage Parse(IDictionary<string, string> headers, string? plan)
    {
        var lower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in headers) lower[kv.Key] = kv.Value;

        double? u5 = ParseDouble(lower, RateHeaders.Util5h);
        double? u7 = ParseDouble(lower, RateHeaders.Util7d);
        if (u5 is null && u7 is null) return RateUsage.Transient();

        return new RateUsage(
            UsageState.Ok,
            u5, u7,
            ParseLong(lower, RateHeaders.Reset5h),
            ParseLong(lower, RateHeaders.Reset7d),
            plan);
    }

    private static double? ParseDouble(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) &&
        double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? ParseLong(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) &&
        long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : null;
}
