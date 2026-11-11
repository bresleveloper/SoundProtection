namespace SoundProtection.Core;

public static class DeviceLabels
{
    private const string FallbackNote = " (chosen output not connected: showing the Windows default)";

    /// <summary>The device name for the panel; says so when the chosen output is gone and the Windows default is shown instead.</summary>
    public static string Describe(string name, bool fellBackToDefault) =>
        fellBackToDefault ? name + FallbackNote : name;

    /// <summary>The device's own name, without the note that <see cref="Describe"/> adds when the Windows default is shown instead.</summary>
    public static string FriendlyName(string described) =>
        described.EndsWith(FallbackNote, StringComparison.Ordinal) ? described[..^FallbackNote.Length] : described;
}
