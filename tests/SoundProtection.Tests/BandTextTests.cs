using System.Globalization;
using SoundProtection.Core;

namespace SoundProtection.Tests;

public class BandTextTests
{
    [Theory]
    [InlineData(0.0, "Off")]
    [InlineData(double.NaN, "Off")]
    [InlineData(3.0, "Off")]            // above the range: still the Off end
    [InlineData(-0.5, "-0.5 dB")]       // the first step below Off
    [InlineData(-32.0, "-32.0 dB")]
    [InlineData(-60.0, "-60.0 dB")]
    public void Value_SaysOffAtTheRightEnd_AndTheCapEverywhereElse(double capDb, string expected) =>
        Assert.Equal(expected, BandText.Value(capDb));

    [Theory]
    [InlineData(0.0, "")]
    [InlineData(0.04, "")]
    [InlineData(0.05, "cutting 0.1 dB")] // the smallest cut that is shown
    [InlineData(4.04, "cutting 4.0 dB")]
    [InlineData(12.0, "cutting 12.0 dB")]
    [InlineData(double.NaN, "")]
    [InlineData(double.PositiveInfinity, "")]
    [InlineData(-1.0, "")]
    public void Cut_IsBlankBelowFiveHundredthsOfADb_ElseSaysTheCut(double cutDb, string expected) =>
        Assert.Equal(expected, BandText.Cut(cutDb));

    [Fact]
    public void Cut_ignores_the_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // a comma where the point should be
            Assert.Equal("cutting 4.0 dB", BandText.Cut(4.04));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Tooltips_ArePlainWords_AndSayAllTheWayRightIsOff()
    {
        Assert.Equal(
            "Bass cap: when the deep, boomy part of the sound goes over this level, only that part is turned down. All the way right = off.",
            BandText.BassTip);
        Assert.Equal(
            "Treble cap: when the sharp, high part of the sound goes over this level, only that part is turned down. All the way right = off.",
            BandText.TrebleTip);
    }
}
