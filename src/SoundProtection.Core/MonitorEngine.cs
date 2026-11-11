namespace SoundProtection.Core;

/// <summary>
/// Turns raw readings of the output into the state the panel shows, and raises
/// <see cref="OverClosed"/> when something has gone over the ceiling. Call from one thread only.
/// </summary>
public sealed class MonitorEngine
{
    private const double QuietAppDbfs = -90.0;

    private readonly PeakHold _hold = new(TimeSpan.FromSeconds(10));
    private readonly MeterBallistics _ballistics = new();
    private readonly OverDetector _detector = new();
    private DateTimeOffset? _previous;

    public event Action<OverEvent>? OverClosed;

    public PanelState Process(AudioSnapshot? snapshot, Settings settings, DateTimeOffset now)
    {
        var elapsed = _previous is { } previous ? Math.Max(0.0, (now - previous).TotalSeconds) : 0.0;
        _previous = now;
        var ceiling = Math.Clamp(settings.CeilingDb, Settings.MinCeilingDb, Settings.MaxCeilingDb);

        if (snapshot is null)
        {
            _detector.Reset();
            _hold.Add(now, Db.Floor);
            var fading = _ballistics.Update(Db.Floor, elapsed);
            return PanelState.Empty(now, ceiling, settings.AlertsEnabled) with
            {
                HoldDb = _hold.Max(now),
                DisplayDb = fading,
            };
        }

        var ear = EarLevel.Compute(snapshot.PeakDbfs, snapshot.VolumeDb, snapshot.Muted);
        IReadOnlyList<AppRow> apps = snapshot.Apps.Count == 0
            ? Array.Empty<AppRow>() // the usual case: skip the allocations, this runs every 5 ms
            : snapshot.Apps
                .Where(a => a.PeakDbfs > QuietAppDbfs)
                .Select(a => new AppRow(a.Name, a.PeakDbfs, EarLevel.Compute(a.PeakDbfs, snapshot.VolumeDb, snapshot.Muted)))
                .OrderByDescending(a => a.EarDb)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .ToList();

        _hold.Add(now, ear);
        var display = _ballistics.Update(ear, elapsed);

        if (settings.AlertsEnabled)
        {
            var closed = _detector.Feed(now, ear, ceiling, apps.Count > 0 ? apps[0].Name : null);
            if (closed != null)
            {
                OverClosed?.Invoke(closed);
            }
        }
        else
        {
            _detector.Reset();
        }

        return new PanelState(
            now,
            HasDevice: true,
            DeviceName: snapshot.DeviceName,
            VolumePercent: snapshot.VolumePercent,
            VolumeDb: snapshot.VolumeDb,
            Muted: snapshot.Muted,
            EarDb: ear,
            HoldDb: _hold.Max(now),
            DisplayDb: display,
            CeilingDb: ceiling,
            Over: _detector.IsOver,
            OverByDb: _detector.CurrentOverDb,
            AlertsEnabled: settings.AlertsEnabled,
            Apps: apps);
    }
}
