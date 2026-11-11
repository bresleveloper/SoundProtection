namespace SoundProtection.Core;

public static class AppNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zoom"] = "Zoom",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        ["teams"] = "Teams",
        ["ms-teams"] = "Teams",
        ["spotify"] = "Spotify",
        ["vlc"] = "VLC",
        ["discord"] = "Discord",
        ["soundprotection"] = "Test tone", // the only sound this app plays itself
    };

    /// <summary>A readable name for a process, as it appears in the panel and in messages.</summary>
    public static string Friendly(string? processName, bool isSystemSounds)
    {
        if (isSystemSounds)
        {
            return "System sounds";
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            return "Unknown";
        }

        var name = processName.Trim();
        return Known.TryGetValue(name, out var friendly) ? friendly : char.ToUpperInvariant(name[0]) + name[1..];
    }
}
