namespace SoundProtection.Core;

/// <summary>One stretch of time during which the level at the ear was above the ceiling.</summary>
public sealed record OverEvent(
    DateTimeOffset Start,
    TimeSpan Duration,
    double MaxOverDb,
    double CeilingDb,
    string? LoudestApp)
{
    /// <summary>The highest level at the ear during the event, in dB below the output's maximum.</summary>
    public double PeakEarDb => CeilingDb + MaxOverDb;
}

/// <summary>
/// Turns a stream of level readings into over-ceiling events. Readings closer together than the
/// merge gap belong to one event, and an event never runs longer than the maximum episode length.
/// </summary>
public sealed class OverDetector
{
    private readonly TimeSpan _mergeGap;
    private readonly TimeSpan _maxEpisode;
    private readonly double _toleranceDb;

    private bool _open;
    private DateTimeOffset _start;
    private DateTimeOffset _lastOver;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private double _maxOverDb;
    private double _ceilingAtMax;
    private string? _loudestApp;

    public OverDetector(TimeSpan? mergeGap = null, TimeSpan? maxEpisode = null, double toleranceDb = 0.1)
    {
        _mergeGap = mergeGap ?? TimeSpan.FromMilliseconds(500);
        _maxEpisode = maxEpisode ?? TimeSpan.FromSeconds(5);
        _toleranceDb = toleranceDb;
    }

    /// <summary>True while an episode is open, including the merge gap after the last over reading.</summary>
    public bool IsOver => _open;

    /// <summary>How far the latest reading was over the ceiling; 0 if it was not over.</summary>
    public double CurrentOverDb { get; private set; }

    /// <summary>Drops any open episode without reporting it.</summary>
    public void Reset()
    {
        _open = false;
        CurrentOverDb = 0;
    }

    /// <summary>Feeds one reading. Returns an event when one has just closed.</summary>
    public OverEvent? Feed(DateTimeOffset time, double earDb, double ceilingDb, string? loudestApp)
    {
        if (time < _lastSeen)
        {
            time = _lastSeen; // the clock stepped back: treat it as "no time has passed"
        }

        _lastSeen = time;

        if (earDb > ceilingDb + _toleranceDb)
        {
            var over = earDb - ceilingDb;
            CurrentOverDb = over;

            OverEvent? closed = null;
            if (_open && time - _start >= _maxEpisode)
            {
                closed = Close(time - _start);
            }

            if (!_open)
            {
                _open = true;
                _start = time;
                _maxOverDb = over;
                _ceilingAtMax = ceilingDb;
                _loudestApp = loudestApp;
            }
            else if (over > _maxOverDb)
            {
                _maxOverDb = over;
                _ceilingAtMax = ceilingDb;
                _loudestApp = loudestApp;
            }

            _lastOver = time;
            return closed;
        }

        CurrentOverDb = 0;
        return _open && time - _lastOver >= _mergeGap ? Close(_lastOver - _start) : null;
    }

    private OverEvent Close(TimeSpan duration)
    {
        _open = false;
        return new OverEvent(_start, duration, _maxOverDb, _ceilingAtMax, _loudestApp);
    }
}
