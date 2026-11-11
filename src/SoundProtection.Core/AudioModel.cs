namespace SoundProtection.Core;

/// <summary>One app that is playing sound to the watched output. <c>PeakDbfs</c> is its digital peak.</summary>
public sealed record AppLevel(string Name, int ProcessId, double PeakDbfs);

public sealed record OutputDevice(string Id, string Name, bool IsDefault);

/// <summary>One reading of an output device. <c>PeakDbfs</c> is read before the Windows volume is applied.</summary>
public sealed record AudioSnapshot(
    string DeviceId,
    string DeviceName,
    bool Muted,
    double VolumeDb,
    double VolumePercent,
    double PeakDbfs,
    IReadOnlyList<AppLevel> Apps);

/// <summary>A read-only view of the Windows output devices.</summary>
public interface IAudioSource : IDisposable
{
    IReadOnlyList<OutputDevice> ListOutputs();

    /// <summary>Reads the given output, or the Windows default output when <paramref name="deviceId"/> is null.</summary>
    AudioSnapshot? Read(string? deviceId);
}
