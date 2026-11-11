namespace SoundProtection.Core;

/// <summary>
/// Calls <see cref="ProtectionHealth.Evaluate"/> once per tick and keeps the three pieces of time-based state it needs, so that
/// Evaluate itself stays a plain function. One is how long sound has been playing without a break: a sound that has just
/// started has no plug-in slot yet, so it is not judged at once. Another is how long the plug-in has been out of step with
/// the settings: a change takes a moment to reach it, so a difference is reported only once it has lasted more than 3 s.
/// The third is when the plug-in last counted as running: <see cref="HealthResult.PluginSeenRunning"/> stays true for 3 s after
/// the last tick at which it did, which is what lets the tray tell, as an over event closes, whether the plug-in was protecting.
/// That piece also remembers whether the plug-in said it was switched on at that tick (<see cref="HealthResult.PluginEnabled"/>),
/// for the same 3 s, with no grace on the way in: a plug-in that says it is off is seen as off at once.
/// Call it from one thread only, on every tick.
/// </summary>
public sealed class HealthTracker
{
    private static readonly TimeSpan ActivityGrace = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MismatchGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RunningMemory = TimeSpan.FromSeconds(3);

    private readonly ActivityGate _activity = new(ActivityGrace);
    private readonly MismatchGate _mismatch = new(MismatchGrace);
    private readonly RunningGate _running = new(RunningMemory);

    /// <param name="soundPlaying">Something is playing on the watched output right now.</param>
    /// <param name="watched">The output being watched, or null when not known.</param>
    /// <param name="settingsNotSaved">The latest attempt to write config.bin failed (<see cref="ProtectionConfigWriter.LastAttemptFailed"/>).</param>
    public HealthResult Evaluate(
        RegistryFacts reg,
        TelemetrySnapshot? tel,
        DateTimeOffset now,
        bool configEnabled,
        double capDb,
        bool soundPlaying,
        WatchedOutput? watched = null,
        bool settingsNotSaved = false)
    {
        var nowUtc = now.UtcDateTime;
        var audioActive = _activity.Update(soundPlaying, now);
        var mismatch = _mismatch.Update(ProtectionHealth.FindMismatch(tel, nowUtc, configEnabled, capDb), configEnabled, capDb, now);
        var health = ProtectionHealth.Evaluate(reg, tel, nowUtc, configEnabled, capDb, audioActive, watched, mismatch, settingsNotSaved);
        var window = _running.Update(health.PluginSeenRunning, health.PluginEnabled, now);
        return health with { PluginSeenRunning = window.SeenRunning, PluginEnabled = window.Enabled };
    }

    /// <summary>
    /// Says whether the plug-in counted as running at this tick or at any tick in the 3 s before it, and whether it said it was
    /// switched on at the last tick at which it ran. The 3 s are counted from the last tick at which it ran, and include their end;
    /// a plug-in that is not seen running is not enabled. When the clock steps back, what was seen is forgotten: a stamp that lies
    /// in the future would otherwise keep the window open for as long as the clock was set back, and then again when it caught up.
    /// </summary>
    private sealed class RunningGate
    {
        private readonly TimeSpan _memory;
        private DateTimeOffset? _lastRan;
        private bool _enabledThen;

        public RunningGate(TimeSpan memory) => _memory = memory;

        public (bool SeenRunning, bool Enabled) Update(bool runningNow, bool enabledNow, DateTimeOffset now)
        {
            if (runningNow)
            {
                _lastRan = now;
                _enabledThen = enabledNow;
                return (true, enabledNow);
            }

            if (_lastRan is not { } last)
            {
                return (false, false);
            }

            if (now < last)
            {
                _lastRan = null;
                return (false, false);
            }

            var seen = now - last <= _memory;
            return (seen, seen && _enabledThen);
        }
    }

    /// <summary>
    /// Lets a difference through only when it has gone on for longer than the grace period without a break. It starts
    /// again when the difference ends, when the settings ask for something new (the plug-in has had no time to answer that
    /// yet; while a slider is being dragged it is always a little behind), or when the clock steps back.
    /// </summary>
    private sealed class MismatchGate
    {
        private readonly TimeSpan _grace;
        private DateTimeOffset? _since;
        private bool _askedOn;
        private double _askedCapDb;

        public MismatchGate(TimeSpan grace) => _grace = grace;

        public SettingsMismatch Update(SettingsMismatch mismatch, bool askedOn, double askedCapDb, DateTimeOffset now)
        {
            if (mismatch == SettingsMismatch.None)
            {
                _since = null;
                return SettingsMismatch.None;
            }

            if (_since is not { } since || now < since || askedOn != _askedOn || askedCapDb != _askedCapDb)
            {
                _since = since = now;
                _askedOn = askedOn;
                _askedCapDb = askedCapDb;
            }

            return now - since > _grace ? mismatch : SettingsMismatch.None;
        }
    }
}
