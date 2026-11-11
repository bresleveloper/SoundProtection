using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SoundProtection.Core;

namespace SoundProtection.App;

/// <summary>
/// Reads the Windows output meters, volume and app sessions through Core Audio. It only reads:
/// nothing here sets a volume, a mute flag or a default device.
/// </summary>
internal sealed class WasapiAudioSource : IAudioSource
{
    private static readonly TimeSpan ResolveEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VolumeEvery = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ListSessionsEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ListSessionsIdleEvery = TimeSpan.FromSeconds(1); // while no app is playing
    private static readonly TimeSpan AppMetersEvery = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan NameCacheFor = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, (string Name, DateTime At)> _processNames = new();
    private readonly List<TrackedSession> _sessions = new();

    // Created on first use by the thread that calls Read, so Core Audio objects are never shared across threads.
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string _deviceName = "";
    private bool _fellBackToDefault;
    private string? _resolvedFor = "\0"; // never equal to a real request, so the first Read resolves
    private DateTime _resolvedAt = DateTime.MinValue;
    private DateTime _listedAt = DateTime.MinValue;
    private DateTime _metersAt = DateTime.MinValue;
    private IReadOnlyList<AppLevel> _apps = Array.Empty<AppLevel>();

    // Volume and mute change rarely, so they are read every 50 ms instead of on every poll.
    private DateTime _volumeAt = DateTime.MinValue;
    private bool _muted;
    private double _volumeDb;
    private double _volumePercent;

    private sealed record TrackedSession(AudioSessionControl Control, int ProcessId, string Name);

    public IReadOnlyList<OutputDevice> ListOutputs()
    {
        var result = new List<OutputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator(); // its own enumerator: this runs on the UI thread
            string? defaultId = null;
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                defaultId = defaultDevice.ID;
            }
            catch (Exception)
            {
                // No default output right now.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new OutputDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                }
            }
        }
        catch (Exception ex)
        {
            Logging.ErrorOnce("list", "Could not list the output devices", ex);
        }

        return result;
    }

    public AudioSnapshot? Read(string? deviceId)
    {
        try
        {
            if (!Resolve(deviceId))
            {
                return null;
            }

            var device = _device!;
            var peak = device.AudioMeterInformation.MasterPeakValue; // the only value read on every poll

            var now = DateTime.UtcNow;
            if (now - _volumeAt >= VolumeEvery)
            {
                var volume = device.AudioEndpointVolume;
                _muted = volume.Mute;
                _volumeDb = volume.MasterVolumeLevel;
                _volumePercent = Math.Round(volume.MasterVolumeLevelScalar * 100.0, 1);
                _volumeAt = now;
            }

            return new AudioSnapshot(
                device.ID,
                _deviceName,
                _muted,
                _volumeDb,
                _volumePercent,
                Db.FromLinear(peak),
                ReadApps(device));
        }
        catch (Exception ex)
        {
            Logging.ErrorOnce("read", "Reading the output failed; the device will be looked up again", ex);
            DropDevice();
            return null;
        }
    }

    public void Dispose()
    {
        DropDevice();
        _enumerator?.Dispose();
    }

    private bool Resolve(string? requestedId)
    {
        var now = DateTime.UtcNow;
        var sameRequest = string.Equals(_resolvedFor, requestedId, StringComparison.Ordinal);
        if (_device != null && sameRequest && now - _resolvedAt < ResolveEvery)
        {
            return true;
        }

        _resolvedFor = requestedId;
        _resolvedAt = now;

        _enumerator ??= new MMDeviceEnumerator();
        var found = Open(requestedId);
        var fellBack = false;
        if (found is null && requestedId is not null)
        {
            found = Open(null); // the chosen output is gone: watch the Windows default instead, and say so
            fellBack = found is not null;
        }

        if (found is null)
        {
            DropDevice();
            return false;
        }

        if (_device != null && _device.ID == found.ID && _fellBackToDefault == fellBack)
        {
            found.Dispose(); // same device as before: keep the object we already have
            return true;
        }

        DropDevice();
        _device = found;
        _fellBackToDefault = fellBack;
        _deviceName = DeviceLabels.Describe(found.FriendlyName, fellBack);
        return true;
    }

    /// <summary>The given output (or the Windows default for null) if it is connected and active; otherwise null.</summary>
    private MMDevice? Open(string? id)
    {
        MMDevice? device = null;
        try
        {
            device = id is null
                ? _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
                : _enumerator!.GetDevice(id);
            if (device.State == DeviceState.Active)
            {
                return device;
            }
        }
        catch (Exception)
        {
            // Not there (unplugged, Bluetooth off, disabled).
        }

        device?.Dispose();
        return null;
    }

    private void DropDevice()
    {
        DisposeSessions();
        _device?.Dispose();
        _device = null;
        _apps = Array.Empty<AppLevel>();
        _listedAt = DateTime.MinValue;
        _metersAt = DateTime.MinValue;
        _volumeAt = DateTime.MinValue;
    }

    private void DisposeSessions()
    {
        foreach (var session in _sessions)
        {
            try
            {
                session.Control.Dispose();
            }
            catch (Exception)
            {
                // Already gone with its device.
            }
        }

        _sessions.Clear();
    }

    private IReadOnlyList<AppLevel> ReadApps(MMDevice device)
    {
        var now = DateTime.UtcNow;
        var listEvery = _sessions.Count == 0 ? ListSessionsIdleEvery : ListSessionsEvery; // listing is the costliest read
        if (now - _listedAt >= listEvery)
        {
            ListSessions(device);
            _listedAt = now;
            _metersAt = DateTime.MinValue; // read the meters straight after a new listing
        }

        if (_sessions.Count == 0)
        {
            _apps = Array.Empty<AppLevel>();
            return _apps;
        }

        if (now - _metersAt >= AppMetersEvery)
        {
            _metersAt = now;
            var levels = new List<AppLevel>(_sessions.Count);
            foreach (var session in _sessions)
            {
                try
                {
                    var peak = session.Control.AudioMeterInformation.MasterPeakValue;
                    levels.Add(new AppLevel(session.Name, session.ProcessId, Db.FromLinear(peak)));
                }
                catch (Exception)
                {
                    // The session ended; the next listing drops it.
                }
            }

            _apps = levels;
        }

        return _apps;
    }

    /// <summary>Lists the apps playing right now and keeps their session objects, so their meters can be read often and cheaply.</summary>
    private void ListSessions(MMDevice device)
    {
        DisposeSessions();
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                var keep = false;
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateActive)
                    {
                        var pid = (int)session.GetProcessID;
                        _sessions.Add(new TrackedSession(session, pid, ProcessName(pid, session.IsSystemSoundsSession)));
                        keep = true;
                    }
                }
                finally
                {
                    if (!keep)
                    {
                        session.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.ErrorOnce("sessions", "Could not list the app sessions", ex);
        }
    }

    private string ProcessName(int pid, bool isSystemSounds)
    {
        if (isSystemSounds)
        {
            return AppNames.Friendly(null, isSystemSounds: true);
        }

        if (_processNames.TryGetValue(pid, out var cached) && DateTime.UtcNow - cached.At < NameCacheFor)
        {
            return cached.Name;
        }

        string? raw = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            raw = process.ProcessName;
        }
        catch (Exception)
        {
            // The process ended, or Windows will not tell us its name.
        }

        var name = AppNames.Friendly(raw, isSystemSounds: false);
        _processNames[pid] = (name, DateTime.UtcNow);
        return name;
    }
}
