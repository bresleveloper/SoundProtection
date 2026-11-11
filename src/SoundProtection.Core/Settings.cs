using System.Text.Json;

namespace SoundProtection.Core;

public sealed class Settings
{
    public const double MinCeilingDb = -80.0;
    public const double MaxCeilingDb = 0.0;

    /// <summary>
    /// The version of the settings file. 1 (or no version) was the ear-level build, where <c>CeilingDb</c> was a level at the ear.
    /// 2 is the build with the plug-in, where it is the cap before the volume slider.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>The cap that a new, or an older, settings file gets.</summary>
    public const double DefaultCeilingDb = -20.0;

    /// <summary>Null for settings that were built in code and have not been saved yet.</summary>
    public int? SettingsVersion { get; set; }

    public bool AlertsEnabled { get; set; } = true;

    /// <summary>The cap: loudness before the volume slider, in dB. In the older ear-level display it is the ceiling at the ear.</summary>
    public double CeilingDb { get; set; } = DefaultCeilingDb;

    /// <summary>The output to watch; null follows the Windows default output.</summary>
    public string? WatchedDeviceId { get; set; }

    public bool ShowNotifications { get; set; } = true;

    /// <summary>False switches the plug-in's protection off. The plug-in stays in line and passes sound through.</summary>
    public bool ProtectionEnabled { get; set; } = true;

    public Settings Clone() => (Settings)MemberwiseClone();
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>
    /// Never throws for a bad, locked or missing file: the defaults are returned instead. A file from the ear-level build
    /// (no version, or a version below 2) keeps its other settings but gets the new default cap, because its old number
    /// meant a level at the ear and would be a different, much quieter, cap now.
    /// </summary>
    public Settings Load()
    {
        if (!File.Exists(_path))
        {
            return Fresh();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path), Options)
                           ?? throw new JsonException("The settings file is empty.");
            if ((settings.SettingsVersion ?? 0) < Settings.CurrentVersion)
            {
                settings.CeilingDb = Settings.DefaultCeilingDb;
                settings.SettingsVersion = Settings.CurrentVersion;
            }

            settings.CeilingDb = Math.Clamp(settings.CeilingDb, Settings.MinCeilingDb, Settings.MaxCeilingDb);
            return settings;
        }
        catch (JsonException)
        {
            KeepBadFile();
            return Fresh();
        }
        catch (IOException)
        {
            return Fresh();
        }
        catch (UnauthorizedAccessException)
        {
            return Fresh();
        }
    }

    /// <summary>Saves a copy of the settings stamped with the current version, so that a saved cap is never migrated again.</summary>
    public void Save(Settings settings)
    {
        var toSave = settings.Clone();
        toSave.SettingsVersion = Math.Max(settings.SettingsVersion ?? 0, Settings.CurrentVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(toSave, Options));
        File.Move(temp, _path, overwrite: true);
    }

    private static Settings Fresh() => new() { SettingsVersion = Settings.CurrentVersion };

    private void KeepBadFile()
    {
        try
        {
            File.Move(_path, _path + ".bad", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
