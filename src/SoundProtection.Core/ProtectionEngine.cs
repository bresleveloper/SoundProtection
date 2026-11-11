namespace SoundProtection.Core;

/// <summary>How much one sound was turned down by the Leveler.</summary>
public sealed record AppTurnDown(string Name, double TurnDownDb);

/// <summary>
/// What the panel and the tray show about protection, at one moment. <c>LoudestInputDb</c> is the loudness of the
/// loudest sound before the Leveler, which is before the Windows volume slider. <c>TurnDownDb</c> is the most the
/// Leveler is turning any sound down, and <c>SafetyCapDb</c> the most the Safety Cap is holding back the total.
/// <c>WatchedOutputCovered</c> is true when the output being watched is one that has the plug-in (see
/// <see cref="HealthResult.WatchedOutputCovered"/>); it goes by the output, not by whether sound is playing. A state that is
/// built without it counts the output as covered. <c>PluginSeenRunning</c> is true when the plug-in counted as running at some
/// tick in the last 3 s (see <see cref="HealthResult.PluginSeenRunning"/>); a state built without it counts the plug-in as not
/// seen running. <c>PluginEnabled</c> is true when, at the last of those ticks, the plug-in said it was switched on (see
/// <see cref="HealthResult.PluginEnabled"/>); a state built without it counts the plug-in as not enabled.
/// </summary>
public sealed record ProtectionState(
    ProtectionStatus Status,
    string StatusText,
    double LoudestInputDb,
    double TurnDownDb,
    double SafetyCapDb,
    IReadOnlyList<AppTurnDown> Apps,
    bool WatchedOutputCovered = true,
    bool PluginSeenRunning = false,
    bool PluginEnabled = false)
{
    /// <summary>
    /// True when the plug-in is installed and set up, so the panel shows loudness before the slider. False means the ear-level
    /// display and the older over-the-ceiling alerts: the plug-in is missing or not working, or sound is on an output it does not cover.
    /// </summary>
    public bool ShowsProtection => Status is ProtectionStatus.Protected or ProtectionStatus.Idle or ProtectionStatus.Off;

    /// <summary>
    /// True when an over-the-ceiling event is not to be raised, because the plug-in is actually protecting the watched output: the
    /// output is one that has the plug-in, the status is Protected or Idle, the plug-in has been seen running within the last 3 s,
    /// and it said it was switched on when it last ran. All four are needed. The status and the time are read as the event closes,
    /// half a second after the last loud reading, when the sound is often over and the status has fallen back to Idle even though
    /// nothing was turning it down: on an output with no plug-in, with the plug-in dead, with the switch off (Off is left out on
    /// purpose: it shows protection, but nothing is turned down), or with the plug-in running but switched off, which the status
    /// shows as green until it has lasted 3 s. In every other case the older alert is raised.
    /// </summary>
    public bool DropsOverEvents =>
        (Status is ProtectionStatus.Protected or ProtectionStatus.Idle) && WatchedOutputCovered && PluginSeenRunning && PluginEnabled;
}

/// <summary>A stretch of time in which the Leveler was turning a sound down by at least 1 dB.</summary>
public sealed record TurnDownEvent(DateTimeOffset Start, TimeSpan Duration, double MaxTurnDownDb, double CapDb, string? App);

/// <summary>
/// Turns a stream of turn-down readings into events, the way <see cref="OverDetector"/> does for the level at the ear:
/// readings closer together than the merge gap belong to one event, and an event never runs longer than the maximum
/// episode length. Call from one thread only.
/// </summary>
public sealed class TurnDownDetector
{
    /// <summary>The smallest turn-down that counts as an event.</summary>
    public const double ThresholdDb = 1.0;

    private readonly TimeSpan _mergeGap;
    private readonly TimeSpan _maxEpisode;

    private bool _open;
    private DateTimeOffset _start;
    private DateTimeOffset _lastOver;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private double _maxDb;
    private double _capAtMax;
    private string? _appAtMax;

    public TurnDownDetector(TimeSpan? mergeGap = null, TimeSpan? maxEpisode = null)
    {
        _mergeGap = mergeGap ?? TimeSpan.FromMilliseconds(500);
        _maxEpisode = maxEpisode ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>True while an episode is open, including the merge gap after the last turned-down reading.</summary>
    public bool IsOpen => _open;

    /// <summary>Feeds one reading. Returns an event when one has just closed.</summary>
    public TurnDownEvent? Feed(DateTimeOffset time, double turnDownDb, double capDb, string? app)
    {
        if (time < _lastSeen)
        {
            time = _lastSeen; // the clock stepped back: treat it as "no time has passed"
        }

        _lastSeen = time;

        if (turnDownDb >= ThresholdDb)
        {
            TurnDownEvent? closed = null;
            if (_open && time - _start >= _maxEpisode)
            {
                closed = Close(time - _start);
            }

            if (!_open)
            {
                _open = true;
                _start = time;
                _maxDb = turnDownDb;
                _capAtMax = capDb;
                _appAtMax = app;
            }
            else if (turnDownDb > _maxDb)
            {
                _maxDb = turnDownDb;
                _capAtMax = capDb;
                _appAtMax = app;
            }

            _lastOver = time;
            return closed;
        }

        return _open && time - _lastOver >= _mergeGap ? Close(_lastOver - _start) : null;
    }

    private TurnDownEvent Close(TimeSpan duration)
    {
        _open = false;
        return new TurnDownEvent(_start, duration, _maxDb, _capAtMax, _appAtMax);
    }
}

/// <summary>
/// Reads the plug-in's telemetry and the apps' own levels and works out what is being turned down.
/// Call <see cref="Process"/> from one thread only; <see cref="TurnedDown"/> is raised on that thread.
/// </summary>
public sealed class ProtectionEngine
{
    /// <summary>The name given to a sound the Leveler is working on that no app could be matched to.</summary>
    public const string OtherSound = "Other sound";

