# Sound Protection: in-path loudness ceiling plan

| | |
|---|---|
| Date | 2026-11-10 |
| Status | **Plan for Ariel's review, audited 2026-11-10.** The audit's changes are listed in section 13; the full audit is in `PLAN-AUDIT.md`. Research and read-only checks only: this plan's research installed, built and changed nothing, and played no audio. |
| Builds on | `HANDOFF.md` and `RESEARCH-AND-PLAN.md`. Their facts still stand except where section 11 corrects them. This plan replaces RESEARCH-AND-PLAN's "Phase 1 first" plan and its section 8. |
| What was touched | Read-only registry and WMI reads on this PC, and web pages. Two test WAV files were generated with ffmpeg in a temp folder, measured, then deleted. |

**Evidence labels.**
- **[V*n*]** verified: a primary page or source file was read. *n* is the row in the evidence log (section 10).
- **[M]** read or measured on this PC, either today or in RESEARCH-AND-PLAN §14.
- **[I]** inferred.
- **[U]** unknown; the test that settles it is named next to it.

**Terms, in plain words**

- **dBFS**: digital level. 0 dBFS is the largest value digital audio can hold. -20 dBFS is one tenth of that amplitude.
- **V (slider dB)**: how much the Windows master slider turns the sound down. On the Realtek Headphones, 20% = -23.7 dB, 34% = -16.0 dB and 100% = 0 dB [M].
- **Ear level** (dB below device max) = digital level + V. 0 is the loudest this output can play. Software cannot know dB SPL (the actual sound pressure at your ear), which depends on the headphones.
- **Limiter**: an automatic volume knob. It turns the sound down only as far as needed to keep it under a threshold, called T. A **brickwall** limiter lets nothing above T through.
- **Look-ahead**: the limiter delays the sound by a few milliseconds. That lets it see a peak coming and lower the gain before the peak plays. Without look-ahead, the first milliseconds of a sudden burst get through.
- **True peak (inter-sample peak)**: the sound wave the DAC rebuilds can rise above every digital sample. A true-peak detector estimates that by oversampling (computing extra points between the samples).
- **APO** (audio processing object): a DLL that Windows' audio engine (`audiodg.exe`) loads to process the sound of one output device. There are three slots:
  - SFX runs on each app's stream.
  - MFX runs per processing mode.
  - **EFX** (endpoint effect) runs after all apps are mixed and "is always applied, even to raw streams" [V1].
- **Exclusive mode**: an app takes the device for itself and skips the Windows mixer and every APO.

---

## 1. The answer

1. **Recommended route.** Build our own small **limiter APO** (a C++ DLL) and add it as the **last endpoint effect (EFX)** on Headphones (Realtek). The C# tray app handles settings, "over by N dB" messages and health checks. **Plan B** is Voicemeeter: its September 2026 build has a look-ahead limiter on every output bus.
2. **Why this route.**
   - EFX processes every shared-mode stream on the device, Zoom and Chrome included, whatever mode they request [V1].
   - It runs before the Realtek hardware volume. This output does its volume in hardware (support mask 0x3 [M]), and Windows substitutes software volume only where the hardware has none [V35]. M3 confirms it with a tone. It adds about 5 ms.
   - It reads the slider inside the audio engine, so a dead tray app cannot relax the ceiling.
   - It reports exact numbers.
   - There is no virtual device, no clock drift, and no silence if the tray app dies.
