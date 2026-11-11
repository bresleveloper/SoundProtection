using SoundProtection.Core;

namespace SoundProtection.Tests;

public class ProtectionEngineTests
{
    private static readonly HealthResult Healthy =
        new(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>());

    private readonly ProtectionEngine _engine = new();
    private readonly List<TurnDownEvent> _events = new();

    public ProtectionEngineTests() => _engine.TurnedDown += _events.Add;

    private static TelemetrySlot Leveler(double inputDb, double reductionDb, double ageMs = 0, DateTimeOffset? at = null) =>
        new(
            SlotKind.Leveler, 1, 48000, 2, ApoMode.Default, 0, 1000,
            (at ?? T.At(0)).UtcDateTime.AddMilliseconds(-ageMs),
            (float)inputDb, (float)reductionDb, (float)reductionDb, -20f);

    private static TelemetrySlot SafetyCap(double reductionDb, double ageMs = 0, DateTimeOffset? at = null) =>
        new(
            SlotKind.SafetyCap, 2, 48000, 2, ApoMode.Default, 0, 1000,
            (at ?? T.At(0)).UtcDateTime.AddMilliseconds(-ageMs),
            -10f, (float)reductionDb, (float)reductionDb, -20f);

    private static TelemetrySnapshot Telemetry(DateTimeOffset at, params TelemetrySlot[] slots) =>
        new(new TelemetryHeader(4242, 1, 1, at.UtcDateTime, 5, -20.0f, -8.0f), slots);

    private ProtectionState Process(TelemetrySnapshot? tel, DateTimeOffset now, params AppLevel[] apps) =>
        _engine.Process(tel, Healthy, apps, -20.0, now);

    /// <summary>One reading in which Zoom (or the given app) is being turned down by <paramref name="reductionDb"/>.</summary>
    private ProtectionState Reading(double ms, double reductionDb, string app = "Zoom", double inputDb = -10.0)
    {
        var at = T.At(ms);
        return Process(Telemetry(at, Leveler(inputDb, reductionDb, at: at)), at, new AppLevel(app, 1, -3.0));
    }

    // ---- Matching and totals ----

    [Fact]
    public void Match_PairsLoudestSlotWithLoudestApp()
    {
        var tel = Telemetry(
            T.At(0),
            Leveler(inputDb: -30, reductionDb: 0.0),
            Leveler(inputDb: -10, reductionDb: 6.0),
            SafetyCap(reductionDb: 1.5));

        var state = Process(tel, T.At(0), new AppLevel("Chrome", 2, -40.0), new AppLevel("Zoom", 1, -5.0));

        Assert.Equal(new[] { "Zoom", "Chrome" }, state.Apps.Select(a => a.Name).ToArray());
        T.Near(6.0, state.Apps[0].TurnDownDb);
        T.Near(0.0, state.Apps[1].TurnDownDb);
        T.Near(-10.0, state.LoudestInputDb);
        T.Near(6.0, state.TurnDownDb);
        T.Near(1.5, state.SafetyCapDb);
    }

    [Fact]
    public void State_CarriesTheHealthStatusAndText()
    {
        var health = new HealthResult(ProtectionStatus.Down, "The plug-in is not running", Array.Empty<string>());

        var state = _engine.Process(null, health, Array.Empty<AppLevel>(), -20.0, T.At(0));

        Assert.Equal(ProtectionStatus.Down, state.Status);
        Assert.Equal("The plug-in is not running", state.StatusText);
    }

    [Fact]
    public void NoTelemetry_GivesTheFloorAndNothingTurnedDown()
    {
        var state = Process(null, T.At(0), new AppLevel("Zoom", 1, -3.0));

        Assert.Equal(Db.Floor, state.LoudestInputDb);
        Assert.Equal(0.0, state.TurnDownDb);
        Assert.Equal(0.0, state.SafetyCapDb);
        Assert.Empty(state.Apps);
    }

