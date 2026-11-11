using SoundProtection.Core;

namespace SoundProtection.Tests;

public class ProtectionHealthTests
{
    private const string HeadphonesKey = "{ab14dbc4-0c0d-4ebb-a2a1-1c2b6f6b3b2e}";
    private const string SpeakersKey = "{9d6b1c2e-1111-4222-8333-444455556666}";
    private const string SpeakersNotProtected = "Sound on Speakers (Realtek(R) Audio) is not protected";
    private const string PluginSwitchedOff = "The plug-in is switched off: your setting has not reached it";
    private const string PluginStillOffWarning = "The plug-in is still switched off";

    private static readonly DateTime Now = T.At(0).UtcDateTime;
    private static readonly WatchedOutput OnHeadphones = new("{0.0.0.00000000}." + HeadphonesKey, "Headphones (Realtek(R) Audio)");
    private static readonly WatchedOutput OnSpeakers = new("{0.0.0.00000000}." + SpeakersKey, "Speakers (Realtek(R) Audio)");

    internal static EndpointFacts Endpoint(
        string name = "Headphones",
        bool leveler = true,
        bool safetyCap = true,
        bool effectsDisabled = false,
        bool exclusiveAllowed = false,
        string? id = null) =>
        new(name, leveler, safetyCap, effectsDisabled, exclusiveAllowed, id);

    internal static RegistryFacts Registry(bool testKeyOn = true, params EndpointFacts[] endpoints) =>
        new(testKeyOn, endpoints.Length == 0 ? new[] { Endpoint() } : endpoints);

    internal static TelemetrySlot Slot(SlotKind kind, double ageMs, DateTime? at = null) =>
        new(kind, 1, 48000, 2, ApoMode.Default, 0, 1000, (at ?? Now).AddMilliseconds(-ageMs), -20f, 0f, 0f, -30f);

    // The ages are measured from "at" (default: the fixed Now), so that a test can build fresh telemetry for a later moment.
    internal static TelemetrySnapshot Telemetry(
        double heartbeatAgeMs = 50,
        double slotAgeMs = 50,
        uint flags = 5,
        bool withSafetyCap = true,
        float capDb = -20.0f,
        DateTime? at = null)
    {
        var slots = new List<TelemetrySlot> { Slot(SlotKind.Leveler, slotAgeMs, at) };
        if (withSafetyCap)
        {
            slots.Add(Slot(SlotKind.SafetyCap, slotAgeMs, at));
        }

        return new TelemetrySnapshot(
            new TelemetryHeader(4242, 1, 100, (at ?? Now).AddMilliseconds(-heartbeatAgeMs), flags, capDb, -8.0f),
            slots);
    }

    private static HealthResult Evaluate(
        RegistryFacts? reg = null,
        TelemetrySnapshot? tel = null,
        bool configEnabled = true,
        double capDb = -20.0,
        bool audioActive = true,
        WatchedOutput? watched = null,
        SettingsMismatch mismatch = SettingsMismatch.None,
        bool settingsNotSaved = false) =>
        ProtectionHealth.Evaluate(reg ?? Registry(), tel, Now, configEnabled, capDb, audioActive, watched, mismatch, settingsNotSaved);

    private static RegistryFacts HeadphonesListed() => Registry(true, Endpoint("Headphones", id: HeadphonesKey));

    // ---- Rules 1 to 8, in order ----

