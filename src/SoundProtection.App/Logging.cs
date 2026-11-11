using System.Text;

namespace SoundProtection.App;

/// <summary>Appends to %APPDATA%\SoundProtection\log.txt. Logging never throws.</summary>
internal static class Logging
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastLogged = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SoundProtection");

    public static string LogPath => Path.Combine(Folder, "log.txt");

    public static void Info(string line) => Write("INFO", line);

    public static void Error(string line, Exception? ex = null) =>
        Write("ERROR", ex is null ? line : $"{line}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Logs a given problem at most once a minute, so a failure that repeats every few milliseconds cannot flood the file.</summary>
    public static void ErrorOnce(string key, string line, Exception? ex = null)
    {
        lock (Gate)
        {
            if (LastLogged.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1))
            {
                return;
            }

            LastLogged[key] = DateTime.UtcNow;
        }

        Error(line, ex);
    }

    private static void Write(string level, string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 1_000_000)
                {
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                }

                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }
}