    [Fact]
    public void StaleSlots_AreIgnored()
    {
        var tel = Telemetry(
            T.At(0),
            Leveler(inputDb: -10, reductionDb: 6.0, ageMs: 1001),
            SafetyCap(reductionDb: 3.0, ageMs: 1001));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0));

        Assert.Equal(Db.Floor, state.LoudestInputDb);
        Assert.Equal(0.0, state.TurnDownDb);
        Assert.Equal(0.0, state.SafetyCapDb);
        Assert.Empty(state.Apps);
    }

    [Fact]
    public void Slots_OneSecondOld_AreStillFresh()
    {
        var tel = Telemetry(T.At(0), Leveler(inputDb: -10, reductionDb: 6.0, ageMs: 1000));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0));

        T.Near(6.0, state.TurnDownDb);
    }

    [Fact]
    public void FreshSlotsAreUsed_WhenTheyAreMixedWithStaleOnes()
    {
        var tel = Telemetry(
            T.At(0),
            Leveler(inputDb: -5, reductionDb: 9.0, ageMs: 5000), // a stream that ended
            Leveler(inputDb: -25, reductionDb: 2.0));

        var state = Process(tel, T.At(0), new AppLevel("Chrome", 2, -20.0));

        T.Near(-25.0, state.LoudestInputDb);
        T.Near(2.0, state.TurnDownDb);
        var only = Assert.Single(state.Apps);
        Assert.Equal("Chrome", only.Name);
    }

    [Fact]
    public void ExtraSlots_BecomeOtherSound()
    {
        var tel = Telemetry(T.At(0), Leveler(-10, 4.0), Leveler(-20, 2.0), Leveler(-30, 0.5));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0));

        Assert.Equal(new[] { "Zoom", "Other sound", "Other sound" }, state.Apps.Select(a => a.Name).ToArray());
        T.Near(4.0, state.Apps[0].TurnDownDb);
        T.Near(2.0, state.Apps[1].TurnDownDb);
        T.Near(0.5, state.Apps[2].TurnDownDb);
    }

    [Fact]
    public void ExtraApps_AreNotListed()
    {
        var tel = Telemetry(T.At(0), Leveler(-10, 4.0));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0), new AppLevel("Chrome", 2, -20.0), new AppLevel("Teams", 3, -30.0));

        var only = Assert.Single(state.Apps);
        Assert.Equal("Zoom", only.Name);
    }

    [Fact]
    public void QuietSlots_AndQuietApps_TakeNoPartInMatching()
    {
        var tel = Telemetry(
            T.At(0),
            Leveler(inputDb: -70.0, reductionDb: 0.0),  // not above -70
            Leveler(inputDb: -69.9, reductionDb: 1.0));

        var state = Process(
            tel,
            T.At(0),
            new AppLevel("Ghost", 9, -90.0),            // not above -90
            new AppLevel("Zoom", 1, -50.0));

        var only = Assert.Single(state.Apps);
        Assert.Equal("Zoom", only.Name);
        T.Near(1.0, only.TurnDownDb);
    }

    [Fact]
    public void TheTotals_UseEveryFreshSlot_EvenOnesTooQuietToMatch()
    {
        // The leveler is still releasing its gain reduction after the sound has stopped.
        var tel = Telemetry(T.At(0), Leveler(inputDb: -110, reductionDb: 4.0));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0));

        T.Near(4.0, state.TurnDownDb);
        T.Near(-110.0, state.LoudestInputDb);
        Assert.Empty(state.Apps);
    }

    [Fact]
    public void SafetyCapSlots_AreNeverMatchedToApps_AndOnlyFeedTheSafetyCapTotal()
    {
        var tel = Telemetry(T.At(0), SafetyCap(reductionDb: 7.0), Leveler(-20, 0.0));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0));

        T.Near(7.0, state.SafetyCapDb);
        T.Near(0.0, state.TurnDownDb);
        T.Near(-20.0, state.LoudestInputDb);
        Assert.Single(state.Apps);
    }

    [Fact]
    public void NotANumber_Readings_AreIgnored()
    {
        var tel = Telemetry(
            T.At(0),
            Leveler(inputDb: double.NaN, reductionDb: double.NaN),
            Leveler(inputDb: -12, reductionDb: 3.0));

        var state = Process(tel, T.At(0), new AppLevel("Zoom", 1, -5.0));

        T.Near(-12.0, state.LoudestInputDb);
        T.Near(3.0, state.TurnDownDb);
        Assert.All(state.Apps, a => Assert.False(double.IsNaN(a.TurnDownDb)));
    }

    [Fact]
    public void NegativeReduction_CountsAsNothing()
    {
        var state = Process(Telemetry(T.At(0), Leveler(-10, -2.0), SafetyCap(-1.0)), T.At(0));

        Assert.Equal(0.0, state.TurnDownDb);
        Assert.Equal(0.0, state.SafetyCapDb);
    }

    // ---- The turn-down detector ----

    [Fact]
    public void TurnDown_EventAfterMergeGap()
    {
        for (var ms = 0; ms <= 1000; ms += 100)
        {
            Reading(ms, 3.0);
        }

        for (var ms = 1100; ms <= 1400; ms += 100)
        {
            Reading(ms, 0.0);
            Assert.Empty(_events); // still inside the 500 ms merge gap
        }

        Reading(1500, 0.0);

        var e = Assert.Single(_events);
        Assert.Equal(T.At(0), e.Start);
        Assert.Equal(TimeSpan.FromSeconds(1), e.Duration);
        T.Near(3.0, e.MaxTurnDownDb);
        Assert.Equal(-20.0, e.CapDb);
        Assert.Equal("Zoom", e.App);
    }

    [Fact]
    public void TurnDown_BelowThreshold_NoEvent()
    {
        for (var ms = 0; ms <= 2000; ms += 100)
        {
            Reading(ms, 0.8);
        }

        Reading(2600, 0.0);
        Reading(3200, 0.0);

        Assert.Empty(_events);
    }

    [Fact]
    public void TurnDown_ExactlyOneDb_OpensAnEvent()
    {
        Reading(0, 1.0);
        Reading(100, 1.0);
        Reading(700, 0.0);

        var e = Assert.Single(_events);
        T.Near(1.0, e.MaxTurnDownDb);
    }

    [Fact]
    public void TurnDown_BurstsCloserThanTheGapMerge_FartherApartAreSeparate()
    {
        Reading(0, 3.0);
        Reading(300, 0.0);
        Reading(400, 4.0);   // 400 ms after the last over reading at 0: merges
        Reading(1000, 0.0);  // closes the first episode
        Reading(2000, 3.0);  // far later: a new episode
        Reading(2600, 0.0);

        Assert.Equal(2, _events.Count);
        T.Near(4.0, _events[0].MaxTurnDownDb);
        Assert.Equal(TimeSpan.FromMilliseconds(400), _events[0].Duration);
        Assert.Equal(T.At(2000), _events[1].Start);
    }

    [Fact]
    public void TurnDown_SplitsAtFiveSeconds()
    {
        for (var ms = 0; ms <= 12000; ms += 100)
        {
            Reading(ms, 3.0);
        }

        Assert.Equal(2, _events.Count);
        Assert.Equal(T.At(0), _events[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), _events[0].Duration);
        Assert.Equal(T.At(5000), _events[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), _events[1].Duration);
    }

    [Fact]
    public void TurnDown_NamesTheAppThatWasTurnedDownMostAtTheMaximum()
    {
        Reading(0, 2.0, "Chrome");
        Reading(100, 5.0, "Zoom");
        Reading(200, 3.0, "Chrome");
        Reading(900, 0.0);

        var e = Assert.Single(_events);
        Assert.Equal("Zoom", e.App);
        T.Near(5.0, e.MaxTurnDownDb);
    }

    [Fact]
    public void TurnDown_WithNoMatchedApp_HasNoAppName()
    {
        var at = T.At(0);
        Process(Telemetry(at, Leveler(inputDb: -110, reductionDb: 4.0, at: at)), at); // an unmatched slot: no apps at all
        at = T.At(800);
        Process(Telemetry(at, Leveler(inputDb: -110, reductionDb: 0.0, at: at)), at);

        var e = Assert.Single(_events);
        Assert.Null(e.App);
    }

    [Fact]
    public void TurnDown_TheTelemetryGoingAway_ClosesTheEpisode()
    {
        Reading(0, 3.0);
        Reading(100, 3.0);

        Process(null, T.At(300));
        Assert.Empty(_events);
        Process(null, T.At(700));

        var e = Assert.Single(_events);
        Assert.Equal(TimeSpan.FromMilliseconds(100), e.Duration);
    }

    [Fact]
    public void TurnDown_AClockThatStepsBack_DoesNotThrowOrGiveNegativeDurations()
    {
        Reading(1000, 3.0);

        Assert.Null(Record.Exception(() => Reading(500, 3.0)));

        Reading(2000, 0.0);
        var e = Assert.Single(_events);
        Assert.True(e.Duration >= TimeSpan.Zero);
    }

    // ---- Fix round 3, ruling 21: the flag that says whether the watched output is covered passes through unchanged ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void State_CarriesTheWatchedOutputCoveredFlagOfTheHealthResult(bool covered)
    {
        var health = new HealthResult(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>(), covered);

        var state = _engine.Process(null, health, Array.Empty<AppLevel>(), -20.0, T.At(0));

        Assert.Equal(covered, state.WatchedOutputCovered);
    }

    [Fact]
    public void State_CarriesTheFlag_WhileSoundIsBeingTurnedDown()
    {
        var uncovered = new HealthResult(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>(), false);
        var at = T.At(0);
        var tel = Telemetry(at, Leveler(inputDb: -10, reductionDb: 6.0, at: at), SafetyCap(reductionDb: 1.5));

        var state = _engine.Process(tel, uncovered, new[] { new AppLevel("Zoom", 1, -3.0) }, -20.0, at);

        T.Near(6.0, state.TurnDownDb);
        Assert.False(state.WatchedOutputCovered);
        Assert.False(state.DropsOverEvents);
    }

    // ---- Fix round 4, ruling 22: the fact that the plug-in was seen running passes through unchanged ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void State_CarriesThePluginSeenRunningFlagOfTheHealthResult_WhateverTheTelemetryNow(bool seen)
    {
        // There is no telemetry at this tick: the fact is the one the health result remembered, not something the engine works out.
        var health = new HealthResult(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>(), true, seen, PluginEnabled: true);

        var state = _engine.Process(null, health, Array.Empty<AppLevel>(), -20.0, T.At(0));

        Assert.Equal(seen, state.PluginSeenRunning);
        Assert.Equal(seen, state.DropsOverEvents);
    }

    // ---- Final-review fix, ruling 26 (I1): the fact that the plug-in reported itself on passes through unchanged too ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void State_CarriesThePluginEnabledFlagOfTheHealthResult_WhateverTheTelemetryNow(bool enabled)
    {
        var health = new HealthResult(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>(), true, true, enabled);

        var state = _engine.Process(null, health, Array.Empty<AppLevel>(), -20.0, T.At(0));

        Assert.Equal(enabled, state.PluginEnabled);
        Assert.Equal(enabled, state.DropsOverEvents);
    }

    [Fact]
    public void State_DoesNotWorkOutPluginEnabledFromTheTelemetry()
    {
        // The telemetry of this tick says the plug-in is on (flags 5), but the health result, which has the 3 s memory, says it is not.
        var at = T.At(0);
        var tel = Telemetry(at, Leveler(inputDb: -10, reductionDb: 6.0, at: at), SafetyCap(reductionDb: 1.5));
        var health = new HealthResult(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>(), true, true, false);

        var state = _engine.Process(tel, health, new[] { new AppLevel("Zoom", 1, -3.0) }, -20.0, at);

        Assert.False(state.PluginEnabled);
        Assert.False(state.DropsOverEvents);
    }

    [Fact]
    public void State_CarriesThePluginEnabledFlag_WhileSoundIsBeingTurnedDown_AndTheTurnDownIsStillReported()
    {
        // Dropping the alert is only about the alert: the numbers and the app list still come from the telemetry.
        var at = T.At(0);
        var tel = Telemetry(at, Leveler(inputDb: -10, reductionDb: 6.0, at: at), SafetyCap(reductionDb: 1.5));
        var off = new HealthResult(ProtectionStatus.Protected, "Protected: cap -20.0 dB", Array.Empty<string>(), true, true, false);

        var state = _engine.Process(tel, off, new[] { new AppLevel("Zoom", 1, -3.0) }, -20.0, at);

        T.Near(6.0, state.TurnDownDb);
        Assert.Equal("Zoom", Assert.Single(state.Apps).Name);
        Assert.False(state.PluginEnabled);
        Assert.False(state.DropsOverEvents);
    }
}

