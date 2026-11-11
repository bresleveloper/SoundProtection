# Sound Protection: in-path loudness ceiling plan

| | |
|---|---|
| Date | 2026-11-10 |
| Status | **Plan for Ariel's review.** Research and read-only checks only. Nothing was installed, built or changed, and no audio was played. |
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
   - It runs before the Realtek hardware volume [I, tested in M2] and adds about 5 ms.
   - It reads the slider inside the audio engine, so a dead tray app cannot relax the ceiling.
   - It reports exact numbers.
   - There is no virtual device, no clock drift, and no silence if the tray app dies.
3. **First, half a day of risk reduction without writing code.** Install Equalizer APO 1.4.2 with the free LoudMax limiter. This proves three things before any C++ is written:
   - a third-party APO loads on this PC's Realtek + Sound Research effect chain on 25H2;
   - protected (DRM) playback still works;
   - how heavy limiting sounds on Zoom.
4. **What X means.** I recommend X as the ceiling at your ear, held at every slider position and computed inside the APO (section 2). A starting value from your own data is **X ≈ -28 dB below device max**: slider 20% plus Zoom's peaks of about -4 dBFS.
5. **What you must approve.**
   - `DisableProtectedAudioDG = 1`, which lets Windows load unsigned APOs. Equalizer APO needs the same setting.
   - Admin installs: Equalizer APO for the trial, then Visual Studio C++ to build ours.
   - Edits to the Headphones effect list.
   - Audio-service restarts or one reboot.
   - Unticking exclusive mode.
   - A repair task that runs as SYSTEM.

   Section 9 has the full list in order.
6. **Biggest risk.** Protection can vanish silently after a driver or Windows update, after Windows switches the effects off by itself, or after someone turns "Audio enhancements" off. The mitigation is a health check (registry plus an APO heartbeat), an auto-repair task, and an optional emergency volume cap.
7. **Effort.** About 9 to 14 working days [I]. If you keep the trial setup, usable interim protection exists after day 1.

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
  - Our APO subscribes to Windows' endpoint-volume notification, available from Windows 11 build 22000. The first notification delivers the current value [V2][V33]. The APO computes T itself.
  - If the APO has no volume value, it assumes the slider is at 100 and uses T = X.
  - Killing the tray app changes nothing.
- A tray app that rewrites the limiter threshold whenever the slider moves would **not** be fail-safe. If the app dies after relaxing T, pushing the slider up breaks the ceiling. I am not offering that design. It is also the only way to get (b) with Equalizer APO and a VST plugin, so route A gets reading (a) only.
- **The one leak in (b).** When the slider jumps up, the sound already on its way to the device plays at the new volume before T tightens.
  - "On its way" is about 10 to 30 ms: one 10 ms engine period [M] plus the device buffer [I].
  - The overshoot is at most the smaller of R and the size of the jump. Volume keys and dragging move in small steps; clicking straight from 20% to 100% is the worst case.
  - With R = 10, the worst case is +10 dB for about 30 ms at most [I]. Test C7 can measure it (optional).
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

