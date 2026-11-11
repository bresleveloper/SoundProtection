namespace SoundProtection.Core;

public enum IconKind
{
    Off,
    Watching,
    Over,
    Down,
}

public static class TrayText
{
    private const int NotifyIconTextLimit = 127;
    private static readonly TimeSpan OverIconHold = TimeSpan.FromSeconds(3);

    /// <summary>The tray tooltip. Windows limits it to 127 characters; this stays well inside that.</summary>
    public static string Tooltip(PanelState s)
    {
        if (!s.AlertsEnabled)
        {
            return "Sound Protection: alerts off";
        }

        if (!s.HasDevice)
        {
            return "Sound Protection: no output device";
        }

        return $"Sound Protection: {Db.Format(s.EarDb)} at ear, ceiling {Db.Format(s.CeilingDb)} (alerts only)";
    }

    /// <summary>The tooltip once the plug-in's state is known: its status line, cut to what Windows allows. Before that, the ear-level one.</summary>
    public static string Tooltip(PanelState s, ProtectionState? protection)
    {
        if (protection is null)
        {
            return Tooltip(s);
        }

        var text = "Sound Protection: " + protection.StatusText;
        return text.Length <= NotifyIconTextLimit ? text : text[..NotifyIconTextLimit];
    }

    public static IconKind Icon(PanelState s, DateTimeOffset now, DateTimeOffset? lastOverClosed)
    {
        if (!s.AlertsEnabled)
        {
            return IconKind.Off;
        }

        var recentlyOver = lastOverClosed is { } closed && now >= closed && now - closed < OverIconHold;
        return s.Over || recentlyOver ? IconKind.Over : IconKind.Watching;
    }

    /// <summary>
    /// The icon once the plug-in's state is known. Red "down" when protection is not installed or not working; grey when it is switched off;
    /// otherwise blue, turning red while something is turned down and for 3 s after an alert. Switching alerts off silences the red but
    /// does not make the icon grey, because protection keeps working. While sound is on an output the plug-in does not cover, and
    /// before the state is known, the ear-level icon (<see cref="Icon(PanelState, DateTimeOffset, DateTimeOffset?)"/>).
    /// </summary>
    public static IconKind Icon(PanelState s, ProtectionState? protection, DateTimeOffset now, DateTimeOffset? lastAlertClosed)
    {
        if (protection is null)
        {
            return Icon(s, now, lastAlertClosed);
        }

        switch (protection.Status)
        {
            case ProtectionStatus.NotInstalled:
            case ProtectionStatus.Down:
                return IconKind.Down;
            case ProtectionStatus.Off:
                return IconKind.Off;
            case ProtectionStatus.NotProtected:
                return Icon(s, now, lastAlertClosed); // the plug-in is not involved: what turned down means nothing here
        }

        if (!s.AlertsEnabled)
        {
            return IconKind.Watching;
        }

        var recentAlert = lastAlertClosed is { } closed && now >= closed && now - closed < OverIconHold;
        return protection.TurnDownDb >= TurnDownDetector.ThresholdDb || recentAlert ? IconKind.Over : IconKind.Watching;
    }
}
