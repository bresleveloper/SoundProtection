namespace SoundProtection.Core;

/// <summary>Allows one notification per key per interval.</summary>
public sealed class NotificationThrottle
{
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, DateTimeOffset> _last = new();

    public NotificationThrottle(TimeSpan interval) => _interval = interval;

    public bool ShouldNotify(string key, DateTimeOffset now)
    {
        if (_last.TryGetValue(key, out var last) && now >= last && now - last < _interval)
        {
            return false;
        }

        _last[key] = now;
        return true;
    }
}
