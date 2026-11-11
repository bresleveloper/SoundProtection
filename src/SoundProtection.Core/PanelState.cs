namespace SoundProtection.Core;

public sealed record AppRow(string Name, double PeakDbfs, double EarDb);

/// <summary>Everything the panel and the tray icon show, at one moment. Levels are dB below the output's maximum.</summary>
public sealed record PanelState(
    DateTimeOffset Time,
    bool HasDevice,
    string DeviceName,
    double VolumePercent,
    double VolumeDb,
    bool Muted,
    double EarDb,
    double HoldDb,
    double DisplayDb,
    double CeilingDb,
    bool Over,
    double OverByDb,
    bool AlertsEnabled,
    IReadOnlyList<AppRow> Apps)
{
    public static PanelState Empty(DateTimeOffset now, double ceilingDb = -28.0, bool alertsEnabled = true) =>
        new(
            now,
            HasDevice: false,
            DeviceName: "No output device",
            VolumePercent: 0,
            VolumeDb: Db.Floor,
            Muted: false,
            EarDb: Db.Floor,
            HoldDb: Db.Floor,
            DisplayDb: Db.Floor,
            CeilingDb: ceilingDb,
            Over: false,
            OverByDb: 0,
            AlertsEnabled: alertsEnabled,
            Apps: Array.Empty<AppRow>());
}
