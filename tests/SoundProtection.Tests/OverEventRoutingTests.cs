using SoundProtection.Core;

namespace SoundProtection.Tests;

/// <summary>
/// Fix rounds 3 and 4 and the final-review fix (rulings 21, 22 and 26): what becomes of an over-the-ceiling event (a level at the ear)
/// on its way to the tray. The tray drops the event when <see cref="ProtectionState.DropsOverEvents"/> is true at the moment the event
/// closes, half a second after the last over reading, and raises it otherwise. That is true only while the plug-in is actually
/// protecting the watched output: the output has it, the status is Protected or Idle, the plug-in has been seen running within the last
/// 3 s, and it has reported itself switched on. These tests run the pieces that the sampler runs, in its order, on a made-up clock (the
/// monitor engine every 10 ms, the protection side every 100 ms), so that the state found at that moment is the real one.
/// </summary>
public class OverEventRoutingTests
{
    private const string HeadphonesKey = "{ab14dbc4-0c0d-4ebb-a2a1-1c2b6f6b3b2e}";
    private const string SpeakersKey = "{9d6b1c2e-1111-4222-8333-444455556666}";

    // 5 dB over the cap of -20: a peak of -5 dBFS, after a Windows volume of -10 dB, is -15 dB at the ear.
    private const double LoudDbfs = -5.0;
    private const double QuietDbfs = -40.0;

    private static readonly Output Headphones = new(HeadphonesKey, "Headphones (Realtek(R) Audio)");
    private static readonly Output Speakers = new(SpeakersKey, "Speakers (Realtek(R) Audio)");

    // The plug-in is installed on the Headphones only.
    private static readonly RegistryFacts HeadphonesInstalled =
        ProtectionHealthTests.Registry(true, ProtectionHealthTests.Endpoint("Headphones", id: HeadphonesKey));

    private sealed record Output(string Key, string Name);

    /// <summary>One over event, with the state that the tray would have found when it closed: that of the latest protection tick, at <c>StateAt</c>.</summary>
    private sealed record Routed(OverEvent Event, ProtectionState State, DateTimeOffset StateAt)
    {
        public bool Dropped => State.DropsOverEvents;
    }

    private sealed class Rig
    {
        private readonly MonitorEngine _monitor = new();
        private readonly HealthTracker _tracker = new();
        private readonly ProtectionEngine _protection = new();
        private readonly Settings _settings;
        private readonly RegistryFacts _registry;
        private readonly Output _output;
        private readonly bool _safeMode;
        private readonly bool _staleFile;
        private readonly double _volumeDb;
        private bool _pluginRuns;
        private bool? _pluginSaysOn;
        private int _ms;
        private ProtectionState? _state;
        private DateTimeOffset _stateAt;

        /// <param name="pluginRuns">The plug-in writes fresh telemetry at every protection tick.</param>
        /// <param name="staleFile">When the plug-in does not run: its telemetry file is still there, with stamps from an hour ago (otherwise there is no file).</param>
        /// <param name="safeMode">The plug-in reports safe mode after repeated crashes.</param>
        /// <param name="pluginSaysOn">
        /// What the running plug-in reports about its own switch, whatever the settings ask: false is a plug-in that runs but has not picked up
        /// the switch. Null (the default) is a plug-in that follows the settings at once.
        /// </param>
        public Rig(
            Output output,
            RegistryFacts? registry = null,
            bool pluginRuns = true,
            bool protectionOn = true,
            double ceilingDb = -20.0,
            double volumeDb = -10.0,
            bool safeMode = false,
            bool staleFile = false,
            bool? pluginSaysOn = null)
        {
            _output = output;
            _registry = registry ?? HeadphonesInstalled;
            _pluginRuns = pluginRuns;
            _pluginSaysOn = pluginSaysOn;
            _volumeDb = volumeDb;
            _safeMode = safeMode;
            _staleFile = staleFile;
            _settings = new Settings { SettingsVersion = 2, CeilingDb = ceilingDb, ProtectionEnabled = protectionOn };
            _monitor.OverClosed += e => Events.Add(new Routed(e, _state!, _stateAt));
        }

        public List<Routed> Events { get; } = new();

        /// <summary>The protection state at every protection tick.</summary>
        public List<ProtectionState> States { get; } = new();

        /// <summary>The latest protection tick at which the plug-in wrote fresh telemetry.</summary>
        public DateTimeOffset LastFreshAt { get; private set; }

        /// <summary>The plug-in stops: from the next protection tick on, there is no fresh telemetry.</summary>
        public void StopPlugin() => _pluginRuns = false;