| Route | Leak-free on a burst | Zoom + Chrome | What bypasses it | Added latency | If a part crashes or is removed | Install | Signing | Undo | Maintenance and licence | Effort |
|---|---|---|---|---|---|---|---|---|---|---|
| **A. Equalizer APO 1.4.2 + LoudMax 1.47 (VST2)** | Yes: 1.25 ms look-ahead, inter-sample option [V19]. Order relative to the vendor effects: [U] | Yes [V1][I] | Exclusive mode; new devices; driver updates | +1.25 ms [V19] | Plugin crash or missing config: **audio passes unprotected** [V12][V13]. APO dropped: unprotected, unnoticed | Admin, reboot [V15], key = 1 [V10] | Unsigned; needs the key | Uninstaller restores the effect list and deletes the key [V10][V11] | EAPO 1.4.2 is from 2025-03-21; developer "seldom active" [V8][V17]. GPL-2.0. LoudMax: freeware, 2026-06-23 | 0.5-1 day |
| **B. Fork EAPO + our own limiter** | Yes | Yes | As A | ~5 ms | As C, unless EAPO's pass-on-error is changed | As A | As A | As A | Us plus upstream; GPL-2.0 [V8] | 5-8 days |
| **C. Own limiter APO (recommended)** | Yes: 5 ms look-ahead plus safety clipper | Yes; EFX covers every mode [V1] | Exclusive mode (untick it); spatial sound [U]; new devices (repair task) | ~5 ms | Tray app dies: still protected. APO dropped or effects disabled: unprotected but **detected** | Admin once, key = 1, audio-service restart | Unsigned; needs the key [V3] | Script restores a snapshot | Ours | 9-14 days, tray app included |
| **D1. Voicemeeter (Sep 2026)** | "Green" bus limiter: 5 ms look-ahead [V21]. Whether it truly holds the limit: [U, test] | Yes, if its virtual input is the default for all roles | Apps set to the real device; new default devices; exclusive mode on the real device | Tens of ms [U] + 5 | Not running: **silence** [I] | Admin, kernel driver, reboot [V22] | Vendor-signed driver [I][V5] | Uninstall + reboot | Active (Sep 2026) [V21]; donationware, closed source | 0.5 day + 2-3 days for the tray app |
| **D2. VB-CABLE + our own router** | Yes | As D1 | As D1 | 20-40 ms [I] | Router dies: silence | Admin, driver, reboot [V23] | Vendor driver | Uninstall | Cable updated Nov 2026 [V23] | 7-12 days (clock drift) |
| **E1. Headset with a built-in shock limiter** | Yes, in hardware | Anything played on that headset | — | 0 | n/a | Purchase | n/a | n/a | Vendor | 0 |

**Notes per route**

- **A.**
  - The stock build loads VST2 plugins only [V12]. A plugin's parameters are set by name with VST's 0-to-1 values, or as a saved ChunkData blob; EAPO's Configuration Editor is the easiest way [V12].
  - Config edits crossfade over about 10 ms [V13].
  - EAPO registers itself in the *single* effect slots and never writes the *composite* slots where this PC's Realtek effects live [V11][M]. Microsoft does not document whether Windows then runs both, or in which order [U → M1].
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
- **Permissions on the Headphones `FxProperties` key:** owner SYSTEM; Administrators **and Users** have SetValue. Installing therefore needs no ownership change here, but any program can also edit the list (risk 1).

### 4.2 Components

