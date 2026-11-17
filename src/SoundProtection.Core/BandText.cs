using System.Globalization;

namespace SoundProtection.Core;

/// <summary>The words the panel shows for the Bass and Treble caps. Plain text only, so the panel and the tests share it.</summary>
public static class BandText
{
    public const string BassTip =
        "Bass cap: when the deep, boomy part of the sound goes over this level, only that part is turned down. All the way right = off.";

    public const string TrebleTip =
        "Treble cap: when the sharp, high part of the sound goes over this level, only that part is turned down. All the way right = off.";

    private const string OffText = "Off";

    // A cut smaller than this rounds to "0.0 dB" and is not worth a line of text.
    private const double SmallestShownCutDb = 0.05;

    /// <summary>
    /// What a cap slider's value reads as: "Off" at the right end (0 dB or above, or not a number), else the cap with one
    /// decimal, such as "-32.0 dB".
    /// </summary>
    public static string Value(double capDb) =>
        double.IsNaN(capDb) || capDb >= BandsConfigFile.MaxCapDb ? OffText : Db.Format(capDb);

    /// <summary>
    /// What a band is being turned down by right now, such as "cutting 4.0 dB". Blank when the cut is under 0.05 dB,
    /// or is not a finite amount of turning down.
    /// </summary>
    public static string Cut(double cutDb) =>
        double.IsFinite(cutDb) && cutDb >= SmallestShownCutDb
            ? "cutting " + cutDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB"
            : string.Empty;
}
