namespace SoundProtection.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--probe")
        {
            var seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 5;
            return Probe.Run(args[1], seconds, args.Length > 3 ? args[3] : null);
        }

        if (args.Length >= 3 && args[0] == "--tone" &&
            double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level) &&
            int.TryParse(args[2], out var milliseconds))
        {
            // --tone <dBFS> <ms> [deviceId]: plays the test tone once and exits. Used to check the playback path at a very low level.
            TestTone.PlayAndWait(args.Length > 3 ? args[3] : null, level, milliseconds);
            return 0;
        }

        if (args.Length >= 2 && args[0] is "--demo-png" or "--demo-png-legacy")
        {
            // --demo-png <file> draws the panel with the plug-in in place; --demo-png-legacy <file> draws the older ear-level one.
            ApplicationConfiguration.Initialize();
            return DemoPng.Run(args[1], legacy: args[0] == "--demo-png-legacy");
        }

        // One instance only: a second launch simply exits.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SoundProtection.Tray", out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Logging.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logging.Error("Unhandled exception", e.ExceptionObject as Exception);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var tray = new TrayApp();
        Application.Run(tray);
        return 0;
    }
}
