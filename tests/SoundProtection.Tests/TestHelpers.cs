namespace SoundProtection.Tests;

internal static class T
{
    public static readonly DateTimeOffset Base = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(double ms) => Base.AddMilliseconds(ms);

    public static void Near(double expected, double actual, double tolerance = 1e-9) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} +/- {tolerance}, got {actual}");
}
