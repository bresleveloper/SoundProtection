using System.Globalization;
using SoundProtection.Core;

namespace SoundProtection.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text);
    }

    [Fact]
    public void A_missing_file_gives_the_defaults()
    {
        var s = new SettingsStore(FilePath).Load();

        Assert.True(s.AlertsEnabled);
        Assert.Equal(-20.0, s.CeilingDb);
        Assert.Null(s.WatchedDeviceId);
        Assert.True(s.ShowNotifications);
        Assert.True(s.ProtectionEnabled);
    }

    [Fact]
    public void Save_then_load_round_trips_every_field()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new Settings
        {
            AlertsEnabled = false,
            CeilingDb = -31.5,
            WatchedDeviceId = "{0.0.0.00000000}.{abc}",
            ShowNotifications = false,
        });

        var s = store.Load();

        Assert.False(s.AlertsEnabled);
        Assert.Equal(-31.5, s.CeilingDb);
        Assert.Equal("{0.0.0.00000000}.{abc}", s.WatchedDeviceId);
        Assert.False(s.ShowNotifications);
    }

    [Fact]
    public void Save_creates_the_folder_and_leaves_no_temp_file()
    {
        new SettingsStore(FilePath).Save(new Settings());

        Assert.True(File.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
    }

    [Fact]
    public void Corrupt_json_gives_defaults_and_keeps_the_bad_file()
    {
        WriteRaw("{ not json");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(-20.0, s.CeilingDb);
        Assert.Equal(Settings.CurrentVersion, s.SettingsVersion);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void A_file_that_is_locked_gives_defaults()
    {
        WriteRaw("{ \"ceilingDb\": -40 }");
        using var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(-20.0, s.CeilingDb);
        Assert.Equal(Settings.CurrentVersion, s.SettingsVersion);
    }

    [Theory]
    [InlineData(12.0, 0.0)]
    [InlineData(-500.0, -80.0)]
    public void An_out_of_range_ceiling_is_clamped_on_load(double stored, double expected)
    {
        WriteRaw("{ \"settingsVersion\": 2, \"ceilingDb\": " + stored.ToString(CultureInfo.InvariantCulture) + " }");

        Assert.Equal(expected, new SettingsStore(FilePath).Load().CeilingDb);
    }

    [Fact]
    public void Clone_is_an_independent_copy()
    {
        var a = new Settings { CeilingDb = -30, WatchedDeviceId = "x" };

        var b = a.Clone();
        b.CeilingDb = -10;

        Assert.Equal(-30, a.CeilingDb);
        Assert.Equal("x", b.WatchedDeviceId);
    }

    [Fact]
    public void OldFile_WithoutVersion_MigratesCapToMinus20()
    {
        // What the ear-level build wrote: a ceiling of -33 dB at the ear and no version.
        WriteRaw("{ \"alertsEnabled\": false, \"ceilingDb\": -33, \"watchedDeviceId\": \"dev-1\", \"showNotifications\": false }");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(-20.0, s.CeilingDb);
        Assert.Equal(2, s.SettingsVersion);
        Assert.False(s.AlertsEnabled); // everything else is kept
        Assert.Equal("dev-1", s.WatchedDeviceId);
        Assert.False(s.ShowNotifications);
        Assert.True(s.ProtectionEnabled);
    }

    [Fact]
    public void OldFile_WithVersion1_AlsoMigrates()
    {
        WriteRaw("{ \"settingsVersion\": 1, \"ceilingDb\": -45 }");

        Assert.Equal(-20.0, new SettingsStore(FilePath).Load().CeilingDb);
    }

    [Fact]
    public void CurrentFile_KeepsCap()
    {
        WriteRaw("{ \"settingsVersion\": 2, \"ceilingDb\": -33, \"protectionEnabled\": false }");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(-33.0, s.CeilingDb);
        Assert.Equal(2, s.SettingsVersion);
        Assert.False(s.ProtectionEnabled);
    }

    [Fact]
    public void ANewerFile_IsLeftAlone()
    {
        WriteRaw("{ \"settingsVersion\": 3, \"ceilingDb\": -33 }");

        var s = new SettingsStore(FilePath).Load();

        Assert.Equal(-33.0, s.CeilingDb);
        Assert.Equal(3, s.SettingsVersion);
    }

    [Fact]
    public void NewSettings_HaveVersion2AndCapMinus20AndProtectionOn()
    {
        var s = new SettingsStore(FilePath).Load(); // no file yet

        Assert.Equal(2, s.SettingsVersion);
        Assert.Equal(Settings.CurrentVersion, s.SettingsVersion);
        Assert.Equal(-20.0, s.CeilingDb);
        Assert.True(s.ProtectionEnabled);
    }

    [Fact]
    public void Migration_HappensOnce_BecauseTheMigratedFileIsSavedWithTheVersion()
    {
        WriteRaw("{ \"ceilingDb\": -33 }");
        var store = new SettingsStore(FilePath);

        var migrated = store.Load();
        migrated.CeilingDb = -35;
        store.Save(migrated);

        Assert.Equal(-35.0, store.Load().CeilingDb); // not reset to -20 a second time
    }

    [Fact]
    public void Save_StampsTheCurrentVersion_SoACapNeverGetsResetByAccident()
    {
        var store = new SettingsStore(FilePath);
        var unversioned = new Settings { CeilingDb = -31.5 }; // built by code, so no version

        store.Save(unversioned);

        Assert.Null(unversioned.SettingsVersion); // the caller's object is not touched
        Assert.Equal(-31.5, store.Load().CeilingDb);
    }

    [Fact]
    public void ProtectionEnabled_RoundTrips()
    {
        var store = new SettingsStore(FilePath);

        store.Save(new Settings { ProtectionEnabled = false, CeilingDb = -25 });

        var s = store.Load();
        Assert.False(s.ProtectionEnabled);
        Assert.Equal(-25.0, s.CeilingDb);
    }
}