public class ProtectionTextTests
{
    private static ProtectionState State(double turnDown = 0.0, params AppTurnDown[] apps) =>
        new(ProtectionStatus.Protected, "Protected: cap -20.0 dB", -12.0, turnDown, 0.0, apps);

    [Fact]
    public void Caption_WhenNothingIsTurnedDown_ExplainsTheNumber() =>
        Assert.Equal("loudest app now, before your volume slider", ProtectionText.Caption(State(0.4, new AppTurnDown("Zoom", 0.4))));

    [Fact]
    public void Caption_WhenAnAppIsTurnedDown_NamesItAndTheAmount() =>
        Assert.Equal(
            "Zoom turned down by 6.2 dB",
            ProtectionText.Caption(State(6.24, new AppTurnDown("Chrome", 1.0), new AppTurnDown("Zoom", 6.24))));

    [Fact]
    public void Caption_AtExactlyOneDb_SaysTurnedDown() =>
        Assert.Equal("Zoom turned down by 1.0 dB", ProtectionText.Caption(State(1.0, new AppTurnDown("Zoom", 1.0))));

    [Fact]
    public void Caption_WithoutAMatchedApp_SaysSound() =>
        Assert.Equal("Sound turned down by 4.0 dB", ProtectionText.Caption(State(4.0)));