1. **`SoundCeiling.Apo.dll`** (C++). One EFX APO class.
   - Interfaces: IAudioProcessingObject, IAudioProcessingObjectRT, IAudioProcessingObjectConfiguration and IAudioSystemEffects2/3, plus IAudioProcessingObjectNotifications for the endpoint volume [V2].
   - Correct COM aggregation (letting Windows' audio engine wrap the object). Another third-party APO hit a recursion bug here [V27].
   - Float32 audio at any sample rate and channel count. No embedded manifest [V3].
   - The real-time function APOProcess never allocates memory, takes locks, touches files or uses pageable memory [V3].
   - Registered by **appending its CLSID to the end of `,15`** (composite EFX), so it runs after the Sound Research and Realtek effects [I: list order = run order; verified in M2].
   - Fail-safe defaults:
     - no config: enabled, with the built-in ceiling;
     - no volume value: assume the slider is at 100;
     - internal DSP fault: fall back to the safety clipper at T. That distorts but never exceeds T.
2. **Config** in `%ProgramData%\SoundCeiling\config.json`: enabled, X, mode, R, look-ahead, release, true-peak.
   - The tray app writes it. A worker thread in the APO watches the folder, as Equalizer APO does [V13].
   - New values reach the real-time path without locks and ramp over 100 ms.
   - The file's ACL must let `audiodg` read it. The audio service cannot read `C:\Users` [V28] and runs in a less-privileged sandbox, LPAC [reported, V27].
3. **Telemetry** in `%ProgramData%\SoundCeiling\<endpoint>.bin`, a memory-mapped file.
   - Contents: heartbeat counter and time, audio format, V, T, input peak (sample and true), output peak, maximum gain reduction, over count, clipper hits, and the config version in use.
   - The APO worker thread writes it every 50 ms, copying values the real-time thread leaves in locked memory.
   - The ACL needs the sandbox SIDs [V27].
4. **`SoundCeiling.Tray`** (C#, .NET 10 [M], WinForms, NAudio 3.x).
   - Icon states: ON, OFF, ALERT, DOWN.
   - Toggle, ceiling controls, messages, event log (CSV), health check and device watcher.
   - Needs no admin.
5. **`SoundCeiling.Admin`** (elevated; runs only for install, uninstall and repair).
   - Snapshots the effect list to a `.reg` file.
   - Writes the COM and APO registration under `HKLM\SOFTWARE\Classes`, with the value set from Microsoft's sample [V3].
   - Appends our CLSID to `,15`.
   - Sets `DisableProtectedAudioDG = 1` and records the previous state.
   - Sets the ACLs on the ProgramData folder.
   - Restarts AudioEndpointBuilder and Audiosrv. Another developer found that the endpoint builder caches effect settings, so restarting Audiosrv alone may not be enough [V27].
   - Uninstall reverses every step.
6. **Repair task** (Task Scheduler, running as SYSTEM).
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
| Effects not switched off: `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5` absent or 0 | Registry. Windows sets this flag after 10 APO load failures [V3], and "Audio enhancements: Off" sets it [V28] | DOWN |
| `DisableProtectedAudioDG` = 1 | Registry | DOWN at the next audio restart |
| Heartbeat newer than 300 ms whenever an app session on that output is active | Telemetry plus session state | DOWN |
| The APO's slider value matches the tray app's own reading | Telemetry vs. IAudioEndpointVolume | WARN |
| Exclusive mode unticked; spatial sound off | Endpoint properties [U: the exact property is found in M0] | WARN |
| An active output has no APO | Device enumeration | WARN, plus "Protect this output?" |

**On DOWN.** The icon turns red, a notification stays on screen, the event is logged, and the repair task is triggered. Optionally (D7), the tray app applies an **emergency cap**: it lowers that output's master volume to X − 1 dB until protection is back, then restores it. Without a limiter, the digital peak cannot exceed 0 dBFS (apart from the inter-sample overshoot in section 2), so the ear level stays at or below X [I].

**Failure summary**

- Tray app dies: protection is unchanged; only the messages stop. Autostart plus restart-on-crash covers this.
- APO fault: falls back to the clipper at T.
- `audiodg` crash: audio restarts [I]. Ten failed loads make Windows switch off every effect on that output [V3]; the health check detects it.
- A driver update removes the registration: protection is gone until repair. The health check flags it within 2 seconds of the next playback, and the task repairs it.

### 4.6 Speech-friendly limiter settings (defaults, tuned in M3)

| Setting | Default | Why |
|---|---|---|
| Look-ahead (= attack time) | 5 ms | The gain is already down when the peak plays. 5 ms is about one voice pitch period, so the gain moves smoothly [I]. For comparison, LoudMax uses 1.25 ms [V19] and Voicemeeter's Green mode 5 ms [V21]. |
| Hold | 20 ms | Longer than the longest pitch period of a low voice (about 12 ms at 85 Hz), so the gain does not follow individual waves. Gain that does sounds like distortion [I]. |
| Release (how fast the gain recovers) | Automatic: 80 ms after an isolated peak, sliding to 400 ms once limiting lasts more than 200 ms | Quick recovery after a cough or a click, and no pumping on a talker who stays loud [I] |
| Detection | True peak, 4× oversampled | Accuracy (section 2) |
| Stereo link | Full | Same gain on both ears, so the sound does not shift sideways |
| Safety clipper | At T | Hard guarantee. It counts its hits, which should stay at 0 on speech. |
| Parameter changes | 100 ms ramps | No clicks on toggle or slider moves |
| Optional pre-stage | Off | If 10 dB or more of limiting sounds squashed: a gentle 2:1 compressor starting about 10 dB below T. It only turns loud parts down, so it is not normalisation. Decided after M3. |

### 4.7 Which outputs are covered

- **Version 1:** Headphones (Realtek), where Zoom plays [M].
- **Speakers (Realtek):** same chain and same install [M]. Protect them if you use them (D6).
- **The two monitors (NVIDIA HDMI):** volume is applied in software [M]. Whether Windows applies it before or after the EFX slot is [U], and it changes what (a) and (b) mean on those outputs. Test it before enabling them.
- **New outputs (USB, Bluetooth):** they get no APO until registered. The tray app asks "Protect this output?", and the repair task can do it automatically if you choose (D6).
- **Bluetooth limitation:** Windows 11 can merge a headset's two Bluetooth profiles into one output, where EFX may not run (a comment in Equalizer APO's code) [V11]. That would need the MFX slot instead; out of scope for v1.

## 5. Milestones with acceptance tests

Effort figures are my estimates [I], for one developer who has not written an APO before.

**M0. Prepare (0.5 day, no installs)**

1. Approval A1: in the Sound control panel, open Headphones → Properties → Advanced and untick "Allow applications to take exclusive control". Diff the registry before and after to find which property the health check should read.
2. Export a registry snapshot. This only writes a file:

   ```
   reg export "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render" render-before.reg
   ```
3. You: check that Smart App Control shows Off (Windows Security → App & browser control). Open HP Audio Control for two minutes and note anything that looks like a limiter or leveller.
4. Measure the slider's dB value at 50% (`SoundCeiling.Measure` from M1, or the old spike code).
5. Decide D1-D3.

Acceptance: the snapshot is saved, exclusive mode is off, and the decisions are recorded.

**M1. Trial with Equalizer APO + LoudMax (0.5-1 day, reversible)**

Steps:

1. Download `EqualizerAPO-x64-1.4.2.exe` (12.0 MB) from SourceForge [V9] and install it (admin).
2. In Device Selector, tick **Headphones only** and leave the troubleshooting options at their defaults. It picks the SFX/EFX slots here because composite keys exist [V11]. Click OK and let the install test run.
3. Reboot when asked. It asks because it sets the key [V15].
4. Download LoudMax VST2 64-bit v1.47 [V19] and put `LoudMax64.dll` in `C:\Program Files\EqualizerAPO\VSTPlugins\` [I: folder from the installer, V10].
5. In EAPO's Configuration Editor, add VSTPlugin → LoudMax. Turn the inter-sample option on, set Output to C (reading (a), for example -10), and save.
6. Build `SoundCeiling.Measure` (C#, .NET 10, NAudio, about 100 lines; approval A5). It records loopback (the mixed output sent to Headphones) to a WAV, prints the device and per-app meters and the slider dB, and asks the device whether it can offload. It contains no calls that change anything.

Acceptance:

- **T-M1-1.** The install test reports success. `tasklist /m /fi "imagename eq audiodg.exe"` (admin) lists `EqualizerAPO.dll` **and** `RltkAPOU64.dll` and `SEAPO64.dll`, so both kinds of slot load. If the vendor DLLs disappear, the single slot overrode the composite slots; record that.
- **T-M1-2 (measuring-tool check).** Set EAPO's Preamp to -10 dB. The loopback peak should drop by 10.0 ± 0.1 dB, which shows loopback is taken after the APOs [U → this test]. Note whether Windows' device meter drops too; that shows whether the device meter reads before or after an inserted APO [U → this test].
- **T-M1-3.** Play the step and burst files at sliders 20, 50 and 100. Loopback sample peak ≤ C + 0.1 dB, and true peak ≤ C + 0.7 dB.
- **T-M1-4 (DRM).** Play protected content in Edge, for example Netflix if you have it. Sound must play: Equalizer APO warns that apps needing a secure audio path may refuse to output audio [V16].
- **T-M1-5.** Zoom "Test speaker", one real call, and YouTube in Chrome. You listen and judge.
- **T-M1-6.** Toggle by switching an Include file. No click should be audible, and loopback should show no dropout.

Exit: either keep Equalizer APO as interim protection with reading (a) until M3, or uninstall it. Uninstalling is also the rollback test: the registry must match the M0 snapshot and the key must be gone.

**M2. APO skeleton: pass-through with heartbeat (2-4 days)**

Toolchain (approval A7):

- Visual Studio 2026 Community with the "Desktop development with C++" workload. Microsoft gives 2.3 to 60 GB for Visual Studio, depending on what is selected [reported, V34]; the installer shows the exact size.
- The Windows SDK.
- The WDK, or its NuGet package, only if APO headers or libraries turn out to be missing [U]. WDK 28000.2526 pairs with VS 2026, and WDK 26100.6584 with VS 2022 [V6].
- Microsoft's SYSVAD SwapAPO and DelayAPO samples (MS-PL licence) as models [V30].

Build steps:

1. Create `src\SoundCeiling.sln` with these projects:
   - `SoundCeiling.Apo` (C++ DLL, dynamic universal C runtime, Embed Manifest = No [V3]);
   - `SoundCeiling.Dsp` (header-only limiter, with tests);
   - `SoundCeiling.Tray`, `SoundCeiling.Admin` and `SoundCeiling.Measure` (C#, .NET 10).
2. Build: `msbuild src\SoundCeiling.sln /p:Configuration=Release /p:Platform=x64`.
3. Install (approval A8): `SoundCeiling.Admin install --output "Headphones (Realtek(R) Audio)"`. It copies the DLL to `C:\Program Files\SoundCeiling\` and does everything in section 4.2 item 5.

Acceptance:

- A heartbeat appears within 1 second of playback start. `tasklist /m` shows our DLL next to the Realtek and Sound Research DLLs.
- Audio is unchanged: a loopback comparison with and without the APO agrees within 0.1 dB.
- The slider value the APO reports equals the slider within 0.1 dB at 20, 34, 50 and 100%.
- With the -20 dBFS tone, the APO's input peak reads -20.0 ± 0.1 dBFS at **every** slider position, proving the APO sits before the hardware volume [I → this test]. If it reads -20 + V instead, the vendor effects apply the volume digitally; reading (b) would then be automatic and the formula changes. Flag it.
- It survives an Audiosrv restart, sleep and resume, headphone unplug and replug, and one hour of playback without dropouts. `{1da5d803…},5` stays absent.
- The offload probe reports that offload is not supported [I → this test].
- Uninstall restores the snapshot exactly.

**M3. The limiter itself (2-3 days)**

- A header-only C++ limiter: our own code, or an adaptation of the MIT-licensed limiter in Signalsmith Basics [V20]. An offline harness runs the WAV set through it before it goes into the APO.
- Modes (a) and (b) with R.

Acceptance, both offline and live through loopback:

- Sample peak ≤ T exactly, and true peak ≤ T + 0.7 dB.
- The telemetry's gain reduction equals input minus output within ±0.1 dB.
- Zero clipper hits on speech.
- The section 6 table passes at sliders 20, 50 and 100.
- You pass a listening test on recorded Zoom audio.

**M4. Tray app (2-3 days)**

Acceptance:

- A left-click toggles protection, and the state survives a restart.
- A message appears within 1 second of an over event, its N is within ±0.5 dB of the telemetry, and it names Zoom or Chrome correctly.
- At most one message per app per 10 seconds.
- Killing the tray app leaves protection on: the step file is still limited.
- The toggle does not click: loopback shows no jump larger than the program's own.

**M5. Robustness (2-3 days)**

Build the repair task, the new-output prompt, the emergency cap and reading (b) with R.

Acceptance:

- Each injected fault raises DOWN or WARN within 5 seconds of playback, and repair restores protection within 1 minute. The faults are: removing our CLSID from `,15`; setting `{1da5d803…},5` to 1; killing audiodg; plugging in a USB headset.
- The emergency cap engages and releases correctly.
- An 8-hour run keeps CPU under 1% of one core for the APO and for the tray app [I], with stable memory.
- Optional: test C7.

Total: 9 to 14 days [I].

## 6. Test plan

**Safety.** These signals play on your real output. Before each test, take the headphones off or set the slider to about 10%, and raise it only on purpose. The -3 and -1 dBFS parts are meant to be loud. (From RESEARCH-AND-PLAN §10.)

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

**Instrument.** `SoundCeiling.Measure` records loopback on Headphones. Microsoft says loopback taps the audio "prior to any volume or muting being applied" [V2]. So loopback is what the output receives after the APOs [I → T-M1-2] and before the slider, and ear level = loopback + V.

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

- **During a call, if audio misbehaves:** turn the tray app Off, which ramps the limiter out. If that is not enough, go to Settings → System → Sound → Headphones → Audio enhancements → Off. That switches off every APO on that output, Realtek's included [I]; switch it back on later.
- **Our APO:** `SoundCeiling.Admin uninstall` does the following:
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
| 1 | Protection silently gone. Causes: a driver or Windows update rewrites the effect list [V28][V17]; Windows disables effects after 10 APO failures [V3]; someone turns "Audio enhancements" off; another program edits the list (Users can write it here [M]) | Likely over months | High | Health check and heartbeat; repair task; emergency cap; DOWN icon |
| 2 | `DisableProtectedAudioDG = 1`. Microsoft calls it a test key [V3]. Apps needing a secure (DRM) audio path may refuse to output audio [V16]. A future update could stop honouring it | Medium | High | DRM test in M1. Keep it the only system-wide change. Plan B (Voicemeeter) needs no key |
| 3 | A third-party APO on a composite Realtek + Sound Research chain: loading and order are undocumented [U] | Medium | High | Tests in M1 and M2, days 1-3. Fallbacks: the single EFX slot, wrapping the vendor chain, or Plan B |
| 4 | Our APO crashes or misbehaves inside audiodg: audio drops, and 10 failures disable every effect | Medium during development | High | Offline DSP tests first; SEH guard (Windows' structured exception handling); tolerant format negotiation; staged install; quick uninstall. Known traps: COM aggregation, sandbox ACLs, cached endpoint settings [V27] |
| 5 | Bypass: exclusive mode, spatial or bitstream formats, unregistered new outputs | Low-medium | High | Untick exclusive mode; keep spatial sound off, with a WARN; device watcher |
| 6 | Reading (b) slider-jump leak | Certain on big jumps | Low-medium | The R cap, or reading (a) if you want zero leak |
| 7 | Heavy limiting sounds squashed or pumps | Likely at X = -28 on Zoom | Medium | Section 4.6 settings; listening test; raise X; optional pre-stage |
| 8 | A Windows feature update breaks the APO. This happened to Dirac's APO on 24H2: complete loss of audio [reported, V31] | Low-medium per update | High | Health check; uninstall path; Plan B ready |
| 9 | Toolchain size, and the learning curve of a first APO | Medium | Medium | Interim Equalizer APO protection; route B as a fallback |
| 10 | Expecting dB SPL | Certain | Medium | The UI says "dB below device max"; optional phone calibration |

## 9. Decisions and approvals for Ariel (in order)

**Decisions**

- **D1.** Which reading of X: (b), a ceiling at your ear with R (recommended), or (a), a fixed digital ceiling.
- **D2.** The starting values: X = -28 dB below device max (from your 20% experience, tuned with "set ceiling from now"), and R = 10.
- **D3.** Whether you accept `DisableProtectedAudioDG = 1`. Routes A, B and C all need it. If not, go to Plan B (Voicemeeter).
- **D4.** Run the Equalizer APO trial first (recommended), and whether to keep it as interim protection afterwards.
- **D5.** Untick exclusive mode on Headphones (recommended).
- **D6.** Which outputs to protect: Headphones only (default), plus Speakers, plus the monitors; and whether new outputs are protected automatically.
- **D7.** The emergency volume cap while protection is DOWN (recommended: on).
- **D8.** True-peak detection (recommended: on).
- **D9.** Whether Off stays off until you turn it on, or turns itself back on after 30 minutes.

**Approvals, in the order they come up**

| # | When | What | Admin | Reboot | Settings or registry touched |
|---|---|---|---|---|---|
| A1 | M0 | Untick exclusive mode on Headphones | No [I] | No | One endpoint property |
| A2 | M0 | Export a registry snapshot to a file | No | No | None (read only) |
| A3 | M1 | Download and install Equalizer APO 1.4.2 | Yes | Yes | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Audio\DisableProtectedAudioDG = 1`; Headphones `FxProperties` `,5` and `,7`; `HKLM\SOFTWARE\EqualizerAPO`; COM registration |
| A4 | M1 | Download LoudMax 1.47 (freeware) and copy its DLL into Equalizer APO's plugin folder | Yes | No | None |
| A5 | M1 | Build and run `SoundCeiling.Measure` (dotnet, NAudio from NuGet); play test tones at low volume | No | No | None |
| A6 | After M1 | Keep or uninstall Equalizer APO | Yes | Yes | Reverses A3 |
| A7 | M2 | Install Visual Studio 2026 Community with the C++ workload (plus the SDK, and the WDK if needed) | Yes | Maybe | None |
| A8 | M2 | Install our APO | Yes | No (service restart) | `HKLM\SOFTWARE\Classes` (COM and AudioEngine entries); Headphones `{d04e05a6…},15`; key = 1; `%ProgramData%\SoundCeiling` ACLs |
| A9 | M4 | Tray app starts with Windows | No | No | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| A10 | M5 | SYSTEM repair task; emergency cap; extra outputs | Yes | No | Task Scheduler; `,15` on other outputs |

## 10. Evidence log

All web sources were accessed 2026-11-09 to 2026-11-10. "Reported" means a third-party or secondary source; I did not verify the claim itself.

| # | Claim | Status | Source |
|---|---|---|---|
| V1 | Three APO slots: SFX, MFX, EFX. "An endpoint effect is always applied, even to raw streams." SFX is not used for raw streams | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture |
| V2 | Windows 11 APO notifications include endpoint volume (fMasterVolume), and the first one delivers the current state. Build 22000+. Loopback taps "prior to any volume or muting". New APOs shipped with Windows 11 devices must pass HLK tests | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects |
| V3 | Real-time rules for APOs. An embedded manifest works with DisableProtectedAudioDG=1 but "when this test key is removed, your APO will fail to load, even if it is WHQL-signed". 10 load failures make Windows set `PKEY_Endpoint_Disable_SysFx = 1`. Example registration values. Componentized APOs are per driver | Verified | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects |
| V4 | `PKEY_CompositeFX_EndpointEffectClsid` is `,15`, a multistring allowing several effects in one slot, from Windows 10 1803. Precedence over the single slot: not documented | Verified / Unknown | https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-endpointeffectclsid |
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
| V27 | Another developer's APO on 24H2 (closed 2026-10-16) hit: a COM aggregation recursion bug; the sandbox (LPAC) blocking shared memory without its SIDs; the endpoint builder caching effect keys; the need for `PKEY_FX_Association`; composite slots working alongside OEM effects | Reported | https://github.com/li589/AuraDSP/issues/5 |
| V28 | Etienne Dechamps' APO notes: EFX runs after mixing and resampling, before Windows' CAudioLimiter; exclusive mode, KS and ASIO bypass the engine; a driver reinstall overwrites the effect settings and endpoint GUIDs can change; the service cannot read `C:\Users`; `Disable_SysFx` is the enhancements checkbox | Reported (expert notes) | https://github.com/dechamps/APO |
| V29 | Windows' audio engine contains CAudioLimiter, and the device meter reads after it (2011) | Reported (Microsoft engineer's blog) | https://matthewvaneerde.wordpress.com/2011/05/11/linearity-of-windows-volume-apis-iaudiometerinformation-and-full-scale-signals/ |
| V30 | Windows-driver-samples: MS-PL licence; VS 2022 + WDK; SYSVAD APO samples SwapAPO, DelayAPO, AecApo, KWSApo | Verified | https://github.com/microsoft/Windows-driver-samples , `…/tree/main/audio/sysvad/APO` |
| V31 | 24H2 plus Dirac's APO (cridspapo.dll) could cause complete loss of audio; the hold was lifted 2025-09-11 | Reported (news) | https://winaero.com/dirac-audio-compatibility-issue-has-been-finally-fixed-in-windows-11-24h2 |
| V32 | `VerifiedAndReputablePolicyState = 0` means Smart App Control is off | Reported (community; Microsoft's page gives no mapping) | https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control |
| V33 | IAudioProcessingObjectNotifications: minimum Windows build 22000; header `audioengineextensionapo.h` | Verified | https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/nn-audioengineextensionapo-iaudioprocessingobjectnotifications |
| V34 | Visual Studio 2026 needs 2.3 to 60 GB depending on features | Reported (search snippet) | https://learn.microsoft.com/en-us/visualstudio/releases/2026/vs-system-requirements |
| M | This PC (read-only, 2026-11-09 and -10). Composite FX keys and their CLSIDs, DLL paths, versions and WHQL signer on both Realtek outputs. `DisableProtectedAudioDG` absent in both candidate keys. SAC policy 0; HVCI running; UMCI 0; Secure Boot 1. Windows 25H2 build 26200.9711. HP Z2 G8 Tower; standard HDA controller. No EAPO, VB-Audio, Voicemeeter or VAC installed. `FxProperties` ACL as in 4.1. ffmpeg burst and inter-sample peaks as in section 6. Slider dB, 10 ms period, Zoom levels and meter position from RESEARCH-AND-PLAN §14 | Measured | Local checks |
| I | Slider fit dB ≈ 34·log10(s); about 10-30 ms of audio in flight; the APO sits before the hardware volume; loopback is after the APOs; offload absent; Zoom used shared mode (its session meter showed real levels, and software meters read 0 in exclusive mode, RESEARCH-AND-PLAN F3); effort figures | Inferred | Tests named in sections 5 and 6 |

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
10. **The brief's "endpoint meter reads before the master volume"** is consistent with Microsoft. Whether it reads before or after an inserted APO is still undocumented. I infer "after EFX" [V28][V29] and test it in T-M1-2. Our design does not depend on it, because the APO reports its own input and output peaks.

## 12. Decisions so far (added 2026-11-10)

- **Route chosen:** Ariel prefers our own limiter (route C, the APO) over Voicemeeter (plan B), after being told that it needs `DisableProtectedAudioDG = 1` (the "test key"). This settles the route question in section 1. It is not a go-ahead for any system change.
- **Still needs an explicit go, each when its turn comes (section 9):** installing the C++ build tools (A7), setting the test key and registering the plug-in (A8), unticking exclusive mode (A1), the SYSTEM repair task (A10).
- **State of this PC (read-only checks, 2026-11-10):** Visual Studio Community 2022 17.2 is installed with managed, web and Office workloads only: no MSVC toolset and no Windows SDK (so no `audioenginebaseapo.h`). `winget` is available and offers Build Tools 17.14.44. 404 GB free on C:.
- **Built so far:** the tray app (monitor, cap slider, test tone) is running from `dist\SoundProtection.exe`; its plan and ledger are in `docs\superpowers\plans\`. It reads Windows' meters from outside and cannot hold sound back; the limiter plug-in is what will.
