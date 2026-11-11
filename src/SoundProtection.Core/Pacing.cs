namespace SoundProtection.Core;

/// <summary>
/// How often to read the output. Every peak read is a call to the Windows audio service, so the reader
/// polls fast only while sound is, or recently was, playing.
/// </summary>
public static class Pacing
{
    public const int ActiveMs = 5;
    public const int IdleMs = 40;
    public const int NoDeviceMs = 250;

    private const double QuietDbfs = -90.0;
    private static readonly TimeSpan ActiveHold = TimeSpan.FromSeconds(2);

    /// <summary>True when there is a signal, or an app is playing (even if it is silent right now).</summary>
    public static bool IsActive(AudioSnapshot s) => s.PeakDbfs > QuietDbfs || s.Apps.Count > 0;

    public static int NextDelayMs(bool hasDevice, bool activeNow, TimeSpan sinceActive) =>
        !hasDevice ? NoDeviceMs : activeNow || sinceActive < ActiveHold ? ActiveMs : IdleMs;
}
