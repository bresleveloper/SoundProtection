using SoundProtection.Core;

namespace SoundProtection.Tests;

public class PacingTests
{
    private static AudioSnapshot Snap(double peak, params AppLevel[] apps) =>
        new("dev", "Headphones", false, 0, 100, peak, apps);

    [Fact]
    public void A_signal_or_a_playing_app_counts_as_active()
    {
        Assert.True(Pacing.IsActive(Snap(-60)));
        Assert.True(Pacing.IsActive(Snap(Db.Floor, new AppLevel("Zoom", 1, Db.Floor))));
        Assert.False(Pacing.IsActive(Snap(Db.Floor)));
        Assert.False(Pacing.IsActive(Snap(-95)));
    }

    [Fact]
    public void No_device_polls_slowly() =>
        Assert.Equal(250, Pacing.NextDelayMs(hasDevice: false, activeNow: false, sinceActive: TimeSpan.Zero));

    [Fact]
    public void Activity_polls_every_5_ms() =>
        Assert.Equal(5, Pacing.NextDelayMs(hasDevice: true, activeNow: true, sinceActive: TimeSpan.FromMinutes(5)));

    [Fact]
    public void Polling_stays_fast_for_two_seconds_after_activity()
    {
        Assert.Equal(5, Pacing.NextDelayMs(true, false, TimeSpan.FromMilliseconds(1999)));
        Assert.Equal(40, Pacing.NextDelayMs(true, false, TimeSpan.FromSeconds(2)));
    }
}
