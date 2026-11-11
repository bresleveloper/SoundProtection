using System.Globalization;
using SoundProtection.Core;

namespace SoundProtection.Tests;

public class DbTests
{
    [Fact] public void FromLinear_one_is_zero_db() => T.Near(0.0, Db.FromLinear(1.0));

    [Fact] public void FromLinear_half_is_minus_6_0206_db() => T.Near(-6.0206, Db.FromLinear(0.5), 0.001);

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(1e-9)]
    [InlineData(-1.0)]
    public void FromLinear_silence_or_invalid_is_floor(double linear) =>
        Assert.Equal(Db.Floor, Db.FromLinear(linear));

    [Fact] public void ToLinear_minus_20_db_is_one_tenth() => T.Near(0.1, Db.ToLinear(-20.0));

    [Theory]
    [InlineData(-31.44, "-31.4 dB")]
    [InlineData(0.0, "0.0 dB")]
    [InlineData(-0.04, "0.0 dB")]
    [InlineData(9.6, "9.6 dB")]
    [InlineData(-99.9, "-99.9 dB")]
    [InlineData(-100.0, "silent")]
    [InlineData(-120.0, "silent")]
    [InlineData(double.NaN, "silent")]
    public void Format_rounds_to_one_decimal_and_marks_silence(double db, string expected) =>
        Assert.Equal(expected, Db.Format(db));

    [Fact]
    public void Format_ignores_the_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("-31.4 dB", Db.Format(-31.44));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}

public class EarLevelTests
{
    [Fact] public void Adds_volume_to_the_digital_peak() => T.Near(-31.4, EarLevel.Compute(-7.7, -23.7, false));

    [Fact] public void Muted_output_is_silent() => Assert.Equal(Db.Floor, EarLevel.Compute(-7.7, -23.7, true));

    [Fact] public void Silent_peak_stays_silent() => Assert.Equal(Db.Floor, EarLevel.Compute(Db.Floor, -23.7, false));

    [Fact] public void Full_volume_leaves_the_peak_unchanged() => T.Near(-10.0, EarLevel.Compute(-10.0, 0.0, false));
}

public class PeakHoldTests
{
    [Fact]
    public void Returns_the_max_inside_the_window()
    {
        var hold = new PeakHold(TimeSpan.FromSeconds(10));
        hold.Add(T.At(0), -30);
        hold.Add(T.At(1000), -20);
        hold.Add(T.At(2000), -40);

        Assert.Equal(-20.0, hold.Max(T.At(3000)));
        Assert.Equal(-40.0, hold.Max(T.At(11500)));
        Assert.Equal(Db.Floor, hold.Max(T.At(13000)));
    }

    [Fact]
    public void Empty_hold_is_floor() =>
        Assert.Equal(Db.Floor, new PeakHold(TimeSpan.FromSeconds(10)).Max(T.At(0)));
}

public class MeterBallisticsTests
{
    [Fact]
    public void Attacks_instantly_and_decays_linearly_without_undershoot()
    {
        var meter = new MeterBallistics(24.0);

        Assert.Equal(-10.0, meter.Update(-10.0, 0.1));
        T.Near(-22.0, meter.Update(-60.0, 0.5));
        Assert.Equal(-60.0, meter.Update(-60.0, 10.0));
    }

    [Fact] public void Starts_at_floor() => Assert.Equal(Db.Floor, new MeterBallistics().Level);
}
