using System.Globalization;

namespace SoundProtection.Core;

/// <summary>Decibel helpers. Levels are dBFS (digital) unless a caller says otherwise.</summary>
public static class Db
{
    /// <summary>The level used for silence and for anything quieter.</summary>
    public const double Floor = -120.0;

    private const double SilentAtOrBelow = -100.0;

    public static double FromLinear(double linear)
    {
        if (double.IsNaN(linear) || linear <= 0.0)
        {
            return Floor;
        }

        var db = 20.0 * Math.Log10(linear);
        return db < Floor ? Floor : db;
    }

    public static double ToLinear(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>One decimal, invariant culture, ASCII minus. "silent" at or below -100 dB.</summary>
    public static string Format(double db)
    {
        if (double.IsNaN(db) || db <= SilentAtOrBelow)
        {
            return "silent";
        }

        var rounded = Math.Round(db, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0.0)
        {
            rounded = 0.0; // turns -0.0 into 0.0
        }

        return rounded.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
    }
}