    [Fact]
    public void PlayingNow_NothingPlaying() =>
        Assert.Equal("Playing now: nothing", ProtectionText.PlayingNow(State()));

    [Fact]
    public void PlayingNow_ListsTheAppsWithWhatWasDone()
    {
        var state = State(6.24, new AppTurnDown("Zoom", 6.24), new AppTurnDown("Chrome", 0.0));

        Assert.Equal("Playing now: Zoom turned down 6.2 dB     Chrome not turned down", ProtectionText.PlayingNow(state));
    }

    [Fact]
    public void PlayingNow_ShowsAtMostFourApps()
    {
        var apps = Enumerable.Range(1, 6).Select(i => new AppTurnDown("App" + i, 0.0)).ToArray();

        var text = ProtectionText.PlayingNow(State(0.0, apps));

        Assert.Contains("App4", text);
        Assert.DoesNotContain("App5", text);
    }

    [Fact]
    public void SliderTip_IsTheCapExplanation() =>
        Assert.Equal(
            "Drag to set the cap: loudness before your volume slider. Louder apps are turned down to it.",
            ProtectionText.SliderTip);

    [Fact]
    public void Caption_WhenTheBiggestTurnDownIsASoundWithNoApp_DoesNotBlameAnApp()
    {
        // Chrome is turned down by 1.2 dB, but the 6.0 dB belongs to a sound no app could be matched to.
        var state = State(6.0, new AppTurnDown("Chrome", 1.2), new AppTurnDown(ProtectionEngine.OtherSound, 6.0));

        Assert.Equal("Sound turned down by 6.0 dB", ProtectionText.Caption(state));
    }