    [Fact]
    public void Rule1_NoEndpointWithBothClsids_IsNotInstalled()
    {
        var onlyLeveler = Evaluate(new RegistryFacts(true, new[] { Endpoint(safetyCap: false) }), Telemetry());
        var onlySafetyCap = Evaluate(new RegistryFacts(true, new[] { Endpoint(leveler: false) }), Telemetry());
        var none = Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), Telemetry());

        foreach (var result in new[] { onlyLeveler, onlySafetyCap, none })
        {
            Assert.Equal(ProtectionStatus.NotInstalled, result.Status);
            Assert.Equal("Protection is not installed", result.Text);
        }
    }

    [Fact]
    public void Rule1_Wins_EvenWhenTheTestKeyIsOffAndProtectionIsOff()
    {
        var result = Evaluate(new RegistryFacts(false, Array.Empty<EndpointFacts>()), tel: null, configEnabled: false);

        Assert.Equal(ProtectionStatus.NotInstalled, result.Status);
    }

    [Fact]
    public void Rule2_EffectsDisabledOnAListedEndpoint_IsDown()
    {
        var result = Evaluate(Registry(true, Endpoint(effectsDisabled: true)), Telemetry());

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("Windows switched off sound effects on Headphones", result.Text);
    }

    [Fact]
    public void Rule2_NamesTheEndpointThatHasEffectsDisabled()
    {
        var result = Evaluate(
            Registry(true, Endpoint("Speakers"), Endpoint("Headphones", effectsDisabled: true)),
            Telemetry());

        Assert.Equal("Windows switched off sound effects on Headphones", result.Text);
    }

    [Fact]
    public void Rule2_IgnoresAnEndpointThatIsNotListed()
    {
        var result = Evaluate(
            Registry(true, Endpoint(), Endpoint("Speakers", leveler: false, safetyCap: false, effectsDisabled: true)),
            Telemetry());

        Assert.Equal(ProtectionStatus.Protected, result.Status);
    }

    [Fact]
    public void Rule3_TestKeyOff_IsDown()
    {
        var result = Evaluate(Registry(testKeyOn: false), Telemetry());

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The test key is off, so Windows will not load the plug-in", result.Text);
    }

    [Fact]
    public void Rule2_BeatsRule3()
    {
        var result = Evaluate(Registry(false, Endpoint(effectsDisabled: true)), Telemetry());

        Assert.Equal("Windows switched off sound effects on Headphones", result.Text);
    }

    [Fact]
    public void Rule4_ProtectionSwitchedOff_IsOff()
    {
        var result = Evaluate(Registry(), Telemetry(), configEnabled: false);

        Assert.Equal(ProtectionStatus.Off, result.Status);
        Assert.Equal("Protection is off", result.Text);
    }

    [Fact]
    public void Rule3_BeatsRule4()
    {
        var result = Evaluate(Registry(testKeyOn: false), Telemetry(), configEnabled: false);

        Assert.Equal(ProtectionStatus.Down, result.Status);
    }

    [Fact]
    public void Rule5_SafeMode_IsDown()
    {
        var result = Evaluate(Registry(), Telemetry(flags: 1 | 2 | 4));

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is in safe mode after repeated crashes", result.Text);
    }

    [Fact]
    public void SafeMode_IsDown()
    {
        var result = Evaluate(Registry(), Telemetry(flags: 2), audioActive: false);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is in safe mode after repeated crashes", result.Text);
    }

    [Fact]
    public void Rule4_BeatsRule5()
    {
        var result = Evaluate(Registry(), Telemetry(flags: 2), configEnabled: false);

        Assert.Equal(ProtectionStatus.Off, result.Status);
    }

    [Fact]
    public void Rule6_NoTelemetryWhileAudioPlays_IsDown()
    {
        var result = Evaluate(Registry(), tel: null, audioActive: true);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
    }

    [Fact]
    public void Rule6_StaleHeartbeatWhileAudioPlays_IsDown()
    {
        var result = Evaluate(Registry(), Telemetry(heartbeatAgeMs: 2001));

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
    }

    [Fact]
    public void Rule6_NoSafetyCapSlot_IsDown()
    {
        var result = Evaluate(Registry(), Telemetry(withSafetyCap: false));

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
    }

    [Fact]
    public void Rule6_StaleSafetyCapSlot_IsDown()
    {
        var result = Evaluate(Registry(), Telemetry(slotAgeMs: 2001));

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
    }

    [Fact]
    public void Rule6_TwoSecondsExactly_StillCountsAsAlive()
    {
        var result = Evaluate(Registry(), Telemetry(heartbeatAgeMs: 2000, slotAgeMs: 2000));

        Assert.Equal(ProtectionStatus.Protected, result.Status);
    }

    [Fact]
    public void Rule6_OnlyAFreshLevelerSlot_IsNotEnough()
    {
        var tel = new TelemetrySnapshot(
            Telemetry().Header,
            new[] { Slot(SlotKind.Leveler, 10), Slot(SlotKind.SafetyCap, 5000) });

        Assert.Equal("The plug-in is not running", Evaluate(Registry(), tel).Text);
    }

    [Fact]
    public void Rule6_AFreshSafetyCapSlotAmongStaleOnes_IsEnough()
    {
        var tel = new TelemetrySnapshot(
            Telemetry().Header,
            new[] { Slot(SlotKind.SafetyCap, 5000), Slot(SlotKind.SafetyCap, 100) });

        Assert.Equal(ProtectionStatus.Protected, Evaluate(Registry(), tel).Status);
    }

    [Fact]
    public void Rule6_NeedsAudio_SoSilenceWithoutTelemetryIsNotDown()
    {
        var result = Evaluate(Registry(), tel: null, audioActive: false);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
    }

    [Fact]
    public void Rule7_AudioPlayingAndPluginRunning_IsProtectedWithTheCap()
    {
        var result = Evaluate(Registry(), Telemetry(), capDb: -20.0);

        Assert.Equal(ProtectionStatus.Protected, result.Status);
        Assert.Equal("Protected: cap -20.0 dB", result.Text);
    }

    [Fact]
    public void Rule7_FormatsTheCapWithOneDecimal()
    {
        Assert.Equal("Protected: cap -33.5 dB", Evaluate(Registry(), Telemetry(capDb: -33.46f), capDb: -33.46).Text);
    }

    // Fix round 1, ruling 8a: the status line says what the plug-in reports, not what the settings ask for.
    [Fact]
    public void Rule7_ShowsTheCapThePluginReports_NotTheSetting()
    {
        var result = Evaluate(Registry(), Telemetry(capDb: -35.5f), capDb: -20.0);

        Assert.Equal(ProtectionStatus.Protected, result.Status);
        Assert.Equal("Protected: cap -35.5 dB", result.Text);
    }

    [Fact]
    public void Rule8_NothingPlaying_IsIdle()
    {
        var result = Evaluate(Registry(), Telemetry(), audioActive: false);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.Equal("Protected (nothing playing)", result.Text);
    }

    // ---- Warnings ----

    [Fact]
    public void ExclusiveAllowed_AddsWarning()
    {
        var result = Evaluate(Registry(true, Endpoint(exclusiveAllowed: true)), Telemetry());

        Assert.Equal(ProtectionStatus.Protected, result.Status);
        Assert.Equal(new[] { "Apps can take exclusive control of Headphones" }, result.Warnings);
    }

    [Fact]
    public void ExclusiveAllowed_GivesOneWarningPerListedEndpoint_AndSkipsUnlistedOnes()
    {
        var result = Evaluate(
            Registry(
                true,
                Endpoint("Headphones", exclusiveAllowed: true),
                Endpoint("Speakers", exclusiveAllowed: true),
                Endpoint("Monitor", leveler: false, safetyCap: false, exclusiveAllowed: true),
                Endpoint("Line out", exclusiveAllowed: false)),
            Telemetry());

        Assert.Equal(
            new[] { "Apps can take exclusive control of Headphones", "Apps can take exclusive control of Speakers" },
            result.Warnings);
    }

    [Fact]
    public void NoWarningsWhenExclusiveModeIsOff() => Assert.Empty(Evaluate(Registry(), Telemetry()).Warnings);

    [Fact]
    public void Warnings_AreKeptEvenWhenDown()
    {
        var result = Evaluate(Registry(false, Endpoint(exclusiveAllowed: true)), Telemetry());

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void NotInstalled_HasNoWarnings()
    {
        var result = Evaluate(new RegistryFacts(true, new[] { Endpoint(leveler: false, exclusiveAllowed: true) }), Telemetry());

        Assert.Equal(ProtectionStatus.NotInstalled, result.Status);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NotInstalled_HasNoWarnings_EvenWithAMismatchAndASettingsFailure()
    {
        var result = Evaluate(
            new RegistryFacts(true, Array.Empty<EndpointFacts>()),
            Telemetry(capDb: -50f),
            watched: OnSpeakers,
            mismatch: SettingsMismatch.CapDiffers,
            settingsNotSaved: true);

        Assert.Equal(ProtectionStatus.NotInstalled, result.Status);
        Assert.Empty(result.Warnings);
    }

    // ---- Fix round 1, ruling 8b: the plug-in is not doing what the settings ask ----

    [Fact]
    public void Mismatch_ConfigNotRead_WarnsWithTheCapInUse()
    {
        var result = Evaluate(Registry(), Telemetry(flags: 4, capDb: -20.0f), capDb: -35.0, mismatch: SettingsMismatch.ConfigNotRead);

        Assert.Equal(new[] { "The plug-in could not read your settings and uses cap -20.0 dB" }, result.Warnings);
        Assert.Equal(ProtectionStatus.Protected, result.Status); // rules 1 to 8 themselves are unchanged
    }

    [Fact]
    public void Mismatch_PluginStillOn_WhileTheSwitchIsOff()
    {
        var result = Evaluate(Registry(), Telemetry(flags: 1 | 4), configEnabled: false, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(new[] { "The plug-in is still switched on" }, result.Warnings);
        Assert.Equal(ProtectionStatus.Off, result.Status);
    }

    [Fact]
    public void Mismatch_PluginStillOff_WhileTheSwitchIsOn()
    {
        // Ruling 26: once the difference has lasted, the status says it, since nothing is being turned down, and the warning that
        // would name the same thing is not shown a second time.
        var result = Evaluate(Registry(), Telemetry(flags: 1), configEnabled: true, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal(PluginSwitchedOff, result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Mismatch_CapDiffers_NamesTheCapThePluginUses()
    {
        var result = Evaluate(Registry(), Telemetry(capDb: -25.04f), capDb: -35.0, mismatch: SettingsMismatch.CapDiffers);

        Assert.Equal(new[] { "The plug-in is still using cap -25.0 dB" }, result.Warnings);
    }

    [Fact]
    public void Mismatch_None_AddsNothing() =>
        Assert.Empty(Evaluate(Registry(), Telemetry(flags: 4, capDb: -50f), mismatch: SettingsMismatch.None).Warnings);

    [Fact]
    public void Mismatch_IsLeftOutWhileThePluginIsNotRunning()
    {
        // No heartbeat for 5 s: what the header says cannot be trusted, so it is not compared with anything.
        var result = Evaluate(Registry(), Telemetry(heartbeatAgeMs: 5000), audioActive: false, mismatch: SettingsMismatch.CapDiffers);

        Assert.Empty(result.Warnings);
    }

    // ---- Finding the mismatch ----

    private static SettingsMismatch Find(TelemetrySnapshot? tel, bool configEnabled = true, double capDb = -20.0) =>
        ProtectionHealth.FindMismatch(tel, Now, configEnabled, capDb);

    [Fact]
    public void FindMismatch_SameSwitchAndCap_IsNone()
    {
        Assert.Equal(SettingsMismatch.None, Find(Telemetry(flags: 5, capDb: -20.0f)));
        Assert.Equal(SettingsMismatch.None, Find(Telemetry(flags: 1, capDb: -20.0f), configEnabled: false)); // both off
    }

    [Fact]
    public void FindMismatch_ConfigNotValid_IsConfigNotRead_EvenWhenTheValuesAgree() =>
        Assert.Equal(SettingsMismatch.ConfigNotRead, Find(Telemetry(flags: 4, capDb: -20.0f), configEnabled: true, capDb: -20.0));

    [Fact]
    public void FindMismatch_SwitchDiffers_InBothDirections()
    {
        Assert.Equal(SettingsMismatch.SwitchDiffers, Find(Telemetry(flags: 1), configEnabled: true));      // plug-in off, switch on
        Assert.Equal(SettingsMismatch.SwitchDiffers, Find(Telemetry(flags: 1 | 4), configEnabled: false)); // plug-in on, switch off
    }

    [Theory]
    [InlineData(-20.04f, -20.0, SettingsMismatch.None)]
    [InlineData(-19.96f, -20.0, SettingsMismatch.None)]
    [InlineData(-20.06f, -20.0, SettingsMismatch.CapDiffers)]
    [InlineData(-19.94f, -20.0, SettingsMismatch.CapDiffers)]
    [InlineData(-35.0f, -20.0, SettingsMismatch.CapDiffers)]
    public void FindMismatch_CapsWithinFiveHundredthsOfADecibelAreTheSame(float reported, double wanted, SettingsMismatch expected) =>
        Assert.Equal(expected, Find(Telemetry(capDb: reported), capDb: wanted));

    [Fact]
    public void FindMismatch_ACapThatIsNotANumber_IsADifference() =>
        Assert.Equal(SettingsMismatch.CapDiffers, Find(Telemetry(capDb: float.NaN)));

    [Fact]
    public void FindMismatch_ConfigNotRead_BeatsTheSwitch_WhichBeatsTheCap()
    {
        Assert.Equal(SettingsMismatch.ConfigNotRead, Find(Telemetry(flags: 0, capDb: -50f), configEnabled: true));
        Assert.Equal(SettingsMismatch.SwitchDiffers, Find(Telemetry(flags: 1, capDb: -50f), configEnabled: true));
    }

    [Fact]
    public void FindMismatch_NeedsARunningPlugin()
    {
        Assert.Equal(SettingsMismatch.None, Find(null));
        Assert.Equal(SettingsMismatch.None, Find(Telemetry(heartbeatAgeMs: 2001, capDb: -50f)));
        Assert.Equal(SettingsMismatch.None, Find(Telemetry(slotAgeMs: 2001, capDb: -50f)));
        Assert.Equal(SettingsMismatch.None, Find(Telemetry(withSafetyCap: false, capDb: -50f)));
    }

    // ---- Fix round 1, ruling 8c: the settings could not be written to config.bin ----

    [Fact]
    public void SettingsNotSaved_Warns_WhetherOrNotThePluginIsRunning()
    {
        const string warning = "Your settings could not be saved for the plug-in";

        var running = Evaluate(Registry(), Telemetry(), settingsNotSaved: true);
        var idle = Evaluate(Registry(), tel: null, audioActive: false, settingsNotSaved: true);
        var down = Evaluate(Registry(), tel: null, audioActive: true, settingsNotSaved: true);

        Assert.Equal(ProtectionStatus.Protected, running.Status);
        Assert.Equal(ProtectionStatus.Idle, idle.Status);
        Assert.Equal(ProtectionStatus.Down, down.Status);
        foreach (var result in new[] { running, idle, down })
        {
            Assert.Equal(new[] { warning }, result.Warnings);
        }
    }

    [Fact]
    public void SettingsNotSaved_IsGoneWhenTheFlagIsClear()
    {
        Assert.Contains("Your settings could not be saved for the plug-in", Evaluate(Registry(), Telemetry(), settingsNotSaved: true).Warnings);

        Assert.Empty(Evaluate(Registry(), Telemetry(), settingsNotSaved: false).Warnings);
    }

    // ---- Fix round 1, ruling 9: rule 6 judges the plug-in only on an output that has it ----

    [Fact]
    public void WatchedOutputIsProtected_Rule6AppliesAsBefore()
    {
        var result = Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnHeadphones);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WatchedOutputIsProtected_AndThePluginIsRunning_IsProtected()
    {
        var result = Evaluate(HeadphonesListed(), Telemetry(), watched: OnHeadphones);

        Assert.Equal(ProtectionStatus.Protected, result.Status);
        Assert.Empty(result.Warnings);
    }

    // ---- Fix round 2, ruling 13: sound on an output that has no plug-in is a state of its own ----

    [Fact]
    public void WatchedOutputIsNotProtected_WithSound_IsTheNotProtectedState()
    {
        // Sound on the Speakers: the plug-in, which is on the Headphones, rightly processes nothing, so it is not "down" either.
        var result = Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.NotProtected, result.Status);
        Assert.Equal(SpeakersNotProtected, result.Text);
    }

    [Fact]
    public void WatchedOutputIsNotProtected_WithSound_HasNoWarningLineThatRepeatsTheText()
    {
        var result = Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnSpeakers);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WatchedOutputIsNotProtected_WithSound_BeatsRule7_EvenWhenThePluginIsProcessingOtherSound()
    {
        // The plug-in is busy with sound on the Headphones, but the sound on the Speakers, which are being watched, is not covered.
        var result = Evaluate(HeadphonesListed(), Telemetry(capDb: -30f), audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.NotProtected, result.Status);
        Assert.Equal(SpeakersNotProtected, result.Text);
    }

    [Fact]
    public void WatchedOutputIsNotProtected_WithoutSound_ButThePluginIsProcessingSound_IsProtected()
    {
        // Rules 7 and 8 go by the Safety Cap slot: sound is coming through the output that has the plug-in.
        var result = Evaluate(HeadphonesListed(), Telemetry(capDb: -30f), audioActive: false, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Protected, result.Status);
        Assert.Equal("Protected: cap -30.0 dB", result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WatchedOutputIsNotProtected_ThePluginRunsButNothingGoesThroughIt_IsIdle()
    {
        // A fresh heartbeat but no fresh Safety Cap slot: the plug-in is up and has nothing to process.
        var result = Evaluate(HeadphonesListed(), Telemetry(slotAgeMs: 5000), audioActive: false, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.Equal("Protected (nothing playing)", result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WatchedOutputIsNotProtected_WithoutSound_HasNoWarning()
    {
        var result = Evaluate(HeadphonesListed(), tel: null, audioActive: false, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.Equal("Protected (nothing playing)", result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NoWatchedOutputKnown_IsNeverTheNotProtectedState()
    {
        var result = Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: null);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
    }

    [Fact]
    public void TheIdMatchIgnoresCase()
    {
        var shouting = new WatchedOutput("{0.0.0.00000000}." + HeadphonesKey.ToUpperInvariant(), "Headphones (Realtek(R) Audio)");
        var capitalKey = Registry(true, Endpoint("Headphones", id: HeadphonesKey.ToUpperInvariant()));

        foreach (var result in new[]
        {
            Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: shouting),
            Evaluate(capitalKey, tel: null, audioActive: true, watched: OnHeadphones),
        })
        {
            Assert.Equal(ProtectionStatus.Down, result.Status); // judged as an output that has the plug-in
            Assert.Equal("The plug-in is not running", result.Text);
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void TheIdMatchIsOnTheKey_NotOnTheName()
    {
        // Another output that happens to be named alike is not the listed one.
        var usbHeadphones = new WatchedOutput("{0.0.0.00000000}." + SpeakersKey, "Headphones (USB Audio)");

        var result = Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: usbHeadphones);

        Assert.Equal(ProtectionStatus.NotProtected, result.Status);
        Assert.Equal("Sound on Headphones (USB Audio) is not protected", result.Text);
    }

    [Fact]
    public void AnEndpointWithOnlyOneEffectListed_IsNotAProtectedOutput()
    {
        var reg = Registry(true, Endpoint("Headphones", id: HeadphonesKey), Endpoint("Speakers", safetyCap: false, id: SpeakersKey));

        var result = Evaluate(reg, tel: null, audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.NotProtected, result.Status);
        Assert.Equal(SpeakersNotProtected, result.Text);
    }

    [Fact]
    public void WithSeveralProtectedOutputs_AnyOfThemCounts()
    {
        var reg = Registry(true, Endpoint("Headphones", id: HeadphonesKey), Endpoint("Speakers", id: SpeakersKey));

        var result = Evaluate(reg, tel: null, audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is not running", result.Text);
    }

    [Fact]
    public void FactsWithoutAnId_CannotTellOutputsApart_SoRule6StillApplies()
    {
        var result = Evaluate(Registry(), tel: null, audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Empty(result.Warnings);
    }

    // ---- The not-protected state comes after rules 1 to 5, each of which still wins over it (sound is playing on the Speakers) ----

    [Fact]
    public void NotProtected_Rule1_NotInstalledWins()
    {
        var result = Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), Telemetry(), audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.NotInstalled, result.Status);
        Assert.Equal("Protection is not installed", result.Text);
    }

    [Fact]
    public void NotProtected_Rule2_EffectsSwitchedOffWins()
    {
        var reg = Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey));

        var result = Evaluate(reg, Telemetry(), audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("Windows switched off sound effects on Headphones", result.Text);
    }

    [Fact]
    public void NotProtected_Rule3_TestKeyOffWins()
    {
        var result = Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), Telemetry(), audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The test key is off, so Windows will not load the plug-in", result.Text);
    }

    [Fact]
    public void NotProtected_Rule4_SwitchedOffWins()
    {
        var result = Evaluate(HeadphonesListed(), Telemetry(), configEnabled: false, audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Off, result.Status);
        Assert.Equal("Protection is off", result.Text);
    }

    [Fact]
    public void NotProtected_Rule5_SafeModeWins()
    {
        var result = Evaluate(HeadphonesListed(), Telemetry(flags: 2), audioActive: true, watched: OnSpeakers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal("The plug-in is in safe mode after repeated crashes", result.Text);
    }

    // ---- The other warnings: the same order as before, minus the line that repeated the not-protected text ----

    private static readonly string[] MismatchNotSavedExclusive =
    {
        "The plug-in is still using cap -25.0 dB",
        "Your settings could not be saved for the plug-in",
        "Apps can take exclusive control of Headphones",
    };

    [Fact]
    public void Warnings_AreInOrder_Mismatch_NotSaved_ExclusiveControl()
    {
        var result = Evaluate(
            Registry(true, Endpoint("Headphones", exclusiveAllowed: true, id: HeadphonesKey)),
            Telemetry(capDb: -25f),
            capDb: -35.0,
            watched: OnHeadphones,
            mismatch: SettingsMismatch.CapDiffers,
            settingsNotSaved: true);

        Assert.Equal(MismatchNotSavedExclusive, result.Warnings);
    }

    [Fact]
    public void TheOtherWarnings_StillApplyInTheNotProtectedState()
    {
        var result = Evaluate(
            Registry(true, Endpoint("Headphones", exclusiveAllowed: true, id: HeadphonesKey)),
            Telemetry(capDb: -25f),
            capDb: -35.0,
            audioActive: true,
            watched: OnSpeakers,
            mismatch: SettingsMismatch.CapDiffers,
            settingsNotSaved: true);

        Assert.Equal(ProtectionStatus.NotProtected, result.Status);
        Assert.Equal(MismatchNotSavedExclusive, result.Warnings);
    }

    // ---- Fix round 3, ruling 21: whether the watched output is covered goes by the endpoint match, never by the sound ----

    // One result for each way the status can come out, other than "not installed", for the same registry and the same watched
    // output. The sound and the plug-in change from one way to the next; the output does not.
    private static (string Way, HealthResult Result)[] EveryWayToComeOut(WatchedOutput watched) => new[]
    {
        ("effects switched off", Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), Telemetry(), audioActive: true, watched: watched)),
        ("test key off", Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), Telemetry(), audioActive: true, watched: watched)),
        ("switched off", Evaluate(HeadphonesListed(), Telemetry(), configEnabled: false, audioActive: true, watched: watched)),
        ("safe mode", Evaluate(HeadphonesListed(), Telemetry(flags: 2), audioActive: true, watched: watched)),
        ("sound, plug-in not running", Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: watched)),
        ("sound, plug-in running", Evaluate(HeadphonesListed(), Telemetry(), audioActive: true, watched: watched)),
        ("no sound, plug-in running", Evaluate(HeadphonesListed(), Telemetry(), audioActive: false, watched: watched)),
        ("no sound, plug-in not running", Evaluate(HeadphonesListed(), tel: null, audioActive: false, watched: watched)),
    };

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void WatchedOutputCovered_FollowsTheEndpointMatch_NotTheSound(bool onHeadphones, bool audioActive, bool pluginRuns)
    {
        var result = Evaluate(HeadphonesListed(), pluginRuns ? Telemetry() : null, audioActive: audioActive, watched: onHeadphones ? OnHeadphones : OnSpeakers);

        Assert.Equal(onHeadphones, result.WatchedOutputCovered);
    }

    [Fact]
    public void WatchedOutputCovered_IsTrueInEveryStatus_WhenTheOutputHasThePlugin()
    {
        var ways = EveryWayToComeOut(OnHeadphones);

        Assert.True(ways.Select(w => w.Result.Status).Distinct().Count() >= 4, "the ways have to give different statuses, or this proves little");
        foreach (var (way, result) in ways)
        {
            Assert.True(result.WatchedOutputCovered, way);
        }
    }

    [Fact]
    public void WatchedOutputCovered_IsFalseInEveryStatus_WhenTheOutputHasNoPlugin()
    {
        var ways = EveryWayToComeOut(OnSpeakers);

        Assert.True(ways.Select(w => w.Result.Status).Distinct().Count() >= 4, "the ways have to give different statuses, or this proves little");
        foreach (var (way, result) in ways)
        {
            Assert.False(result.WatchedOutputCovered, way);
        }
    }

    [Fact]
    public void WatchedOutputCovered_IsFalse_WhenNothingIsInstalled()
    {
        // No endpoint lists both effects, so no output is covered, whichever output is watched.
        var nothingListed = new RegistryFacts(true, Array.Empty<EndpointFacts>());

        foreach (var watched in new WatchedOutput?[] { OnHeadphones, OnSpeakers, null })
        {
            var result = Evaluate(nothingListed, Telemetry(), audioActive: true, watched: watched);

            Assert.Equal(ProtectionStatus.NotInstalled, result.Status);
            Assert.False(result.WatchedOutputCovered);
        }
    }

    [Fact]
    public void WatchedOutputCovered_IsTrue_WhenTheWatchedOutputIsNotKnown()
    {
        // The older rule, kept: with no output to judge, the output counts as covered.
        foreach (var audioActive in new[] { true, false })
        {
            Assert.True(Evaluate(HeadphonesListed(), tel: null, audioActive: audioActive, watched: null).WatchedOutputCovered);
        }
    }

    [Fact]
    public void WatchedOutputCovered_IsTrue_ForFactsWithoutAnId()
    {
        // Facts that do not say which output they are about cannot rule an output out: the older rule, kept.
        var result = Evaluate(Registry(), tel: null, audioActive: true, watched: OnSpeakers);

        Assert.True(result.WatchedOutputCovered);
    }

    [Fact]
    public void WatchedOutputCovered_IsFalse_ForAnEndpointThatListsOnlyOneEffect()
    {
        var onlySafetyCap = Registry(true, Endpoint("Headphones", id: HeadphonesKey), Endpoint("Speakers", leveler: false, id: SpeakersKey));
        var onlyLeveler = Registry(true, Endpoint("Headphones", id: HeadphonesKey), Endpoint("Speakers", safetyCap: false, id: SpeakersKey));

        Assert.False(Evaluate(onlySafetyCap, tel: null, audioActive: true, watched: OnSpeakers).WatchedOutputCovered);
        Assert.False(Evaluate(onlyLeveler, tel: null, audioActive: true, watched: OnSpeakers).WatchedOutputCovered);
    }

    [Fact]
    public void WatchedOutputCovered_IsTrue_WhenAnyOfSeveralListedOutputsMatches()
    {
        var reg = Registry(true, Endpoint("Headphones", id: HeadphonesKey), Endpoint("Speakers", id: SpeakersKey));

        Assert.True(Evaluate(reg, tel: null, audioActive: false, watched: OnSpeakers).WatchedOutputCovered);
        Assert.True(Evaluate(reg, tel: null, audioActive: false, watched: OnHeadphones).WatchedOutputCovered);
    }

    [Fact]
    public void WatchedOutputCovered_MatchesTheKeyIgnoringCase_NotTheName()
    {
        var shouting = new WatchedOutput("{0.0.0.00000000}." + HeadphonesKey.ToUpperInvariant(), "Headphones (Realtek(R) Audio)");
        var capitalKey = Registry(true, Endpoint("Headphones", id: HeadphonesKey.ToUpperInvariant()));
        var namedAlike = new WatchedOutput("{0.0.0.00000000}." + SpeakersKey, "Headphones (USB Audio)");

        Assert.True(Evaluate(HeadphonesListed(), tel: null, audioActive: false, watched: shouting).WatchedOutputCovered);
        Assert.True(Evaluate(capitalKey, tel: null, audioActive: false, watched: OnHeadphones).WatchedOutputCovered);
        Assert.False(Evaluate(HeadphonesListed(), tel: null, audioActive: false, watched: namedAlike).WatchedOutputCovered);
    }

    [Fact]
    public void AHealthResultBuiltWithoutTheFlag_CountsTheWatchedOutputAsCovered() =>
        Assert.True(new HealthResult(ProtectionStatus.Protected, "x", Array.Empty<string>()).WatchedOutputCovered);

    // ---- Fix round 4, ruling 22: whether the plug-in counts as running at this tick goes by its telemetry alone ----
    // (HealthTracker widens that to "at some tick in the last 3 s"; Evaluate on its own knows only this tick.)

    [Theory]
    [InlineData(50, 50, true, true)]      // both stamps fresh
    [InlineData(2000, 2000, true, true)]  // 2 s exactly still counts, as in rule 6
    [InlineData(2100, 50, true, false)]   // a stale heartbeat
    [InlineData(50, 2100, true, false)]   // a stale Safety Cap slot
    [InlineData(50, 50, false, false)]    // only a fresh Leveler slot: the Safety Cap, which sees all the sound, is missing
    public void PluginSeenRunning_FollowsTheFreshnessTestOfRule6(double heartbeatAgeMs, double slotAgeMs, bool withSafetyCap, bool expected)
    {
        var tel = Telemetry(heartbeatAgeMs, slotAgeMs, withSafetyCap: withSafetyCap);

        var result = Evaluate(HeadphonesListed(), tel, audioActive: false, watched: OnHeadphones);

        Assert.Equal(expected, result.PluginSeenRunning);
    }

    [Fact]
    public void PluginSeenRunning_IsTrueInEveryStatus_WhileTheTelemetryIsFresh()
    {
        var fresh = Telemetry();
        var results = new (string Way, HealthResult Result)[]
        {
            ("not installed", Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), fresh, watched: OnHeadphones)),
            ("effects switched off", Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), fresh, watched: OnHeadphones)),
            ("test key off", Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), fresh, watched: OnHeadphones)),
            ("switched off", Evaluate(HeadphonesListed(), fresh, configEnabled: false, watched: OnHeadphones)),
            ("safe mode", Evaluate(HeadphonesListed(), Telemetry(flags: 2), watched: OnHeadphones)),
            ("not protected", Evaluate(HeadphonesListed(), fresh, audioActive: true, watched: OnSpeakers)),
            ("protected", Evaluate(HeadphonesListed(), fresh, audioActive: true, watched: OnHeadphones)),
            ("idle", Evaluate(HeadphonesListed(), fresh, audioActive: false, watched: OnHeadphones)),
        };

        Assert.Equal(6, results.Select(r => r.Result.Status).Distinct().Count()); // every status there is, or this proves little
        foreach (var (way, result) in results)
        {
            Assert.True(result.PluginSeenRunning, way);
        }
    }

    [Fact]
    public void PluginSeenRunning_IsFalseInEveryStatus_WhenThereIsNoTelemetry()
    {
        var results = new (string Way, HealthResult Result)[]
        {
            ("not installed", Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), tel: null, watched: OnHeadphones)),
            ("effects switched off", Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), tel: null, watched: OnHeadphones)),
            ("test key off", Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), tel: null, watched: OnHeadphones)),
            ("switched off", Evaluate(HeadphonesListed(), tel: null, configEnabled: false, watched: OnHeadphones)),
            ("not protected", Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnSpeakers)),
            ("not running", Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnHeadphones)),
            ("idle", Evaluate(HeadphonesListed(), tel: null, audioActive: false, watched: OnHeadphones)),
        };

        Assert.Equal(5, results.Select(r => r.Result.Status).Distinct().Count());
        foreach (var (way, result) in results)
        {
            Assert.False(result.PluginSeenRunning, way);
        }
    }

    [Fact]
    public void AHealthResultBuiltWithoutTheFact_CountsThePluginAsNotSeenRunning() =>
        Assert.False(new HealthResult(ProtectionStatus.Protected, "x", Array.Empty<string>()).PluginSeenRunning);

    // ---- Final-review fix, ruling 26 (I1): the switch is on, the plug-in runs, and it reports itself switched off ----
    // Nothing is turned down then: the Leveler is at unity and the Safety Cap lets everything up to full scale through. The plug-in's own
    // state is carried in PluginEnabled (the Enabled flag of its header, while it counts as running). Once the settings have had the
    // 3 s grace to arrive, the status says so, in place of the warning that would name the same thing.

    [Theory]
    [InlineData(5u, true)]   // ConfigValid and Enabled
    [InlineData(4u, true)]   // Enabled alone: no valid config, so the plug-in runs on its defaults
    [InlineData(7u, true)]   // and in safe mode
    [InlineData(1u, false)]  // ConfigValid alone
    [InlineData(0u, false)]
    [InlineData(2u, false)]  // safe mode alone
    public void PluginEnabled_FollowsTheEnabledFlagOfTheHeader_WhileThePluginRuns(uint flags, bool expected)
    {
        var result = Evaluate(HeadphonesListed(), Telemetry(flags: flags), audioActive: false, watched: OnHeadphones);

        Assert.True(result.PluginSeenRunning);
        Assert.Equal(expected, result.PluginEnabled);
    }

    [Theory]
    [InlineData(2100, 50, true)]    // a stale heartbeat
    [InlineData(50, 2100, true)]    // a stale Safety Cap slot
    [InlineData(50, 50, false)]     // no Safety Cap slot
    public void PluginEnabled_IsFalse_WhenThePluginDoesNotCountAsRunning_WhateverTheFlagSays(double heartbeatAgeMs, double slotAgeMs, bool withSafetyCap)
    {
        // The header means nothing when the plug-in is not running, so its Enabled flag is not taken as the plug-in being on.
        var tel = Telemetry(heartbeatAgeMs, slotAgeMs, flags: 5, withSafetyCap: withSafetyCap);

        var result = Evaluate(HeadphonesListed(), tel, audioActive: false, watched: OnHeadphones);

        Assert.False(result.PluginSeenRunning);
        Assert.False(result.PluginEnabled);
    }

    [Fact]
    public void PluginEnabled_IsTrueInEveryStatus_WhileThePluginRunsAndReportsItselfOn()
    {
        var on = Telemetry(flags: 5);
        var results = new (string Way, HealthResult Result)[]
        {
            ("not installed", Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), on, watched: OnHeadphones)),
            ("effects switched off", Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), on, watched: OnHeadphones)),
            ("test key off", Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), on, watched: OnHeadphones)),
            ("switched off", Evaluate(HeadphonesListed(), on, configEnabled: false, watched: OnHeadphones)),
            ("safe mode", Evaluate(HeadphonesListed(), Telemetry(flags: 7), watched: OnHeadphones)),
            ("not protected", Evaluate(HeadphonesListed(), on, audioActive: true, watched: OnSpeakers)),
            ("protected", Evaluate(HeadphonesListed(), on, audioActive: true, watched: OnHeadphones)),
            ("idle", Evaluate(HeadphonesListed(), on, audioActive: false, watched: OnHeadphones)),
        };

        Assert.Equal(6, results.Select(r => r.Result.Status).Distinct().Count()); // every status there is, or this proves little
        foreach (var (way, result) in results)
        {
            Assert.True(result.PluginEnabled, way);
        }
    }

    [Fact]
    public void PluginEnabled_IsFalseInEveryStatus_WhileThePluginRunsAndReportsItselfOff()
    {
        var off = Telemetry(flags: 1);
        var results = new (string Way, HealthResult Result)[]
        {
            ("not installed", Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), off, watched: OnHeadphones)),
            ("effects switched off", Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), off, watched: OnHeadphones)),
            ("test key off", Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), off, watched: OnHeadphones)),
            ("switched off", Evaluate(HeadphonesListed(), off, configEnabled: false, watched: OnHeadphones)),
            ("safe mode", Evaluate(HeadphonesListed(), Telemetry(flags: 3), watched: OnHeadphones)),
            ("not protected", Evaluate(HeadphonesListed(), off, audioActive: true, watched: OnSpeakers)),
            ("protected", Evaluate(HeadphonesListed(), off, audioActive: true, watched: OnHeadphones)),
            ("idle", Evaluate(HeadphonesListed(), off, audioActive: false, watched: OnHeadphones)),
            ("not reached", Evaluate(HeadphonesListed(), off, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers)),
        };

        Assert.Equal(6, results.Select(r => r.Result.Status).Distinct().Count());
        foreach (var (way, result) in results)
        {
            Assert.True(result.PluginSeenRunning, way);
            Assert.False(result.PluginEnabled, way);
        }
    }

    [Fact]
    public void PluginEnabled_IsFalseInEveryStatus_WhenThereIsNoTelemetry()
    {
        var results = new (string Way, HealthResult Result)[]
        {
            ("not installed", Evaluate(new RegistryFacts(true, Array.Empty<EndpointFacts>()), tel: null, watched: OnHeadphones)),
            ("effects switched off", Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), tel: null, watched: OnHeadphones)),
            ("test key off", Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), tel: null, watched: OnHeadphones)),
            ("switched off", Evaluate(HeadphonesListed(), tel: null, configEnabled: false, watched: OnHeadphones)),
            ("not protected", Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnSpeakers)),
            ("not running", Evaluate(HeadphonesListed(), tel: null, audioActive: true, watched: OnHeadphones)),
            ("idle", Evaluate(HeadphonesListed(), tel: null, audioActive: false, watched: OnHeadphones)),
        };

        Assert.Equal(5, results.Select(r => r.Result.Status).Distinct().Count());
        foreach (var (way, result) in results)
        {
            Assert.False(result.PluginEnabled, way);
        }
    }

    [Fact]
    public void AHealthResultBuiltWithoutTheFact_CountsThePluginAsNotEnabled() =>
        Assert.False(new HealthResult(ProtectionStatus.Protected, "x", Array.Empty<string>()).PluginEnabled);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PluginSwitchedOff_AfterTheGrace_IsDown_WhetherOrNotSoundPlays(bool audioActive)
    {
        var result = Evaluate(
            HeadphonesListed(), Telemetry(flags: 1), audioActive: audioActive, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal(PluginSwitchedOff, result.Text);
        Assert.Empty(result.Warnings); // the status takes the place of "The plug-in is still switched off"
        Assert.True(result.WatchedOutputCovered);
        Assert.True(result.PluginSeenRunning);
        Assert.False(result.PluginEnabled);
    }

    [Fact]
    public void PluginSwitchedOff_AfterTheGrace_IsDown_AlsoWithNoSound_OnAnOutputThatHasNoPlugin()
    {
        // Nothing plays here, so it is the plug-in's own Safety Cap slot that shows it is working (for another output), and it is off.
        var result = Evaluate(
            HeadphonesListed(), Telemetry(flags: 1), audioActive: false, watched: OnSpeakers, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal(PluginSwitchedOff, result.Text);
        Assert.False(result.WatchedOutputCovered);
    }

    [Theory]
    [InlineData(true, ProtectionStatus.Protected, "Protected: cap -20.0 dB")]
    [InlineData(false, ProtectionStatus.Idle, "Protected (nothing playing)")]
    public void PluginSwitchedOff_BeforeTheGrace_RulesSevenAndEightStayAsBuilt(bool audioActive, ProtectionStatus status, string text)
    {
        // HealthTracker has not passed the difference on yet, so the status is green as before. The plug-in's own flag is carried all
        // the same, for the tray to see that nothing is being turned down.
        var result = Evaluate(HeadphonesListed(), Telemetry(flags: 1), audioActive: audioActive, watched: OnHeadphones);

        Assert.Equal(status, result.Status);
        Assert.Equal(text, result.Text);
        Assert.Empty(result.Warnings);
        Assert.True(result.PluginSeenRunning);
        Assert.False(result.PluginEnabled);
    }

    [Fact]
    public void PluginSwitchedOff_TheStatusTakesTheWarningsPlace_TheOtherWarningsStay()
    {
        var result = Evaluate(
            Registry(true, Endpoint("Headphones", exclusiveAllowed: true, id: HeadphonesKey)),
            Telemetry(flags: 1),
            watched: OnHeadphones,
            mismatch: SettingsMismatch.SwitchDiffers,
            settingsNotSaved: true);

        Assert.Equal(ProtectionStatus.Down, result.Status);
        Assert.Equal(
            new[] { "Your settings could not be saved for the plug-in", "Apps can take exclusive control of Headphones" },
            result.Warnings);
    }

    [Fact]
    public void PluginSwitchedOff_EarlierRulesStillWin_AndTheirWarningsStayAsBuilt()
    {
        var off = Telemetry(flags: 1);
        var results = new (string Way, HealthResult Result, ProtectionStatus Status, string Text)[]
        {
            (
                "effects switched off",
                Evaluate(Registry(true, Endpoint(effectsDisabled: true, id: HeadphonesKey)), off, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers),
                ProtectionStatus.Down,
                "Windows switched off sound effects on Headphones"),
            (
                "test key off",
                Evaluate(Registry(false, Endpoint(id: HeadphonesKey)), off, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers),
                ProtectionStatus.Down,
                "The test key is off, so Windows will not load the plug-in"),
            (
                "safe mode",
                Evaluate(HeadphonesListed(), Telemetry(flags: 3), watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers),
                ProtectionStatus.Down,
                "The plug-in is in safe mode after repeated crashes"),
            (
                "sound on an output without the plug-in",
                Evaluate(HeadphonesListed(), off, audioActive: true, watched: OnSpeakers, mismatch: SettingsMismatch.SwitchDiffers),
                ProtectionStatus.NotProtected,
                SpeakersNotProtected),
        };

        foreach (var (way, result, status, text) in results)
        {
            Assert.Equal(status, result.Status);
            Assert.Equal(text, result.Text);
            Assert.True(new[] { PluginStillOffWarning }.SequenceEqual(result.Warnings), way); // nothing said it for them, so the warning stays
        }
    }

    [Fact]
    public void PluginSwitchedOff_NeedsARunningPlugin()
    {
        // No heartbeat for 5 s: the header cannot be trusted, so what it says about being off is not acted on.
        var stale = Telemetry(heartbeatAgeMs: 5000, flags: 1);

        var idle = Evaluate(HeadphonesListed(), stale, audioActive: false, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers);
        var notRunning = Evaluate(HeadphonesListed(), stale, audioActive: true, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(ProtectionStatus.Idle, idle.Status);
        Assert.Equal(ProtectionStatus.Down, notRunning.Status);
        Assert.Equal("The plug-in is not running", notRunning.Text);
    }

    [Fact]
    public void PluginSwitchedOff_NeedsThePluginToSayItIsOff()
    {
        // A difference in the switch is only that when the plug-in itself reports the opposite of what is asked. A plug-in that reports
        // itself on, with the switch on, is not "switched off", whatever mismatch was passed in.
        var result = Evaluate(HeadphonesListed(), Telemetry(flags: 5), audioActive: false, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.True(result.PluginEnabled);
    }

    [Theory]
    [InlineData(SettingsMismatch.None)]
    [InlineData(SettingsMismatch.CapDiffers)]
    [InlineData(SettingsMismatch.ConfigNotRead)]
    public void PluginSwitchedOff_OtherKindsOfMismatch_StayAsBuilt(SettingsMismatch mismatch)
    {
        var result = Evaluate(HeadphonesListed(), Telemetry(flags: 1), audioActive: false, watched: OnHeadphones, mismatch: mismatch);

        Assert.Equal(ProtectionStatus.Idle, result.Status);
        Assert.Equal("Protected (nothing playing)", result.Text);
        Assert.False(result.PluginEnabled);
    }

    [Fact]
    public void TheReverseCase_PluginStillOn_WhileTheSwitchIsOff_IsUnchanged()
    {
        // The switch is off and the plug-in still reports itself on: rule 4 is what applies, and the warning stays, as built.
        var result = Evaluate(HeadphonesListed(), Telemetry(flags: 5), configEnabled: false, watched: OnHeadphones, mismatch: SettingsMismatch.SwitchDiffers);

        Assert.Equal(ProtectionStatus.Off, result.Status);
        Assert.Equal("Protection is off", result.Text);
        Assert.Equal(new[] { "The plug-in is still switched on" }, result.Warnings);
        Assert.True(result.PluginEnabled);
    }
}

public class ActivityGateTests
{
    [Fact]
    public void Activity_Counts_OnlyAfterTheGracePeriod()
    {
        var gate = new ActivityGate(TimeSpan.FromMilliseconds(600));

        Assert.False(gate.Update(true, T.At(0)));
        Assert.False(gate.Update(true, T.At(599)));
        Assert.True(gate.Update(true, T.At(600)));
        Assert.True(gate.Update(true, T.At(5000)));
    }

    [Fact]
    public void Silence_ResetsTheGrace()
    {
        var gate = new ActivityGate(TimeSpan.FromMilliseconds(600));
        gate.Update(true, T.At(0));
        Assert.True(gate.Update(true, T.At(700)));

        Assert.False(gate.Update(false, T.At(800)));
        Assert.False(gate.Update(true, T.At(900)));
        Assert.False(gate.Update(true, T.At(1499)));
        Assert.True(gate.Update(true, T.At(1500)));
    }

    [Fact]
    public void Nothing_PlayingIsNeverActive()
    {
        var gate = new ActivityGate(TimeSpan.FromMilliseconds(600));

        Assert.False(gate.Update(false, T.At(0)));
        Assert.False(gate.Update(false, T.At(10_000)));
    }

    [Fact]
    public void AClockThatStepsBack_DoesNotThrowOrStayActive()
    {
        var gate = new ActivityGate(TimeSpan.FromMilliseconds(600));
        gate.Update(true, T.At(10_000));
        Assert.True(gate.Update(true, T.At(11_000)));

        Assert.Null(Record.Exception(() => gate.Update(true, T.At(500))));
    }
}
