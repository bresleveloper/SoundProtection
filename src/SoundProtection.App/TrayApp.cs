using System.ComponentModel;
using System.Diagnostics;
using SoundProtection.Core;
using Message = SoundProtection.Core.Message; // not System.Windows.Forms.Message

namespace SoundProtection.App;

/// <summary>The tray icon, its menu, the pop-up alerts and the panel, wired to the sampler.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const int UacDeclined = 1223; // ERROR_CANCELLED: the person closed the administrator prompt

    private readonly SettingsStore _store = new(Path.Combine(Logging.Folder, "settings.json"));
    private readonly WasapiAudioSource _source = new();
    private readonly MonitorEngine _engine = new();
    private readonly NotificationThrottle _throttle = new(TimeSpan.FromSeconds(10));
    private readonly Dictionary<IconKind, Icon> _icons = new();
    private readonly NotifyIcon _tray = new();
    private readonly PanelForm _panel = new();
    private readonly Sampler _sampler;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly ToolStripMenuItem _protectionItem = new("Protection");
    private readonly ToolStripMenuItem _alertsItem = new("Alerts");
    private readonly ToolStripMenuItem _popupsItem = new("Show pop-up alerts");
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows");
    private readonly ToolStripMenuItem _repairItem = new("Repair protection\u2026") { Enabled = false };
    private readonly ToolStripMenuItem _removeItem = new("Remove protection (undo)\u2026") { Enabled = false };

    private volatile Settings _settings;
    private DateTimeOffset? _lastAlertClosed;
    private IconKind? _shownIcon;
    private DateTimeOffset _trayRefreshedAt = DateTimeOffset.MinValue;
    private bool _cleanedUp;

    public TrayApp()
    {
        _settings = _store.Load();
        foreach (var kind in Enum.GetValues<IconKind>())
        {
            _icons[kind] = IconFactory.Create(kind);
        }

        _sampler = new Sampler(
            _source,
            _engine,
            () => _settings,
            SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        _sampler.OverClosed += OnOverClosed;
        _sampler.TurnedDown += ShowTurnedDown;

        _panel.Icon = _icons[IconKind.Watching];
        _panel.CeilingChanged += db => Mutate(s => s.CeilingDb = db);
        _panel.AlertsChanged += on =>
        {
            Mutate(s => s.AlertsEnabled = on);
            _alertsItem.Checked = on;
        };
        _panel.DeviceChosen += id => Mutate(s => s.WatchedDeviceId = id);
        _panel.UseHoldAsCeiling += UseLastPeakAsCeiling;
        _panel.PlayTestTone += () => TestTone.Play(_settings.WatchedDeviceId);

        var open = new ToolStripMenuItem("Open panel") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        open.Click += (_, _) => ShowPanel();
        _protectionItem.Checked = _settings.ProtectionEnabled;
        _protectionItem.Click += (_, _) =>
        {
            var on = !_settings.ProtectionEnabled;
            Mutate(s => s.ProtectionEnabled = on);
            _protectionItem.Checked = on;
        };
        _alertsItem.Checked = _settings.AlertsEnabled;
        _alertsItem.Click += (_, _) =>
        {
            var on = !_settings.AlertsEnabled;
            Mutate(s => s.AlertsEnabled = on);
            _alertsItem.Checked = on;
            _panel.SetAlerts(on);
        };
        _popupsItem.Checked = _settings.ShowNotifications;
        _popupsItem.Click += (_, _) =>
        {
            var on = !_settings.ShowNotifications;
            Mutate(s => s.ShowNotifications = on);
            _popupsItem.Checked = on;
        };
        _startupItem.Checked = StartupRegistration.IsEnabled();
        _startupItem.Click += (_, _) => ToggleStartup();
        var test = new ToolStripMenuItem("Send a test alert");
        test.Click += (_, _) => SendTestAlert();
        _repairItem.Click += (_, _) => RunInstaller("Repair");
        _removeItem.Click += (_, _) => RunInstaller("Uninstall");
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            open, _protectionItem, new ToolStripSeparator(),
            _alertsItem, _popupsItem, _startupItem, test, new ToolStripSeparator(),
            _repairItem, _removeItem, new ToolStripSeparator(),
            exit,
        });
        menu.Opening += (_, _) =>
        {
            // The sampler checks for the installer every 2 s, so opening the menu does no disk work.
            var installed = _sampler.InstallerExists;
            _repairItem.Enabled = installed;
            _removeItem.Enabled = installed;
        };

        _tray.ContextMenuStrip = menu;
        _tray.Icon = _icons[IconKind.Watching];
        _tray.Text = "Sound Protection";
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                TogglePanel();
            }
        };
        _tray.Visible = true;

        _timer.Tick += OnTick;
        _timer.Start();
        _sampler.Start();
        Logging.Info("Started");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CleanUp();
        }

        base.Dispose(disposing);
    }

    private void Mutate(Action<Settings> change)
    {
        var before = _settings;
        var copy = before.Clone();
        change(copy);
        _settings = copy;
        if (copy.CeilingDb != before.CeilingDb || copy.ProtectionEnabled != before.ProtectionEnabled)
        {
            _sampler.RequestConfigWrite(); // after the new settings are in place; the write itself happens off this thread
        }

        try
        {
            _store.Save(copy);
        }
        catch (Exception ex)
        {
            Logging.ErrorOnce("save", "Could not save the settings", ex);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var state = _sampler.Latest;
        var protection = _sampler.Protection;
        var visible = _panel.Visible;
        _timer.Interval = visible ? 33 : 250; // redraw fast only while the panel is open
        if (visible)
        {
            _panel.ShowState(state, protection);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _trayRefreshedAt < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _trayRefreshedAt = now;
        var kind = TrayText.Icon(state, protection?.State, now, _lastAlertClosed);
        if (_shownIcon != kind)
        {
            _tray.Icon = _icons[kind];
            _shownIcon = kind;
        }

        var tip = TrayText.Tooltip(state, protection?.State);
        if (_tray.Text != tip)
        {
            _tray.Text = tip;
        }
    }

    /// <summary>
    /// A level at the ear went over the ceiling. That alert belongs to the older display: while the plug-in is actually protecting
    /// the watched output the alerts are about what it turned down, and a level after the volume slider means nothing against the
    /// cap. That is so only when the output has the plug-in, the status is Protected or Idle, the plug-in has been seen running
    /// within the last 3 s, and it has said it is switched on (<see cref="ProtectionState.DropsOverEvents"/>). In every other case
    /// nothing is turning the sound down, so the event is raised: with no plug-in on the output, with the plug-in dead, switched
    /// off (by the switch, or running but not yet told to protect), in safe mode or not installed.
    /// The state is read as the event closes, half a second after the last loud reading, when the sound may well be over.
    /// </summary>
    private void OnOverClosed(OverEvent e)
    {
        if (_sampler.Protection?.State.DropsOverEvents == true)
        {
            return;
        }

        ShowOver(e);
    }

    private void ShowOver(OverEvent e)
    {
        var now = DateTimeOffset.UtcNow;
        _lastAlertClosed = now;
        _panel.AddEvent(e);
        Notify(Messages.Over(e), e.LoudestApp, now);
    }

    private void ShowTurnedDown(TurnDownEvent e)
    {
        var now = DateTimeOffset.UtcNow;
        _lastAlertClosed = now;
        _panel.AddEvent(e);
        Notify(Messages.TurnedDown(e), e.App, now);
    }

    // The balloon respects the alerts switch, the pop-up switch and the 10 s limit per app.
    private void Notify(Message message, string? app, DateTimeOffset now)
    {
        var settings = _settings;
        if (settings.AlertsEnabled && settings.ShowNotifications && _throttle.ShouldNotify(app ?? "Sound", now))
        {
            _tray.ShowBalloonTip(8000, message.Title, message.Body, ToolTipIcon.Warning);
        }
    }

    /// <summary>Lets you see what an alert looks like, and check that Windows shows pop-ups from this app.</summary>
    private void SendTestAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = _settings;
        if (_sampler.Protection?.State.DropsOverEvents == true)
        {
            ShowTurnedDown(new TurnDownEvent(
                now, TimeSpan.FromSeconds(1.8), 6.2, ProtectionConfigFile.EffectiveCapDb(settings.CeilingDb), "Test sound"));
        }
        else
        {
            ShowOver(new OverEvent(now, TimeSpan.FromSeconds(1.8), 9.6, settings.CeilingDb, "Test sound"));
        }
    }

    private void UseLastPeakAsCeiling()
    {
        var hold = _sampler.Latest.HoldDb;
        if (hold <= -100)
        {
            return; // nothing has played in the last 10 seconds, so there is no peak to use
        }

        // Held to what the slider and the spinner can show, so that the panel and the setting always agree.
        var ceiling = Math.Clamp(Math.Round(hold * 2) / 2, CeilingSlider.MinDb, CeilingSlider.MaxDb);
        Mutate(s => s.CeilingDb = ceiling);
        _panel.SetCeiling(ceiling);
    }

    private void TogglePanel()
    {
        if (_panel.Visible)
        {
            _panel.Hide();
        }
        else
        {
            ShowPanel();
        }
    }

    private void ShowPanel()
    {
        var settings = _settings;
        _panel.SetOutputs(_source.ListOutputs(), settings.WatchedDeviceId);
        _panel.SetCeiling(settings.CeilingDb);
        _panel.SetAlerts(settings.AlertsEnabled);
        if (!_panel.Visible)
        {
            _panel.Show();
        }

        if (_panel.WindowState == FormWindowState.Minimized)
        {
            _panel.WindowState = FormWindowState.Normal;
        }

        _panel.Activate();
    }

    private void ToggleStartup()
    {
        try
        {
            var on = !StartupRegistration.IsEnabled();
            StartupRegistration.Set(on, Environment.ProcessPath ?? Application.ExecutablePath);
            _startupItem.Checked = on;
        }
        catch (Exception ex)
        {
            Logging.Error("Could not change the Start with Windows entry", ex);
        }
    }

    // Starting it can wait on the Windows administrator prompt, so it is done off the UI thread.
    private static void RunInstaller(string action) => Task.Run(() => StartInstaller(action));

    /// <summary>Runs the installer to repair or remove the plug-in. It changes the machine's audio settings, so Windows asks for permission first.</summary>
    private static void StartInstaller(string action)
    {
        var path = ProtectionPaths.InstallerPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\" -Action {action}",
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacDeclined)
        {
            // The person said no at the administrator prompt. Nothing was started.
        }
        catch (Exception ex)
        {
            Logging.Error("Could not start the installer for " + action, ex);
        }
    }

    private void ExitApp()
    {
        CleanUp();
        ExitThread();
    }

    private void CleanUp()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _timer.Stop();
        _tray.Visible = false;
        _sampler.Dispose();
        _source.Dispose();
        _tray.Dispose();
        _panel.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        Logging.Info("Stopped");
    }
}
