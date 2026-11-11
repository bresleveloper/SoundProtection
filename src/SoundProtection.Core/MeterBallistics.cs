namespace SoundProtection.Core;

/// <summary>Meter display behaviour: rises instantly, falls at a fixed dB per second.</summary>
public sealed class MeterBallistics
{
    private readonly double _decayDbPerSecond;

    public MeterBallistics(double decayDbPerSecond = 24.0) => _decayDbPerSecond = decayDbPerSecond;

    public double Level { get; private set; } = Db.Floor;

    public double Update(double db, double elapsedSeconds)
    {
        Level = db >= Level
            ? db
            : Math.Max(db, Level - _decayDbPerSecond * Math.Max(0.0, elapsedSeconds));
        return Level;
    }
}
