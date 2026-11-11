using SoundProtection.Core;

namespace SoundProtection.Tests;

public class MonitorEngineTests
{
    private readonly MonitorEngine _engine = new();
    private readonly List<OverEvent> _events = new();
    private readonly Settings _settings = new() { CeilingDb = -28.0 };

    public MonitorEngineTests() => _engine.OverClosed += _events.Add;

    private static AudioSnapshot Snap(double peak, double volumeDb, bool muted = false, params AppLevel[] apps) =>
        new("dev1", "Headphones (Realtek(R) Audio)", muted, volumeDb, 20, peak, apps);

    [Fact]
    public void Reports_the_ear_level_as_peak_plus_volume()
    {
        var s = _engine.Process(Snap(-7.7, -23.7), _settings, T.At(0));

        T.Near(-31.4, s.EarDb);
        Assert.False(s.Over);
        T.Near(s.EarDb, s.DisplayDb);
        T.Near(s.EarDb, s.HoldDb);
        Assert.True(s.HasDevice);
        Assert.Equal("Headphones (Realtek(R) Audio)", s.DeviceName);
        Assert.Equal(-28.0, s.CeilingDb);
    }

    [Fact]
    public void Going_over_the_ceiling_is_reported_and_the_event_closes_after_the_gap()
    {
        var s = _engine.Process(Snap(-4, -10), _settings, T.At(0));
        Assert.True(s.Over);
        T.Near(14, s.OverByDb);

        _engine.Process(Snap(Db.Floor, -10), _settings, T.At(100));
        Assert.Empty(_events);

        _engine.Process(Snap(Db.Floor, -10), _settings, T.At(700));
        var e = Assert.Single(_events);
        T.Near(14, e.MaxOverDb);
    }

    [Fact]
    public void A_muted_output_is_silent_and_never_over()
    {
        var s = _engine.Process(Snap(0, 0, muted: true), _settings, T.At(0));

        Assert.Equal(Db.Floor, s.EarDb);
        Assert.False(s.Over);
        Assert.True(s.Muted);
    }

    [Fact]
    public void Volume_at_zero_percent_is_silent()
    {
        var s = _engine.Process(Snap(0, Db.Floor), _settings, T.At(0));

        Assert.Equal(Db.Floor, s.EarDb);
        Assert.False(s.Over);
    }

    [Fact]
    public void No_device_gives_the_empty_state_and_closes_an_open_episode_silently()
    {
        _engine.Process(Snap(-4, -10), _settings, T.At(0));

        var s = _engine.Process(null, _settings, T.At(100));

        Assert.False(s.HasDevice);
        Assert.Equal("No output device", s.DeviceName);
        Assert.Equal(Db.Floor, s.EarDb);
        Assert.False(s.Over);

        _engine.Process(null, _settings, T.At(900));
        Assert.Empty(_events);
    }

    [Fact]
    public void Switching_alerts_off_in_the_middle_of_an_episode_raises_nothing()
    {
        _engine.Process(Snap(-4, -10), _settings, T.At(0));
        var off = new Settings { AlertsEnabled = false, CeilingDb = -28.0 };

        var s = _engine.Process(Snap(-4, -10), off, T.At(100));

        Assert.False(s.Over);
        Assert.False(s.AlertsEnabled);

        _engine.Process(Snap(Db.Floor, -10), off, T.At(900));
        Assert.Empty(_events);
    }

    [Fact]
    public void Apps_are_sorted_loudest_first_and_quiet_ones_are_left_out()
    {
        var s = _engine.Process(
            Snap(-4, -10, false, new AppLevel("Chrome", 2, -30), new AppLevel("Zoom", 1, -10), new AppLevel("Ghost", 3, -95)),
            _settings,
            T.At(0));

        Assert.Equal(new[] { "Zoom", "Chrome" }, s.Apps.Select(a => a.Name).ToArray());
        T.Near(-20, s.Apps[0].EarDb);
    }

    [Fact]
    public void The_event_names_the_app_that_was_loudest_at_the_maximum()
    {
        _engine.Process(
            Snap(-4, -10, false, new AppLevel("Chrome", 2, -20), new AppLevel("Zoom", 1, -4)),
            _settings,
            T.At(0));
        _engine.Process(Snap(Db.Floor, -10), _settings, T.At(700));

        Assert.Equal("Zoom", Assert.Single(_events).LoudestApp);
    }

    [Fact]
    public void The_ten_second_hold_expires()
    {
        _engine.Process(Snap(-10, -10), _settings, T.At(0)); // ear -20

        T.Near(-20, _engine.Process(Snap(-50, -10), _settings, T.At(9000)).HoldDb);
        T.Near(-60, _engine.Process(Snap(-50, -10), _settings, T.At(11000)).HoldDb);
    }

    [Fact]
    public void The_empty_state_has_no_device()
    {
        var s = PanelState.Empty(T.At(0));

        Assert.False(s.HasDevice);
        Assert.Equal(Db.Floor, s.EarDb);
        Assert.Empty(s.Apps);
    }
}
