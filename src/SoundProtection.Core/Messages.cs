using System.Globalization;

namespace SoundProtection.Core;

public sealed record Message(string Title, string Body);

public static class Messages
{
    /// <summary>The pop-up alert for an event.</summary>
    public static Message Over(OverEvent e) =>
        new(
            $"Over your ceiling by {Db.Format(e.MaxOverDb)}",
            $"{AppOrSound(e)} peaked at {Db.Format(e.PeakEarDb)} (ceiling {Db.Format(e.CeilingDb)}) " +
            $"for {Duration(e.Duration)}. Reported only: no limiter is connected.");

    /// <summary>One line for the panel's event list.</summary>
    public static string Line(OverEvent e) =>
        $"{e.Start.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {AppOrSound(e)}  " +
        $"+{Db.Format(e.MaxOverDb)} over ({Duration(e.Duration)})";

    /// <summary>The pop-up alert for a sound that the plug-in turned down.</summary>
    public static Message TurnedDown(TurnDownEvent e) =>
        new(
            $"Turned down by {Db.Format(e.MaxTurnDownDb)}",
            $"{AppOrSound(e.App)} went over your cap ({Db.Format(e.CapDb)}) for {Duration(e.Duration)}. " +
            "Sound Protection turned it down.");

    /// <summary>One line for the panel's event list.</summary>
    public static string Line(TurnDownEvent e) =>
        $"{e.Start.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {AppOrSound(e.App)}  " +
        $"turned down {Db.Format(e.MaxTurnDownDb)} ({Duration(e.Duration)})";

    private static string AppOrSound(OverEvent e) => AppOrSound(e.LoudestApp);

    private static string AppOrSound(string? app) =>
        string.IsNullOrWhiteSpace(app) ? "Sound" : app;

    private static string Duration(TimeSpan duration) =>
        duration.TotalSeconds < 0.1
            ? "under 0.1 s"
            : duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