        /// <summary>The plug-in picks up the switch: from the next protection tick on, it reports what the settings ask.</summary>
        public void PluginCatchesUp() => _pluginSaysOn = null;

        public void Play(double peakDbfs, int ms) => Run(peakDbfs, ms);

        public void Silence(int ms) => Run(null, ms);

        private void Run(double? peakDbfs, int ms)
        {
            for (var end = _ms + ms; _ms < end; _ms += 10)
            {
                Step(peakDbfs);
            }
        }

        private void Step(double? peakDbfs)
        {
            var now = T.At(_ms);
            var snapshot = new AudioSnapshot(
                "{0.0.0.00000000}." + _output.Key,
                _output.Name,
                Muted: false,
                VolumeDb: _volumeDb,
                VolumePercent: 20,
                PeakDbfs: peakDbfs ?? -120.0,
                Apps: peakDbfs is { } peak ? new[] { new AppLevel("Zoom", 1234, peak) } : Array.Empty<AppLevel>());

            if (_ms % 100 == 0)
            {
                ProtectionTick(snapshot, now);
            }

            _monitor.Process(snapshot, _settings, now);
        }

        // What Sampler.Evaluate does: the output that was read is the watched one, and the tracker and the engine are fed on every tick.
        private void ProtectionTick(AudioSnapshot snapshot, DateTimeOffset now)
        {
            var watched = new WatchedOutput(snapshot.DeviceId, DeviceLabels.FriendlyName(snapshot.DeviceName));
            var cap = ProtectionConfigFile.EffectiveCapDb(_settings.CeilingDb);
            var reportsOn = _pluginSaysOn ?? _settings.ProtectionEnabled;
            var flags = (reportsOn ? 5u : 1u) | (_safeMode ? 2u : 0u);
            TelemetrySnapshot? tel = null;
            if (_pluginRuns)
            {
                tel = ProtectionHealthTests.Telemetry(flags: flags, capDb: cap, at: now.UtcDateTime);
                LastFreshAt = now;
            }
            else if (_staleFile)
            {
                tel = ProtectionHealthTests.Telemetry(flags: flags, capDb: cap, at: now.UtcDateTime.AddHours(-1));
            }

            var health = _tracker.Evaluate(_registry, tel, now, _settings.ProtectionEnabled, cap, Pacing.IsActive(snapshot), watched);
            _state = _protection.Process(tel, health, snapshot.Apps, cap, now);
            _stateAt = now;
            States.Add(_state);
        }
    }

    // ---- An output that has no plug-in: the event is raised, however the sound ends ----

    [Theory]
    [InlineData(300, true)]
    [InlineData(800, true)]
    [InlineData(1500, true)]
    [InlineData(300, false)]
    [InlineData(800, false)]
    [InlineData(1500, false)]
    public void UncoveredOutput_LoudThenSilence_TheOverEventIsRaised(int loudMs, bool pluginRuns)
    {
        var rig = new Rig(Speakers, pluginRuns: pluginRuns);

        rig.Silence(1000);
        rig.Play(LoudDbfs, loudMs);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        // The sound is over by the time the event closes, so the status is back to protected or idle: that was the case that got dropped.
        Assert.True(routed.State.ShowsProtection, routed.State.StatusText);
        Assert.False(routed.Dropped);
        Assert.False(routed.State.WatchedOutputCovered);
    }

    [Fact]
    public void UncoveredOutput_TheTestTone_RaisesTheOverEvent()
    {
        // The panel's test tone: 1 kHz at -20 dBFS for 2 s, at the Windows volume, with the cap dragged below the level it gives at the ear.
        var rig = new Rig(Speakers, ceilingDb: -50.0, volumeDb: -23.7);

        rig.Silence(1000);
        rig.Play(-20.0, 2000);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.True(routed.State.ShowsProtection, routed.State.StatusText);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void UncoveredOutput_LoudThenQuietButStillPlaying_TheOverEventIsRaised()
    {
        var rig = new Rig(Speakers);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 1500);
        rig.Play(QuietDbfs, 1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.NotProtected, routed.State.Status);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void UncoveredOutput_WhenTheFactsDoNotSayWhichOutputTheyAreAbout_CountsAsCovered()
    {
        // Facts without an endpoint id cannot rule an output out, so the output is treated as one that has the plug-in, as before.
        var rig = new Rig(Speakers, ProtectionHealthTests.Registry(true, ProtectionHealthTests.Endpoint("Headphones")));

        rig.Silence(1000);
        rig.Play(LoudDbfs, 800);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.True(routed.Dropped);
        Assert.True(routed.State.WatchedOutputCovered);
    }

    // ---- An output that has the plug-in, with protection showing: the event is dropped, as before ----

    [Theory]
    [InlineData(300)]
    [InlineData(800)]
    [InlineData(1500)]
    public void CoveredOutput_WithProtectionShowing_TheOverEventIsStillDropped(int loudMs)
    {
        var rig = new Rig(Headphones);

        rig.Silence(1000);
        rig.Play(LoudDbfs, loudMs);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.True(routed.State.ShowsProtection, routed.State.StatusText);
        Assert.True(routed.State.WatchedOutputCovered);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.True(routed.State.PluginEnabled);
        Assert.True(routed.Dropped);
    }

    [Fact]
    public void CoveredOutput_WhileTheSoundGoesOn_TheOverEventIsStillDropped()
    {
        var rig = new Rig(Headphones);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 1500);
        rig.Play(QuietDbfs, 1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Protected, routed.State.Status);
        Assert.True(routed.Dropped);
    }