    [Theory]
    [InlineData(ProtectionStatus.Protected, true)]
    [InlineData(ProtectionStatus.Idle, true)]
    [InlineData(ProtectionStatus.Off, true)]
    [InlineData(ProtectionStatus.Down, false)]
    [InlineData(ProtectionStatus.NotInstalled, false)]
    [InlineData(ProtectionStatus.NotProtected, false)]
    public void ShowsProtection_OnlyWhenThePluginIsInstalledAndSetUp(ProtectionStatus status, bool expected) =>
        Assert.Equal(expected, new ProtectionState(status, "x", -12.0, 0.0, 0.0, Array.Empty<AppTurnDown>()).ShowsProtection);

    // ---- Fix round 4, ruling 22 (which narrows ruling 21): over events are dropped only in Protected or Idle, on a covered output,
    // while the plug-in has been seen running in the last 3 s. Off is not in the list: with the switch off nothing is turned down. ----

    [Theory]
    [InlineData(ProtectionStatus.Protected, true, true, true)]
    [InlineData(ProtectionStatus.Protected, true, false, false)]
    [InlineData(ProtectionStatus.Protected, false, true, false)]
    [InlineData(ProtectionStatus.Protected, false, false, false)]
    [InlineData(ProtectionStatus.Idle, true, true, true)]
    [InlineData(ProtectionStatus.Idle, true, false, false)]
    [InlineData(ProtectionStatus.Idle, false, true, false)]
    [InlineData(ProtectionStatus.Idle, false, false, false)]
    [InlineData(ProtectionStatus.Off, true, true, false)]
    [InlineData(ProtectionStatus.Off, true, false, false)]
    [InlineData(ProtectionStatus.Off, false, true, false)]
    [InlineData(ProtectionStatus.Off, false, false, false)]
    [InlineData(ProtectionStatus.Down, true, true, false)]
    [InlineData(ProtectionStatus.Down, true, false, false)]
    [InlineData(ProtectionStatus.Down, false, true, false)]
    [InlineData(ProtectionStatus.Down, false, false, false)]
    [InlineData(ProtectionStatus.NotInstalled, true, true, false)]
    [InlineData(ProtectionStatus.NotInstalled, true, false, false)]
    [InlineData(ProtectionStatus.NotInstalled, false, true, false)]
    [InlineData(ProtectionStatus.NotInstalled, false, false, false)]
    [InlineData(ProtectionStatus.NotProtected, true, true, false)]
    [InlineData(ProtectionStatus.NotProtected, true, false, false)]
    [InlineData(ProtectionStatus.NotProtected, false, true, false)]
    [InlineData(ProtectionStatus.NotProtected, false, false, false)]
    public void DropsOverEvents_OnlyInProtectedOrIdle_OnACoveredOutput_WhileThePluginIsSeenRunning_AndReportsItselfOn(
        ProtectionStatus status, bool covered, bool seenRunning, bool expected) =>
        Assert.Equal(
            expected,
            new ProtectionState(status, "x", -12.0, 0.0, 0.0, Array.Empty<AppTurnDown>(), covered, seenRunning, PluginEnabled: true).DropsOverEvents);

