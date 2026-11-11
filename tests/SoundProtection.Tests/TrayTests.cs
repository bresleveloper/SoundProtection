using SoundProtection.Core;

namespace SoundProtection.Tests;

public class AppNamesTests
{
    [Theory]
    [InlineData("zoom", "Zoom")]
    [InlineData("CHROME", "Chrome")]
    [InlineData("msedge", "Edge")]
    [InlineData("ms-teams", "Teams")]
    [InlineData("notepad", "Notepad")]
    [InlineData("SoundProtection", "Test tone")] // the only sound this app plays itself
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Friendly_names(string? process, string expected) =>
        Assert.Equal(expected, AppNames.Friendly(process, isSystemSounds: false));

    [Fact]
    public void The_system_sounds_session_has_its_own_name() =>
        Assert.Equal("System sounds", AppNames.Friendly("svchost", isSystemSounds: true));
}

public class TrayTextTests
{
    private static PanelState State(
        bool hasDevice = true,
        double ear = -31.4,
        double ceiling = -28.0,
        bool over = false,
        bool alerts = true) =>
        PanelState.Empty(T.At(0)) with
        {
            HasDevice = hasDevice,
            EarDb = ear,
            CeilingDb = ceiling,
            Over = over,
            AlertsEnabled = alerts,
        };

    [Fact]
    public void Tooltip_shows_the_ear_level_and_the_ceiling() =>
        Assert.Equal(
            "Sound Protection: -31.4 dB at ear, ceiling -28.0 dB (alerts only)",
            TrayText.Tooltip(State()));

    [Fact]
    public void Tooltip_when_alerts_are_off() =>
        Assert.Equal("Sound Protection: alerts off", TrayText.Tooltip(State(alerts: false)));

    [Fact]
    public void Tooltip_when_there_is_no_device() =>
        Assert.Equal("Sound Protection: no output device", TrayText.Tooltip(State(hasDevice: false)));

    [Fact]
    public void Tooltip_fits_the_notify_icon_limit_in_the_worst_case() =>
        Assert.True(TrayText.Tooltip(State(ear: -99.9, ceiling: -80.0)).Length <= 127);

    [Fact]
    public void Icon_is_off_when_alerts_are_off() =>
        Assert.Equal(IconKind.Off, TrayText.Icon(State(alerts: false, over: true), T.At(0), null));

    [Fact]
    public void Icon_is_over_while_over() =>
        Assert.Equal(IconKind.Over, TrayText.Icon(State(over: true), T.At(0), null));

