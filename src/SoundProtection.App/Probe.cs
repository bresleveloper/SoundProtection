using System.Globalization;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// <c>--probe file [seconds] [deviceId]</c>: reads the Windows default output (or the given output) every 5 ms
/// and writes the highest peak of every 100 ms to a text file. Read-only. Used to check the reader against live audio.
/// </summary>
internal static class Probe
{
    public static int Run(string outputPath, int seconds, string? deviceId = null)
    {
        using var source = new WasapiAudioSource();
        var lines = new List<string>();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var nextLine = DateTime.UtcNow.AddMilliseconds(100);
        var windowPeak = Db.Floor;
        AudioSnapshot? last = null;

        while (DateTime.UtcNow < end)
        {
            var snapshot = source.Read(deviceId);
            if (snapshot != null)
            {
                last = snapshot;
                windowPeak = Math.Max(windowPeak, snapshot.PeakDbfs);
            }

            if (DateTime.UtcNow >= nextLine)
            {
                lines.Add(Line(snapshot, windowPeak));
                windowPeak = Db.Floor;
                nextLine = nextLine.AddMilliseconds(100);
            }

            HighResTimer.Sleep(5);
        }

        _ = last;
        File.WriteAllLines(outputPath, lines);
        return 0;
    }

    private static string Line(AudioSnapshot? s, double peak)
    {
        var time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (s is null)
        {
            return time + " | no output device";
        }

        var ear = EarLevel.Compute(peak, s.VolumeDb, s.Muted);
        var apps = string.Join(", ", s.Apps.Select(a => a.Name + ":" + F1(a.PeakDbfs)));
        return string.Join(
            " | ",
            time,
            s.DeviceName,
            s.VolumePercent.ToString("0.#", CultureInfo.InvariantCulture) + " %",
            F1(s.VolumeDb) + " dB",
            s.Muted ? "muted" : "live",
            "peak " + F1(peak),
            "ear " + F1(ear),
            apps);
    }

    private static string F1(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
