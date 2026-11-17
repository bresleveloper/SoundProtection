using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// <c>--demo-png file</c> renders the panel with made-up loud readings to a PNG, then exits; <c>--demo-png-legacy file</c> does the same for
/// the older ear-level display, which the panel falls back to when protection is not installed. For checking the layout.
/// </summary>
internal static class DemoPng
{
    public static int Run(string path, bool legacy = false)
    {
        using var form = new PanelForm
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-12000, -12000),
            ShowInTaskbar = false,
        };
        form.SetOutputs(
            new[]
            {
                new OutputDevice("1", "Headphones (Realtek(R) Audio)", true),
                new OutputDevice("2", "Speakers (Realtek(R) Audio)", false),
            },
            null);
        form.SetAlerts(true);
        form.Show();

        if (legacy)
        {
            ShowEarLevel(form);
        }
        else
        {
            ShowProtection(form);
        }

        for (var i = 0; i < 5; i++)
        {
            Application.DoEvents();
        }

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return 0;
    }

    // The plug-in is in place: the number is the loudest sound before the volume slider, and Zoom is being turned down to the cap.
    // The bass cap is off, and the treble cap is at -32 dB and is turning the high part of the sound down by 4 dB.
    private static void ShowProtection(PanelForm form)
    {
        const double cap = -20.0;
        form.SetCeiling(cap);
        form.SetBands(bassCapDb: 0.0, trebleCapDb: -32.0);

        var now = DateTimeOffset.UtcNow;
        form.AddEvent(new TurnDownEvent(now.AddSeconds(-90), TimeSpan.FromSeconds(0.4), 3.2, cap, "Chrome"));
        form.AddEvent(new TurnDownEvent(now.AddSeconds(-12), TimeSpan.FromSeconds(1.8), 6.2, cap, "Zoom"));

        var protection = new ProtectionView(
            new ProtectionState(
                ProtectionStatus.Protected,
                "Protected: cap " + Db.Format(cap),
                LoudestInputDb: -14.2,
                TurnDownDb: 6.2,
                SafetyCapDb: 0.0,
                Apps: new[]
                {
                    new AppTurnDown("Zoom", 6.2),
                    new AppTurnDown("Chrome", 0.0),
                },
                BassCutDb: 0.0,
                TrebleCutDb: 4.0),
            new[] { "Apps can take exclusive control of Headphones (Realtek(R) Audio)" });

        form.ShowState(EarState(now, cap, earDb: -43.7, holdDb: -40.1, over: false, overByDb: 0), protection);
    }

    // The plug-in is not installed: the level at the ear and the alerts of the older display, under a red status line.
    // The sliders show the same bass and treble caps as above, but there is no plug-in to say what they are cutting.
    private static void ShowEarLevel(PanelForm form)
    {
        const double ceiling = -28.0;
        form.SetCeiling(ceiling);
        form.SetBands(bassCapDb: 0.0, trebleCapDb: -32.0);

        var now = DateTimeOffset.UtcNow;
        form.AddEvent(new OverEvent(now.AddSeconds(-90), TimeSpan.FromSeconds(0.4), 3.2, ceiling, "Chrome"));
        form.AddEvent(new OverEvent(now.AddSeconds(-12), TimeSpan.FromSeconds(1.8), 9.6, ceiling, "Zoom"));

        var protection = new ProtectionView(
            new ProtectionState(
                ProtectionStatus.NotInstalled,
                "Protection is not installed",
                LoudestInputDb: Db.Floor,
                TurnDownDb: 0.0,
                SafetyCapDb: 0.0,
                Apps: Array.Empty<AppTurnDown>()),
            Array.Empty<string>());

        form.ShowState(
            EarState(now, ceiling, earDb: -21.9, holdDb: -18.4, over: true, overByDb: 6.1) with
            {
                Apps = new[]
                {
                    new AppRow("Zoom", -0.2, -23.9),
                    new AppRow("Chrome", -31.5, -55.2),
                },
            },
            protection);
    }

    private static PanelState EarState(DateTimeOffset now, double ceilingDb, double earDb, double holdDb, bool over, double overByDb) =>
        new(
            now,
            HasDevice: true,
            DeviceName: "Headphones (Realtek(R) Audio)",
            VolumePercent: 20,
            VolumeDb: -23.7,
            Muted: false,
            EarDb: earDb,
            HoldDb: holdDb,
            DisplayDb: earDb,
            CeilingDb: ceilingDb,
            Over: over,
            OverByDb: overByDb,
            AlertsEnabled: true,
            Apps: Array.Empty<AppRow>());
}
