namespace SoundProtection.App;

/// <summary>
/// Where the plug-in's files are. The installer creates the data folder (and lets the tray write the config in it);
/// the tray never creates it.
/// </summary>
internal static class ProtectionPaths
{
    /// <summary>%ProgramData%\SoundProtection</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SoundProtection");

    /// <summary>The 32-byte config that the tray writes and the plug-in reads.</summary>
    public static string ConfigPath => Path.Combine(DataDir, "config.bin");

    /// <summary>The memory-mapped telemetry file that the plug-in writes and the tray reads.</summary>
    public static string TelemetryPath => Path.Combine(DataDir, "telemetry", "apo.bin");

    /// <summary>%ProgramFiles%\SoundProtection\SoundProtection.Install.ps1, which repairs or removes the installation.</summary>
    public static string InstallerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SoundProtection", "SoundProtection.Install.ps1");
}
