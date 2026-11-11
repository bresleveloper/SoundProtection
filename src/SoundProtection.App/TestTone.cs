using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// Plays a short 1 kHz tone through an output, only when the user asks, so the cap can be tested without Zoom or
/// YouTube. It plays through Windows' shared mixer at the user's current Windows volume and changes no setting.
/// </summary>
internal static class TestTone
{
    public const double DefaultLevelDbfs = -20.0;
    public const int DefaultMilliseconds = 2000;

    private const int SampleRate = 48000;
    private const double FrequencyHz = 1000.0;
    private const int RampMilliseconds = 50;

    /// <summary>Starts the tone on its own thread and returns at once.</summary>
    public static void Play(string? deviceId, double levelDbfs = DefaultLevelDbfs, int milliseconds = DefaultMilliseconds)
    {
        var thread = new Thread(() => PlayAndWait(deviceId, levelDbfs, milliseconds))
        {
            IsBackground = true,
            Name = "SoundProtection.TestTone",
        };
        thread.Start();
    }

    /// <summary>Plays the tone and returns when it has finished (or after a safety timeout).</summary>
    public static void PlayAndWait(string? deviceId, double levelDbfs, int milliseconds)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = Open(enumerator, deviceId);
            var provider = new ToneSampleProvider(new ToneGenerator(SampleRate, FrequencyHz, levelDbfs, milliseconds, RampMilliseconds));
            using var finished = new ManualResetEventSlim();
            using var output = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithLatency(100).Build();
            output.PlaybackStopped += (_, _) => finished.Set();
            output.Init(provider);
            output.Play();
            finished.Wait(milliseconds + 3000);
            output.Stop();
        }
        catch (Exception ex)
        {
            Logging.Error("The test tone could not be played", ex);
        }
    }

    /// <summary>The chosen output if it is connected, otherwise the Windows default.</summary>
    private static MMDevice Open(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (deviceId is not null)
        {
            try
            {
                var chosen = enumerator.GetDevice(deviceId);
                if (chosen.State == DeviceState.Active)
                {
                    return chosen;
                }

                chosen.Dispose();
            }
            catch (Exception)
            {
                // The chosen output is gone: use the default.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    private sealed class ToneSampleProvider : ISampleProvider
    {
        private readonly ToneGenerator _tone;

        public ToneSampleProvider(ToneGenerator tone) => _tone = tone;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public int Read(Span<float> buffer) => _tone.FillStereo(buffer);
    }
}
