namespace SoundProtection.Core;

public static class EarLevel
{
    /// <summary>
    /// Level at the ear in dB below the loudest the output can play (0 = loudest):
    /// the digital peak plus the Windows volume in dB. A muted output is silent.
    /// This is not dB SPL.
    /// </summary>
    public static double Compute(double peakDbfs, double volumeDb, bool muted) =>
        muted ? Db.Floor : Math.Max(Db.Floor, peakDbfs + volumeDb);
}
