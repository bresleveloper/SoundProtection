using Microsoft.Win32;

namespace SoundProtection.App;

/// <summary>The opt-in "Start with Windows" entry in the current user's Run key. Nothing else is written to the registry.</summary>
internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SoundProtection";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool on, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (on)
        {
            key.SetValue(ValueName, "\"" + exePath + "\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
