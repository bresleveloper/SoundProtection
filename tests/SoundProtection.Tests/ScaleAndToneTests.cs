using SoundProtection.Core;

namespace SoundProtection.Tests;

public class DbScaleTests
{
    [Theory]
    [InlineData(-30.0, 0.5)]
    [InlineData(-60.0, 0.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-80.0, 0.0)] // below the scale clamps to its start
    [InlineData(5.0, 1.0)] // above the scale clamps to its end
    public void ToFraction_places_a_level_on_a_minus_60_to_0_scale(double db, double expected) =>
        T.Near(expected, DbScale.ToFraction(db, -60, 0));

    [Theory]
    [InlineData(0.5, -30.0)]
    [InlineData(0.4999, -30.0)] // snaps to the 0.5 dB step
    [InlineData(0.3, -42.0)]
    [InlineData(0.0, -60.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(-1.0, -60.0)] // outside the slider clamps
    [InlineData(2.0, 0.0)]
    public void FromFraction_snaps_to_the_step_and_stays_on_the_scale(double fraction, double expected) =>
        T.Near(expected, DbScale.FromFraction(fraction, -60, 0, 0.5));

    [Fact]
    public void A_scale_with_no_width_gives_position_zero() =>
        T.Near(0.0, DbScale.ToFraction(-10, -10, -10));
}

public class ToneGeneratorTests
{
    private static float[] Render(ToneGenerator tone, int bufferSize = 2048)
    {
        var all = new List<float>();
        var buffer = new float[bufferSize];
        int written;
        while ((written = tone.FillStereo(buffer, 0, buffer.Length)) > 0)
        {
            all.AddRange(buffer.Take(written));
        }

        return all.ToArray();
    }

    private static ToneGenerator TwoSecondTone() => new(48000, 1000.0, -20.0, 2000, 50);

    [Fact]
    public void The_peak_matches_the_requested_level()
    {
        var samples = Render(TwoSecondTone());

        T.Near(-20.0, Db.FromLinear(samples.Max(Math.Abs)), 0.01);
    }

    [Fact]
    public void Length_is_duration_times_rate_in_stereo() =>
        Assert.Equal(48000 * 2 * 2, Render(TwoSecondTone()).Length);

    [Fact]
    public void Both_channels_carry_the_same_samples()
    {
        var samples = Render(TwoSecondTone());

        for (var i = 0; i < samples.Length; i += 2)
        {
            Assert.Equal(samples[i], samples[i + 1]);
        }
    }

    [Fact]
    public void It_fades_in_and_out_instead_of_clicking()
    {
        var samples = Render(TwoSecondTone());
        const int ramp = 2400; // 50 ms at 48 kHz
        var frames = samples.Length / 2;

        for (var i = 0; i < ramp; i++)
        {
            Assert.True(Math.Abs(samples[2 * i]) <= 0.1 * i / ramp + 1e-6, $"fade-in frame {i}");
            Assert.True(Math.Abs(samples[2 * (frames - 1 - i)]) <= 0.1 * i / ramp + 1e-6, $"fade-out frame {i}");
        }
    }

    [Fact]
    public void Reading_in_small_chunks_gives_the_same_samples()
    {
        var whole = Render(TwoSecondTone());
        var chunked = Render(TwoSecondTone(), bufferSize: 6);

        Assert.Equal(whole, chunked);
    }

    [Fact]
    public void The_span_overload_gives_the_same_samples()
    {
        var fromArray = Render(TwoSecondTone(), bufferSize: 480);

        var tone = TwoSecondTone();
        var all = new List<float>();
        var buffer = new float[480];
        int written;
        while ((written = tone.FillStereo(buffer.AsSpan())) > 0)
        {
            all.AddRange(buffer.Take(written));
        }

        Assert.Equal(fromArray, all.ToArray());
    }

    [Fact]
    public void A_finished_tone_returns_zero()
    {
        var tone = TwoSecondTone();
        Render(tone);

        Assert.Equal(0, tone.FillStereo(new float[64], 0, 64));
    }

    [Fact]
    public void A_tone_shorter_than_its_fades_still_renders_quieter_than_full_level()
    {
        var samples = Render(new ToneGenerator(48000, 1000.0, -20.0, 20, 50));

        Assert.Equal(48000 * 20 / 1000 * 2, samples.Length);
        Assert.True(samples.Max(Math.Abs) < 0.1f);
    }
}
