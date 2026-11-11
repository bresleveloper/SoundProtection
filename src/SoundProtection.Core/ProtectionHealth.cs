namespace SoundProtection.Core;

public enum ProtectionStatus
{
    NotInstalled,
    Down,
    Off,
    Idle,
    Protected,

    /// <summary>Sound is playing on an output that has no plug-in, so nothing turns it down. Shown in amber, with the ear-level display.</summary>
    NotProtected,
}

/// <summary>
/// What the registry says about one output: whether our two effects are listed on it and whether Windows will run them.
/// <c>EndpointId</c> is the name of its registry key (like <c>{ab14dbc4-...}</c>), which is the end of the output's Core Audio id;
/// it is null when the facts do not say which output they are about.
/// </summary>
public sealed record EndpointFacts(
    string Name,
    bool LevelerListed,
    bool SafetyCapListed,
    bool EffectsDisabled,
    bool ExclusiveAllowed,
    string? EndpointId = null);

/// <summary>Registry facts: the outputs that matter, and whether the test key that lets Windows load an unsigned plug-in is on.</summary>
public sealed record RegistryFacts(bool TestKeyOn, IReadOnlyList<EndpointFacts> Endpoints);

/// <summary>The output the app is watching: the one chosen in the panel, or the Windows default when none is chosen.</summary>
public sealed record WatchedOutput(string DeviceId, string Name);

/// <summary>
/// What the rules decided. <c>WatchedOutputCovered</c> is true when the watched output is one that the registry lists with both
/// effects. It goes by the output alone: it does not depend on whether sound is playing, whether the plug-in is running, or which
/// status came out. It is false when nothing is installed (then no output is covered), and true when the watched output is not
/// known or the facts do not say which output they are about, because neither can rule an output out.
/// <c>PluginSeenRunning</c> is true when the plug-in counted as running (the test of rules 6 and 7: a heartbeat and a Safety Cap
/// slot, both within <see cref="ProtectionHealth.Alive"/>) at this tick or, in a result from <see cref="HealthTracker"/>, at any tick
/// in the last 3 s. It goes by the telemetry alone, not by the status, the sound, the output or the registry. A result built without
/// it counts the plug-in as not seen running, so that nothing is taken as protection without evidence.
/// <c>PluginEnabled</c> is true when the plug-in counted as running and its header said it is switched on (the Enabled flag) at this
/// tick or, in a result from <see cref="HealthTracker"/>, at the last tick in the last 3 s at which it ran. It goes by the telemetry
/// alone, like <c>PluginSeenRunning</c>, and a plug-in that is not seen running is not enabled. A result built without it counts the
/// plug-in as not enabled, for the same reason.
/// </summary>
public sealed record HealthResult(
    ProtectionStatus Status,
    string Text,
    IReadOnlyList<string> Warnings,
    bool WatchedOutputCovered = true,
    bool PluginSeenRunning = false,
    bool PluginEnabled = false);

/// <summary>How what the running plug-in reports differs from what the tray asked for. Only the first match is named.</summary>
public enum SettingsMismatch
{
    None,

    /// <summary>The plug-in could not read config.bin, so it runs on the values it had.</summary>
    ConfigNotRead,

    /// <summary>The plug-in is switched on while the switch is off, or the other way round.</summary>
    SwitchDiffers,

    /// <summary>The plug-in uses another cap.</summary>
    CapDiffers,
}

/// <summary>Decides, from the registry facts and the plug-in's telemetry, whether protection is really in place.</summary>
public static class ProtectionHealth
{
    /// <summary>How old the plug-in's heartbeat, or its Safety Cap's last processing, may be before the plug-in counts as not running.</summary>
    public static readonly TimeSpan Alive = TimeSpan.FromSeconds(2);

    /// <summary>Two caps this close together (in dB) are the same cap.</summary>
    public const double CapToleranceDb = 0.05;