    // ---- Down and not installed: the event is raised, as in the older mode ----

    [Fact]
    public void CoveredOutput_InDown_TheOverEventIsRaised()
    {
        // The test key is off, so Windows will not load the plug-in: that is Down whether or not sound plays.
        var rig = new Rig(Headphones, ProtectionHealthTests.Registry(false, ProtectionHealthTests.Endpoint("Headphones", id: HeadphonesKey)));

        rig.Silence(1000);
        rig.Play(LoudDbfs, 300);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Down, routed.State.Status);
        Assert.True(routed.State.WatchedOutputCovered);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void CoveredOutput_PluginNotRunning_WhileTheSoundGoesOn_TheOverEventIsRaised()
    {
        // "The plug-in is not running" is shown while sound plays on an output that has it.
        var rig = new Rig(Headphones, pluginRuns: false);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 1500);
        rig.Play(QuietDbfs, 1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Down, routed.State.Status);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void NotInstalled_TheOverEventIsRaised()
    {
        var rig = new Rig(Speakers, new RegistryFacts(true, Array.Empty<EndpointFacts>()), pluginRuns: false);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 300);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.NotInstalled, routed.State.Status);
        Assert.False(routed.Dropped);
    }

    // ---- Fix round 4, ruling 22: the event is dropped only while the plug-in is actually protecting the watched output ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchedOff_TheOverEventIsRaised_OnAnyOutput(bool onHeadphones)
    {
        // The plug-in runs, and Headphones are an output that has it, but with the switch off nothing is turned down.
        var rig = new Rig(onHeadphones ? Headphones : Speakers, protectionOn: false);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 800);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Off, routed.State.Status);
        Assert.Equal(onHeadphones, routed.State.WatchedOutputCovered);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void CoveredOutput_InSafeMode_TheOverEventIsRaised()
    {
        // Safe mode is Down whether or not sound plays. The plug-in is running, so only the status can be what raises the event.
        var rig = new Rig(Headphones, safeMode: true);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 800);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Down, routed.State.Status);
        Assert.True(routed.State.WatchedOutputCovered);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void CoveredOutput_WindowsSwitchedTheEffectsOff_TheOverEventIsRaised()
    {
        var rig = new Rig(
            Headphones,
            ProtectionHealthTests.Registry(true, ProtectionHealthTests.Endpoint("Headphones", effectsDisabled: true, id: HeadphonesKey)));

        rig.Silence(1000);
        rig.Play(LoudDbfs, 800);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Down, routed.State.Status);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.False(routed.Dropped);
    }

    [Fact]
    public void NotInstalled_EvenWhileAPluginRuns_TheOverEventIsRaised()
    {
        // Nothing is listed in the registry, but there is fresh telemetry: only the status can be what raises the event.
        var rig = new Rig(Headphones, new RegistryFacts(true, Array.Empty<EndpointFacts>()));

        rig.Silence(1000);
        rig.Play(LoudDbfs, 800);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.NotInstalled, routed.State.Status);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.False(routed.Dropped);
    }

    // The plug-in is not running: there is no telemetry file, or there is one that nothing has updated for an hour. The status is Down
    // while the sound plays, but a loud sound that ends in silence closes its event in Idle, which looks like protection (ruling 21 dropped it).
    [Theory]
    [InlineData(300, false)]
    [InlineData(800, false)]
    [InlineData(1500, false)]
    [InlineData(300, true)]
    [InlineData(800, true)]
    [InlineData(1500, true)]
    public void CoveredOutput_ThePluginIsDead_LoudThenSilence_TheOverEventIsRaised(int loudMs, bool staleFile)
    {
        var rig = new Rig(Headphones, pluginRuns: false, staleFile: staleFile);

        rig.Silence(1000);
        rig.Play(LoudDbfs, loudMs);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Idle, routed.State.Status);
        Assert.True(routed.State.ShowsProtection);
        Assert.True(routed.State.WatchedOutputCovered);
        Assert.False(routed.State.PluginSeenRunning);
        Assert.False(routed.Dropped);
    }

    // The plug-in stops writing at a made-up moment. Its last fresh tick is LastFreshAt, and the window is 3 s from there. A 300 ms burst
    // starts on a tick; its event closes 500 ms after the last loud step, and the tray reads the state of the latest 100 ms tick before
    // that, which is 700 ms after the burst began. So the silence between the stop and the burst puts that state where the test wants it.
    [Theory]
    [InlineData(2900, true)]
    [InlineData(3000, true)]
    [InlineData(3100, false)]
    public void CoveredOutput_ThePluginStopsRunning_TheOverEventIsDroppedFor3Seconds_ThenRaised(int msSinceLastFresh, bool dropped)
    {
        var rig = new Rig(Headphones);

        rig.Silence(1000);
        rig.StopPlugin();
        rig.Silence(msSinceLastFresh - 800);
        rig.Play(LoudDbfs, 300);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(msSinceLastFresh, (routed.StateAt - rig.LastFreshAt).TotalMilliseconds); // the timeline is the one meant
        Assert.Equal(ProtectionStatus.Idle, routed.State.Status);
        Assert.True(routed.State.WatchedOutputCovered);
        Assert.Equal(dropped, routed.State.PluginSeenRunning);
        Assert.Equal(dropped, routed.Dropped);
    }

    // ---- The flag goes by the output, not by the sound ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFlag_StaysTheSame_WhileTheSoundStartsAndStops(bool covered)
    {
        var rig = new Rig(covered ? Headphones : Speakers);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 1500);
        rig.Silence(1000);
        rig.Play(QuietDbfs, 1000);
        rig.Silence(1000);

        Assert.All(rig.States, s => Assert.Equal(covered, s.WatchedOutputCovered));
        Assert.True(rig.States.Select(s => s.Status).Distinct().Count() >= 2, "the sound has to change the status, or this proves nothing");
    }

    // ---- Final-review fix, ruling 26 (I1): the plug-in runs, but it reports itself switched off ----

    private const string IdleText = "Protected (nothing playing)";
    private const string SwitchedOffText = "The plug-in is switched off: your setting has not reached it";

    // The switch is on and the plug-in has not picked it up (its header says off), from the very start. The state is green for the first
    // 3 s and Down after that. Either way nothing is turned down, so the event is raised, at once and with no grace: in the first second
    // too. The tray reads the state of the latest 100 ms tick before the close; for a 300 ms burst that is 700 ms after the burst began.
    [Theory]
    [InlineData(0, 700, ProtectionStatus.Idle, IdleText)]             // in the first second
    [InlineData(1000, 1700, ProtectionStatus.Idle, IdleText)]
    [InlineData(2000, 2700, ProtectionStatus.Idle, IdleText)]
    [InlineData(2200, 2900, ProtectionStatus.Idle, IdleText)]         // 2.9 s into the mismatch: still green
    [InlineData(2300, 3000, ProtectionStatus.Idle, IdleText)]         // 3.0 s: not more than 3 s
    [InlineData(2400, 3100, ProtectionStatus.Down, SwitchedOffText)]  // 3.1 s
    [InlineData(4000, 4700, ProtectionStatus.Down, SwitchedOffText)]
    public void CoveredOutput_ThePluginRunsButIsOff_LoudThenSilence_TheOverEventIsRaised(
        int silenceMs, int stateAtMs, ProtectionStatus status, string text)
    {
        var rig = new Rig(Headphones, pluginSaysOn: false);

        rig.Silence(silenceMs);
        rig.Play(LoudDbfs, 300);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(T.At(stateAtMs), routed.StateAt); // the timeline is the one meant
        Assert.Equal(status, routed.State.Status);
        Assert.Equal(text, routed.State.StatusText);
        Assert.True(routed.State.WatchedOutputCovered);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.False(routed.State.PluginEnabled);
        Assert.False(routed.Dropped);
    }

    [Theory]
    [InlineData(1000, ProtectionStatus.Protected, "Protected: cap -20.0 dB")]  // green: the grace is not over
    [InlineData(3000, ProtectionStatus.Down, SwitchedOffText)]
    public void CoveredOutput_ThePluginRunsButIsOff_WhileTheSoundGoesOn_TheOverEventIsRaised(int silenceMs, ProtectionStatus status, string text)
    {
        var rig = new Rig(Headphones, pluginSaysOn: false);

        rig.Silence(silenceMs);
        rig.Play(LoudDbfs, 800);
        rig.Play(QuietDbfs, 1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(status, routed.State.Status);
        Assert.Equal(text, routed.State.StatusText);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.False(routed.State.PluginEnabled);
        Assert.False(routed.Dropped);
    }

    // The other side of the same rule: a plug-in that reports itself on drops the event from the very first tick, with no grace.
    [Theory]
    [InlineData(0, 700)]
    [InlineData(1000, 1700)]
    public void CoveredOutput_ThePluginIsOn_InTheFirstSeconds_TheOverEventIsDropped(int silenceMs, int stateAtMs)
    {
        var rig = new Rig(Headphones);

        rig.Silence(silenceMs);
        rig.Play(LoudDbfs, 300);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(T.At(stateAtMs), routed.StateAt);
        Assert.Equal(ProtectionStatus.Idle, routed.State.Status);
        Assert.True(routed.State.PluginSeenRunning);
        Assert.True(routed.State.PluginEnabled);
        Assert.True(routed.Dropped);
    }

    [Fact]
    public void CoveredOutput_ThePluginCatchesUp_AfterTheDownStatus_TheNextOverEventIsDropped()
    {
        var rig = new Rig(Headphones, pluginSaysOn: false);

        rig.Silence(4000);
        rig.Play(LoudDbfs, 300);  // the plug-in is still off, and has been for more than 3 s
        rig.Silence(1500);
        rig.PluginCatchesUp();
        rig.Silence(500);
        rig.Play(LoudDbfs, 300);  // now it reports itself on
        rig.Silence(1500);

        Assert.Equal(2, rig.Events.Count);
        Assert.Equal(ProtectionStatus.Down, rig.Events[0].State.Status);
        Assert.False(rig.Events[0].State.PluginEnabled);
        Assert.False(rig.Events[0].Dropped);
        Assert.Equal(ProtectionStatus.Idle, rig.Events[1].State.Status);
        Assert.Equal(IdleText, rig.Events[1].State.StatusText);
        Assert.True(rig.Events[1].State.PluginEnabled);
        Assert.True(rig.Events[1].Dropped);
    }

    // The plug-in was off when it stopped. It still counts as seen running for 3 s, but what it last said was "off", and that is
    // remembered for the same 3 s: the event is raised inside the window as well as outside it. (When it was on, the existing test
    // above has it dropped inside the window and raised outside.)
    [Theory]
    [InlineData(2900)]
    [InlineData(3000)]
    [InlineData(3100)]
    public void CoveredOutput_ThePluginWasOff_ThenStopsRunning_TheOverEventIsRaised_InsideTheWindowToo(int msSinceLastFresh)
    {
        var rig = new Rig(Headphones, pluginSaysOn: false);

        rig.Silence(1000);
        rig.StopPlugin();
        rig.Silence(msSinceLastFresh - 800);
        rig.Play(LoudDbfs, 300);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(msSinceLastFresh, (routed.StateAt - rig.LastFreshAt).TotalMilliseconds); // the timeline is the one meant
        Assert.Equal(ProtectionStatus.Idle, routed.State.Status);
        Assert.Equal(msSinceLastFresh <= 3000, routed.State.PluginSeenRunning);
        Assert.False(routed.State.PluginEnabled);
        Assert.False(routed.Dropped);
    }

    // The reverse case is as it was: the switch is off, the plug-in has not noticed, and rule 4 (Off) is what raises the event.
    [Fact]
    public void CoveredOutput_TheSwitchIsOffButThePluginIsStillOn_TheOverEventIsRaised_AsBefore()
    {
        var rig = new Rig(Headphones, protectionOn: false, pluginSaysOn: true);

        rig.Silence(1000);
        rig.Play(LoudDbfs, 800);
        rig.Silence(1500);

        var routed = Assert.Single(rig.Events);
        Assert.Equal(ProtectionStatus.Off, routed.State.Status);
        Assert.Equal("Protection is off", routed.State.StatusText);
        Assert.True(routed.State.PluginEnabled);
        Assert.False(routed.Dropped);
    }
}