    private const double SlotQuietDb = -70.0;   // a stream quieter than this is not matched to an app
    private const double AppQuietDbfs = -90.0;  // an app quieter than this is not playing
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(1);

    private readonly TurnDownDetector _detector = new();

    public event Action<TurnDownEvent>? TurnedDown;

    public ProtectionState Process(
        TelemetrySnapshot? tel,
        HealthResult health,
        IReadOnlyList<AppLevel> apps,
        double capDb,
        DateTimeOffset now)
    {
        var levelers = new List<TelemetrySlot>();
        var loudest = double.NegativeInfinity;
        var turnDown = 0.0;
        var safetyCap = 0.0;

        if (tel is not null)
        {
            var nowUtc = now.UtcDateTime;
            foreach (var slot in tel.Slots)
            {
                if (!IsFresh(slot, nowUtc))
                {
                    continue;
                }

                var reduction = double.IsFinite(slot.GainReductionDb) ? Math.Max(0.0, slot.GainReductionDb) : 0.0;
                if (slot.Kind == SlotKind.Leveler)
                {
                    levelers.Add(slot);
                    turnDown = Math.Max(turnDown, reduction);
                    if (double.IsFinite(slot.InputDb))
                    {
                        loudest = Math.Max(loudest, slot.InputDb);
                    }
                }
                else if (slot.Kind == SlotKind.SafetyCap)
                {
                    safetyCap = Math.Max(safetyCap, reduction);
                }
            }
        }

        var loudestInput = double.IsNegativeInfinity(loudest) ? Db.Floor : Math.Max(loudest, Db.Floor);
        var matched = Match(levelers, apps);

        var closed = _detector.Feed(now, turnDown, capDb, ProtectionText.WhoIsTurnedDown(matched, turnDown));
        if (closed is not null)
        {
            TurnedDown?.Invoke(closed);
        }

        return new ProtectionState(
            health.Status,
            health.Text,
            loudestInput,
            turnDown,
            safetyCap,
            matched,
            health.WatchedOutputCovered,
            health.PluginSeenRunning,
            health.PluginEnabled);
    }

    // A slot counts while it has processed sound within the last second, before or after now.
    private static bool IsFresh(TelemetrySlot slot, DateTime nowUtc)
    {
        var age = nowUtc - slot.LastProcessUtc;
        return age <= FreshWindow && age >= -FreshWindow;
    }

    // The loudest stream is taken to be the loudest app, the next loudest the next loudest app, and so on.
    private static IReadOnlyList<AppTurnDown> Match(List<TelemetrySlot> levelers, IReadOnlyList<AppLevel> apps)
    {
        var streams = levelers
            .Where(s => s.InputDb > SlotQuietDb)
            .OrderByDescending(s => s.InputDb)
            .ThenBy(s => s.InstanceId)
            .ToList();
        if (streams.Count == 0)
        {
            return Array.Empty<AppTurnDown>();
        }

        var playing = apps
            .Where(a => a.PeakDbfs > AppQuietDbfs)
            .OrderByDescending(a => a.PeakDbfs)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();

        var result = new List<AppTurnDown>(streams.Count);
        for (var i = 0; i < streams.Count; i++)
        {
            var reduction = double.IsFinite(streams[i].GainReductionDb) ? Math.Max(0.0, streams[i].GainReductionDb) : 0.0;
            result.Add(new AppTurnDown(i < playing.Count ? playing[i].Name : OtherSound, reduction));
        }

        return result;
    }
}

/// <summary>The words the panel and the alerts use about protection.</summary>
public static class ProtectionText
{
    public const string SliderTip =
        "Drag to set the cap: loudness before your volume slider. Louder apps are turned down to it.";

    private const string Explanation = "loudest app now, before your volume slider";
    private const double SameAmountDb = 0.05;

    /// <summary>The line next to the big number: what the number is, or, when something is being turned down, which app and by how much.</summary>
    public static string Caption(ProtectionState s) =>
        s.TurnDownDb < TurnDownDetector.ThresholdDb
            ? Explanation
            : $"{WhoIsTurnedDown(s.Apps, s.TurnDownDb) ?? "Sound"} turned down by {Db.Format(s.TurnDownDb)}";

    /// <summary>"Playing now: ..." with up to four apps, each with what was done to it.</summary>
    public static string PlayingNow(ProtectionState s) =>
        s.Apps.Count == 0
            ? "Playing now: nothing"
            : "Playing now: " + string.Join("     ", s.Apps.Take(4).Select(Describe));

    /// <summary>
    /// The app being turned down the most, or null when that is a sound with no app matched to it. The total is
    /// what the Leveler is doing at most; an app is named only if it accounts for that amount.
    /// </summary>
    internal static string? WhoIsTurnedDown(IReadOnlyList<AppTurnDown> apps, double totalTurnDownDb)
    {
        AppTurnDown? top = null;
        foreach (var app in apps)
        {
            if (top is null || app.TurnDownDb > top.TurnDownDb)
            {
                top = app;
            }
        }

        return top is not null && top.Name != ProtectionEngine.OtherSound && top.TurnDownDb >= totalTurnDownDb - SameAmountDb
            ? top.Name
            : null;
    }

    private static string Describe(AppTurnDown app) =>
        app.TurnDownDb >= TurnDownDetector.ThresholdDb
            ? $"{app.Name} turned down {Db.Format(app.TurnDownDb)}"
            : $"{app.Name} not turned down";
}