    /// <summary>
    /// The first rule that matches wins, in this order: 1 nothing is installed, 2 Windows switched the effects off, 3 the test
    /// key is off, 4 protection is switched off, 5 the plug-in is in safe mode; then sound is playing on an output that has no
    /// plug-in (<see cref="ProtectionStatus.NotProtected"/>); 6 sound is playing on an output that has it, and it is not
    /// running; then the plug-in is running but says it is switched off while the switch is on and that has lasted
    /// (<paramref name="mismatch"/> is <see cref="SettingsMismatch.SwitchDiffers"/>), which is Down whether or not sound plays,
    /// because nothing is turned down; 7 sound is being processed; 8 nothing is. An output counts as listed when it has both effects.
    /// Warnings are kept whatever the status, except when nothing is installed. They are, in this order: the plug-in not doing
    /// what the settings ask (<paramref name="mismatch"/>, already held back until it has lasted), settings that could not be
    /// saved, and outputs where apps can take exclusive control. The status that says the plug-in is switched off takes the place
    /// of the warning that says the same, so that it is not shown twice.
    /// Whether the plug-in counts as running at this tick, and whether it says it is switched on, are passed on whatever the status,
    /// as <see cref="HealthResult.PluginSeenRunning"/> and <see cref="HealthResult.PluginEnabled"/>. This function knows only this
    /// tick; <see cref="HealthTracker"/> widens them to the last 3 s, so that this stays a plain function of its arguments.
    /// </summary>
    /// <param name="watched">
    /// The output being watched, or null when not known. Rule 6 applies only when it is one of the listed outputs. On any other
    /// output the plug-in has nothing to process: with sound on it the status is "not protected", and without sound
    /// rules 7 and 8 go by whether the plug-in is processing sound for another output. Whether the output is one of the listed
    /// outputs is also passed on, as <see cref="HealthResult.WatchedOutputCovered"/>, whatever the status and whether or not
    /// sound is playing.
    /// </param>
    /// <param name="mismatch">A difference between the settings and the plug-in that is to be reported (see <see cref="HealthTracker"/>).</param>
    /// <param name="settingsNotSaved">The latest attempt to write config.bin failed.</param>
    public static HealthResult Evaluate(
        RegistryFacts reg,
        TelemetrySnapshot? tel,
        DateTime nowUtc,
        bool configEnabled,
        double capDb,
        bool audioActive,
        WatchedOutput? watched = null,
        SettingsMismatch mismatch = SettingsMismatch.None,
        bool settingsNotSaved = false)
    {
        var listed = reg.Endpoints.Where(e => e.LevelerListed && e.SafetyCapListed).ToList();
        var running = IsRunning(tel, nowUtc);
        var enabled = running && tel is { Header.Enabled: true };
        if (listed.Count == 0)
        {
            return new HealthResult(
                ProtectionStatus.NotInstalled,
                "Protection is not installed",
                Array.Empty<string>(),
                WatchedOutputCovered: false,
                PluginSeenRunning: running,
                PluginEnabled: enabled);
        }

        var unprotectedOutput = FindUnprotected(watched, listed);
        var covered = unprotectedOutput is null;
        var warnings = Warnings(listed, tel, running, mismatch, settingsNotSaved);

        var withoutEffects = listed.FirstOrDefault(e => e.EffectsDisabled);
        if (withoutEffects is not null)
        {
            return new HealthResult(ProtectionStatus.Down, $"Windows switched off sound effects on {withoutEffects.Name}", warnings, covered, running, enabled);
        }

        if (!reg.TestKeyOn)
        {
            return new HealthResult(ProtectionStatus.Down, "The test key is off, so Windows will not load the plug-in", warnings, covered, running, enabled);
        }

        if (!configEnabled)
        {
            return new HealthResult(ProtectionStatus.Off, "Protection is off", warnings, covered, running, enabled);
        }

        if (tel is not null && tel.Header.SafeMode)
        {
            return new HealthResult(ProtectionStatus.Down, "The plug-in is in safe mode after repeated crashes", warnings, covered, running, enabled);
        }

        // Sound on an output the plug-in does not cover. The plug-in is not at fault, so this is not "down"; the panel and the
        // tray act as they do without the plug-in, because the level at the ear is all there is to go by.
        if (audioActive && unprotectedOutput is not null)
        {
            return new HealthResult(ProtectionStatus.NotProtected, $"Sound on {unprotectedOutput.Name} is not protected", warnings, covered, running, enabled);
        }

        // Only an output that has the plug-in (or one that is not known) gets here with sound playing.
        if (audioActive && !running)
        {
            return new HealthResult(ProtectionStatus.Down, "The plug-in is not running", warnings, covered, running, enabled);
        }

        // The plug-in runs and the switch is on, but the plug-in says it is off, and has for more than the grace the tracker holds the
        // mismatch back for: the setting has not reached it, so nothing is turned down, whatever the sound is doing. This is not green.
        // The status is that fact, so the warning that would say it again ("still switched off") is left out.
        if (mismatch == SettingsMismatch.SwitchDiffers && running && !enabled)
        {
            return new HealthResult(
                ProtectionStatus.Down,
                "The plug-in is switched off: your setting has not reached it",
                Warnings(listed, tel, running, SettingsMismatch.None, settingsNotSaved),
                covered,
                running,
                enabled);
        }

        // On an output that has the plug-in, sound on that output is what to protect. On any other output nothing is playing
        // here, so it is the plug-in's own Safety Cap slot that says whether it has sound to process (from another output).
        var processing = unprotectedOutput is null ? audioActive : running;
        return processing
            ? new HealthResult(ProtectionStatus.Protected, "Protected: cap " + Db.Format(tel?.Header.CapDb ?? capDb), warnings, covered, running, enabled)
            : new HealthResult(ProtectionStatus.Idle, "Protected (nothing playing)", warnings, covered, running, enabled);
    }

