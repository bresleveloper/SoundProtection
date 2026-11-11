namespace SoundProtection.Core;

/// <summary>Maximum of the values added during the last <c>window</c>, in O(1) amortised time.</summary>
public sealed class PeakHold
{
    private readonly TimeSpan _window;

    // Values strictly decrease from front to back, so the front is always the maximum.
    private readonly LinkedList<(DateTimeOffset Time, double Db)> _items = new();

    public PeakHold(TimeSpan window) => _window = window;

    public void Add(DateTimeOffset time, double db)
    {
        while (_items.Last is { } last && last.Value.Db <= db)
        {
            _items.RemoveLast();
        }

        _items.AddLast((time, db));
    }

    public double Max(DateTimeOffset now)
    {
        while (_items.First is { } first && first.Value.Time <= now - _window)
        {
            _items.RemoveFirst();
        }

        return _items.First?.Value.Db ?? Db.Floor;
    }
}
