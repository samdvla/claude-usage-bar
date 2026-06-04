namespace ClaudeUsage.Core;

public enum HealthLevel { Green, Orange, Red }

public static class Health
{
    // Parity with the macOS app: >=0.8 red, >=0.5 orange, else green.
    public static HealthLevel Level(double util)
    {
        if (util >= 0.80) return HealthLevel.Red;
        if (util >= 0.50) return HealthLevel.Orange;
        return HealthLevel.Green;
    }
}