    /// <summary>
    /// Compares what the running plug-in reports with what the tray asked for: the switch and the cap (within
    /// <see cref="CapToleranceDb"/>). Nothing is compared unless the plug-in is running, because only then does its header mean
    /// anything. When several things differ, the first of these is named: the config could not be read, the switch, the cap.
    /// </summary>
    public static SettingsMismatch FindMismatch(TelemetrySnapshot? tel, DateTime nowUtc, bool configEnabled, double capDb)
    {
        if (tel is null || !IsRunning(tel, nowUtc))
        {
            return SettingsMismatch.None;
        }

        var header = tel.Header;
        if (!header.ConfigValid)
        {
            return SettingsMismatch.ConfigNotRead;
        }

        if (header.Enabled != configEnabled)
        {
            return SettingsMismatch.SwitchDiffers;
        }

        // A cap that is not a number is not within the tolerance of anything.
        return Math.Abs(header.CapDb - capDb) <= CapToleranceDb ? SettingsMismatch.None : SettingsMismatch.CapDiffers;
    }

    private static string[] Warnings(
        List<EndpointFacts> listed,
        TelemetrySnapshot? tel,
        bool running,
        SettingsMismatch mismatch,
        bool settingsNotSaved)
    {
        var warnings = new List<string>();
        if (running && tel is not null && MismatchText(mismatch, tel.Header) is { } difference)
        {
            warnings.Add(difference);
        }

        if (settingsNotSaved)
        {
            warnings.Add("Your settings could not be saved for the plug-in");
        }

        warnings.AddRange(listed.Where(e => e.ExclusiveAllowed).Select(e => $"Apps can take exclusive control of {e.Name}"));
        return warnings.Distinct().ToArray();
    }

    // {X} is the cap the plug-in reports; for the switch it is the plug-in's own state that is named.
    private static string? MismatchText(SettingsMismatch mismatch, TelemetryHeader plugin) =>
        mismatch switch
        {
            SettingsMismatch.ConfigNotRead => $"The plug-in could not read your settings and uses cap {Db.Format(plugin.CapDb)}",
            SettingsMismatch.SwitchDiffers => plugin.Enabled ? "The plug-in is still switched on" : "The plug-in is still switched off",
            SettingsMismatch.CapDiffers => $"The plug-in is still using cap {Db.Format(plugin.CapDb)}",
            _ => null,
        };

    // The watched output, when it is known and is not one of the listed outputs; otherwise null. It is a listed output when its
    // Core Audio id ends with the output's registry key name. Facts that do not say which output they are about cannot rule it
    // out, so they count as a match; that keeps rule 6 on guard when in doubt. With no watched output known, the rules are as
    // they were.
    private static WatchedOutput? FindUnprotected(WatchedOutput? watched, List<EndpointFacts> listed) =>
        watched is not null && !listed.Any(e => string.IsNullOrWhiteSpace(e.EndpointId)
            || watched.DeviceId.EndsWith(e.EndpointId.Trim(), StringComparison.OrdinalIgnoreCase))
            ? watched
            : null;

    // Running means: a heartbeat in the last 2 s, and the Safety Cap (which sees all the sound) processing in the last 2 s.
    private static bool IsRunning(TelemetrySnapshot? tel, DateTime nowUtc) =>
        tel is not null
        && IsRecent(tel.Header.HeartbeatUtc, nowUtc)
        && tel.Slots.Any(s => s.Kind == SlotKind.SafetyCap && IsRecent(s.LastProcessUtc, nowUtc));

    // A time that is far ahead of now (the clock was set back) is not recent either: it could be from a plug-in that died long ago.
    private static bool IsRecent(DateTime stampUtc, DateTime nowUtc)
    {
        var age = nowUtc - stampUtc;
        return age <= Alive && age >= -Alive;
    }
}

/// <summary>
/// Says "audio is active" only after sound has been playing without a break for the grace period. A stream that has just
/// started has no plug-in slot yet, and without this the status would flash "not running" at the start of every sound.
/// </summary>
public sealed class ActivityGate
{
    private readonly TimeSpan _grace;
    private DateTimeOffset? _since;

    public ActivityGate(TimeSpan grace) => _grace = grace;

    /// <summary>Feeds one reading. Returns true once <paramref name="playing"/> has been true for the whole grace period.</summary>
    public bool Update(bool playing, DateTimeOffset now)
    {
        if (!playing)
        {
            _since = null;
            return false;
        }

        if (_since is not { } since || now < since)
        {
            _since = since = now; // a new stretch of sound, or the clock stepped back: start counting again
        }

        return now - since >= _grace;
    }
}
