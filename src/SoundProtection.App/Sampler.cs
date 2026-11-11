using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// What the protection thread knows, at one moment: the engine's state and the warnings about the outputs.
/// Immutable, so the UI thread can read it while the protection thread builds the next one.
/// </summary>
internal sealed record ProtectionView(ProtectionState State, IReadOnlyList<string> Warnings);

/// <summary>
/// Runs two background threads. The sampler reads the watched output every few milliseconds, feeds the engine, and keeps
/// the latest <see cref="PanelState"/> (the ear-level display); over-ceiling events are posted to the UI context.
/// The protection thread, ten times a second, reads the plug-in's telemetry, and every 2 s checks the registry; it keeps the
/// latest <see cref="ProtectionView"/>, and turn-down events are posted to the UI context. It also keeps config.bin equal to
/// the settings. Nothing here blocks the UI thread.
/// </summary>
internal sealed class Sampler : IDisposable
{
    private const int ProtectionTickMs = 100;
    private static readonly TimeSpan SlowTick = TimeSpan.FromSeconds(2);          // registry facts, and the check that config.bin is right
    private static readonly TimeSpan ReadingMaxAge = TimeSpan.FromSeconds(1);

    private readonly IAudioSource _source;
    private readonly MonitorEngine _engine;
    private readonly Func<Settings> _settings;
    private readonly ProtectionEngine _protectionEngine = new();
    private Thread? _thread;
    private Thread? _protectionThread;
    private volatile bool _stop;
    private volatile PanelState _latest = PanelState.Empty(DateTimeOffset.UtcNow);
    private volatile Reading? _reading;
    private volatile ProtectionView? _protection;
    private volatile bool _installerExists;
    private int _configDirty;
    private int _configSyncRunning;

    public Sampler(IAudioSource source, MonitorEngine engine, Func<Settings> settings, SynchronizationContext ui)
    {
        _source = source;
        _engine = engine;
        _settings = settings;
        _engine.OverClosed += e => ui.Post(_ => OverClosed?.Invoke(e), null);
        _protectionEngine.TurnedDown += e => ui.Post(_ => TurnedDown?.Invoke(e), null);
    }

    public PanelState Latest => _latest;

    /// <summary>The plug-in's state, or null until the first registry check has worked.</summary>
    public ProtectionView? Protection => _protection;

    /// <summary>True when the installer is on disk, as of the last 2 s check, so that Repair and Remove can be offered.</summary>
    public bool InstallerExists => _installerExists;

    /// <summary>Raised on the UI context.</summary>
    public event Action<OverEvent>? OverClosed;

    /// <summary>Raised on the UI context.</summary>
    public event Action<TurnDownEvent>? TurnedDown;

    /// <summary>
    /// Asks for config.bin to be brought up to date with the settings. Returns at once; the protection thread does the write.
    /// Call it after the new settings are in place. Several requests close together become one write.
    /// </summary>
    public void RequestConfigWrite() => Volatile.Write(ref _configDirty, 1);

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "SoundProtection.Sampler",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        _protectionThread = new Thread(ProtectionLoop)
        {
            IsBackground = true,
            Name = "SoundProtection.Protection",
        };
        _protectionThread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(500);
        _protectionThread?.Join(500);
    }

    private void Loop()
    {
        var lastActive = DateTimeOffset.MinValue;
        while (!_stop)
        {
            var delay = Pacing.ActiveMs;
            try
            {
                var settings = _settings();
                var snapshot = _source.Read(settings.WatchedDeviceId);
                var now = DateTimeOffset.UtcNow;
                _reading = new Reading(snapshot, now);
                _latest = _engine.Process(snapshot, settings, now);

                var active = snapshot != null && Pacing.IsActive(snapshot);
                if (active)
                {
                    lastActive = now;
                }

                delay = Pacing.NextDelayMs(snapshot != null, active, now - lastActive);
            }
            catch (Exception ex)
            {
                Logging.ErrorOnce("sampler", "A sampling step failed", ex);
            }

            HighResTimer.Sleep(delay);
        }
    }

    private void ProtectionLoop()
    {
        var writer = new ProtectionConfigWriter(ProtectionPaths.ConfigPath);
        var health = new HealthTracker();
        RegistryFacts? facts = null;
        var nextSlow = DateTimeOffset.MinValue;

        while (!_stop)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                if (now >= nextSlow)
                {
                    nextSlow = now + SlowTick;
                    facts = ReadRegistry() ?? facts; // a failed read keeps what was known
                    _installerExists = File.Exists(ProtectionPaths.InstallerPath);

                    // This is also how a config that is missing or wrong gets put right: at start-up, after the installer has
                    // made the folder, or after something else has changed the file. When nothing differs, nothing is written.
                    RequestConfigWrite();
                }

                StartConfigSyncIfNeeded(writer);

                if (facts is not null)
                {
                    _protection = Evaluate(facts, _settings(), health, writer.LastAttemptFailed, now);
                }
            }
            catch (Exception ex)
            {
                Logging.ErrorOnce("protection", "A protection step failed", ex);
            }

            Thread.Sleep(ProtectionTickMs);
        }
    }

    // The tracker is fed on every tick, whatever the outcome, so that its clocks (sound playing, plug-in out of step) keep time.
    private ProtectionView Evaluate(RegistryFacts facts, Settings settings, HealthTracker tracker, bool settingsNotSaved, DateTimeOffset now)
    {
        var reading = _reading;
        var snapshot = reading is not null && now - reading.At < ReadingMaxAge ? reading.Snapshot : null;
        var playing = snapshot is not null && Pacing.IsActive(snapshot);
        // The output that was read: the chosen one, or the Windows default when none is chosen (or the chosen one is gone).
        var watched = snapshot is null ? null : new WatchedOutput(snapshot.DeviceId, DeviceLabels.FriendlyName(snapshot.DeviceName));
        var cap = ProtectionConfigFile.EffectiveCapDb(settings.CeilingDb);

        var telemetry = ApoTelemetry.TryRead(ProtectionPaths.TelemetryPath);
        var health = tracker.Evaluate(facts, telemetry, now, settings.ProtectionEnabled, cap, playing, watched, settingsNotSaved);
        var state = _protectionEngine.Process(telemetry, health, snapshot?.Apps ?? Array.Empty<AppLevel>(), cap, now);
        return new ProtectionView(state, health.Warnings);
    }

    private static RegistryFacts? ReadRegistry()
    {
        try
        {
            return ProtectionRegistry.Read();
        }
        catch (Exception ex)
        {
            Logging.ErrorOnce("registry", "Could not read the registry for protection", ex);
            return null;
        }
    }

    // A write can take half a second when the plug-in has the file open, so it runs on the thread pool, one at a time.
    // A request that comes in while a write is running stays pending and is picked up by the next tick. The settings are read
    // after the request is taken, so a change that raced with this tick is either written now or requests another write.
    private void StartConfigSyncIfNeeded(ProtectionConfigWriter writer)
    {
        if (Volatile.Read(ref _configDirty) == 0 || Interlocked.CompareExchange(ref _configSyncRunning, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _configDirty, 0);
        var settings = _settings();
        var enabled = settings.ProtectionEnabled;
        var cap = settings.CeilingDb;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                writer.Sync(enabled, cap);
            }
            catch (Exception ex)
            {
                Logging.ErrorOnce("config", "Could not write the protection config", ex);
            }
            finally
            {
                Volatile.Write(ref _configSyncRunning, 0);
            }
        });
    }

    private sealed record Reading(AudioSnapshot? Snapshot, DateTimeOffset At);
}