    // Ruling 26: the fourth condition. The 24 rows above are for a plug-in that reports itself on; for one that does not,
    // the answer is no, whatever the status, the output and the sighting.
    [Fact]
    public void DropsOverEvents_NeverWhenThePluginHasNotReportedItselfOn_WhateverTheRest()
    {
        foreach (var status in Enum.GetValues<ProtectionStatus>())
        {
            foreach (var covered in new[] { true, false })
            {
                foreach (var seenRunning in new[] { true, false })
                {
                    var state = new ProtectionState(
                        status, "x", -12.0, 0.0, 0.0, Array.Empty<AppTurnDown>(), covered, seenRunning, PluginEnabled: false);

                    Assert.False(state.DropsOverEvents, $"{status}, covered {covered}, seen running {seenRunning}");
                }
            }
        }
    }

    [Fact]
    public void AStateBuiltWithoutTheFlags_CountsTheOutputAsCovered_AndThePluginAsNotSeenRunning_AndNotEnabled()
    {
        // The safe way round: nothing says the plug-in is protecting, so over events are not dropped.
        var state = new ProtectionState(ProtectionStatus.Protected, "x", -12.0, 0.0, 0.0, Array.Empty<AppTurnDown>());

        Assert.True(state.WatchedOutputCovered);
        Assert.False(state.PluginSeenRunning);
        Assert.False(state.PluginEnabled);
        Assert.False(state.DropsOverEvents);
    }

    [Fact]
    public void AStateBuiltWithOnlyTheTwoOlderFlags_DoesNotDropOverEvents()
    {
        // Covered and seen running, as before ruling 26, but nothing says the plug-in is switched on.
        var state = new ProtectionState(ProtectionStatus.Protected, "x", -12.0, 0.0, 0.0, Array.Empty<AppTurnDown>(), true, true);

        Assert.True(state.PluginSeenRunning);
        Assert.False(state.PluginEnabled);
        Assert.False(state.DropsOverEvents);
    }
}
