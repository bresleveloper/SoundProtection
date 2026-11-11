using SoundProtection.Core;

namespace SoundProtection.Tests;

public class HealthTrackerTests
{
    private const string PluginOnWarning = "The plug-in is still switched on";
    private const string PluginOffWarning = "The plug-in is still switched off";
    private const string PluginSwitchedOffStatus = "The plug-in is switched off: your setting has not reached it";
    private const string CapWarning = "The plug-in is still using cap -20.0 dB";
    private const string NotReadWarning = "The plug-in could not read your settings and uses cap -20.0 dB";

    private const string SpeakersNotProtected = "Sound on Speakers (Realtek(R) Audio) is not protected";

    private static readonly RegistryFacts Registry = ProtectionHealthTests.Registry(
        true,
        ProtectionHealthTests.Endpoint("Headphones", id: "{ab14dbc4-0c0d-4ebb-a2a1-1c2b6f6b3b2e}"));

    // An output that is not the listed one.
    private static readonly WatchedOutput Speakers =
        new("{0.0.0.00000000}.{9d6b1c2e-1111-4222-8333-444455556666}", "Speakers (Realtek(R) Audio)");

    // One protection tick at ms after the start. The telemetry is fresh at that moment; what the plug-in reports and what the
    // settings ask for are the arguments.
    private static HealthResult Tick(
        HealthTracker tracker,
        double ms,
        uint pluginFlags = 5,
        float pluginCap = -20.0f,
        bool wantOn = true,
        double wantCap = -20.0,
        bool pluginAlive = true,
        bool soundPlaying = false,
        WatchedOutput? watched = null,
        bool settingsNotSaved = false)
    {
        var now = T.At(ms);
        var tel = pluginAlive ? ProtectionHealthTests.Telemetry(flags: pluginFlags, capDb: pluginCap, at: now.UtcDateTime) : null;
        return tracker.Evaluate(Registry, tel, now, wantOn, wantCap, soundPlaying, watched, settingsNotSaved);
    }

    // The plug-in is wrong from the start. Every 100 ms until 3.0 s inclusive there is no warning; at 3.1 s there is this one.
    private static void AssertShownOnlyAfterMoreThanThreeSeconds(Func<double, HealthResult> tick, string expected)
    {
        for (var ms = 0; ms <= 3000; ms += 100)
        {
            Assert.True(tick(ms).Warnings.Count == 0, $"at {ms} ms the warning should not show yet");
        }

        Assert.Equal(new[] { expected }, tick(3100).Warnings);
        Assert.Equal(new[] { expected }, tick(9000).Warnings); // and it stays while the mismatch lasts
    }

    // ---- The 3 s gate, for each of the three kinds of mismatch ----