    [Fact]
    public void Icon_stays_over_for_three_seconds_after_an_event()
    {
        Assert.Equal(IconKind.Over, TrayText.Icon(State(), T.At(2999), T.At(0)));
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), T.At(3000), T.At(0)));
    }

    [Fact]
    public void Icon_is_watching_otherwise() =>
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), T.At(0), null));

    // ---- With the plug-in's protection state ----

    private static ProtectionState Protection(
        ProtectionStatus status = ProtectionStatus.Protected,
        string text = "Protected: cap -20.0 dB",
        double turnDown = 0.0) =>
        new(status, text, -12.0, turnDown, 0.0, Array.Empty<AppTurnDown>());

    [Theory]
    [InlineData(ProtectionStatus.NotInstalled, "Protection is not installed")]
    [InlineData(ProtectionStatus.Down, "The plug-in is not running")]
    [InlineData(ProtectionStatus.Down, "The plug-in is switched off: your setting has not reached it")]
    [InlineData(ProtectionStatus.Off, "Protection is off")]
    [InlineData(ProtectionStatus.Idle, "Protected (nothing playing)")]
    [InlineData(ProtectionStatus.Protected, "Protected: cap -20.0 dB")]
    [InlineData(ProtectionStatus.NotProtected, "Sound on Speakers (Realtek(R) Audio) is not protected")]
    public void Tooltip_with_protection_is_the_status_text(ProtectionStatus status, string text) =>
        Assert.Equal("Sound Protection: " + text, TrayText.Tooltip(State(), Protection(status, text)));

    [Fact]
    public void Tooltip_with_protection_is_cut_to_127_characters()
    {
        var tip = TrayText.Tooltip(State(), Protection(ProtectionStatus.Down, new string('x', 300)));

        Assert.Equal(127, tip.Length);
        Assert.StartsWith("Sound Protection: xxx", tip);
    }

    [Fact]
    public void Tooltip_in_the_not_protected_state_is_cut_to_127_characters()
    {
        var longName = "Speakers (" + new string('x', 200) + ")";

        var tip = TrayText.Tooltip(State(), Protection(ProtectionStatus.NotProtected, $"Sound on {longName} is not protected"));

        Assert.Equal(127, tip.Length);
        Assert.StartsWith("Sound Protection: Sound on Speakers (xxx", tip);
    }

    [Fact]
    public void Tooltip_with_protection_keeps_a_short_text_whole() =>
        Assert.Equal(
            "Sound Protection: Windows switched off sound effects on Headphones",
            TrayText.Tooltip(State(), Protection(ProtectionStatus.Down, "Windows switched off sound effects on Headphones")));

    [Fact]
    public void Tooltip_before_the_first_protection_check_is_the_ear_level_one() =>
        Assert.Equal(TrayText.Tooltip(State()), TrayText.Tooltip(State(), null));

    [Theory]
    [InlineData(ProtectionStatus.NotInstalled)]
    [InlineData(ProtectionStatus.Down)]
    public void Icon_is_down_when_protection_is_not_installed_or_down(ProtectionStatus status)
    {
        Assert.Equal(IconKind.Down, TrayText.Icon(State(), Protection(status), T.At(0), null));
        Assert.Equal(IconKind.Down, TrayText.Icon(State(alerts: false), Protection(status), T.At(0), null));
        Assert.Equal(IconKind.Down, TrayText.Icon(State(over: true), Protection(status), T.At(0), T.At(0)));
    }

    [Fact]
    public void Icon_is_off_when_protection_is_switched_off() =>
        Assert.Equal(IconKind.Off, TrayText.Icon(State(), Protection(ProtectionStatus.Off), T.At(0), null));

    [Theory]
    [InlineData(ProtectionStatus.Protected)]
    [InlineData(ProtectionStatus.Idle)]
    public void Icon_is_watching_while_protected(ProtectionStatus status) =>
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), Protection(status), T.At(0), null));

    [Fact]
    public void Icon_is_over_while_something_is_turned_down_by_a_dB_or_more()
    {
        Assert.Equal(IconKind.Over, TrayText.Icon(State(), Protection(turnDown: 1.0), T.At(0), null));
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), Protection(turnDown: 0.9), T.At(0), null));
    }

    [Fact]
    public void Icon_stays_over_for_three_seconds_after_a_turn_down_alert()
    {
        Assert.Equal(IconKind.Over, TrayText.Icon(State(), Protection(), T.At(2999), T.At(0)));
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), Protection(), T.At(3000), T.At(0)));
    }

    [Fact]
    public void Icon_with_protection_ignores_the_ear_level_over_flag() =>
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(over: true), Protection(), T.At(0), null));

    [Fact]
    public void Icon_with_alerts_off_stays_calm_but_not_grey_because_protection_still_works() =>
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(alerts: false), Protection(turnDown: 6.0), T.At(0), T.At(0)));

    // ---- Sound on an output without the plug-in: the icon follows the older, ear-level logic ----

    private static ProtectionState NotProtected(double turnDown = 0.0) =>
        Protection(ProtectionStatus.NotProtected, "Sound on Speakers (Realtek(R) Audio) is not protected", turnDown);

    [Fact]
    public void Icon_is_watching_in_the_not_protected_state() =>
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), NotProtected(), T.At(0), null));

    [Fact]
    public void Icon_is_over_in_the_not_protected_state_while_the_level_at_the_ear_is_over() =>
        Assert.Equal(IconKind.Over, TrayText.Icon(State(over: true), NotProtected(), T.At(0), null));

    [Fact]
    public void Icon_stays_over_for_three_seconds_after_an_alert_in_the_not_protected_state()
    {
        Assert.Equal(IconKind.Over, TrayText.Icon(State(), NotProtected(), T.At(2999), T.At(0)));
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), NotProtected(), T.At(3000), T.At(0)));
    }

    [Fact]
    public void Icon_is_off_in_the_not_protected_state_when_alerts_are_off() =>
        Assert.Equal(IconKind.Off, TrayText.Icon(State(alerts: false, over: true), NotProtected(), T.At(0), null));

    [Fact]
    public void Icon_in_the_not_protected_state_ignores_what_the_plug_in_turns_down() =>
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), NotProtected(turnDown: 6.0), T.At(0), null));

    [Fact]
    public void Icon_before_the_first_protection_check_is_the_ear_level_one()
    {
        Assert.Equal(IconKind.Over, TrayText.Icon(State(over: true), null, T.At(0), null));
        Assert.Equal(IconKind.Off, TrayText.Icon(State(alerts: false), null, T.At(0), null));
        Assert.Equal(IconKind.Watching, TrayText.Icon(State(), null, T.At(0), null));
    }

    // ---- Final-review fix, ruling 26 (I1): a plug-in that runs but reports itself switched off, from the tracker to the icon and the tip ----

    [Fact]
    public void ARunningPluginThatIsSwitchedOff_IsGreenForThreeSeconds_ThenARedIconAndTheReasonInTheTip()
    {
        var registry = ProtectionHealthTests.Registry(
            true, ProtectionHealthTests.Endpoint("Headphones", id: "{ab14dbc4-0c0d-4ebb-a2a1-1c2b6f6b3b2e}"));
        var tracker = new HealthTracker();
        var engine = new ProtectionEngine();

        // The switch is on, nothing plays, and the plug-in writes fresh telemetry that says it is off (flags: the config was read, not enabled).
        ProtectionState Tick(double ms)
        {
            var now = T.At(ms);
            var tel = ProtectionHealthTests.Telemetry(flags: 1, at: now.UtcDateTime);
            var health = tracker.Evaluate(registry, tel, now, true, -20.0, false, null, false);
            return engine.Process(tel, health, Array.Empty<AppLevel>(), -20.0, now);
        }

        for (var ms = 0; ms <= 3000; ms += 100)
        {
            var green = Tick(ms);
            Assert.True(TrayText.Icon(State(), green, T.At(ms), null) == IconKind.Watching, $"at {ms} ms the icon should still be the calm one");
            Assert.Equal("Sound Protection: Protected (nothing playing)", TrayText.Tooltip(State(), green));
        }

        foreach (var ms in new[] { 3100, 9000 })
        {
            var down = Tick(ms);
            var tip = TrayText.Tooltip(State(), down);

            Assert.Equal(ProtectionStatus.Down, down.Status);
            Assert.Equal(IconKind.Down, TrayText.Icon(State(), down, T.At(ms), null));
            Assert.Equal(IconKind.Down, TrayText.Icon(State(alerts: false), down, T.At(ms), null));
            Assert.Equal("Sound Protection: The plug-in is switched off: your setting has not reached it", tip);
            Assert.True(tip.Length <= 127);
        }
    }
}
