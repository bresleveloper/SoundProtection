namespace SoundProtection.Core;

/// <summary>Maps dB levels to positions on a bar or slider, so the meter and the cap slider always line up.</summary>
public static class DbScale
{
    /// <summary>Where a level sits between <paramref name="min"/> (0.0) and <paramref name="max"/> (1.0). Levels off the scale are clamped.</summary>
    public static double ToFraction(double db, double min, double max) =>
        max <= min ? 0.0 : Math.Clamp((db - min) / (max - min), 0.0, 1.0);

    /// <summary>The level at a position from 0 to 1, snapped to <paramref name="step"/> dB and kept on the scale.</summary>
    public static double FromFraction(double fraction, double min, double max, double step)
    {
        var db = min + Math.Clamp(fraction, 0.0, 1.0) * (max - min);
        var snapped = Math.Round(db / step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, min, max) + 0.0; // + 0.0 turns -0.0 into 0.0
    }
}