3. **First, everything that changes nothing on the PC** (M0, M1): build the plug-in, test it on recorded sound inside a test program, and write and rehearse a one-step undo. **Then the first real load, on a harmless output** (a monitor's audio output) **and without the test key** (M2). Microsoft ties the signature check to DRM-protected playback [V42], so the key may not be needed; you are asked for it only if that load fails (M2-K). Only then does the plug-in go on the Headphones, at low volume (M3). The Equalizer APO trial is no longer on the path (section 13).
4. **What X means.** I recommend X as the ceiling at your ear, held at every slider position and computed inside the APO (section 2). A starting value from your own data is **X ≈ -28 dB below device max**: slider 20% plus Zoom's peaks of about -4 dBFS.
5. **What you must approve, each on its own when its turn comes** (no other message counts as a yes):
   - copying the plug-in into Program Files and registering it with Windows (system-wide);
   - adding it to a monitor output's effect list for the first test;
   - each restart of the audio service, and any reboot;
   - `DisableProtectedAudioDG = 1` (the "test key"), only if the plug-in does not load without it;
   - adding it to the Headphones effect list;
   - unticking exclusive mode;
   - later and optional: start with Windows, the SYSTEM repair task, the emergency volume cap.

   The C++ build tools are already approved and installed (section 12). Section 9 has the full list in order, each with its undo.
6. **Biggest risk.** Protection can vanish silently after a driver or Windows update, after Windows switches the effects off by itself, or after someone turns "Audio enhancements" off. The mitigation is a health check (registry plus an APO heartbeat), an auto-repair task, and an optional emergency volume cap.
7. **Order.** The work is ordered by dependency, risk and your approvals (section 5), not by a schedule.

## 2. What "X dB" means

There are two readings. Both satisfy "even if master is 100, nothing is louder than X". They differ only when the slider is below 100.

**(a) Fixed digital ceiling.** The limiter keeps the digital signal at or below C dBFS, so the ear peak is C + V. Nothing tracks the slider, which makes this fail-safe by construction. To be usable, C must sit above normal speech peaks: Zoom's ordinary peaks here were -4 to -10 dBFS [M]. A low C such as -28 would squash every voice.

**(b) Ceiling at the ear.** You set X as an ear level. The limiter threshold follows the slider: T = min(0, X − V). The ear peak stays at or below X at every slider position, and the limiter only acts when the sound would really exceed X.

**(b) with a relaxation limit R (recommended):** T = min(0, X − V, X + R). R caps how far the threshold may relax as the slider goes down. Once V is lower than −R, the ceiling becomes stricter than X. R also caps the one leak that (b) has (see "Fail-safety" below).

Worked example: X = -28 at the ear, R = 10, and C = -10 for reading (a). V at 50% is about -10.2 dB [I]. That comes from the fit dB ≈ 34·log10(slider/100), which matches all four measured points (20%, 34%, 67% and 100%); M0 measures it directly.

| Slider | V (dB) | (a) C = -10: ear max | (b): T / ear max | (b), R = 10: T / ear max |
|---|---|---|---|---|
| 100% | 0.0 | -10.0 | -28.0 / -28.0 | -28.0 / -28.0 |
| 50% | -10.2 | -20.2 | -17.8 / -28.0 | -18.0 / -28.2 |
| 20% | -23.7 | -33.7 | -4.3 / -28.0 | -18.0 / -41.7 |

**The Zoom case at slider 50%.** The loud talker peaks at -4 dBFS [M]. The quiet talker is assumed to peak at -25 dBFS. Without a limiter the gap between them is 21 dB.

- With (a), C = -10: the loud talker reaches the ear at -20.2 and the quiet one at -35.2. The gap is 15 dB, with 6 dB of limiting.
- With (b), X = -28: the loud talker reaches -28.0 and the quiet one -35.2. The gap is 7 dB, with 14 dB of limiting. That much limiting is audible; section 4.6 covers the settings that keep it acceptable.

**Where -28 comes from.** At slider 20% (-23.7 dB) you found the loud talker bearable. Their peaks were about -4 dBFS, so their ear level was about -28 [I]. The tray app's "set ceiling from now" button will measure this properly.

**Fail-safety**

- Reading (a) needs nothing outside the limiter, so it is fail-safe.
- Reading (b) is fail-safe only if T is computed where the audio is processed.
  - Our APO subscribes to the **dB form** of Windows' endpoint-volume notification: `APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME2`, field `masterVolumeInDb`, through `IAudioProcessingObjectNotifications2`, from build 22621 [V36][V37]. The older `ENDPOINT_VOLUME` form (build 22000 [V33]) gives only a 0-to-1 audio-tapered number, not dB [V38], so it must never be used as dB. Windows sends the current value as soon as the APO registers [V37]; the APO also reads it once itself, because an older Microsoft sample says to [V2]. The APO computes T itself, from the loudest channel.
  - If the APO has no volume value, it assumes the slider is at 100 and uses T = X.
  - Killing the tray app changes nothing.
- A tray app that rewrites the limiter threshold whenever the slider moves would **not** be fail-safe. If the app dies after relaxing T, pushing the slider up breaks the ceiling. I am not offering that design. It is also the only way to get (b) with Equalizer APO and a VST plugin, so route A gets reading (a) only.
- **The one leak in (b).** When the slider jumps up, the sound already on its way to the device plays at the new volume before T tightens.
  - "On its way" is about 10 to 30 ms: one 10 ms engine period [M] plus the device buffer [I].
  - The overshoot is at most the smaller of R and the size of the jump. Volume keys and dragging move in small steps; clicking straight from 20% to 100% is the worst case.
  - With R = 10, the worst case is +10 dB for about 30 ms at most [I]. Test C7 can measure it (optional).
  - This bound holds only if T **tightens at once** when the slider goes up (through the 5 ms look-ahead). Only a relaxing T may ramp. A 100 ms ramp on every change would make the leak longer (section 4.6).
- **Route D (Voicemeeter) has no such leak.** The slider acts on the virtual device, before the limiter, so reading (b) comes for free. The real device's own volume must stay fixed, and the tray app watches it.

**Accuracy**

- **Sample peaks: exact.** A final safety clipper at T guarantees that no sample exceeds T.
- **True peaks.** Between samples, the rebuilt wave can exceed the samples by:
  - up to 3.0 dB for a 12 kHz tone (a quarter of the 48 kHz sample rate);
  - at most 1.25 dB for content below 8 kHz;
  - at most 0.3 dB for content below 4 kHz.

  These figures are derived from under-read = −20·log10 cos(π·f/fs). Measured here [M]: a 12 kHz tone with a true peak of -1.0 dBFS shows a sample peak of -4.0 dBFS. Detecting peaks at 4 times the sample rate cuts the worst case to 0.69 dB, so I recommend true-peak detection; it is cheap.
- **Hardware volume.** Ear level assumes the Realtek hardware volume matches the dB value Windows reports [I].
- **No dB SPL.** A phone sound-level app can give a rough calibration later, within about ±3 to 5 dB (RESEARCH-AND-PLAN §2.3).

**Recommended default:** reading (b) with R = 10, and X set from your own "loud talker is bearable" moment (about -28 to start). **Yours to decide:** (a) or (b); the value of X; and R, between 0 and 20. A larger R holds the ceiling at exactly X over more of the slider range, but allows a bigger slider-jump leak.

## 3. Route comparison

| Route | Leak-free on a burst | Zoom + Chrome | What bypasses it | Added latency | If a part crashes or is removed | Install | Signing | Undo | Maintenance and licence |
|---|---|---|---|---|---|---|---|---|---|
| **A. Equalizer APO 1.4.2 + LoudMax 1.47 (VST2)** | Yes: 1.25 ms look-ahead, inter-sample option [V19]. Order relative to the vendor effects: [U] | Yes [V1][I] | Exclusive mode; new devices; driver updates | +1.25 ms [V19] | Plugin crash or missing config: **audio passes unprotected** [V12][V13]. APO dropped: unprotected, unnoticed | Admin, reboot [V15], key = 1 [V10] | Unsigned; needs the key | Uninstaller restores the effect list and deletes the key [V10][V11] | EAPO 1.4.2 is from 2025-03-21; developer "seldom active" [V8][V17]. GPL-2.0. LoudMax: freeware, 2026-06-23 |
| **B. Fork EAPO + our own limiter** | Yes | Yes | As A | ~5 ms | As C, unless EAPO's pass-on-error is changed | As A | As A | As A | Us plus upstream; GPL-2.0 [V8] |
| **C. Own limiter APO (recommended)** | Yes: 5 ms look-ahead plus safety clipper | Yes; EFX covers every mode [V1] | Exclusive mode (untick it); spatial sound [U]; new devices (repair task) | ~5 ms | Tray app dies: still protected. APO dropped or effects disabled: unprotected but **detected** | Admin once; audio-service restart; key only if the no-key load fails [U → M2] | Unsigned. Microsoft ties the signature check to DRM-protected graphs [V42]; whether ordinary playback needs the key on 26200 is [U → M2] | One-step undo, rehearsed before the first registration (section 7) | Ours |
| **D1. Voicemeeter (Sep 2026)** | "Green" bus limiter: 5 ms look-ahead [V21]. Whether it truly holds the limit: [U, test] | Yes, if its virtual input is the default for all roles | Apps set to the real device; new default devices; exclusive mode on the real device | Tens of ms [U] + 5 | Not running: **silence** [I] | Admin, kernel driver, reboot [V22] | Vendor-signed driver [I][V5] | Uninstall + reboot | Active (Sep 2026) [V21]; donationware, closed source |
| **D2. VB-CABLE + our own router** | Yes | As D1 | As D1 | 20-40 ms [I] | Router dies: silence | Admin, driver, reboot [V23] | Vendor driver | Uninstall | Cable updated Nov 2026 [V23]; needs clock-drift handling |
| **E1. Headset with a built-in shock limiter** | Yes, in hardware | Anything played on that headset | — | 0 | n/a | Purchase | n/a | n/a | Vendor |

**Notes per route**

- **A.**
  - The stock build loads VST2 plugins only [V12]. A plugin's parameters are set by name with VST's 0-to-1 values, or as a saved ChunkData blob; EAPO's Configuration Editor is the easiest way [V12].
  - Config edits crossfade over about 10 ms [V13].
  - EAPO registers itself in the *single* effect slots and never writes the *composite* slots where this PC's Realtek effects live [V11][M]. Microsoft does not document whether Windows then runs both, or in which order [U; matters only if the optional Equalizer APO diagnostic in section 5 is used].
  - LoudMax is freeware and may not be redistributed [V19]. Other limiter plugins were not evaluated; LoudMax is enough for the trial.
- **B.**
  - Building needs Visual Studio, FFTW, libsndfile and exactly muParserX 3.0.1; Qt is needed only for its GUIs [V16].
  - EAPO already reads the endpoint volume inside the APO, for its loudness correction [V14], so reading (b) would be possible there too.
  - Choose B only if our own APO skeleton (M2) stalls.
- **D1.**
  - The bus limiter runs from 0 to -20 dBFS and is set with `Bus[i].Limit`.
  - "Green" mode has 5 ms of "integration time" (look-ahead). "Yellow" is real time, without look-ahead [V21].
  - The Remote API lets a tray app read levels and set the limit [V21].
  - All three editions got the new limiter in the September 2026 releases (3.1.3.0 / 2.1.3.0 / 1.1.3.0) [V21][I].
- **E1.** For example, Jabra PeakStop removes spikes inside the headset itself [V26]. The reported levels are 105 to 118 dB SPL, fixed by the vendor [reported], far above a comfort ceiling. Good as a second layer, not as your X.

**Other routes and why not (one line each)**

- A watchdog that turns apps down after a burst starts: rejected by you, because it leaks the first tens of ms.
- Capping the master slider: rejected by you. It survives only as an optional emergency fallback (section 4.5).
- Windows Loudness Equalization, or swapping to Microsoft's generic driver: rejected by you.
- HP Audio Control and the Sound Research and Realtek effects: no limiter or compressor found in a static string check (RESEARCH-AND-PLAN §14) [M].
- Virtual Audio Cable (Muzychenko): paid, and has no limiter [V24].
- Open-source virtual audio drivers: only test-signed betas [V25]. Secure Boot is on here [M], so they won't load.
- A browser extension: covers YouTube only, not the Zoom app.
- A new Windows feature that caps loudness: none found (searched 2026-11-09).

## 4. Recommended design (route C)

### 4.1 What is on this PC today [M]

- **Headphones effect chain:**
  - SFX: Realtek RtkAPOSFX.
  - MFX: Realtek RtkAPOMFX.
  - EFX: **SEAPOEFX** (Sound Research 2.0.10.76), then **RtkAPOEFX** (Realtek 11.0.6000.994, dated 2021-12-07).
  - All are WHQL-signed. They are stored in the composite keys `{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13 ,14 ,15`, plus the offload keys `,19 ,20`.
  - Speakers have the same chain. The two monitors have Microsoft's SFX and MFX only.
- **Hardware:** an HP Z2 G8 Tower with a standard "High Definition Audio Controller", so hardware offload (a mixing path that skips the APOs) is unlikely [I; probed in M2].
- **Security state:**
  - Smart App Control: off (`VerifiedAndReputablePolicyState = 0` [V32]).
  - Memory integrity (HVCI): running.
  - User-mode code integrity: not enforced.
  - Secure Boot: on.
  - `DisableProtectedAudioDG`: not set.
  - `audiodg.exe` runs as an ordinary process: no process protection (`PS_PROTECTION = 0`) and no "Microsoft-signed only" image-load policy (read 2026-11-10 while it was running) [M]. So the kernel does not block an unsigned DLL there; whether the audio engine checks signatures itself is what M2 tests.
  - The Realtek and Sound Research plug-in DLLs are signed "Microsoft Windows Hardware Compatibility Publisher" (WHQL) [M].
- **Permissions on the Headphones `FxProperties` key:** owner SYSTEM; Administrators **and Users** have SetValue (Users through an inherited entry) [M]. Installing therefore needs no ownership change here, but any program can also edit the list (risk 1). The key also grants read to the `lpacMedia` app-container capability and SetValue to the `audioDeviceConfiguration` capability [M: capability SIDs derived on this PC]. The first suggests that audiodg reads effect lists from a sandbox (section 4.2 item 2).
- **Value types:** `,13`, `,14`, `,15`, `,19` and `,20` are REG_MULTI_SZ lists [M], as Microsoft describes the composite keys [V4]. On the two monitor outputs the single EFX slot `,7` and its mode key `{d3993a3f…},7` are empty [M].

### 4.2 Components

1. **`SoundCeiling.Apo.dll`** (C++). One EFX APO class.
   - Interfaces: IAudioProcessingObject, IAudioProcessingObjectRT, IAudioProcessingObjectConfiguration and IAudioSystemEffects2/3, plus IAudioProcessingObjectNotifications and IAudioProcessingObjectNotifications2 for the endpoint volume in dB [V2][V37].
   - Built on `CBaseAudioProcessingObject`. Its header and libraries are in the Windows SDK now on this PC (listed on disk [M]; Microsoft points to the SDK's `Include\um` folder [V42]), so **the WDK is not needed**. The install reports a successful test link (section 12); the audit did not re-run it. **ATL is not installed** [M], so the COM part is hand-written the way Equalizer APO does it: a delegating IUnknown, a separate non-delegating one, and an outer pointer for aggregation [V39]. Microsoft's SwapAPO sample uses ATL and a WDK project type [V43]: read it as a model, do not build it.
   - Correct COM aggregation (letting Windows' audio engine wrap the object). Another third-party APO hit a recursion bug here [V27]; the M1 test host exercises aggregation before Windows ever loads the DLL.
   - Float32 audio at any sample rate and channel count. Registration Flags 0xF (in place, and the formats must match), as the Sound Research EFX on this PC uses [M]; the SDK's APO_FLAG_DEFAULT is 0xE [M]. It accepts every float format it is offered, because a rejected format counts towards Windows' 10-failure switch-off [V3]. No embedded manifest [V3].
   - The real-time function APOProcess never allocates memory, takes locks, touches files or uses pageable memory [V3].
   - **Ear-safety rules in the code:**
     - the gain never goes above 1, so the plug-in can only make sound quieter;
     - a final clamp holds every output sample within ±T and turns NaN or infinity into 0. It is compiled with `/fp:precise`, so the check cannot be optimised away;
     - denormals are flushed, with the CPU's floating-point mode saved and restored on every call;
     - development builds carry a fixed extra clamp at -20 dBFS, whatever the config says;
     - a crash-loop guard: after 3 unclean starts it runs clamp-only and reports DOWN. Windows' 10-failure rule does not count crashes inside APOProcess [V3][I].
   - Registered by **appending its CLSID to the end of `,15`** (composite EFX), so that it runs after the Sound Research and Realtek effects. Microsoft does not document the order inside a composite list [V4], so this is [U]; M3 tests it.
   - Fail-safe defaults:
     - no config: enabled, with the built-in ceiling;
     - no volume value: assume the slider is at 100;
     - internal DSP fault: fall back to the safety clipper at T. That distorts but never exceeds T.
2. **Config** in `%ProgramData%\SoundCeiling\config.json`: enabled, X, mode, R, look-ahead, release, true-peak.
   - The tray app writes it. A worker thread in the APO watches the folder, as Equalizer APO does [V13].
   - New values reach the real-time path without locks. Values that relax the ceiling ramp over 100 ms; values that tighten it apply at once (section 4.6).
   - The folder's ACL must let `audiodg` read it. The audio service cannot read `C:\Users` [V28]. It probably runs in a less-privileged app-container sandbox (LPAC): another developer reports it [V27], and on this PC the effect-list key grants read to the `lpacMedia` capability [M].
   - `C:\ProgramData`'s default ACL has no app-container entries [M]. So the installer creates `%ProgramData%\SoundCeiling` with explicit entries, as narrow as they can be: SYSTEM and Administrators full control; the signed-in user modify (the tray app writes the config); LOCAL SERVICE (audiodg's account), ALL APPLICATION PACKAGES (S-1-15-2-1) and ALL RESTRICTED APPLICATION PACKAGES (S-1-15-2-2) read only. Only the subfolder `telemetry` gives those three modify, so a sandboxed app cannot raise the ceiling by editing the config. Another developer's sandbox fix used these two app-package SIDs [V27].
   - The DLL lives in `C:\Program Files\SoundCeiling\`, which already grants read and execute to Users and to both app-package groups [M]. The COM registration keys under `HKLM\SOFTWARE\Classes` inherit read for app containers and for the `registryRead` capability [M].
   - Alternative channel, Microsoft-supported: the effects property store (`IAudioSystemEffectsPropertyStore`). Microsoft says it "is readable and writable by ... non-admin desktop applications, and APOs" and can carry messages back from the APO [V40]. Whether it works for a plug-in registered without an INF is [U]. Try it only if the file channel hits sandbox problems.
3. **Telemetry** in `%ProgramData%\SoundCeiling\telemetry\<endpoint>.bin`, a memory-mapped file.
   - Contents: heartbeat counter and time, audio format, V, T, input peak (sample and true), output peak, maximum gain reduction, over count, clipper hits, and the config version in use.
   - The APO worker thread writes it every 50 ms, copying values the real-time thread leaves in locked memory.
   - Its `telemetry` subfolder gives audiodg's account and the app-package groups modify; the config stays read-only for them (item 2) [V27][M].
4. **`SoundCeiling.Tray`** (C#, .NET 10 [M], WinForms, NAudio 3.x).
   - Icon states: ON, OFF, ALERT, DOWN.
   - Toggle, ceiling controls, messages, event log (CSV), health check and device watcher.
   - Needs no admin.
5. **`SoundCeiling.Admin`** (elevated; runs only for install, uninstall and repair).
   - Snapshots the effect list to a `.reg` file.
   - Writes the COM and APO registration under `HKLM\SOFTWARE\Classes`, with the value set from Microsoft's sample [V3].
   - Appends our CLSID to `,15` (Headphones), or writes the empty EFX slot `,7` plus its mode key on the harmless test output (M2). Each is a separate step with its own OK.
   - Sets `DisableProtectedAudioDG = 1` and records the previous state **only** in gate M2-K: if the no-key load failed and you said yes.
   - Sets the ACLs on the ProgramData folder.
   - Restarts AudioEndpointBuilder and Audiosrv only when you OK that restart, in this order: stop Audiosrv, stop AudioEndpointBuilder, start AudioEndpointBuilder, start Audiosrv. Another developer found that the endpoint builder caches effect settings, so restarting Audiosrv alone may not be enough [V27].
   - Has a dry-run mode that prints every change it would make and makes none.
   - `undo` is the one-step undo of section 7. It is written and rehearsed in M1, before the first registration.
6. **Repair task** (Task Scheduler, running as SYSTEM). **Optional, after version 1, with its own OK.** Version 1 uses a "Repair" button in the tray app instead, which asks for admin each time.
   - Runs at boot, at logon, every 15 minutes, and on driver-install events [I; exact event IDs confirmed in M5].
   - Finds protected endpoints by name and device, not by GUID: endpoint GUIDs can change when a driver is reinstalled [V28].
   - Re-appends the CLSID if it is missing, restarts the audio services, and notifies the tray app.

### 4.3 Data flow

```
Zoom / Chrome / other apps (shared mode)
   │  per-app SFX (Realtek) → all apps mixed → MFX (Realtek)
   ▼
EFX chain:  Sound Research ─► Realtek ─► SoundCeiling limiter ◄── config.json (X, mode, R, on/off)
                                            │     ▲
                                            │     └── slider dB (Windows endpoint-volume notification)
                                            ├──► <endpoint>.bin (heartbeat, peaks, gain reduction) ──► tray app
                                            ▼
Windows' own CAudioLimiter (keeps samples within full scale) [V28][V29]
   ▼
Realtek driver ─► codec hardware volume (= the slider) ─► headphones
```

The tray app also reads each app's own level meter, to name the app that was loud [M], plus the endpoint volume and the registry state used by the health check.

### 4.4 Messages and toggle

- **When an event fires.** An "over" event is input true peak > T in the APO telemetry.
- **The number in the message.** The ear overshoot is input peak + V − X for reading (b), or input peak − C for reading (a). The APO measures both the input and the output, so the number is exact. It does not depend on where Windows' device meter sits.
- **Naming the app.** The loudest per-app meter in the same 100 ms names the source. These meters read before mixing and before our limiter [M]. Example: `Zoom would have been 9.6 dB over your ceiling (14:02:31, 1.8 s). Held at -28 dB.`
- **Limiting the noise.** Events less than 500 ms apart merge, and each app gets at most one message per 10 seconds, plus a summary (RESEARCH-AND-PLAN 7.4). The tooltip shows the current ceiling at your ear.
- **Off.** Off ramps T up to 0 dBFS over 100 ms. The APO and its 5 ms delay stay in place, so turning it off or on never clicks. Fully bypassing the APO would remove the delay and click, so Off is never a bypass. The icon goes grey; auto-on after 30 minutes is optional (D9).

### 4.5 Health check and fail-safe behaviour

The tray app runs the checks below every 2 seconds and on device or volume events. None needs admin.

| Check | How | If it fails |
|---|---|---|
| Our CLSID is still last in `,15` on each protected output | Registry, readable without admin [M] | DOWN |
| Effects not switched off: `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5` absent or 0 | Registry. Windows sets this flag after 10 APO load failures [V3], and "Audio enhancements: Off" sets it [V28]. Microsoft's text says the flag covers effects installed by the driver's INF [V41]; whether it also bypasses our hand-registered plug-in is [U], tested in M2 | DOWN |
| `DisableProtectedAudioDG` = 1 (only if gate M2-K set it) | Registry | DOWN at the next audio restart |
| Heartbeat newer than 300 ms whenever an app session on that output is active | Telemetry plus session state | DOWN |
| The APO's slider value matches the tray app's own reading | Telemetry vs. IAudioEndpointVolume | WARN |
| Exclusive mode unticked; spatial sound off | Endpoint properties [U: the exact property is found in M0] | WARN |
| An active output has no APO | Device enumeration | WARN, plus "Protect this output?" |

**On DOWN.** The icon turns red, a notification stays on screen, the event is logged, and the tray offers Repair (or the optional repair task runs, if installed). Optionally (D7), the tray app applies an **emergency cap**: it lowers that output's master volume to X − 3 dB until protection is back, then restores it. Without a limiter, the digital peak cannot exceed 0 dBFS, and between samples content below 12 kHz adds at most 3 dB (section 2), so the ear level stays at or below X for such content [I]. Strong content above 12 kHz could overshoot more; it is rare in speech.

**Failure summary**

- Tray app dies: protection is unchanged; only the messages stop. Autostart plus restart-on-crash covers this.
- APO fault: falls back to the clipper at T.
- `audiodg` crash: audio restarts [I]. Ten failed loads make Windows switch off every effect on that output [V3]; the health check detects it.
- A driver update removes the registration: protection is gone until repair. The health check flags it within 2 seconds of the next playback, and Repair (or the optional task) restores it.
- A crash inside APOProcess: Windows' 10-failure rule counts only the results of CoCreateInstance, IsInputFormatSupported, IsOutputFormatSupported and LockForProcess [V3], so a crash during processing may repeat at every audio restart [I]. The crash-loop guard (4.2 item 1) and the one-step undo (section 7) cover it.
- A broken sample value (NaN or infinity): the final clamp outputs 0 instead. A NaN must never reach the driver, where conversion to whole numbers could turn it into a full-scale click [I].
- A hang inside APOProcess: sound stops (silence, not loudness); the heartbeat stops, so the tray shows DOWN and offers the one-step undo.

### 4.6 Speech-friendly limiter settings (defaults, tuned in M3)

| Setting | Default | Why |
|---|---|---|
| Look-ahead (= attack time) | 5 ms | The gain is already down when the peak plays. 5 ms is about one voice pitch period, so the gain moves smoothly [I]. For comparison, LoudMax uses 1.25 ms [V19] and Voicemeeter's Green mode 5 ms [V21]. |
| Hold | 20 ms | Longer than the longest pitch period of a low voice (about 12 ms at 85 Hz), so the gain does not follow individual waves. Gain that does sounds like distortion [I]. |
| Release (how fast the gain recovers) | Automatic: 80 ms after an isolated peak, sliding to 400 ms once limiting lasts more than 200 ms | Quick recovery after a cough or a click, and no pumping on a talker who stays loud [I] |
| Detection | True peak, 4× oversampled | Accuracy (section 2) |
| Stereo link | Full | Same gain on both ears, so the sound does not shift sideways |
| Safety clipper | At T | Hard guarantee. It counts its hits, which should stay at 0 on speech. |
| Parameter changes | 100 ms ramps when the ceiling relaxes (slider down, X raised, Off). Tightening (slider up, X lowered, On) acts at once, through the look-ahead | No clicks, and no extra leak when the slider jumps up (section 2) |
| Optional pre-stage | Off | If 10 dB or more of limiting sounds squashed: a gentle 2:1 compressor starting about 10 dB below T. It only turns loud parts down, so it is not normalisation. Decided after M3. |

### 4.7 Which outputs are covered

- **Version 1:** Headphones (Realtek), where Zoom plays [M].
- **Speakers (Realtek):** same chain and same install [M]. Protect them if you use them (D6).
- **The two monitors (NVIDIA HDMI):** volume is applied in software [M]. Whether Windows applies it before or after the EFX slot is [U], and it changes what (a) and (b) mean on those outputs. Test it before enabling them.
- **New outputs (USB, Bluetooth):** they get no APO until registered. The tray app asks "Protect this output?", and the repair task can do it automatically if you choose (D6).
- **Bluetooth limitation:** Windows 11 can merge a headset's two Bluetooth profiles into one output, where EFX may not run (a comment in Equalizer APO's code) [V11]. That would need the MFX slot instead; out of scope for v1.
- **DRM-protected playback:** without the test key, Windows may refuse to load an unsigned plug-in into a graph that plays protected content [V42]. That playback would then be unprotected, and each refusal counts towards the 10-failure switch-off [V3]. Tested in M2 and M3 if you play such content.
- **Always unprotected:** apps that take exclusive control of the device while that box is ticked; apps using ASIO or kernel streaming [V28]; anything while the plug-in is DOWN or switched Off; and any gain the driver or codec adds after Windows' engine [U] (the "set ceiling from now" calibration absorbs a fixed one).
- **What the panel says:** one line per output: "Protected" (fresh heartbeat, plug-in last in the list), or "NOT protected" with the reason (not added, DOWN, exclusive mode allowed, DRM playback, Bluetooth). It never shows "Protected" from settings alone.

## 5. Milestones with acceptance tests

Steps that change nothing on the PC come first (M0, M1). Every step that changes it is a gate: you are asked when its turn comes, each gate on its own, and each has a written undo. No other message counts as a yes. Section 9 lists the gates (A1-A10).

**M0. Prepare (changes nothing on the PC)**

1. Record your decisions D1, D2 and D6 (section 9).
2. Export registry snapshots. This only writes files:

   ```
   reg export "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render" render-before.reg
   reg export "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio" audio-before.reg
   ```
3. You: check that Smart App Control shows Off (Windows Security → App & browser control). Open HP Audio Control briefly and note anything that looks like a limiter or leveller.
4. Read the slider's dB value at 50% from the running tray app, which already shows the Windows volume in dB.
5. Zero-code slider check: headphones off your head, slider at 10% and then at 20%, play the tray app's -20 dBFS test tone each time. Its device meter should read -20 dBFS both times, as RESEARCH-AND-PLAN §14 (F2) found with Zoom. That fits "the plug-in will see the sound before the slider"; M3 settles it with the plug-in's own meter.

Acceptance: the snapshots are saved and the decisions are recorded. (Unticking exclusive mode moved to M5: it changes the PC, so it is a gate.)

**M1. Build and bench-test (changes nothing on the PC)**

Toolchain: approved and installed (section 12): Build Tools 2022 17.14 with MSVC 14.44 and the Windows SDK 10.0.26100.0. The plug-in base class and its libraries are in that SDK, so the WDK is not needed [M]. ATL is not installed, so the COM code is hand-written as in Equalizer APO [M][V39]. The install reports that a test DLL deriving from the base class linked with `/NODEFAULTLIB:atls.lib audiobaseprocessingobject.lib audiomediatypecrt.lib audioeng.lib ole32.lib` [reported; not re-run by the audit]. Microsoft's SYSVAD SwapAPO and DelayAPO samples (MS-PL licence) are models to read, not to build: their project type needs the WDK [V30][V43].

Build steps:

1. Create `src\SoundCeiling.sln` with these projects:
   - `SoundCeiling.Apo` (C++ DLL, dynamic universal C runtime, Embed Manifest = No [V3], `/fp:precise`);
   - `SoundCeiling.Dsp` (header-only limiter, with tests): our own code, or an adaptation of the MIT-licensed limiter in Signalsmith Basics [V20]; modes (a) and (b) with R;
   - `SoundCeiling.ApoHost` (a test program that loads the DLL directly, with no registration, and drives it the way the audio engine does);
   - `SoundCeiling.Tray`, `SoundCeiling.Admin` and `SoundCeiling.Measure` (C#, .NET 10).
2. Build: `msbuild src\SoundCeiling.sln /p:Configuration=Release /p:Platform=x64`.

Acceptance (all on the bench; nothing is registered and no setting changes):

- **Limiter, offline:** the section 6 signal table passes, with sample peak ≤ T exactly, true peak ≤ T + 0.7 dB and no sample above T at the start of the burst. The telemetry's gain reduction equals input minus output within ±0.1 dB. Zero clipper hits on speech.
- **Edge cases:** NaN, infinity and denormal input; silence; full-scale noise; 44.1, 48 and 96 kHz; 1, 2, 6 and 8 channels; a format change between two LockForProcess calls. The output is always finite, within ±T, and never louder than the input.
- **Test host:** `SoundCeiling.ApoHost` creates the plug-in through `DllGetClassObject` **with an outer object**, as the engine aggregates it [V27][V39], then runs Initialize, IsInputFormatSupported, LockForProcess, a long APOProcess run and UnlockForProcess. No allocation or lock happens inside APOProcess.
- **Undo, rehearsed:** the installer and the one-step undo (section 7) exist. Their dry-run on the real keys prints only the intended changes and writes nothing. Run against a scratch copy of the keys under `HKCU\Software\SoundCeiling\UndoRehearsal`, install-then-undo leaves the copy byte-for-byte as it was, including values that did not exist before.
- **Measuring tool:** `SoundCeiling.Measure` records loopback on a chosen output to a WAV, prints the device meter, the per-app meters and the slider dB, and asks the device whether it can offload. It contains no calls that change anything.
- **Listening:** you pass a listening test on recorded Zoom audio, run through the limiter offline.

**M2. First real load: a harmless output, no test key (gates A3, A4, A5)**

The target is one monitor's audio output (C24-40 or VA2405-FHD), with nothing plugged into that monitor's audio jack. Its EFX slot `,7` and EFX mode key are empty today, so nothing of the vendor's is displaced [M]; Equalizer APO uses this same single-slot form [V45]. The test build can only make sound quieter.

1. Gate A3: copy the plug-in in and register it.
2. Gate A4: add it to that monitor's effect list.
3. Gate A5: restart the audio services.
4. **Load test**, with a test build that only halves the sound (-6.02 dB) and writes a heartbeat. Set that monitor's Windows slider to 100%: this output's volume is done in software (mask 0x2 [M]), and at 100% it cannot blur the reading. If the monitor has its own speakers, turn them down in the monitor's menu; loopback still records. Play the tray app's -20 dBFS tone to the monitor and record loopback there: about -26.0 dBFS means it loaded; -20.0 means it did not. This check needs none of our files, so a sandbox problem with the files cannot pass for "not loaded" [V27]. It assumes loopback is taken after the plug-in [I]: if loopback reads -20.0, check with `tasklist /m /fi "imagename eq audiodg.exe"` (admin, read-only) whether `SoundCeiling.Apo.dll` is loaded at all, to tell "not loaded" from "loopback is taken before the plug-in". Note also whether Windows' device meter drops by the same 6 dB, which shows whether it reads after an inserted plug-in (section 11, item 10).
5. If it did not load: read the Application event log and the switch-off flag `{1da5d803…},5`, run the one-step undo, and only then ask for gate A6 (M2-K).

Acceptance:

- The load signature is -6.0 ± 0.1 dB. The heartbeat and telemetry work, or the ACL fix is found and goes into the installer.
- It survives an audio restart, sleep and resume, and a full Zoom call's length of playback without dropouts. `{1da5d803…},5` stays absent.
- "Audio enhancements: Off" on that monitor: record whether the plug-in is bypassed [V41][U], then switch it back on.
- Optional, if you play such content: a DRM-protected clip (for example Netflix in Edge) plays; note whether the plug-in was in its path [V42].
- The limiter build replaces the test build (gate A5 again) and passes the section 6 table on the monitor.
- **Undo, for real:** run the one-step undo. The registry matches the M0 snapshots exactly and the monitor plays normally. Only after this does anything touch the Headphones.

**M2-K. Only if the no-key load failed: the test key (gate A6)**

The test key is a Windows setting that lets unsigned plug-ins load (`DisableProtectedAudioDG = 1`). A reboot follows, as its own OK: the value is reported to be read at boot [V27], and Equalizer APO asks for a reboot after setting it [V15]. Then M2 is repeated from step 3. Undo: delete the value and reboot. With the key set, also run the DRM test: apps needing a secure audio path may refuse to play [V16].

**Optional diagnostic, only if M2 fails for reasons we cannot see: Equalizer APO + LoudMax trial (was M1)**

It needs its own OKs (install, test key, reboot). It cannot answer the no-key question, because its installer always sets the key [V44], and it never uses the composite slots our plug-in uses [V45].

Steps:

1. Download `EqualizerAPO-x64-1.4.2.exe` (12.0 MB) from SourceForge [V9] and install it (admin).
2. In Device Selector, tick **Headphones only** and leave the troubleshooting options at their defaults. It picks the SFX/EFX slots here because composite keys exist [V11]. Click OK and let the install test run.
3. Reboot when asked. It asks because it sets the key [V15].
4. Download LoudMax VST2 64-bit v1.47 [V19] and put `LoudMax64.dll` in `C:\Program Files\EqualizerAPO\VSTPlugins\` [I: folder from the installer, V10].
5. In EAPO's Configuration Editor, add VSTPlugin → LoudMax. Turn the inter-sample option on, set Output to C (reading (a), for example -10), and save.
6. Measure with `SoundCeiling.Measure` from M1.

Acceptance:

- **T-M1-1.** The install test reports success. `tasklist /m /fi "imagename eq audiodg.exe"` (admin) lists `EqualizerAPO.dll` **and** `RltkAPOU64.dll` and `SEAPO64.dll`, so both kinds of slot load. If the vendor DLLs disappear, the single slot overrode the composite slots; record that.
- **T-M1-2 (measuring-tool check).** Set EAPO's Preamp to -10 dB. The loopback peak should drop by 10.0 ± 0.1 dB, which shows loopback is taken after the APOs [U → this test]. Note whether Windows' device meter drops too; that shows whether the device meter reads before or after an inserted APO [U → this test].
- **T-M1-3.** Play the step and burst files at sliders 20, 50 and 100. Loopback sample peak ≤ C + 0.1 dB, and true peak ≤ C + 0.7 dB.
- **T-M1-4 (DRM).** Play protected content in Edge, for example Netflix if you have it. Sound must play: Equalizer APO warns that apps needing a secure audio path may refuse to output audio [V16].
- **T-M1-5.** Zoom "Test speaker", one real call, and YouTube in Chrome. You listen and judge.
- **T-M1-6.** Toggle by switching an Include file. No click should be audible, and loopback should show no dropout.

Exit: uninstall it (its own OK). The registry must match the M0 snapshot and the key must be gone.

**M3. Headphones, at low volume (gates A7, A5)**

Safety for every live step: headphones **off your head**, slider at 10% or less, the -20 dBFS tone first, and the development clamp (-20 dBFS) still in the build.

1. Gate A7: append the plug-in to the end of the Headphones EFX list `,15` (`SoundCeiling.Admin install --output "Headphones (Realtek(R) Audio)"`). Gate A5: restart the audio services.
2. **Slider check** (settles where the slider sits, section 2): play the tone at slider 10% and at 20%. The plug-in's input peak must read -20.0 ± 0.1 dBFS both times. Then it sees the sound before the slider, as the hardware-volume mask predicts [V35][M], and T = min(0, X − V, X + R) is right. If it reads -20 + V instead, a vendor effect applies the volume digitally, and the plug-in must not subtract V again: stop and flag it.
3. **Order check** (settles "last in the chain", which Microsoft does not document [V4]): turn on one HP Audio Control effect that changes the level, for example a bass boost. The plug-in's output peak must equal the loopback peak within 0.1 dB. If loopback is higher, a vendor effect runs after ours: stop and flag it.
4. The section 6 table at sliders 20, 50 and 100, still with the headphones off your head (while the development clamp is in, the expected values are capped at -20 dBFS). Then the release build replaces the development build (gate A5).
5. Real use: Zoom "Test speaker", one real call, and YouTube in Chrome. You listen and judge.

Acceptance:

- A heartbeat appears within 1 second of playback start. `tasklist /m` (admin) shows our DLL next to the Realtek and Sound Research DLLs.
- The slider value the plug-in reports equals the slider within 0.1 dB at 20, 34, 50 and 100%. With the balance moved off centre, it uses the louder channel.
- Steps 2 to 4 pass. It survives an audio restart, sleep and resume, unplugging and replugging the headphones, and a full Zoom call's length of playback. `{1da5d803…},5` stays absent.
- The offload probe reports that offload is not supported [I → this test].
- The undo's dry-run on the Headphones keys shows exactly the reverse of step 1.

**M4. Tray app as the plug-in's remote control (changes nothing on the PC; start-with-Windows is gate A8)**

It can be built alongside M1 and goes live with M3.

Acceptance:

- The panel says honestly what is not protected (section 4.7), and shows DOWN within 2 seconds when the heartbeat stops.
- A left-click toggles protection, and the state survives a restart.
- A message appears within 1 second of an over event, its N is within ±0.5 dB of the telemetry, and it names Zoom or Chrome correctly.
- At most one message per app per 10 seconds.
- Killing the tray app leaves protection on: the step file is still limited.
- The toggle does not click: loopback shows no jump larger than the program's own.

**M5. Close the gaps (gates A9 and A10; each item its own OK)**

1. Gate A9: untick exclusive mode on Headphones (Sound control panel → Headphones → Properties → Advanced → "Allow applications to take exclusive control"). Diff the registry before and after to find the property the health check reads. A community script uses `{b3f8fa53-0004-438e-9003-51a46e139bfc},3` and `,4` [V47, reported]; neither is set on this PC [M]. Undo: tick the box again.
2. Optional, gate A10: the new-output prompt, the emergency cap, the SYSTEM repair task, more outputs. (Reading (b) with R is built in M1.)

Acceptance:

- Each injected fault raises DOWN or WARN within 5 seconds of playback, and Repair restores protection within 1 minute. The faults are: removing our CLSID from `,15`; setting `{1da5d803…},5` to 1; killing audiodg; plugging in a USB headset. Injecting them changes the PC, so each injection session is its own OK.
- The emergency cap, if chosen, engages and releases correctly.
- An overnight run keeps CPU under 1% of one core for the APO and for the tray app [I], with stable memory.
- Optional: test C7.

## 6. Test plan

**Safety.** These signals play on your real output. Before each test, take the headphones off or set the slider to about 10%, and raise it only on purpose. The -3 and -1 dBFS parts are meant to be loud. (From RESEARCH-AND-PLAN §10.)

**Stricter rule for the plug-in's first live tests (M2, M3):** a harmless output first (M2). Then Headphones **off your head**, slider at 10% or less, only the -20 dBFS tone until the plug-in has passed, and a development build whose extra clamp keeps the output at or below -20 dBFS whatever it is told.

**Signals.** ffmpeg 8.0.1 is on this PC [M]. The first four commands were checked in RESEARCH-AND-PLAN §10; the burst and inter-sample commands were run and measured today.

```powershell
ffmpeg -f lavfi -i "aevalsrc=0.1*sin(2*PI*1000*t):s=48000:d=5"    tone_m20.wav    # -20.0 dBFS
ffmpeg -f lavfi -i "aevalsrc=0.0316*sin(2*PI*1000*t):s=48000:d=3" a_m30.wav
ffmpeg -f lavfi -i "aevalsrc=0.708*sin(2*PI*1000*t):s=48000:d=3"  b_m3.wav
ffmpeg -i a_m30.wav -i b_m3.wav -filter_complex "concat=n=2:v=0:a=1" step_m30_to_m3.wav   # -30 then -3
ffmpeg -f lavfi -i "aevalsrc='if(between(t,1,1.05),0.891*sin(2*PI*1000*t),0)':s=48000:d=3" burst_1k_m1.wav
ffmpeg -f lavfi -i "aevalsrc='0.891*sin(2*PI*12000*t+PI/4)':s=48000:d=3" isp_12k_tp_m1.wav
ffmpeg -i FILE.wav -af volumedetect -f null -                  # sample peak (max_volume)
ffmpeg -i FILE.wav -af "ebur128=peak=true+sample" -f null -    # true peak
```

Measured today [M]:

- The burst file has a sample peak of -1.0 dBFS and a true peak of -1.0 dBFS (a 50 ms burst).
- The inter-sample file has a sample peak of -4.0 dBFS. Its real true peak is -1.0 dBFS, but ffmpeg reads -0.4: ffmpeg over-reads this 12 kHz case by 0.6 dB. Use a ±0.7 dB tolerance on true-peak results.

**Instrument.** `SoundCeiling.Measure` records loopback on Headphones. Microsoft says loopback taps the audio "prior to any volume or muting being applied" [V2]. So loopback is what the output receives after the APOs [I → the M2 load-signature test] and before the slider, and ear level = loopback + V.

**Expected loopback peaks.** For reading (b) with X = -28 and R = 10, T is -28.0 at slider 100, -18.0 at 50 and -18.0 at 20. Each cell gives loopback / ear in dB.

| Signal | Slider 100 | Slider 50 | Slider 20 |
|---|---|---|---|
| tone -20 | -28.0 / -28.0 | -20.0 / -30.2 (not limited) | -20.0 / -43.7 |
| step -30 → -3 | -30 → -28.0 / at most -28.0 | -30 → -18.0 / at most -28.2 | -30 → -18.0 / at most -41.7 |
| burst -1, 50 ms | at most -28.0 from the first sample | at most -18.0 | at most -18.0 |
| inter-sample 12k (true peak -1) | true peak at most -27.3 | true peak at most -17.3 | true peak at most -17.3 |

For reading (a) with C = -10, every slider position gives the same loopback: tone -20 → -20 (untouched), step → -30 then -10, burst at most -10. Ear level = loopback + V.

Pass criteria: sample peak ≤ T, true peak ≤ T + 0.7 dB, and **no sample above T at the start of the burst**.

**Coverage and bypass tests**

| # | Test | Expected |
|---|---|---|
| C1 | Zoom "Test speaker" and a live call | APO input shows activity; the message names Zoom |
| C2 | YouTube in Chrome and in Edge | Same, naming Chrome or Edge |
| C3 | Windows system sounds | Limited (same chain) |
| C4 | Measure tool opens Headphones in exclusive mode | Box unticked: opening fails, so nothing can bypass. Box ticked: WARN, a documented bypass |
| C5 | Spatial sound (Windows Sonic) on | Does the APO still see the audio? [U] If not, WARN whenever spatial sound is on |
| C6 | Plug in a USB headset, or connect Bluetooth | "Unprotected output" WARN within 5 s; after protecting it, it is covered |
| C7 (optional) | Jump the slider from 20 to 100 while the loud step plays. Needs a 3.5 mm cable from headphone out to line or mic in | Measured overshoot at most min(R, jump), for at most 30 ms |
| C8 | Sleep and resume; unplug and replug; Audiosrv restart | The heartbeat returns and no WARN remains |

## 7. Rollback

- **The one-step undo** (written and rehearsed in M1, before the first registration; used for real at the end of M2, before the Headphones are touched): `SoundCeiling Undo` on the desktop, one double-click and one admin prompt. It:
  - writes back the exact before-values (type and data) of every registry value the installer touched, and deletes the values that did not exist before;
  - removes the COM and plug-in registration;
  - deletes `DisableProtectedAudioDG` only if gate A6 set it;
  - restarts the audio services in the order of section 4.2 item 5 (running the undo is your OK for that restart);
  - compares the registry with the M0 snapshots and prints "restored" or the differences.
- **During a call, if audio misbehaves:** turn the tray app Off, which ramps the limiter out. If that is not enough, run the one-step undo. Without admin rights at hand: unplug the headphones, or go to Settings → System → Sound → Headphones → Audio enhancements → Off. That switches off the effects on that output, Realtek's included; whether it also bypasses our plug-in is [U] (Microsoft's text covers INF-installed effects [V41]), and M2 tests it. Switch it back on later.
- **Our APO, full removal:** `SoundCeiling.Admin uninstall` is the one-step undo plus deleting the files. It does the following:
  - removes our CLSID from `,15`, or restores the snapshot;
  - unregisters the COM and APO entries;
  - restores `DisableProtectedAudioDG` to its previous state (not set on this PC [M]);
  - deletes the repair task and the ProgramData folder;
  - restarts the audio services.

  For the tray app, remove its HKCU Run entry and delete its folder.
- **Equalizer APO:** Settings → Apps → Equalizer APO → Uninstall. It restores the original effect values and deletes the key [V10][V11]. Reboot, then delete `LoudMax64.dll`.
- **Manual fallback:** import `render-before.reg` (admin), then restart AudioEndpointBuilder.
- **Last resort:** reinstall the Realtek driver. It rewrites the effect settings from its INF; endpoint GUIDs may change [V28].
- **Plan B (Voicemeeter):** uninstall it, reboot [V22], and set Headphones as the default device again.

## 8. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | Protection silently gone. Causes: a driver or Windows update rewrites the effect list [V28][V17]; Windows disables effects after 10 APO failures [V3]; someone turns "Audio enhancements" off; another program edits the list (Users can write it here [M]) | Likely in long use | High | Health check and heartbeat; repair task; emergency cap; DOWN icon |
| 2 | `DisableProtectedAudioDG = 1`, if it turns out to be needed. Microsoft calls it a test key [V3]. Apps needing a secure (DRM) audio path may refuse to output audio [V16]. A future update could stop honouring it | Medium | High | Try without it first (M2); set it only on your OK (gate A6); DRM test. Plan B (Voicemeeter) needs no key |
| 3 | A third-party APO on a composite Realtek + Sound Research chain: loading and order are undocumented [U] | Medium | High | Load test on a harmless output (M2); order and slider checks on the Headphones (M3). Fallbacks: the single EFX slot, wrapping the vendor chain, or Plan B |
| 4 | Our APO crashes or misbehaves inside audiodg: audio drops, and 10 failures disable every effect | Medium during development | High | Bench tests in a test host first (M1); harmless output first (M2); SEH guard (Windows' structured exception handling); crash-loop guard; tolerant format negotiation; one-step undo rehearsed before the first registration. Known traps: COM aggregation, sandbox ACLs, cached endpoint settings [V27] |
| 5 | Bypass: exclusive mode, spatial or bitstream formats, unregistered new outputs | Low-medium | High | Untick exclusive mode; keep spatial sound off, with a WARN; device watcher |
| 6 | Reading (b) slider-jump leak | Certain on big jumps | Low-medium | The R cap, or reading (a) if you want zero leak |
| 7 | Heavy limiting sounds squashed or pumps | Likely at X = -28 on Zoom | Medium | Section 4.6 settings; listening test; raise X; optional pre-stage |
| 8 | A Windows feature update breaks the APO. This happened to Dirac's APO on 24H2: complete loss of audio [reported, V31] | Low-medium per update | High | Health check; uninstall path; Plan B ready |
| 9 | Toolchain gaps: no WDK and no ATL on this PC [M] | Low | Low | Neither is needed: the base class is in the SDK, and the COM code is hand-written [V39]. Route B or Plan B as fallbacks |
| 10 | Expecting dB SPL | Certain | Medium | The UI says "dB below device max"; optional phone calibration |
| 11 | A bug sends loud noise to the headphones, or a NaN that the driver turns into a full-scale click [I] | Low | High | Gain never above 1; final clamp with NaN → 0; development clamp at -20 dBFS; harmless output first, then headphones off the head at slider 10% or less |
| 12 | A crash inside processing repeats at every audio restart, because Windows' 10-failure rule does not count it [V3][I] | Low-medium during development | Medium | Crash-loop guard; one-step undo; the tray shows DOWN |
| 13 | The sandbox blocks the config or telemetry files, which looks like "not loaded" [V27] | Medium | Medium | Explicit ACL (4.2 item 2); the M2 load test is measured in loopback and needs no files |
| 14 | DRM-protected playback without the key: the plug-in is refused for that graph [V42], and each refusal counts towards the 10-failure switch-off [V3] | Depends on your use | Medium | DRM test in M2 and M3; the health check watches the switch-off flag; key only on your OK |

## 9. Decisions and approvals for Ariel (in order)

**Decisions**

- **D1.** Which reading of X: (b), a ceiling at your ear with R (recommended), or (a), a fixed digital ceiling.
- **D2.** The starting values: X = -28 dB below device max (from your 20% experience, tuned with "set ceiling from now"), and R = 10.
- **D3.** Whether you accept `DisableProtectedAudioDG = 1` if, and only if, the no-key load in M2 fails. Route C may not need it (section 13). If you say no then, go to Plan B (Voicemeeter).
- **D4.** Dropped by the audit: the Equalizer APO trial is no longer recommended (section 13). It stays an optional diagnostic.
- **D5.** Untick exclusive mode on Headphones (recommended; in M5, gate A9).
- **D6.** Which outputs to protect: Headphones only (default), plus Speakers, plus the monitors; and whether new outputs are protected automatically.
- **D7.** The emergency volume cap while protection is DOWN (recommended: on).
- **D8.** True-peak detection (recommended: on).
- **D9.** Whether Off stays off until you turn it on, or turns itself back on after 30 minutes.

**Approvals, in the order they come up** (renumbered by the audit; each is asked on its own, when its turn comes)

| # | When | What | Admin | Restart or reboot | What changes | Undo |
|---|---|---|---|---|---|---|
| A1 | M0 | Export registry snapshots to files | No | No | Nothing (read only; writes two files) | Delete the files |
| A2 | M1 | Build and bench-test; run `SoundCeiling.Measure`; play test tones at low volume | No | No | Nothing | None needed |
| A3 | M2 | Copy the plug-in to `C:\Program Files\SoundCeiling\`, register its COM class and plug-in entry, and create `%ProgramData%\SoundCeiling` with its ACL | Yes | No | `HKLM\SOFTWARE\Classes` (CLSID and AudioEngine entries); two folders. Nothing is audible until an output lists the plug-in | One-step undo |
| A4 | M2 | Add the plug-in to one monitor output's effect list | Yes | No | That monitor's `FxProperties` `,7` and `{d3993a3f…},7`, both empty today [M] | One-step undo |
| A5 | M2, M3, and after each new build | Restart the audio services | Yes | Audio restart: sound stops briefly | Nothing lasting | None needed |
| A6 | M2-K, only if the no-key load failed | Set `DisableProtectedAudioDG = 1` (the test key); the reboot after it is asked separately | Yes | Reboot | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio` | Delete the value, reboot |
| A7 | M3 | Append the plug-in to the end of the Headphones EFX list | Yes | Audio restart (A5) | Headphones `{d04e05a6…},15` | One-step undo |
| A8 | M4 | Tray app starts with Windows (if it does not already) | No | No | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | Untick it in the tray menu |
| A9 | M5 | Untick exclusive mode on Headphones | No [I] | No | One endpoint property | Tick it again |
| A10 | M5, optional | SYSTEM repair task; emergency cap; more outputs | Yes | No | Task Scheduler; `,15` on other outputs | Delete the task; one-step undo |

The C++ build tools (old A7) are approved and installed (section 12). The Equalizer APO steps (old A3, A4 and A6) left the main path; they come back only with the optional diagnostic in section 5, each with its own OK.

## 10. Evidence log

All web sources were accessed 2026-11-09 to 2026-11-10. "Reported" means a third-party or secondary source; I did not verify the claim itself.

| # | Claim | Status | Source |
|---|---|---|---|
| V1 | Three APO slots: SFX, MFX, EFX. "An endpoint effect is always applied, even to raw streams." SFX is not used for raw streams | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture |
| V2 | Windows 11 APO notifications include endpoint volume (fMasterVolume), and the first one delivers the current state. Build 22000+. Loopback taps "prior to any volume or muting". New APOs shipped with Windows 11 devices must pass HLK tests | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects |
| V3 | Real-time rules for APOs. An embedded manifest works with DisableProtectedAudioDG=1 but "when this test key is removed, your APO will fail to load, even if it is WHQL-signed". 10 load failures make Windows set `PKEY_Endpoint_Disable_SysFx = 1`. Example registration values. Componentized APOs are per driver | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects |
| V4 | `PKEY_CompositeFX_EndpointEffectClsid` is `,15`, a multistring allowing several effects in one slot, from Windows 10 1803. Precedence over the single slot: not documented. The order inside the list: not documented (checked again by the audit) | Verified / Unknown | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-endpointeffectclsid |
| V5 | From Windows 10 1607 with Secure Boot on, kernel drivers must be signed by Microsoft | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later- |
| V6 | WDK 28000.2526 goes with VS 2026, WDK 26100.6584 with VS 2022; a WDK NuGet package exists | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk |
| V7 | 24H2 and 25H2 "share a common core operating system with an identical set of system files" | Verified | https://support.microsoft.com/en-us/help/5054156 |
| V8 | Equalizer APO: GPLv2; "Windows 7 - 11 have been tested"; project last updated 2025-11-28 | Verified | https://sourceforge.net/projects/equalizerapo/ |
| V9 | EAPO 1.4.2 released 2025-03-21; x64 installer 12.0 MB. Changelog: 1.3 made SFX/EFX the default; 1.3.1 restarts audio services; 1.2 added loudness correction and VST | Verified | https://sourceforge.net/projects/equalizerapo/files/1.4.2/ |
| V10 | EAPO installer sets `DisableProtectedAudioDG=1` under `HKLM\Software\Microsoft\Windows\CurrentVersion\Audio` and deletes it on uninstall; gives Users full access to the config folder; has a VSTPlugins folder | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/Setup/Setup.nsi |
| V11 | EAPO writes only the single slots `,1 ,2 ,5 ,6 ,7` and never the composite `,13-,15`; defaults to SFX/EFX when composite keys exist; code comment: Windows 11 Bluetooth combined devices, "EFX will not work then" | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/DeviceAPOInfo.cpp |
| V12 | EAPO VST syntax `VSTPlugin Library … ChunkData … <Param> <value>`; VST2 SDK header only; a plugin crash passes audio through; plugin latency is ignored | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/filters/VSTPluginFilterFactory.cpp , `…/filters/VSTPluginFilter.cpp`, `…/helpers/` |
| V13 | EAPO watches its config folder and crossfades about 10 ms on change; a missing config results in pass-through | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/FilterEngine.cpp |
| V14 | EAPO reads the endpoint volume in dB inside the APO (IAudioEndpointVolume, default device) | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/filters/loudnessCorrection/VolumeController.cpp |
| V15 | EAPO install test: it restarts AudioSrv and the APO reports back over a named pipe; it asks for a reboot when it fixed the key | Verified | `…/DeviceSelector/DeviceTestThread.cpp`, `…/DeviceSelector/DeviceSelector.cpp` (same repository) |
| V16 | DisableProtectedAudioDG "disables the signature check for APOs"; "applications requiring a secure audio path may … refuse to output audio altogether"; build dependencies | Verified | https://sourceforge.net/p/equalizerapo/wiki/Developer%20documentation/ |
| V17 | Ticket #341: 1.4.2 stopped working after a 25H2 update (26200.7628), fixed by a restore; a helper could not reproduce. Ticket #364 (Nov 2026): the developer "is seldom active" | Verified (forum) | https://sourceforge.net/p/equalizerapo/tickets/341/ , https://sourceforge.net/p/equalizerapo/tickets/364/ |
| V18 | Windows 11 with Realtek driver 6.0.9244.1 worked with EAPO 1.4 in SFX/EFX mode (2024-10-19) | Verified (forum) | https://sourceforge.net/p/equalizerapo/discussion/general/thread/69c07605f6/ |
| V19 | LoudMax 1.47 (2026-06-23): freeware, no redistribution; VST2 32/64-bit and VST3 64-bit; look-ahead 1.25 ms; inter-sample detection; automatic release | Verified | https://loudmax.blogspot.com/ |
| V20 | Signalsmith Basics: MIT licence, limiter with up to 100 ms look-ahead | Verified | https://github.com/Signalsmith-Audio/basics |
| V21 | Voicemeeter Sep 2026: Green limiter with 5 ms latency, Yellow in real time, on every bus at 0 to -20 dB, `Bus[i].limit`, versions 3.1.3.0 / 2.1.3.0 / 1.1.3.0 | Verified | https://voicemeeter.com/voicemeeter-updates-september-2026/ |
| V22 | Voicemeeter Standard 1.1.3.0 (Sep 2026): donationware; admin install and reboot; bus "Integrated Limiter and Peak Remover" | Verified | https://vb-audio.com/Voicemeeter/ |
| V23 | VB-CABLE pack 46 (Nov 2026): donationware; XP to Windows 11 and ARM64; admin and reboot; signing not stated | Verified | https://vb-audio.com/Cable/ |
| V24 | Virtual Audio Cable: Windows 11 supported; paid; no limiter mentioned | Verified | https://vac.muzychenko.net/en/ |
| V25 | VirtualDrivers Virtual-Audio-Driver: beta that "requires test signing" | Verified | https://github.com/VirtualDrivers/Virtual-Audio-Driver |
| V26 | Jabra PeakStop "eliminates potentially harmful sound spikes" (no dB value on the page; 105-118 dB SPL reported elsewhere) | Verified / reported | https://www.jabra.com/supportpages/hearing-protection |
| V27 | Another developer's APO on 24H2 (closed 2026-10-16) hit: a COM aggregation recursion bug; the sandbox (LPAC) blocking shared memory without its SIDs; the endpoint builder caching effect keys; the need for `PKEY_FX_Association`; composite slots working alongside OEM effects. Audit additions: the fix for the sandbox was adding S-1-15-2-1 and S-1-15-2-2 to the shared memory's SDDL; restart order Audiosrv stop, AudioEndpointBuilder stop, AudioEndpointBuilder start, Audiosrv start; the key reported to be read only at boot; a self-signed build loaded (and crashed the same way, from the aggregation bug); a plug-in placed in Temp was skipped | Reported | https://github.com/li589/AuraDSP/issues/5 |
| V28 | Etienne Dechamps' APO notes: EFX runs after mixing and resampling, before Windows' CAudioLimiter; exclusive mode, KS and ASIO bypass the engine; a driver reinstall overwrites the effect settings and endpoint GUIDs can change; the service cannot read `C:\Users`; `Disable_SysFx` is the enhancements checkbox | Reported (expert notes) | https://github.com/dechamps/APO |
| V29 | Windows' audio engine contains CAudioLimiter, and the device meter reads after it (2011) | Reported (Microsoft engineer's blog) | https://matthewvaneerde.wordpress.com/2011/05/11/linearity-of-windows-volume-apis-iaudiometerinformation-and-full-scale-signals/ |
| V30 | Windows-driver-samples: MS-PL licence; VS 2022 + WDK; SYSVAD APO samples SwapAPO, DelayAPO, AecApo, KWSApo | Verified | https://github.com/microsoft/Windows-driver-samples , `…/tree/main/audio/sysvad/APO` |
| V31 | 24H2 plus Dirac's APO (cridspapo.dll) could cause complete loss of audio; the hold was lifted 2025-09-11 | Reported (news) | https://winaero.com/dirac-audio-compatibility-issue-has-been-finally-fixed-in-windows-11-24h2 |
| V32 | `VerifiedAndReputablePolicyState = 0` means Smart App Control is off | Reported (community; Microsoft's page gives no mapping) | https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control |
| V33 | IAudioProcessingObjectNotifications: minimum Windows build 22000; header `audioengineextensionapo.h` | Verified | https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/nn-audioengineextensionapo-iaudioprocessingobjectnotifications |
| V34 | Visual Studio 2026 needs 2.3 to 60 GB depending on features | Reported (search snippet) | https://learn.microsoft.com/en-us/visualstudio/releases/2026/vs-system-requirements |
| V35 | "The system automatically substitutes a software implementation for any function ... that the endpoint device does not implement in hardware" (volume, mute, peak meter) | Verified | https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-queryhardwaresupport |
| V36 | `APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME2` (value 4): "Endpoint volume notification for an endpoint that includes master and channel volume, in dB". `AUDIO_VOLUME_NOTIFICATION_DATA2` has `masterVolumeInDb`, `volumeMinInDb`, `volumeMaxInDb`, `volumeIncrementInDb`, `step`, `stepCount` and `channelVolumesInDb[]` | Verified | https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/ne-audioengineextensionapo-apo_notification_type , https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/ns-audioengineextensionapo-audio_volume_notification_data2 |
| V37 | `IAudioProcessingObjectNotifications2::GetApoNotificationRegistrationInfo2`: minimum build 22621; before 22621 Windows calls only the old method. Its sample: "The OS will immediately fire a notification for the above notification types, so we do not need to query the OS for the current values" | Verified | https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/nf-audioengineextensionapo-iaudioprocessingobjectnotifications2-getaponotificationregistrationinfo2 |
| V38 | `AUDIO_VOLUME_NOTIFICATION_DATA.fMasterVolume` is "normalized to the range from 0.0 to 1.0 ... described by a nonlinear, audio-tapered curve", so it is not dB | Verified | https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/ns-endpointvolume-audio_volume_notification_data |
| V39 | Equalizer APO's own plug-in class: `class EqualizerAPO : public CBaseAudioProcessingObject, public IAudioSystemEffects, public INonDelegatingUnknown`, constructor `EqualizerAPO(IUnknown* pUnkOuter)`, delegating and non-delegating IUnknown methods, no ATL | Verified (source, from a GitHub fork of the official code) | https://raw.githubusercontent.com/TheFireKahuna/equalizerAPO64/main/EqualizerAPO/EqualizerAPO.h |
| V40 | Settings framework: `IAudioSystemEffectsPropertyStore` "is readable and writable by an ISV/IHV service, a UWP store application, non-admin desktop applications, and APOs. Additionally, this can act as the mechanism for APOs to deliver messages back"; Default, User and Volatile stores. HLK requires the CAPX APIs for APOs that ship with devices (certification, not loading) | Verified (text); not verified for a plug-in registered without an INF | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects |
| V41 | `PKEY_AudioEndpoint_Disable_SysFx` "applies only to the local-effects and global-effects APOs that were installed by the .inf file that installed the audio adapter" and "affects only the target endpoint device" | Verified | https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-disable-sysfx |
| V42 | CoCreateInstance failure reasons include "The graph is running protected content, and the APO is not properly signed". The failure count tracks CoCreateInstance, IsInputFormatSupported, IsOutputFormatSupported and LockForProcess, resets on success, limit 10. `CBaseAudioProcessingObject` is declared in Baseaudioprocessingobject.h; the interface headers are in `Windows Kits\<build>\Include\um`. An embedded manifest triggers APIs "forbidden within a protected environment" | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects |
| V43 | SwapAPO sample: `PlatformToolset` WindowsApplicationForDrivers10.0 (a WDK toolset) and `$(DDK_INC_PATH)`; links AudioBaseProcessingObjectV140.lib, audiomediatypecrt.lib, AudioEng.lib, rtworkq.lib; its classes use ATL (`CComObjectRootEx`, `CComCoClass`) with `CBaseAudioProcessingObject`, IAudioProcessingObjectNotifications and IAudioSystemEffects3 | Verified | https://raw.githubusercontent.com/microsoft/Windows-driver-samples/main/audio/sysvad/APO/SwapAPO/SwapAPO.vcxproj , `…/SwapAPO.h` |
| V44 | Equalizer APO Setup.nsi: install writes `DisableProtectedAudioDG` = 1 and uninstall deletes it; installs to `$PROGRAMFILES64\EqualizerAPO`; grants Users (S-1-5-32-545) FullAccess on the config folder; registers with regsvr32 | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/Setup/Setup.nsi |
| V45 | Equalizer APO DeviceAPOInfo.cpp: writes only `,1 ,2 ,5 ,6 ,7`, never `,13-,15`; adds `{d3993a3f…},5/6/7` = DEFAULT when absent; deletes `{1da5d803…},5` on install ("force-enable enhancements"); comment "bluetooth devices may be combined in Windows 11, EFX will not work then" | Verified | https://sourceforge.net/p/equalizerapo/code/ci/main/tree/DeviceAPOInfo.cpp |
| V46 | A throwaway spike on Windows 11 (build not given): "It loads unsigned with `DisableProtectedAudioDG` at 0 and at 1", checked by a measured gain change | Reported | https://github.com/SilvunDev/goxlr-hub/pull/9 |
| V47 | Other own-APO projects set the key and call it required (VolumeX, SonicLift). VolumeX: 5 ms look-ahead brickwall, `Global\` shared memory, DLL in `C:\Program Files\VolumeX\Engine\`, "Some Windows updates reset the APO policy". SonicLift: "Updates/driver installs can reset FxProperties". A community script for exclusive mode writes `{b3f8fa53-0004-438e-9003-51a46e139bfc},3` and `,4` | Reported | https://github.com/ferhad24/VolumeX , https://github.com/denoso1993/soniclift , https://techcommunity.microsoft.com/discussions/windowspowershell/disable-exclusive-mode-for-all-audio-devices-via-ps/3653408 |
| V48 | Vista design: "The DRM system in Vista requires that the audio samples be processed in a protected process" (Larry Osterman, Microsoft, 2007). A reader's comment: the protection can be switched off with a registry key, "Of course protected content won't play". PUMA is "the user-mode audio engine in the Protected Environment" | Verified (post) / reported (comment) | https://learn.microsoft.com/en-us/archive/blogs/larryosterman/what-is-audiodg-exe , https://learn.microsoft.com/en-us/windows/win32/coreaudio/protected-user-mode-audio--puma- |
| V49 | `{D04E05A6…},13` is `PKEY_CompositeFX_StreamEffectClsid` (SFX) and `,14` is `PKEY_CompositeFX_ModeEffectClsid` (MFX), each "a composite that is expressed as a reg multistring in the registry to allow multiple effects in a single position". "The INF file property key instructs the audio endpoint builder to set the CLSIDs for endpoint APOs into the effects property store", so a driver reinstall can write the lists again [I; reported in V28, V47]. None of the three pages says in which order the listed effects run | Verified / Unknown | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-streameffectclsid , https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-modeeffectclsid |
| M (audit) | This PC, read-only, 2026-11-10: audiodg `PS_PROTECTION` 0, signature, image-load and dynamic-code policies 0; Audiosrv runs as LocalService and is audiodg's parent (session 0); the composite values are REG_MULTI_SZ; FxProperties SDDL with `(A;CIID;CCDC;;;BU)`, `lpacMedia` read and `audioDeviceConfiguration` set-value (capability SIDs derived with DeriveCapabilitySidsFromName); the monitors' `,7` is empty; SDK 10.0.26100.0 with `baseaudioprocessingobject.h`, `audioenginebaseapo.h`, `audioengineextensionapo.h` and the libraries `audiobaseprocessingobject.lib`, `AudioBaseProcessingObjectV140.lib`, `audioeng.lib`, `audiomediatypecrt.lib`; MSVC 14.44 `cl.exe`; no WDK and no `atlbase.h`; APO_FLAG_DEFAULT = 0xE; `C:\Program Files` gives read and execute to Users and both app-package groups, `C:\ProgramData` has no app-package entries; keys under `HKLM\SOFTWARE\Classes` inherit read for app containers and `registryRead`; the Realtek and Sound Research DLLs are WHQL-signed | Measured | Commands listed in `PLAN-AUDIT.md` |
| M | This PC (read-only, 2026-11-09 and -10). Composite FX keys and their CLSIDs, DLL paths, versions and WHQL signer on both Realtek outputs. `DisableProtectedAudioDG` absent in both candidate keys. SAC policy 0; HVCI running; UMCI 0; Secure Boot 1. Windows 25H2 build 26200.9711. HP Z2 G8 Tower; standard HDA controller. No EAPO, VB-Audio, Voicemeeter or VAC installed. `FxProperties` ACL as in 4.1. ffmpeg burst and inter-sample peaks as in section 6. Slider dB, 10 ms period, Zoom levels and meter position from RESEARCH-AND-PLAN §14 | Measured | Local checks |
| I | Slider fit dB ≈ 34·log10(s); about 10-30 ms of audio in flight; no vendor effect applies the slider digitally (fits F2); loopback is after the APOs; offload absent; Zoom used shared mode (its session meter showed real levels, and software meters read 0 in exclusive mode, RESEARCH-AND-PLAN F3); a crash inside APOProcess is not counted by the 10-failure rule; a NaN can become a full-scale sample in integer conversion | Inferred | Tests named in sections 5 and 6 |

## 11. Corrections to RESEARCH-AND-PLAN.md and to the brief

1. **"The Realtek Headphones and Speakers endpoints have no effects (APOs) registered" is wrong.** Both carry Realtek SFX, MFX and EFX plus a Sound Research EFX, in the newer *composite* keys `,13 ,14 ,15` (plus offload keys `,19 ,20`) [M]. The earlier check probably looked only at the single-slot keys. This also explains the missing Enhancements tab: the vendor's own app replaces it.
2. **`DisableProtectedAudioDG` lives under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio`,** not `HKLM\SYSTEM\CurrentControlSet\Control\Audio` as the brief says [V10][V16]. It is not set in either place on this PC [M].
3. **Section 5, O4 ("own APO … needs a signed driver package … not realistic").** I disagree. The mechanism Equalizer APO uses (registry entries plus the test key) is open to us too, and is my recommendation. Signed packaging is needed only to avoid the key.
4. **F4 (loopback vs. master volume "not stated by Microsoft").** Microsoft now states it: loopback is taken before volume by default [V2].
5. **Section 8's lean towards O3 (virtual cable plus our own limiter).** I prefer the APO: no clock drift, no silence on a crash, and about 5 ms instead of tens of ms. Voicemeeter now has a look-ahead bus limiter [V21], which makes it a better Plan B than building our own router.
6. **Monitor outputs.** Microsoft's effects there sit in the SFX/MFX slots (`,5 ,6`), although their names say "LFX/GFX" [M]. Minor.
7. **Equalizer APO.** "VST2 only" and "no limiter" are confirmed from source [V12]. GPLv2 is now verified [V8]. Add ticket #341 (25H2, January 2026) to the known update breakages [V17]. A non-admin program *can* write its config folder [V10].
8. **VB-CABLE signing.** The vendor still does not state it, but with Secure Boot on the driver must be signed by Microsoft to load at all [V5][I].
9. **The brief's stack.** C#/.NET 10/WinForms/NAudio stays right for the tray app. The in-path part must be native C++, because audiodg loads COM DLLs, so a C++ toolchain is added.
10. **The brief's "endpoint meter reads before the master volume"** is consistent with Microsoft. Whether it reads before or after an inserted APO is still undocumented. I infer "after EFX" [V28][V29] and test it in M2 step 4 (the load test; it was T-M1-2 before the audit). Our design does not depend on it, because the APO reports its own input and output peaks.

## 12. Decisions so far (added 2026-11-10)

- **Route chosen:** Ariel prefers our own limiter (route C, the APO) over Voicemeeter (plan B), after being told that it needs `DisableProtectedAudioDG = 1` (the "test key"). This settles the route question in section 1. It is not a go-ahead for any system change.
- **Still needs an explicit go, each when its turn comes (section 9, numbers as renumbered by the audit):** copying in and registering the plug-in (A3); adding it to one monitor output (A4); each audio-service restart (A5); the test key, only if the no-key load fails, with its reboot asked separately (A6); the Headphones effect list (A7); starting with Windows (A8); unticking exclusive mode (A9); the optional SYSTEM repair task, emergency cap and more outputs (A10).
- **C++ build tools: approved by Ariel and installed (2026-11-10).** Build Tools for Visual Studio 2022 17.14 with MSVC 14.44 and the Windows SDK 10.0.26100.0. No WDK and no ATL. The SDK holds the plug-in base class header and its libraries [M, audit file listing]. A test DLL deriving from the base class compiled and linked [reported by the install; not re-run by the audit].
- **State of this PC before the approved install (read-only checks, 2026-11-10):** Visual Studio Community 2022 17.2 is installed with managed, web and Office workloads only: no MSVC toolset and no Windows SDK (so no `audioenginebaseapo.h`). `winget` is available and offers Build Tools 17.14.44. 404 GB free on C:.
- **Built so far:** the tray app (monitor, cap slider, test tone) is running from `dist\SoundProtection.exe`; its plan and ledger are in `docs\superpowers\plans\`. It reads Windows' meters from outside and cannot hold sound back; the limiter plug-in is what will.

## 13. Audit changes (2026-11-10)

The audit's findings, sources and open questions are in `PLAN-AUDIT.md`. The text before the audit is saved as `PIPELINE-LIMITER-PLAN.before-audit.md`. Each change below names where it is and why it was made.

1. **The test key is tried last, not first** (sections 1, 3, 4.2 item 5, 5 M2 and M2-K, 8 row 2, 9 D3 and A6). Why: Microsoft names "the graph is running protected content, and the APO is not properly signed" as the load failure [V42], which ties the signature check to DRM playback. `audiodg` runs with no process protection and no signature policy [M]. One project reports an unsigned plug-in loading with the key at 0 [V46]; others call the key required [V47]; Equalizer APO's installer always sets it, so its wide use proves nothing either way [V44]. So whether build 26200 needs the key is unknown, and the cheapest way to find out is a no-key load on an output where a mistake cannot hurt. The key is asked for only if that load fails, and its reboot is a separate OK.
2. **A harmless output comes first** (new M2: a monitor's audio output with nothing plugged in). Why: its effect slot is empty, so nothing of the vendor's is displaced [M], and a bug there cannot reach your ears.
3. **The Equalizer APO trial left the main path** (old M1, now an optional diagnostic; sections 1, 3 note A, 9 D4 and old A3, A4, A6). Why: it cannot answer the key question [V44]; it uses the single slots, not the composite slots our plug-in uses [V45], so it tests neither our load path nor our order; and it costs two installs, the test key and a reboot.
4. **New bench milestone M1** (build and test inside a test program; nothing registered). Why: the known traps (COM aggregation recursion [V27], NaN and format changes) can be found without changing the PC.
5. **One-step undo written and rehearsed before the first registration** (section 7, M1, end of M2). Why: the brief requires it, and it is the fastest exit if a live test goes wrong. It is rehearsed on a scratch copy of the keys, then used for real on the monitor before the Headphones are touched.
6. **Ear-safety rules in the code** (4.2 item 1, 4.5, 8 rows 11 and 12): gain never above 1; a final clamp that also turns NaN into 0, compiled with `/fp:precise`; denormals flushed with the floating-point mode restored; a -20 dBFS clamp in development builds; a crash-loop guard. Why: a crash during processing is not counted by Windows' 10-failure rule [V3][I], and a NaN could become a full-scale click in the driver [I].
7. **Stricter first live tests** (section 6, M3): harmless output first; then headphones off your head, slider at 10% or less, the -20 dBFS tone only, and the development clamp. Why: the brief's safety rule.
8. **The volume notification is the dB form** (sections 2 and 4.2 item 1): `APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME2`, `masterVolumeInDb`, through `IAudioProcessingObjectNotifications2` [V36][V37]. Why: the plan cited the older notification, whose value is a 0-to-1 audio-tapered number, not dB [V38]; used as dB, it would set the wrong ceiling.
9. **The ceiling tightens at once and relaxes slowly** (sections 2, 4.2 item 2, 4.6). Why: the plan ramped every change over 100 ms, which would let a slider jump up exceed X for the whole ramp, longer than the leak section 2 describes.
10. **Where the slider sits** (sections 1 item 2, M0 step 5, M3 step 2). Why: this output does its volume in hardware (mask 0x3 [M]), and Windows adds software volume only where the hardware has none [V35]. So "the plug-in sees the sound before the slider" now rests on a measurement and a Microsoft statement, with a zero-code check in M0 and the decisive check (the plug-in's own input meter at 10% and 20%) in M3.
11. **The order inside the composite list is marked unknown** (4.2 item 1, M3 step 3). Why: the plan said "list order = run order; verified in M2", but Microsoft documents the list, not its order [V4], and nothing had verified it. M3 now checks it with a vendor effect that changes the level.
12. **SDK, not WDK; hand-written COM** (4.2 item 1, M1, 8 row 9, 12). Why: the base class header and its libraries are in the installed SDK [M]; ATL is absent [M], so the COM code follows Equalizer APO's pattern [V39]; SwapAPO needs a WDK project type [V43], so it is read, not built. Flags 0xF match the Sound Research EFX on this PC [M].
13. **Where files live and who can read them** (4.1, 4.2 items 2 and 3): DLL in Program Files, whose ACL already admits app containers [M]; an explicit ACL on `%ProgramData%\SoundCeiling`, whose default has no app-container entries [M], read-only for audiodg and app containers except a `telemetry` subfolder, so no sandboxed app can raise the ceiling; the effects property store as a Microsoft-supported alternative [V40]. Why: `audiodg` probably reads from a sandbox (LPAC): another developer hit it [V27], and the effect-list key grants read to the `lpacMedia` capability [M].
14. **The load test needs none of our files** (M2 step 4): a test build halves the sound, measured in loopback with the monitor's slider at 100%, with `tasklist /m` to tell "not loaded" from "loopback taken before the plug-in". Why: a sandbox problem with the files must not look like "not loaded" [V27], and the monitor's software volume (mask 0x2 [M]) must not blur the reading.
15. **"Audio enhancements: Off" may not bypass our plug-in** (4.5, 7, M2). Why: Microsoft's text limits that switch to effects installed by the driver's INF [V41]. M2 tests it; the rollback text no longer promises it.
16. **Restart order** (4.2 item 5): stop Audiosrv, stop AudioEndpointBuilder, start AudioEndpointBuilder, start Audiosrv; restarts only on your OK. Why: the endpoint builder caches effect settings [V27].
17. **Every change is a gate with an undo** (section 5 intro, section 9 table A1-A10 with an Undo column). Exclusive mode moved from M0 to M5 (A9), because it changes the PC; restarts (A5) and the reboot after the key are their own OKs; fault injection in M5 is its own OK. Why: the brief's rule that nothing changes without its own yes.
18. **Less building for version 1** (4.2 item 6, M5, 9 A10): the SYSTEM repair task and the emergency cap became optional; version 1 has a Repair button that asks for admin. Why: a SYSTEM task is a lasting system-wide change that version 1 does not need.
19. **Emergency cap margin X − 1 → X − 3 dB** (4.5). Why: between samples, content below 12 kHz can rise up to 3 dB above the digital peak (section 2), so 1 dB was not enough.
20. **What stays unprotected, and an honest panel** (4.7, M4). Why: the brief asks the panel to say so. DRM playback without the key, exclusive mode, ASIO and kernel streaming, Off, DOWN, and gain after the engine are listed; the panel never shows "Protected" from settings alone.
21. **Risks added** (8 rows 11 to 14): a loud-noise bug, a crash repeating at every restart, the sandbox hiding the files, DRM refusals counting towards the switch-off. Rows 2, 3, 4 and 9 updated to the new order.
22. **All time and effort estimates removed** (section 1 item 7 "Effort" → "Order", the Effort column of section 3, milestone durations, "Total", "for two minutes", "one hour", "8-hour", "over months"). Why: your rule. Timings that are part of the design or a test limit (every 2 seconds, within 5 seconds, 100 ms ramps) stay.
23. **Evidence** (section 10): V4 and V27 extended; V35 to V49 added; an audit measurement row; the inferred row updated. Section 11 item 10 points at the M2 load test; section 12 records the installed toolchain and the renumbered gates.