    [Fact]
    public void CapMismatch_IsShownOnlyAfterMoreThanThreeSeconds()
    {
        var tracker = new HealthTracker();

        AssertShownOnlyAfterMoreThanThreeSeconds(ms => Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0), CapWarning);
    }

    [Fact]
    public void ConfigNotRead_IsShownOnlyAfterMoreThanThreeSeconds()
    {
        var tracker = new HealthTracker();

        AssertShownOnlyAfterMoreThanThreeSeconds(ms => Tick(tracker, ms, pluginFlags: 4, pluginCap: -20.0f), NotReadWarning);
    }

    [Fact]
    public void PluginStillOn_IsShownOnlyAfterMoreThanThreeSeconds()
    {
        var tracker = new HealthTracker();

        AssertShownOnlyAfterMoreThanThreeSeconds(ms => Tick(tracker, ms, pluginFlags: 1 | 4, wantOn: false), PluginOnWarning);
    }

    // Ruling 26: with the switch on and the plug-in running but reporting itself off, the 3 s grace ends in a status, not a warning: Down,
    // with the text below. Until then the status is green and there is nothing to read.
    [Fact]
    public void PluginStillOff_IsDownOnlyAfterMoreThanThreeSeconds()
    {
        var tracker = new HealthTracker();

        for (var ms = 0; ms <= 3000; ms += 100)
        {
            var before = Tick(tracker, ms, pluginFlags: 1, wantOn: true);
            Assert.True(before.Status == ProtectionStatus.Idle, $"at {ms} ms the status should still be green: {before.Text}");
            Assert.Equal("Protected (nothing playing)", before.Text);
            Assert.Empty(before.Warnings);
            Assert.True(before.PluginSeenRunning);
            Assert.False(before.PluginEnabled); // no grace for this: alerts are raised from the first tick
        }

        foreach (var ms in new[] { 3100, 9000 }) // and it stays while the plug-in stays off
        {
            var after = Tick(tracker, ms, pluginFlags: 1, wantOn: true);
            Assert.Equal(ProtectionStatus.Down, after.Status);
            Assert.Equal(PluginSwitchedOffStatus, after.Text);
            Assert.Empty(after.Warnings); // not said a second time as "still switched off"
            Assert.False(after.PluginEnabled);
        }
    }

    [Fact]
    public void PluginStillOff_WithSoundPlaying_IsDownOnlyAfterMoreThanThreeSeconds()
    {
        var tracker = new HealthTracker();

        for (var ms = 0; ms <= 3000; ms += 100)
        {
            var before = Tick(tracker, ms, pluginFlags: 1, wantOn: true, soundPlaying: true);
            Assert.Equal(ms >= 600 ? ProtectionStatus.Protected : ProtectionStatus.Idle, before.Status); // the sound gate is 600 ms
            Assert.Empty(before.Warnings);
            Assert.False(before.PluginEnabled);
        }

        foreach (var ms in new[] { 3100, 9000 })
        {
            var after = Tick(tracker, ms, pluginFlags: 1, wantOn: true, soundPlaying: true);
            Assert.Equal(ProtectionStatus.Down, after.Status);
            Assert.Equal(PluginSwitchedOffStatus, after.Text);
            Assert.Empty(after.Warnings);
        }
    }

    [Fact]
    public void PluginStillOff_TheBoundary_IsMoreThanThreeSeconds_NotThreeSecondsExactly()
    {
        var tracker = new HealthTracker();
        Tick(tracker, 0, pluginFlags: 1);

        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 2900, pluginFlags: 1).Status);
        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 3000, pluginFlags: 1).Status);
        Assert.Equal(ProtectionStatus.Down, Tick(tracker, 3001, pluginFlags: 1).Status);
        Assert.Equal(ProtectionStatus.Down, Tick(tracker, 3100, pluginFlags: 1).Status);
    }

    [Fact]
    public void TheBoundary_IsMoreThanThreeSeconds_NotThreeSecondsExactly()
    {
        var tracker = new HealthTracker();
        Tick(tracker, 0, pluginCap: -20.0f, wantCap: -35.0);

        Assert.Empty(Tick(tracker, 2900, pluginCap: -20.0f, wantCap: -35.0).Warnings);
        Assert.Empty(Tick(tracker, 3000, pluginCap: -20.0f, wantCap: -35.0).Warnings);
        Assert.Single(Tick(tracker, 3001, pluginCap: -20.0f, wantCap: -35.0).Warnings);
    }

    // ---- Clearing, restarting ----

    [Fact]
    public void TheWarning_IsClearedAtOnce_WhenThePluginCatchesUp()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3200; ms += 100)
        {
            Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0);
        }

        Assert.Single(Tick(tracker, 3300, pluginCap: -20.0f, wantCap: -35.0).Warnings);

        Assert.Empty(Tick(tracker, 3400, pluginCap: -35.0f, wantCap: -35.0).Warnings);
    }

    [Fact]
    public void AMismatchThatEndsAndComesBack_IsCountedFromTheStartAgain()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 2900; ms += 100)
        {
            Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0);
        }

        Tick(tracker, 3000, pluginCap: -35.0f, wantCap: -35.0); // caught up for a moment

        for (var ms = 3100; ms <= 6000; ms += 100)
        {
            Assert.Empty(Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0).Warnings); // 2.9 s since it came back
        }

        Assert.Single(Tick(tracker, 6200, pluginCap: -20.0f, wantCap: -35.0).Warnings);
    }

    [Fact]
    public void ANewRequest_StartsTheCountAgain()
    {
        // While the slider is being dragged the plug-in is always a little behind; that is not a plug-in that ignores the settings.
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 2900; ms += 100)
        {
            Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0);
        }

        Assert.Empty(Tick(tracker, 3100, pluginCap: -20.0f, wantCap: -36.0).Warnings);
        Assert.Empty(Tick(tracker, 6000, pluginCap: -20.0f, wantCap: -36.0).Warnings);
        Assert.Single(Tick(tracker, 6200, pluginCap: -20.0f, wantCap: -36.0).Warnings);
    }

    [Fact]
    public void WhenThePluginStopsRunning_TheCountIsLost_AndTheWarningGoes()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3200; ms += 100)
        {
            Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0);
        }

        Assert.Single(Tick(tracker, 3300, pluginCap: -20.0f, wantCap: -35.0).Warnings);

        Assert.Empty(Tick(tracker, 3400, pluginCap: -20.0f, wantCap: -35.0, pluginAlive: false).Warnings);
        Assert.Empty(Tick(tracker, 3500, pluginCap: -20.0f, wantCap: -35.0).Warnings); // back, and counting from here
        Assert.Single(Tick(tracker, 6600, pluginCap: -20.0f, wantCap: -35.0).Warnings);
    }

    [Fact]
    public void AClockThatStepsBack_StartsTheCountAgain()
    {
        var tracker = new HealthTracker();
        for (var ms = 10_000; ms <= 13_200; ms += 100)
        {
            Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0);
        }

        Assert.Empty(Tick(tracker, 500, pluginCap: -20.0f, wantCap: -35.0).Warnings);
    }

    // ---- The pieces together ----

    [Fact]
    public void ThePluginsCap_NotTheSetting_IsInTheStatusText()
    {
        var tracker = new HealthTracker();
        Tick(tracker, 0, soundPlaying: true, pluginCap: -30.0f, wantCap: -20.0);

        var result = Tick(tracker, 700, soundPlaying: true, pluginCap: -30.0f, wantCap: -20.0);

        Assert.Equal(ProtectionStatus.Protected, result.Status);
        Assert.Equal("Protected: cap -30.0 dB", result.Text);
    }

    [Fact]
    public void Sound_IsJudgedOnlyAfterTheGracePeriod()
    {
        // A sound that has just started has no plug-in slot for a moment, so "not running" waits for 600 ms of sound.
        var tracker = new HealthTracker();

        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 0, pluginAlive: false, soundPlaying: true).Status);
        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 599, pluginAlive: false, soundPlaying: true).Status);
        Assert.Equal(ProtectionStatus.Down, Tick(tracker, 600, pluginAlive: false, soundPlaying: true).Status);
        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 700, pluginAlive: false, soundPlaying: false).Status);
    }

    [Fact]
    public void TheWatchedOutputAndTheSettingsFailure_ReachTheRules()
    {
        var tracker = new HealthTracker();
        Tick(tracker, 0, pluginAlive: false, soundPlaying: true, watched: Speakers, settingsNotSaved: true);

        var result = Tick(tracker, 700, pluginAlive: false, soundPlaying: true, watched: Speakers, settingsNotSaved: true);

        Assert.Equal(ProtectionStatus.NotProtected, result.Status); // not Down, which is what an output with the plug-in would give
        Assert.Equal(SpeakersNotProtected, result.Text);
        Assert.Equal(new[] { "Your settings could not be saved for the plug-in" }, result.Warnings);

        var later = Tick(tracker, 800, pluginAlive: false, soundPlaying: true, watched: Speakers, settingsNotSaved: false);
        Assert.Empty(later.Warnings);
    }

    [Fact]
    public void TheNotProtectedState_WaitsForTheSoundGate_AndEndsWhenTheSoundStops()
    {
        // The same 600 ms of sound that "not running" waits for: a click that is gone before then is not judged.
        var tracker = new HealthTracker();

        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 0, pluginAlive: false, soundPlaying: true, watched: Speakers).Status);
        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 599, pluginAlive: false, soundPlaying: true, watched: Speakers).Status);

        var sound = Tick(tracker, 600, pluginAlive: false, soundPlaying: true, watched: Speakers);
        Assert.Equal(ProtectionStatus.NotProtected, sound.Status);
        Assert.Equal(SpeakersNotProtected, sound.Text);

        Assert.Equal(ProtectionStatus.Idle, Tick(tracker, 700, pluginAlive: false, soundPlaying: false, watched: Speakers).Status);
    }

    [Fact]
    public void ARunningPluginWithMatchingSettings_HasNoWarnings()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 5000; ms += 100)
        {
            Assert.Empty(Tick(tracker, ms, pluginFlags: 5, pluginCap: -20.0f, wantOn: true, wantCap: -20.0, soundPlaying: true).Warnings);
        }
    }

    // ---- Fix round 4, ruling 22: the plug-in counts as seen running until 3 s after the last tick at which it ran ----

    private static bool SeenRunning(HealthTracker tracker, double ms, bool pluginAlive, bool soundPlaying = false) =>
        Tick(tracker, ms, pluginAlive: pluginAlive, soundPlaying: soundPlaying).PluginSeenRunning;

    [Fact]
    public void ThePlugin_IsNotSeenRunning_UntilItHasRun()
    {
        var tracker = new HealthTracker();

        for (var ms = 0; ms <= 5000; ms += 100)
        {
            Assert.False(SeenRunning(tracker, ms, pluginAlive: false), $"at {ms} ms");
        }

        Assert.True(SeenRunning(tracker, 5100, pluginAlive: true));
    }

    [Fact]
    public void TheWindow_IsThreeSeconds_FromTheLastTickAtWhichThePluginRan()
    {
        var tracker = new HealthTracker();
        Assert.True(SeenRunning(tracker, 1000, pluginAlive: true));
        Assert.True(SeenRunning(tracker, 1100, pluginAlive: true)); // the last tick at which it ran

        Assert.True(SeenRunning(tracker, 1200, pluginAlive: false));
        Assert.True(SeenRunning(tracker, 4000, pluginAlive: false));  // 2.9 s after
        Assert.True(SeenRunning(tracker, 4100, pluginAlive: false));  // 3.0 s after: still inside
        Assert.False(SeenRunning(tracker, 4101, pluginAlive: false)); // a millisecond more
        Assert.False(SeenRunning(tracker, 4200, pluginAlive: false)); // 3.1 s after
        Assert.False(SeenRunning(tracker, 9000, pluginAlive: false)); // and it stays out
    }

    [Fact]
    public void ATickAtWhichThePluginRan_StartsTheWindowAgain()
    {
        var tracker = new HealthTracker();
        Assert.True(SeenRunning(tracker, 0, pluginAlive: true));
        Assert.True(SeenRunning(tracker, 2900, pluginAlive: false));
        Assert.True(SeenRunning(tracker, 3000, pluginAlive: true)); // back for a moment

        Assert.True(SeenRunning(tracker, 5900, pluginAlive: false));  // 2.9 s after that
        Assert.False(SeenRunning(tracker, 6100, pluginAlive: false)); // 3.1 s after that
    }

    [Fact]
    public void TheWindow_GoesByThePluginAlone_NotByTheStatusOrTheSound()
    {
        // The plug-in stops while sound plays. After the 600 ms sound gate the status is Down, and the plug-in is still seen
        // running for the 3 s; the window is not cut short by the status, and it is not held open by the sound.
        var tracker = new HealthTracker();
        Tick(tracker, 0, soundPlaying: true);
        Tick(tracker, 100, soundPlaying: true); // the last tick at which it ran

        var inside = Tick(tracker, 1000, pluginAlive: false, soundPlaying: true);
        Assert.Equal(ProtectionStatus.Down, inside.Status);
        Assert.True(inside.PluginSeenRunning);

        var outside = Tick(tracker, 3200, pluginAlive: false, soundPlaying: true);
        Assert.Equal(ProtectionStatus.Down, outside.Status);
        Assert.False(outside.PluginSeenRunning);
    }

    [Fact]
    public void TheWindow_IsTheSame_WhetherOrNotSoundIsPlaying()
    {
        foreach (var soundPlaying in new[] { false, true })
        {
            var tracker = new HealthTracker();
            Tick(tracker, 0, soundPlaying: soundPlaying);

            Assert.True(SeenRunning(tracker, 2900, pluginAlive: false, soundPlaying: soundPlaying), $"sound {soundPlaying}, 2.9 s");
            Assert.False(SeenRunning(tracker, 3100, pluginAlive: false, soundPlaying: soundPlaying), $"sound {soundPlaying}, 3.1 s");
        }
    }

    [Fact]
    public void AClockThatStepsBack_ForgetsWhenThePluginWasSeen()
    {
        var tracker = new HealthTracker();
        Assert.True(SeenRunning(tracker, 10_000, pluginAlive: true));

        Assert.False(SeenRunning(tracker, 500, pluginAlive: false));    // the clock was set back: nothing is trusted
        Assert.False(SeenRunning(tracker, 10_050, pluginAlive: false)); // and when it reaches the old stamp again, that is not a fresh sighting
    }

    // ---- Final-review fix, ruling 26 (I1): whether the plug-in reported itself on, kept over the same 3 s window ----

    private static bool PluginEnabled(HealthTracker tracker, double ms, uint pluginFlags = 5, bool pluginAlive = true) =>
        Tick(tracker, ms, pluginFlags: pluginFlags, pluginAlive: pluginAlive).PluginEnabled;

    [Fact]
    public void PluginEnabled_FollowsTheHeader_WhileThePluginRuns_WithNoGrace()
    {
        var tracker = new HealthTracker();

        Assert.True(PluginEnabled(tracker, 0, pluginFlags: 5));
        Assert.True(PluginEnabled(tracker, 100, pluginFlags: 5));
        Assert.False(PluginEnabled(tracker, 200, pluginFlags: 1)); // off: seen at once, the grace is only for the words on screen
        Assert.False(PluginEnabled(tracker, 300, pluginFlags: 1));
        Assert.True(PluginEnabled(tracker, 400, pluginFlags: 5));  // and on again at once
    }

    [Fact]
    public void PluginEnabled_IsNotTrue_UntilThePluginHasRun()
    {
        var tracker = new HealthTracker();

        for (var ms = 0; ms <= 5000; ms += 100)
        {
            Assert.False(PluginEnabled(tracker, ms, pluginAlive: false), $"at {ms} ms");
        }

        Assert.True(PluginEnabled(tracker, 5100, pluginFlags: 5));
    }

    [Fact]
    public void PluginEnabled_StaysTrue_ForThreeSecondsAfterTheLastTickAtWhichThePluginRanOn()
    {
        var tracker = new HealthTracker();
        Assert.True(PluginEnabled(tracker, 1000, pluginFlags: 5));
        Assert.True(PluginEnabled(tracker, 1100, pluginFlags: 5)); // the last tick at which it ran

        Assert.True(PluginEnabled(tracker, 1200, pluginAlive: false));
        Assert.True(PluginEnabled(tracker, 4000, pluginAlive: false));  // 2.9 s after
        Assert.True(PluginEnabled(tracker, 4100, pluginAlive: false));  // 3.0 s after: still inside
        Assert.False(PluginEnabled(tracker, 4101, pluginAlive: false)); // a millisecond more
        Assert.False(PluginEnabled(tracker, 4200, pluginAlive: false)); // 3.1 s after
        Assert.False(PluginEnabled(tracker, 9000, pluginAlive: false)); // and it stays out
    }

    [Fact]
    public void PluginEnabled_StaysFalse_ThroughTheWindow_WhenThePluginLastRanOff()
    {
        var tracker = new HealthTracker();
        Assert.False(PluginEnabled(tracker, 1000, pluginFlags: 1));

        foreach (var ms in new[] { 1100, 3900, 4000, 4100, 4200 })
        {
            Assert.False(PluginEnabled(tracker, ms, pluginAlive: false), $"at {ms} ms");
        }
    }

    [Fact]
    public void PluginEnabled_ComesFromTheLastTickAtWhichThePluginRan_NotFromAnEarlierOne()
    {
        // On, then off, then gone: it was last seen off, so it counts as off for the window.
        var wasOff = new HealthTracker();
        Assert.True(PluginEnabled(wasOff, 0, pluginFlags: 5));
        Assert.False(PluginEnabled(wasOff, 100, pluginFlags: 1));
        Assert.False(PluginEnabled(wasOff, 200, pluginAlive: false));
        Assert.False(PluginEnabled(wasOff, 3000, pluginAlive: false));

        // Off, then on, then gone: it was last seen on, for the window counted from that tick.
        var wasOn = new HealthTracker();
        Assert.False(PluginEnabled(wasOn, 0, pluginFlags: 1));
        Assert.True(PluginEnabled(wasOn, 100, pluginFlags: 5));
        Assert.True(PluginEnabled(wasOn, 200, pluginAlive: false));
        Assert.True(PluginEnabled(wasOn, 3100, pluginAlive: false));  // 3.0 s after the last tick at which it ran
        Assert.False(PluginEnabled(wasOn, 3200, pluginAlive: false)); // 3.1 s
    }

    [Fact]
    public void PluginEnabled_IsTheSame_WhetherOrNotSoundIsPlaying()
    {
        foreach (var soundPlaying in new[] { false, true })
        {
            var tracker = new HealthTracker();
            Tick(tracker, 0, pluginFlags: 5, soundPlaying: soundPlaying);

            Assert.True(Tick(tracker, 2900, pluginAlive: false, soundPlaying: soundPlaying).PluginEnabled, $"sound {soundPlaying}, 2.9 s");
            Assert.False(Tick(tracker, 3100, pluginAlive: false, soundPlaying: soundPlaying).PluginEnabled, $"sound {soundPlaying}, 3.1 s");
        }
    }

    [Fact]
    public void PluginEnabled_AClockThatStepsBack_ForgetsIt()
    {
        var tracker = new HealthTracker();
        Assert.True(PluginEnabled(tracker, 10_000, pluginFlags: 5));

        Assert.False(PluginEnabled(tracker, 500, pluginAlive: false));    // the clock was set back: nothing is trusted
        Assert.False(PluginEnabled(tracker, 10_050, pluginAlive: false)); // and the old stamp is not a fresh sighting
    }

    [Fact]
    public void PluginStillOff_ThePluginComingOnForAMoment_StartsTheCountAgain()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 2900; ms += 100)
        {
            Tick(tracker, ms, pluginFlags: 1);
        }

        Tick(tracker, 3000, pluginFlags: 5); // on for a moment

        for (var ms = 3100; ms <= 6000; ms += 100)
        {
            var result = Tick(tracker, ms, pluginFlags: 1);
            Assert.True(result.Status == ProtectionStatus.Idle, $"at {ms} ms, 2.9 s since it went off again: {result.Text}");
        }

        var down = Tick(tracker, 6200, pluginFlags: 1);
        Assert.Equal(ProtectionStatus.Down, down.Status);
        Assert.Equal(PluginSwitchedOffStatus, down.Text);
    }

    [Fact]
    public void PluginStillOff_ThePluginCatchingUpInTime_NeverGoesDown()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 2900; ms += 100)
        {
            Assert.Equal(ProtectionStatus.Idle, Tick(tracker, ms, pluginFlags: 1).Status);
        }

        var caught = Tick(tracker, 3000, pluginFlags: 5);
        Assert.Equal(ProtectionStatus.Idle, caught.Status);
        Assert.True(caught.PluginEnabled);

        for (var ms = 3100; ms <= 9000; ms += 100)
        {
            var result = Tick(tracker, ms, pluginFlags: 5);
            Assert.True(result.Status == ProtectionStatus.Idle, $"at {ms} ms: {result.Text}");
            Assert.Empty(result.Warnings);
            Assert.True(result.PluginEnabled);
        }
    }

    [Fact]
    public void PluginStillOff_TheSwitchBeingTurnedOn_StartsTheCount_FromThatMoment()
    {
        // The switch was off and so was the plug-in: nothing is wrong. When the switch goes on the plug-in is given its 3 s.
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 10_000; ms += 100)
        {
            Assert.Equal(ProtectionStatus.Off, Tick(tracker, ms, pluginFlags: 1, wantOn: false).Status);
        }

        for (var ms = 10_100; ms <= 13_100; ms += 100)
        {
            var result = Tick(tracker, ms, pluginFlags: 1, wantOn: true);
            Assert.True(result.Status == ProtectionStatus.Idle, $"at {ms} ms, the switch went on at 10.1 s: {result.Text}");
            Assert.Empty(result.Warnings);
        }

        var down = Tick(tracker, 13_200, pluginFlags: 1, wantOn: true);
        Assert.Equal(ProtectionStatus.Down, down.Status);
        Assert.Equal(PluginSwitchedOffStatus, down.Text);
        Assert.Empty(down.Warnings);
    }

    [Fact]
    public void PluginStillOff_ThePluginComingOnAfterTheSwitch_ClearsTheStatusAtOnce()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3200; ms += 100)
        {
            Tick(tracker, ms, pluginFlags: 1);
        }

        Assert.Equal(ProtectionStatus.Down, Tick(tracker, 3300, pluginFlags: 1).Status);

        var caught = Tick(tracker, 3400, pluginFlags: 5);
        Assert.Equal(ProtectionStatus.Idle, caught.Status);
        Assert.Equal("Protected (nothing playing)", caught.Text);
        Assert.Empty(caught.Warnings);
        Assert.True(caught.PluginEnabled);
    }

    [Fact]
    public void PluginStillOff_ThePluginStopping_ChangesTheStatusToNotRunning_NotToSwitchedOff()
    {
        // Down for "switched off" needs a plug-in that runs. Once it stops (and sound plays) the older rule 6 says so.
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3200; ms += 100)
        {
            Tick(tracker, ms, pluginFlags: 1, soundPlaying: true);
        }

        Assert.Equal(PluginSwitchedOffStatus, Tick(tracker, 3300, pluginFlags: 1, soundPlaying: true).Text);

        var gone = Tick(tracker, 3400, pluginAlive: false, soundPlaying: true);
        Assert.Equal(ProtectionStatus.Down, gone.Status);
        Assert.Equal("The plug-in is not running", gone.Text);
        Assert.False(gone.PluginEnabled);
    }

    [Theory]
    [InlineData(4u)]   // enabled, but it could not read the settings
    [InlineData(0u)]   // off, and it could not read the settings: that is the first kind of mismatch that is named, so it is the warning
    public void ConfigNotRead_StaysAWarning_AfterTheGrace_AsBuilt(uint pluginFlags)
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3000; ms += 100)
        {
            Tick(tracker, ms, pluginFlags: pluginFlags);
        }

        var result = Tick(tracker, 3100, pluginFlags: pluginFlags);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.Equal(new[] { NotReadWarning }, result.Warnings);
        Assert.Equal((pluginFlags & 4u) != 0, result.PluginEnabled);
    }

    [Fact]
    public void CapMismatch_StaysAWarning_AfterTheGrace_AsBuilt()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3000; ms += 100)
        {
            Tick(tracker, ms, pluginCap: -20.0f, wantCap: -35.0);
        }

        var result = Tick(tracker, 3100, pluginCap: -20.0f, wantCap: -35.0);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.Equal(new[] { CapWarning }, result.Warnings);
        Assert.True(result.PluginEnabled);
    }

    [Fact]
    public void TheReverseCase_PluginStillOn_WhileTheSwitchIsOff_IsStillAWarning_NotAStatus()
    {
        var tracker = new HealthTracker();
        for (var ms = 0; ms <= 3000; ms += 100)
        {
            var before = Tick(tracker, ms, pluginFlags: 5, wantOn: false);
            Assert.Equal(ProtectionStatus.Off, before.Status);
            Assert.Empty(before.Warnings);
            Assert.True(before.PluginEnabled);
        }

        foreach (var ms in new[] { 3100, 9000 })
        {
            var after = Tick(tracker, ms, pluginFlags: 5, wantOn: false);
            Assert.Equal(ProtectionStatus.Off, after.Status);
            Assert.Equal("Protection is off", after.Text);
            Assert.Equal(new[] { PluginOnWarning }, after.Warnings);
            Assert.True(after.PluginEnabled);
        }
    }
}
