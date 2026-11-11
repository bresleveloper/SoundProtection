# Sound Protection Tray + Panel (v1: monitor) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows 11 tray app. Its panel shows which output the sound goes to and the live output level in dB (below the output's maximum). It alerts when a ceiling X is exceeded and by how much, and has an on/off toggle. v1 works on its own and only reads. It is also the front end for the Voicemeeter limiter, which a later plan adds.

**Architecture:** C# on .NET 10. `SoundProtection.Core` holds all logic with no Windows dependency: dB maths, peak hold, over-ceiling detection, alert throttling, messages, settings, and an engine that turns audio readings into the panel's state. `SoundProtection.App` is a WinForms tray app: a NAudio reader of the Windows meters, a high-resolution sampling thread, the tray icon and the panel. Nothing in the App ever sets a volume, a mute flag or a device.

**Tech Stack:** .NET SDK 10.0.403 (installed), WinForms, NAudio (latest stable, MIT), xUnit.

**Spec:** the Requirements below (Ariel's messages of 2026-11-10) plus `PIPELINE-LIMITER-PLAN.md` sections 2, 4.3-4.5 and 4.7. Measurements: `RESEARCH-AND-PLAN.md` section 14.

## Requirements

1. A tray icon (Windows 11 hidden-icons area) and a panel. Ariel: "make something that can have a tray and panel showing what output the sound, and what are the actual output decibels".
2. The panel shows the output device the sound goes to with Windows' volume for it, the actual output level now in dB, the ceiling X, and which app is loudest.
3. An alert when something goes over X, saying by how much (original ask). An on/off toggle (original ask).
4. "dB" means dB below the loudest the output can play (0 = max): digital peak dBFS + Windows volume dB. It is not dB SPL, and the panel says so.
5. v1 only reads Windows' meters. With no limiter it reports and does not protect, and the panel says so in words.
6. Later, in a separate plan that needs Ariel's explicit OK to install Voicemeeter: drive Voicemeeter's bus limiter from the ceiling, and read the physical output behind it.

## Global Constraints

- SDK 10.0.403. Target frameworks: `net10.0` (Core, Tests), `net10.0-windows` (App). Nullable enabled.
- No admin rights and no driver. The only registry write is the opt-in "Start with Windows" value `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SoundProtection`.
- Defaults: ceiling -28.0 dB, range -80.0 to 0.0; over tolerance 0.1 dB; merge gap 500 ms; max episode 5 s; one notification per app per 10 s; peak-hold window 10 s; meter decay 24 dB/s; `Db.Floor` = -120.0; "silent" at or below -100 dB.
- Polling: endpoint peak every 5 ms (high-resolution waitable timer), app sessions every 250 ms, default-device re-check every 1 s, panel redraw 30 Hz while visible.
- All numbers format with `CultureInfo.InvariantCulture` and an ASCII hyphen-minus.
- Settings file `%APPDATA%\SoundProtection\settings.json`. Log file `%APPDATA%\SoundProtection\log.txt`.
- One instance only (named mutex `Local\SoundProtection.Tray`).
- Layout: `SoundProtection.sln` at the project root; `src\SoundProtection.Core`, `src\SoundProtection.App`, `tests\SoundProtection.Tests`; published to `dist\`.
- This folder is not a git repository, so there are no commit steps.

## Review Focus

1. Muted output, or volume at 0 %: no false "over" (Task 3 engine tests).
2. No output device, or the device vanishes (Bluetooth off, unplugged): the "no output device" state, and any open episode closes silently (Task 3).
3. Alerts switched off while an episode is open: no late notification (Task 3).
4. Missing, corrupt or out-of-range settings file: defaults or clamped values, and the app still starts (Task 3).
5. Clock steps backwards (resume, time sync) or two readings share a timestamp: no exception and no negative duration (Task 2).

## Task 1: Solution scaffold and level maths

**Files:**
- Create: `SoundProtection.sln`, `src/SoundProtection.Core/SoundProtection.Core.csproj`, `src/SoundProtection.Core/Db.cs`, `EarLevel.cs`, `PeakHold.cs`, `MeterBallistics.cs`
- Create: `tests/SoundProtection.Tests/SoundProtection.Tests.csproj`, `tests/SoundProtection.Tests/LevelMathTests.cs`

**Interfaces:**
- Produces (namespace `SoundProtection.Core`):
  - `static class Db { const double Floor = -120.0; static double FromLinear(double linear); static double ToLinear(double db); static string Format(double db); }`
  - `static class EarLevel { static double Compute(double peakDbfs, double volumeDb, bool muted); }`
  - `sealed class PeakHold { PeakHold(TimeSpan window); void Add(DateTimeOffset time, double db); double Max(DateTimeOffset now); }`
  - `sealed class MeterBallistics { MeterBallistics(double decayDbPerSecond = 24.0); double Level { get; } double Update(double db, double elapsedSeconds); }`

- [ ] **Step 1: Scaffold.** `dotnet new sln`, `classlib` (net10.0) for Core, `xunit` (net10.0) for Tests, tests reference Core, remove template classes. Run `dotnet build`. Expected: succeeds.
- [ ] **Step 2: Write the failing tests** in `LevelMathTests.cs`:
  - `Db.FromLinear(1.0) == 0.0`; `FromLinear(0.5)` is -6.0206 within 0.001; `FromLinear(0.0)`, `FromLinear(double.NaN)` and `FromLinear(1e-9)` all equal `Db.Floor`; `ToLinear(-20.0)` is 0.1 within 1e-9.
  - `Db.Format(-31.44) == "-31.4 dB"`, `Format(0.0) == "0.0 dB"`, `Format(-100.0) == "silent"`, `Format(Db.Floor) == "silent"`; with `CurrentCulture` set to `de-DE`, `Format(-31.44)` still equals `"-31.4 dB"`.
  - `EarLevel.Compute(-7.7, -23.7, false)` is -31.4 within 1e-9; muted gives `Db.Floor`; `Compute(Db.Floor, -23.7, false)` is `Db.Floor`; `Compute(-10, 0, false)` is -10.
  - `PeakHold(10 s)`: add (0 s, -30), (1 s, -20), (2 s, -40). `Max(3 s)` is -20. `Max(11.5 s)` is -40. `Max(13 s)` is `Db.Floor`.
  - `MeterBallistics(24)`: `Update(-10, 0.1)` returns -10 (instant attack). Then `Update(-60, 0.5)` returns -22. Then `Update(-60, 10)` returns -60 (never below the target).
- [ ] **Step 3: Run** `dotnet test`. Expected: compile failure (types missing).
- [ ] **Step 4: Implement** the four types. `PeakHold` keeps a monotonic deque (values strictly decreasing from front to back) so `Max` is O(1) amortised. `FromLinear` clamps to `Floor`.
- [ ] **Step 5: Run** `dotnet test`. Expected: all pass.

## Task 2: Over-ceiling detection, alert throttle, messages

**Files:**
- Create: `src/SoundProtection.Core/OverDetector.cs` (holds `OverEvent`), `NotificationThrottle.cs`, `Messages.cs`
- Test: `tests/SoundProtection.Tests/AlertTests.cs`

**Interfaces:**
- Consumes: `Db` from Task 1.
- Produces:
  - `sealed record OverEvent(DateTimeOffset Start, TimeSpan Duration, double MaxOverDb, double CeilingDb, string? LoudestApp) { double PeakEarDb => CeilingDb + MaxOverDb; }`
  - `sealed class OverDetector { OverDetector(TimeSpan? mergeGap = null, TimeSpan? maxEpisode = null, double toleranceDb = 0.1); OverEvent? Feed(DateTimeOffset time, double earDb, double ceilingDb, string? loudestApp); bool IsOver { get; } double CurrentOverDb { get; } void Reset(); }`
  - `sealed class NotificationThrottle { NotificationThrottle(TimeSpan interval); bool ShouldNotify(string key, DateTimeOffset now); }`
  - `sealed record Message(string Title, string Body); static class Messages { static Message Over(OverEvent e); }`

Rules the tests pin: a sample is "over" when `earDb > ceilingDb + toleranceDb`. An episode opens on the first over sample and keeps `MaxOverDb` and the app that was loudest at that maximum. It closes when `time - lastOverTime >= mergeGap` on a not-over sample, returning the event with `Duration = lastOverTime - Start`. An over sample with `time - Start >= maxEpisode` closes the episode (`Duration = time - Start`) and opens a new one at that time. A `time` earlier than the last seen time is treated as equal to it.

- [ ] **Step 1: Write the failing tests** in `AlertTests.cs`, with `t(ms)` = 2026-11-10 12:00:00 UTC plus `ms`:
  - Quiet input: `Feed(t(0), -40, -28, "Zoom")` is null and `IsOver` is false.
  - One burst: `Feed(t(0), -20, -28, "Zoom")` is null, `IsOver` is true, `CurrentOverDb` is 8 within 1e-9. Then `Feed(t(100), -25, -28, "Zoom")` is null; `Feed(t(200), -35, -28, null)` is null (inside the gap); `Feed(t(700), -35, -28, null)` returns an event: `Start == t(0)`, `Duration == 100 ms`, `MaxOverDb` 8, `CeilingDb` -28, `LoudestApp` "Zoom". `IsOver` is false afterwards.
  - Merge: over at t(0), under at t(300), over at t(400), under at t(1000) gives exactly one event, `Duration == 400 ms`.
  - Separate: over at t(0), under at t(600) closes event 1; over at t(1000), under at t(1600) closes event 2.
  - Tolerance: `Feed(t(0), -27.95, -28, "Zoom")` leaves `IsOver` false.
  - Loudest app: samples (t(0), -20, "Chrome"), (t(10), -15, "Zoom"), (t(20), -22, "Chrome"), then silence until the event closes: `LoudestApp == "Zoom"`, `MaxOverDb` 13.
  - Long episode: over every 100 ms from t(0) to t(12000) at -20 against -28 yields events starting at t(0) and t(5000), each with `Duration == 5 s`, and `IsOver` still true at the end.
  - Clock steps back: `Feed(t(1000), -20, -28, "Zoom")` then `Feed(t(500), -20, -28, "Zoom")` throws nothing, and any event later returned has `Duration >= TimeSpan.Zero`.
  - `Reset()` while over: `IsOver` is false and the next quiet `Feed` returns null.
  - Throttle (10 s): first `ShouldNotify("Zoom", t(0))` true; `t(5000)` false; `t(10000)` true; `"Chrome"` at `t(5000)` true.
  - Messages: `Over(new OverEvent(t(0), TimeSpan.FromSeconds(1.8), 9.6, -28.0, "Zoom"))` has Title `"Over your ceiling by 9.6 dB"` and Body `"Zoom peaked at -18.4 dB (ceiling -28.0 dB) for 1.8 s. Reported only: no limiter is connected."`. With `LoudestApp` null the Body starts `"Sound peaked"`. With a duration under 0.1 s the Body contains `"for under 0.1 s"`.
- [ ] **Step 2: Run** `dotnet test`. Expected: compile failure.
- [ ] **Step 3: Implement** the types as specified above.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.

## Task 3: Settings and the monitor engine

**Files:**
- Create: `src/SoundProtection.Core/Settings.cs` (holds `SettingsStore`), `AudioModel.cs`, `PanelState.cs`, `MonitorEngine.cs`
- Test: `tests/SoundProtection.Tests/SettingsTests.cs`, `MonitorEngineTests.cs`

**Interfaces:**
- Consumes: `Db`, `EarLevel`, `PeakHold`, `MeterBallistics`, `OverDetector`, `OverEvent` from Tasks 1-2.
- Produces:
  - `sealed class Settings { bool AlertsEnabled = true; double CeilingDb = -28.0; string? WatchedDeviceId = null; bool ShowNotifications = true; }`
  - `sealed class SettingsStore { SettingsStore(string path); Settings Load(); void Save(Settings settings); }`
  - `sealed record AppLevel(string Name, int ProcessId, double PeakDbfs); sealed record OutputDevice(string Id, string Name, bool IsDefault);`
  - `sealed record AudioSnapshot(string DeviceId, string DeviceName, bool Muted, double VolumeDb, double VolumePercent, double PeakDbfs, IReadOnlyList<AppLevel> Apps);`
  - `interface IAudioSource : IDisposable { IReadOnlyList<OutputDevice> ListOutputs(); AudioSnapshot? Read(string? deviceId); }` (a null `deviceId` means the Windows default output)
  - `sealed record AppRow(string Name, double PeakDbfs, double EarDb);`
  - `sealed record PanelState(DateTimeOffset Time, bool HasDevice, string DeviceName, double VolumePercent, double VolumeDb, bool Muted, double EarDb, double HoldDb, double DisplayDb, double CeilingDb, bool Over, double OverByDb, bool AlertsEnabled, IReadOnlyList<AppRow> Apps);`
  - `sealed class MonitorEngine { event Action<OverEvent>? OverClosed; PanelState Process(AudioSnapshot? snapshot, Settings settings, DateTimeOffset now); }`

Engine rules: `EarDb = EarLevel.Compute(...)`; `HoldDb` = `PeakHold(10 s)` of `EarDb`; `DisplayDb` = `MeterBallistics` of `EarDb` (elapsed time since the previous call, 0 on the first). Apps with `PeakDbfs` at or below -90 are left out; the rest are sorted by `EarDb` (peak + volume) descending, and the first is the loudest app. With `AlertsEnabled` true the detector is fed every call; with it false the detector is `Reset()` and `Over` is false. A null snapshot gives `HasDevice` false, `DeviceName` "No output device", all levels `Db.Floor`, and resets the detector. `SettingsStore.Load` returns defaults for a missing file; for corrupt JSON it moves the file to `<path>.bad` and returns defaults; it clamps `CeilingDb` to [-80, 0]. `Save` creates the directory and writes a temp file, then replaces the target.

- [ ] **Step 1: Write the failing tests.**
  - `SettingsTests`: missing file gives the defaults above; save then load round-trips every field; `{ not json` is replaced by defaults and `<path>.bad` exists; a file with `"ceilingDb": 12` loads as 0.0 and `-500` as -80.0; after `Save` no `.tmp` file remains and a missing parent directory is created.
  - `MonitorEngineTests` (snapshot helper: peak, volumeDb, muted, apps):
    - Basic: peak -7.7, volume -23.7 gives `EarDb` -31.4 within 1e-9, `Over` false, `DisplayDb` and `HoldDb` equal `EarDb`.
    - Over: peak -4, volume -10, ceiling -28 gives `Over` true and `OverByDb` 14. Then silence at `now`+100 ms and +700 ms makes `OverClosed` fire exactly once with `MaxOverDb` 14.
    - Muted with peak 0: `EarDb == Db.Floor`, `Over` false, no event. Volume 0 % (`VolumeDb` = `Db.Floor`) with peak 0: `EarDb == Db.Floor`.
    - Null snapshot: `HasDevice` false, `DeviceName == "No output device"`; a null snapshot after an over reading raises no `OverClosed`.
    - Alerts off in the middle of an episode: `Over` false and no `OverClosed` follows.
    - Apps: three apps sorted by `EarDb` descending; one at -95 dBFS is omitted; the event's `LoudestApp` is the loudest at the maximum.
    - Hold: a loud reading at t, quiet at t+9 s: `HoldDb` still the loud value; at t+11 s it is the quiet value.
- [ ] **Step 2: Run** `dotnet test`. Expected: compile failure.
- [ ] **Step 3: Implement** `Settings`/`SettingsStore` with `System.Text.Json` (camelCase) and `MonitorEngine` as specified.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.

## Task 4: Live Windows audio, read-only

**Files:**
- Create: `src/SoundProtection.App/SoundProtection.App.csproj` (WinExe, `net10.0-windows`, `UseWindowsForms`, NAudio, reference to Core), `WasapiAudioSource.cs`, `HighResTimer.cs`, `Sampler.cs`, `Logging.cs`, `Program.cs` (only `--probe` for now)

**Interfaces:**
- Consumes: `IAudioSource`, `AudioSnapshot`, `OutputDevice`, `AppLevel`, `MonitorEngine`, `Settings`, `PanelState`, `OverEvent`.
- Produces:
  - `sealed class WasapiAudioSource : IAudioSource`. Resolves the Windows default render device (console role) or the device with the given id; re-resolves every 1 s; reads endpoint peak (`MasterPeakValue`, converted with `Db.FromLinear`), `MasterVolumeLevel` (dB), the scalar volume x 100, and mute; reads app sessions every 250 ms and reuses the last list between; names apps by process name (`zoom` -> "Zoom", `chrome` -> "Chrome", `msedge` -> "Edge", the system-sounds session -> "System sounds", failures -> "Unknown"); `Read` returns null instead of throwing when the device is missing or a COM call fails.
  - `static class HighResTimer { static void Sleep(int milliseconds); }` using `CreateWaitableTimerExW` with `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (0x2) and access 0x1F0003, falling back to `Thread.Sleep`.
  - `sealed class Sampler : IDisposable { Sampler(IAudioSource source, MonitorEngine engine, Func<Settings> settings, SynchronizationContext ui); void Start(); PanelState Latest { get; } event Action<OverEvent>? OverClosed; }`. The thread loops every 5 ms, calls `source.Read(settings().WatchedDeviceId)` then `engine.Process`, stores `Latest`, and posts `OverClosed` to `ui`. It catches and logs exceptions (at most one log line per minute per message).
  - `static class Logging { static void Info(string line); static void Error(string line, Exception? ex = null); }` appending to the log file, never throwing.
  - `--probe <file> [seconds]`: writes one line every 100 ms for the duration (default 5 s): time, device name, volume %, volume dB, muted, peak dBFS, ear dB, apps as `name:peakDbfs`. Then exits.

- [ ] **Step 1: Create the App project**, add `NAudio`, reference Core, and implement the types above. The App must reference no API that sets volume, mute or device.
- [ ] **Step 2: Build.** `dotnet build`. Expected: succeeds with no warnings about the new files.
- [ ] **Step 3: Probe with nothing playing.** `dotnet run --project src/SoundProtection.App -- --probe <scratchpad>\probe-silent.txt 3`. Expected: ~30 lines; device name equals the Windows default output (Headphones (Realtek(R) Audio) at last check); volume dB matches 34 x log10(percent/100) within 0.3 dB; peak is -120 when silent.
- [ ] **Step 4: Probe with audio playing** (Ariel plays any video; I play nothing). Expected: peak above -60 dBFS, ear dB equals peak + volume dB within 0.1, and the playing app (for example "Chrome") appears in the apps column.

## Task 5: Tray icon and panel

**Files:**
- Create in `src/SoundProtection.App/`: `IconFactory.cs`, `MeterBar.cs`, `PanelForm.cs`, `TrayApp.cs`, `StartupRegistration.cs`; modify `Program.cs`

**Interfaces:**
- Consumes: `Sampler`, `MonitorEngine`, `SettingsStore`, `Settings`, `PanelState`, `OverEvent`, `Messages`, `NotificationThrottle`, `WasapiAudioSource`, `Db`.
- Produces:
  - `enum IconKind { Off, Watching, Over }`; `static class IconFactory { static Icon Create(IconKind kind); }` (draws a speaker glyph in grey, blue or red, and releases native handles).
  - `sealed class MeterBar : Control { void Set(double displayDb, double holdDb, double ceilingDb, bool over); }` (scale -60 to 0 dB, ceiling marker, peak-hold tick, double-buffered).
  - `sealed class PanelForm : Form { void ShowState(PanelState state); void AddEvent(OverEvent e, Message m); event Action<double>? CeilingChanged; event Action<bool>? AlertsChanged; event Action<string?>? DeviceChosen; event Action? UseHoldAsCeiling; }`
  - `sealed class TrayApp : ApplicationContext`; `StartupRegistration { static bool IsEnabled(); static void Set(bool on, string exePath); }`
  - `Program.Main`: single-instance mutex (a second launch exits); high-DPI per monitor; `--probe` from Task 4; `--demo-png <file>` renders the panel with a synthetic loud state and exits; a global exception handler that logs and keeps the tray alive.

Panel layout (sketch):

```
+-- Sound Protection --------------------------------- [ Alerts: ON ] --+
| Output:  [ Follow Windows default v ]   Headphones (Realtek(R) Audio)   |
| Windows volume: 20 %  (-23.7 dB)                                        |
|                                                                         |
|     -31.4 dB      at your ear. 0 = the loudest this output can play     |
| [##############..........|.......]  -60 ......... -28 ......... 0       |
| Last 10 s peak: -27.9 dB       Ceiling: [ -28.0 ]  [ Use that peak ]    |
| Limiter: not connected. Alerts only, nothing is held back.              |
| Apps:    Zoom  -34.2 dB     Chrome  -52.0 dB                            |
| Events:  14:02:31  Zoom  +9.6 dB over (1.8 s)                           |
+-------------------------------------------------------------------------+
```

Behaviour: tray left-click toggles the panel; closing the panel hides it; the menu has Open panel, Alerts on/off, Start with Windows (off by default), Exit. The icon is grey when alerts are off, red for 3 s after an over event, otherwise blue. The tooltip reads `Sound Protection: <ear dB> at ear, ceiling <x> dB (alerts only)`, trimmed to 127 characters. A balloon notification (title and body from `Messages.Over`) shows when `ShowNotifications` and alerts are on and `NotificationThrottle` allows it. Settings changes save at once. The panel redraws at 30 Hz only while visible. The panel never shows "protected" while no limiter is connected.

- [ ] **Step 1: Implement the types above** and wire `TrayApp`: `SettingsStore` -> `WasapiAudioSource` -> `MonitorEngine` -> `Sampler` -> panel and notifications.
- [ ] **Step 2: Build.** Expected: succeeds.
- [ ] **Step 3: Render the demo.** `dotnet run --project src/SoundProtection.App -- --demo-png <scratchpad>\panel.png`. Read the PNG. Expected: layout matches the sketch, text is not clipped, the ceiling marker and the red "over" state are visible.
- [ ] **Step 4: Launch for real**, wait 5 s, and check: the process is running, `log.txt` has no errors, a second launch exits at once and leaves one process.

## Task 6: Publish and live check

- [ ] **Step 1: Run the whole suite.** `dotnet test`. Expected: all pass.
- [ ] **Step 2: Publish.** `dotnet publish src/SoundProtection.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist`. Expected: `dist\SoundProtection.exe`.
- [ ] **Step 3: Probe the published exe** with audio playing (as Task 4, Step 4). Expected: the same values as the `dotnet run` probe.
- [ ] **Step 4: Start `dist\SoundProtection.exe`** and leave it running for Ariel. Report the path, where the tray icon is (hidden-icons flyout), and the one fact that matters: v1 reports and does not protect.

## Not in this plan

- Voicemeeter: install (driver, reboot, default-device change, all needing Ariel's OK), the Remote API wrapper, writing the limiter threshold, reading the physical output behind it, and the bypass check (an app playing straight to the real device). A new plan follows after Voicemeeter's API docs are read and the install is approved.
- dB SPL calibration, a CSV event log, per-app volume control.
