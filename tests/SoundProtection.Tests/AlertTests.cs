using SoundProtection.Core;

namespace SoundProtection.Tests;

public class OverDetectorTests
{
    private readonly OverDetector _detector = new();
    private readonly List<OverEvent> _events = new();

    private void Feed(double ms, double earDb, string? app = "Zoom", double ceilingDb = -28)
    {
        var e = _detector.Feed(T.At(ms), earDb, ceilingDb, app);
        if (e != null)
        {
            _events.Add(e);
        }
    }

    [Fact]
    public void Quiet_input_never_opens_an_episode()
    {
        Feed(0, -40);

        Assert.Empty(_events);
        Assert.False(_detector.IsOver);
    }

    [Fact]
    public void One_burst_closes_after_the_merge_gap_with_its_maximum()
    {
        Feed(0, -20);
        Assert.True(_detector.IsOver);
        T.Near(8, _detector.CurrentOverDb);

        Feed(100, -25);
        Feed(200, -35, null); // inside the gap
        Assert.Empty(_events);

        Feed(700, -35, null);

        var e = Assert.Single(_events);
        Assert.Equal(T.At(0), e.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(100), e.Duration);
        T.Near(8, e.MaxOverDb);
        Assert.Equal(-28.0, e.CeilingDb);
        Assert.Equal("Zoom", e.LoudestApp);
        Assert.False(_detector.IsOver);
    }

    [Fact]
    public void Bursts_closer_than_the_gap_merge_into_one_event()
    {
        Feed(0, -20);
        Feed(300, -40);
        Feed(400, -22);
        Feed(1000, -40);

        var e = Assert.Single(_events);
        Assert.Equal(TimeSpan.FromMilliseconds(400), e.Duration);
        T.Near(8, e.MaxOverDb);
    }

    [Fact]
    public void Bursts_farther_apart_than_the_gap_are_separate_events()
    {
        Feed(0, -20);
        Feed(600, -40);
        Feed(1000, -20);
        Feed(1600, -40);

        Assert.Equal(2, _events.Count);
        Assert.Equal(T.At(0), _events[0].Start);
        Assert.Equal(T.At(1000), _events[1].Start);
    }

    [Fact]
    public void Overshoot_inside_the_tolerance_is_ignored()
    {
        Feed(0, -27.95);

        Assert.False(_detector.IsOver);
        Assert.Empty(_events);
    }

    [Fact]
    public void Event_names_the_app_that_was_loudest_at_the_maximum()
    {
        Feed(0, -20, "Chrome");
        Feed(10, -15, "Zoom");
        Feed(20, -22, "Chrome");
        Feed(700, -40, null);

        var e = Assert.Single(_events);
        Assert.Equal("Zoom", e.LoudestApp);
        T.Near(13, e.MaxOverDb);
    }

    [Fact]
    public void A_long_episode_is_split_every_five_seconds()
    {
        for (var ms = 0; ms <= 12000; ms += 100)
        {
            Feed(ms, -20);
        }

        Assert.Equal(2, _events.Count);
        Assert.Equal(T.At(0), _events[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), _events[0].Duration);
        Assert.Equal(T.At(5000), _events[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), _events[1].Duration);
        Assert.True(_detector.IsOver);
    }

    [Fact]
    public void A_clock_that_steps_back_does_not_throw_or_give_negative_durations()
    {
        Feed(1000, -20);

        Assert.Null(Record.Exception(() => Feed(500, -20)));

        Feed(2000, -40, null);
        var e = Assert.Single(_events);
        Assert.True(e.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void Reset_drops_an_open_episode_silently()
    {
        Feed(0, -20);

        _detector.Reset();

        Assert.False(_detector.IsOver);
        Feed(1000, -40, null);
        Assert.Empty(_events);
    }
}

public class NotificationThrottleTests
{
    [Fact]
    public void Allows_one_notification_per_key_per_interval()
    {
        var throttle = new NotificationThrottle(TimeSpan.FromSeconds(10));

        Assert.True(throttle.ShouldNotify("Zoom", T.At(0)));
        Assert.False(throttle.ShouldNotify("Zoom", T.At(5000)));
        Assert.True(throttle.ShouldNotify("Zoom", T.At(10000)));
        Assert.True(throttle.ShouldNotify("Chrome", T.At(5000)));
    }

    [Fact]
    public void A_clock_that_steps_back_does_not_silence_a_key()
    {
        var throttle = new NotificationThrottle(TimeSpan.FromSeconds(10));

        Assert.True(throttle.ShouldNotify("Zoom", T.At(20000)));
        Assert.True(throttle.ShouldNotify("Zoom", T.At(1000)));
    }
}

public class MessagesTests
{
    private static OverEvent Event(string? app, double seconds = 1.8) =>
        new(T.At(0), TimeSpan.FromSeconds(seconds), 9.6, -28.0, app);

    [Fact]
    public void Over_names_the_app_the_peak_the_ceiling_and_the_duration()
    {
        var message = Messages.Over(Event("Zoom"));

        Assert.Equal("Over your ceiling by 9.6 dB", message.Title);
        Assert.Equal(
            "Zoom peaked at -18.4 dB (ceiling -28.0 dB) for 1.8 s. Reported only: no limiter is connected.",
            message.Body);
    }

    [Fact]
    public void An_unknown_app_is_called_Sound() =>
        Assert.StartsWith("Sound peaked", Messages.Over(Event(null)).Body);

    [Fact]
    public void A_very_short_burst_says_under_a_tenth_of_a_second() =>
        Assert.Contains("for under 0.1 s", Messages.Over(Event("Zoom", 0.04)).Body);

    [Fact]
    public void The_list_line_has_the_local_time_app_overshoot_and_duration()
    {
        var zoom = Event("Zoom");
        var quick = Event(null, 0.04);
        var time = zoom.Start.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal($"{time}  Zoom  +9.6 dB over (1.8 s)", Messages.Line(zoom));
        Assert.Equal($"{time}  Sound  +9.6 dB over (under 0.1 s)", Messages.Line(quick));
    }

    private static TurnDownEvent TurnDown(string? app, double seconds = 3.4, double max = 6.24) =>
        new(T.At(0), TimeSpan.FromSeconds(seconds), max, -20.0, app);

    [Fact]
    public void TurnedDown_TitleBodyAndLine()
    {
        var e = TurnDown("Zoom");
        var time = e.Start.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var message = Messages.TurnedDown(e);

        Assert.Equal("Turned down by 6.2 dB", message.Title);
        Assert.Equal("Zoom went over your cap (-20.0 dB) for 3.4 s. Sound Protection turned it down.", message.Body);
        Assert.Equal($"{time}  Zoom  turned down 6.2 dB (3.4 s)", Messages.Line(e));
    }

    [Fact]
    public void TurnedDown_WithoutAnApp_SaysSound()
    {
        var e = TurnDown(null);
        var time = e.Start.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        Assert.StartsWith("Sound went over your cap", Messages.TurnedDown(e).Body);
        Assert.Equal($"{time}  Sound  turned down 6.2 dB (3.4 s)", Messages.Line(e));
    }

    [Fact]
    public void TurnedDown_AVeryShortBurst_SaysUnderATenthOfASecond() =>
        Assert.Contains("for under 0.1 s.", Messages.TurnedDown(TurnDown("Zoom", 0.04)).Body);
}
