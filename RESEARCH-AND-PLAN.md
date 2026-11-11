# Sound Protection: research findings and plan

| | |
|---|---|
| Date | 2026-11-09 |
| Status | **Draft for Ariel's review.** Research and read-only measurements only. Nothing built and no setting changed. The only install was .NET SDK 10, at Ariel's request. |
| Follows | `HANDOFF.md` in this folder |
| What was touched | Read-only checks of this PC, web searches and page reads, a throwaway read-only measurement program (section 14, deleted afterwards), and short test WAV files in a temp folder (deleted afterwards). |

**Evidence labels used throughout**

- **[verified]** a page or file was read this session and says it.
- **[reported]** a search result, forum post or third-party listing says it; the source itself was not read.
- **[mine]** my own knowledge or inference; not verified this session.
- **[test]** can only be settled by measuring on this PC (see the spike in section 6).

---

## 0. Summary

**What you want.** A small Windows 11 program in the hidden-icons tray where you set a maximum output level. It keeps watching while Zoom, YouTube and other apps play at different levels, tells you when something went over and by how much, and can be switched on and off.

**Can it be built?** Yes. The core needs no driver, no admin rights and no audio capture. Windows already gives every app a peak meter and a volume knob, and gives the output device a master volume. A tray app can read those, compare them with your maximum, notify you, and optionally turn the offending app down.

**Does it already exist?** Not as a whole (section 4). The closest are four small projects. None of them measures per-app loudness, reports "how much", and offers tray, toggle and notifications together. Build it, and borrow ideas.

**What you asked for, clarified 2026-11-09.** A ceiling in dB that holds whatever the Windows slider says: "even if master is 100, nothing is louder than X". That is a limiter, and there are two ways to build it. A program that only watches and reacts (Phase 1) turns an app down when it would exceed X, but cannot stop the first few hundredths of a second of a sudden loud sound (my estimate; it cannot be measured read-only), and that leak is at full level exactly when the slider is high. A limiter placed in the audio path (Phase 2) stops everything above X, at any slider position, with no leak. So the full request needs Phase 2, and Phase 1 is its foundation (tray icon, toggle, meters, messages). Separately, the spike found a small optional guard (section 7.2b): stopping the slider itself from going above a position you choose. It is off by default and is not what you asked for.

**Recommended path**

1. Phase 0: a few zero-code checks (section 6). The read-only parts are done (section 14).
2. Spike: a throwaway measurement script, no install (section 6). Done read-only (section 14).
3. Phase 1: "Sound Guardian", the tray app: toggle, live meters, messages with "how much", and a reactive ceiling that turns loud apps down at any slider position. C# on .NET 10 with NAudio (section 7).
4. Phase 2: the limiter in the audio path, so nothing above your ceiling ever passes (section 8). This is what "even at master 100" really needs.

**What I would not build on: Equalizer APO.** It is the well-known open-source system-wide route, but the newest release is from March 2025, it is an unsigned component that needs Windows' signature check for audio components switched off, the stock build reportedly loads only VST2 plugins, it has no built-in limiter, and two tickets about 24H2/25H2 are open (section 4.3).

**What I need from you:** section 11. The answer that changes the most is whether the in-path limiter (Phase 2) is the goal, with the tray app built first as its base.

---

## 1. Your request as I understand it

Your words: "how can i build something to my windows? i am win 11. websearch what exits in the opensource scene. final want it to define max decibels and open multi apps that can throw variatey of sound levels mainly zoom or youtube. so i want to set a max protetction to my output. also to get a message if something passed, and how much, and to be able to toggle on and off. have it on the hidden icons tray."

| ID | Requirement | Source |
|---|---|---|
| R1 | You define a maximum level ("max decibels") | your words |
| R2 | Handles several apps at once that play at different levels, mainly Zoom and YouTube | your words |
| R3 | "Max protection" on the output | your words |
| R4 | A message when something went past the maximum, and by how much | your words |
| R5 | On/off toggle | your words |
| R6 | Lives in the Windows 11 hidden-icons tray | your words |
| R7 | Even out loud and quiet Zoom participants (the original ask in HANDOFF section 1) | HANDOFF, treated as secondary |

**My guesses, for you to confirm or reject**

- Non-goals: not a microphone tool, not an equalizer, no network or cloud, not a certified sound-level meter, not a medical device.
- Anti-goals: it must never leave you with silent audio and no explanation. It must not install drivers or change system audio settings without asking. It must not spam notifications during a call.

**Where the HANDOFF open questions stand**

1. Which option first? Superseded. You now want a tool, so the options are regrouped in section 5.
2. Which output device does Zoom use? Still open. Phase 0 step 3 answers it.
3. Own listening only, or a reusable tool? Answered: a reusable tool with a tray icon and settings.
4. Comfortable changing the audio driver? Still open. Phase 1 needs no driver.

---

## 2. Windows audio facts that drive the design

### 2.1 The path

```
 Zoom.exe    --\                                  +-------------------+
 chrome.exe  ---+--> [ Windows audio engine ] --> | master volume     | --> driver --> headphones
 other apps  --/      (mixes all apps)            | (the slider, dB)  |
      |                      |                    +-------------------+
      |                      |
 per-app "session":     endpoint peak meter reads here, BEFORE master volume   [verified]
 own volume knob +      loopback capture taps the mix; its relation to master
 own peak meter         volume is not stated by Microsoft                      [test]
```

### 2.2 Facts

| # | Fact | Why it matters | Evidence |
|---|---|---|---|
| F1 | Every app that plays sound gets its own session with its own volume and its own peak meter. | Per-app names in messages ("Zoom +9 dB") and per-app turning-down are possible. | [verified] NAudio 3.3.0 lists AudioSessionControl, SimpleAudioVolume and AudioMeterInformation |
| F2 | The device (endpoint) peak meter for playback "monitors the peak levels in the output stream before the stream is attenuated by the endpoint volume controls". `GetPeakValue` returns 0.0 to 1.0, the peak of the previous device period. | The meter shows the digital level regardless of your Windows volume slider, and lags one period (10.00 ms on every active output here, measured). Real loudness at the ear = meter level + master-volume gain. Measured with live Zoom audio: the device meter read the same as Zoom's own session meter (+0.10 dB median) although the master volume was -16 dB, so it is before the master volume (section 14). | [verified] Microsoft Learn, and measured |
| F3 | Exclusive-mode streams bypass the mixer. "In exclusive mode, a software peak meter always reports a peak value of 0.0." Loopback cannot use them. In-path processing is skipped for them. | Your headphone device has both exclusive-mode boxes ticked (HANDOFF section 3). An app using exclusive mode would slip past any protection. | [verified] Microsoft Learn |
| F4 | Whether loopback capture is taken before or after the master volume is not stated by Microsoft. Community reports conflict. | Only matters if we capture audio. Phase 1 does not. | [verified silent], [test] |
| F5 | Per-app capture ("process loopback") exists. Microsoft lists Windows 10 build 20348 as the minimum (one page says 20438, NAudio's doc says 19041). Your build is 26200, above all of them. | Option for exact per-app loudness later. | [verified] |
| F6 | Microsoft's pages do not say whether a session's meter reads before or after that session's own volume. | Decides how the app learns the "true" level of an app it has turned down. | [test] (not done: needs a volume change) |
| F7 | New tray icons on Windows 11 start in the hidden-icons flyout (a per-app `IsPromoted` flag starts at 0) until the user drags them out. | R6 is the default behaviour; no tricks needed. | [reported] Microsoft Tech Community and Eleven Forum threads |
| F8 | Toasts from unpackaged desktop apps need an app identity (AUMID, shortcut, CLSID). The simpler `NotifyIcon` balloon tip needs none and Windows shows it as a toast. | Version 1 uses the balloon tip. Toasts with buttons come later. | [reported] Microsoft Learn snippets, [mine] |
| F9 | Microsoft says that on Windows 11 an app whose window is occluded, minimized, invisible or inaudible may not get a finer timer resolution than the default. | A tray app polling every 10 ms might get ticks of roughly 15 ms. Measured here (section 14): default sleep gives about 15.6 ms ticks, while a high-resolution waitable timer kept 2 ms and 10 ms ticks within about 1 ms in a background console process. Use that timer. | [verified] for window-owning apps, measured for a windowless console process |
| F10 | Windows remembers each app's volume per device. | If the app turns Zoom down and then crashes, Zoom stays quiet next time. It needs restore-on-start. | [mine], [test] (not done: needs a write) |

### 2.3 What "decibels" can mean here

- **dBFS** is the digital level. 0 dBFS is the loudest value the stream can carry. Software measures this exactly.
- **Master volume** is Windows' slider. It adds attenuation in dB (zero or negative). Your screenshots showed 32 (HANDOFF section 3).
- **dB SPL** is the sound pressure at the ear. It depends on the headphones' sensitivity and the amplifier. Software cannot know it.

So the maximum has to be set on an **effective** scale:

```
effective level = digital level (dBFS) + master-volume gain (dB, zero or negative)
```

0 means "the loudest this device can play". A ceiling of -20 means "20 dB below the loudest this device can play". With this definition, raising the Windows slider cannot silently defeat the ceiling.

Optional later: calibrate to approximate dB SPL by playing a known tone and reading it with a phone sound-level app. Expect roughly +/-3 to 5 dB of error.

Rules of thumb [mine]: about +10 dB sounds twice as loud, and each +3 dB halves the safe listening time. ITU-T H.870, the WHO/ITU safe-listening standard, is described as an equal-energy framework [verified in the ITU summary]. The numeric limits I remember (about 80 dB(A) for 40 hours a week) are not verified here. The ITU text is free.

---

## 3. Zoom and YouTube specifics

- **Zoom** has no listener-side or host-side volume normalization (HANDOFF section 2). It plays one mixed stream. It is installed per-user at `%APPDATA%\Zoom\bin\Zoom.exe` [verified on this PC].
- **Which device Zoom uses.** On this PC today, Zoom's live audio session is on **Headphones (Realtek(R) Audio)**, which is also the default device for the console, multimedia and communications roles, so the communications-default question does not matter right now (section 14). In general, "Same as System" most likely follows the Windows default playback device, not the "default communications device" [reported, weak].
- **YouTube "Stable volume".** Google's help page has steps for Android, iPhone/iPad, Computer and TV, and says: "By default, stable volume is turned on when you're watching videos on YouTube." On a computer it is in the video's Settings (gear) menu [verified]. It evens out YouTube's own loudness and cannot touch Zoom. Check it has not been turned off. An earlier search summary said mobile only; the page itself has Computer steps.
- **Windows "Communications" ducking.** The registry value for it (`UserDuckingPreference`) is not set on this PC, so the Windows default applies. I believe that default lowers other sounds by 80% when Windows detects communications activity [mine]. That could be lowering YouTube while Zoom runs.
- **What Windows' own Sound settings offer.** Windows has no maximum-loudness limit. The closest built-in thing is **Loudness Equalization**. It evens out loud and quiet sounds but does not enforce a dB ceiling. It appears (`Win+R`, `mmsys.cpl`, Playback, the device, Properties, **Enhancements** tab) only when the audio driver provides Microsoft's effects. Your Realtek Headphones and Speakers do not (HANDOFF section 3). Your two monitor outputs carry Microsoft's effects components, so they probably do (section 14). In Settings, System, Sound, click the device and open the **Audio enhancements** dropdown; if it lists only Device Default Effects and Off, there is nothing more there. HP Audio Control may have its own loudness-type option (section 14).
- **Browsers.** Chrome and Edge normally show up as one audio session for all tabs [mine, test]. The app can say "Chrome", not "this tab".

---

## 4. What exists in open source and free software

### 4.1 Closest prior art (tools that limit or protect volume)

- **SoundBrake** (`github.com/eneswritescode/soundbrake`)
  - MIT licence. Go, with hidden PowerShell running C# COM code on every poll. Inno Setup installer.
  - Measures the master-volume **setting** in whole percent, not loudness. The endpoint peak meter only decides "is something playing". Polls every 5 seconds.
  - Time-based tiers: 75 to 84% for 60 cumulative minutes, or 85 to 94% for 20 minutes, gives a notification. 95% or more for 4 minutes sets the volume to 70 and notifies.
  - Tray icon (item "Silence 24 h"), native notifications with a doubling cooldown. A settings window and an on/off toggle are not stated.
  - v1.0.0 from March 2026, 8 stars, 1 commit, "not yet code-signed".
  - **Fit:** closest in spirit. It ignores what is playing, so it cannot catch a scream at a low volume setting. Good for UX ideas only.
- **PeakFlow** (`github.com/zadzora/PeakFlow`)
  - Custom "personal use only" licence; no redistribution. Python with sounddevice, numpy and tkinter.
  - Reads a VB-Audio Virtual Cable as an input and measures block peaks in dBFS. Limits with a real sample-level limiter (hard clip or soft-knee compressor). Slider from -1 to -40 dBFS.
  - Tkinter window with meters. Tray and toggle not stated. Input device index is hard-coded to 2.
  - Last commit July 2025, 0 stars.
  - **Fit:** proves the "virtual cable plus own limiter" design (option O3 below). Cannot be forked because of the licence.
- **ez-volume-normalizer** (`github.com/e-zijlstra/ez-volume-normalizer`)
  - Custom licence: use only, no copying, redistribution or derivative works without written consent. D language with GTK3.
  - Measures with WASAPI loopback. Sets Windows master volume in dB from the measured level. Only lowers, never boosts.
  - GTK window with a power button. Last commit January 2024, 9 stars, tested on Windows 10.
  - **Fit:** same idea as option O1 using loopback. Cannot be forked.
- **Volume Limiter** (SourceForge)
  - Public domain. Visual Basic. Caps the maximum volume **setting**, with a password lock. Minimizes to the tray.
  - Last update 2016-11-22, for Vista, 7 and 8. Obsolete.
- **Seen in search, not inspected:** WinSoftVol (MIT kernel filter driver that disables hardware volume controls) [reported]; "Wale", a peak trimmer listed on Softpedia [reported].

**Conclusion:** nothing found is loudness-aware, per-app, quantified, and complete with tray, toggle and notifications. Building is justified.

### 4.2 Building blocks we can reuse

- **NAudio 3.3.0** (stable, 2026-10-19; 3.4.0-preview.1 from 2026-10-24 exists) [verified].
  - MIT licence. Requires `net9.0` or later (older .NET needs NAudio 2.x).
  - Contains MMDeviceEnumerator, AudioSessionManager, AudioSessionControl with `AudioMeterInformation` and `SimpleAudioVolume` (package NAudio.Wasapi).
  - `WasapiLoopbackCapture` is marked legacy. Process loopback is `WasapiRecorderBuilder.WithProcessLoopback(...)`.
  - The endpoint master-volume class (`AudioEndpointVolume`) was not confirmed in the release notes. Check at build time.
- **Microsoft Core Audio APIs** (native), the layer under NAudio.
- **EarTrumpet** (`github.com/File-New-Project/EarTrumpet`): C# per-app volume mixer for Windows, MIT per a SourceForge mirror [reported, licence not checked at source]. A reference for session handling and tray behaviour. Not inspected.
- **Toast notifications:** `Microsoft.Toolkit.Uwp.Notifications` (needs target framework `net6.0-windows10.0.17763.0` or later), or the Windows App SDK `AppNotificationBuilder` as its successor [reported].
- **ffmpeg 8.0.1** is already on this PC. Used for exact test tones (section 10).
- Python route [mine]: `pycaw` and `pystray` exist and would work today with Python 3.14.

### 4.3 System-wide processing hosts (only needed for a hard limiter)

- **Equalizer APO** (open source, GPL-2.0 [mine])
  - Newest release **1.4.2, 2025-03-21**; nothing newer listed as of 2026-11-09 [verified].
  - Windows support stated as "Windows Vista or later (Windows 7 - 11 have been tested)". Nothing on 24H2 or 25H2 [verified].
  - Needs `DisableProtectedAudioDG` "because it is an unsigned APO"; developer docs say it "disables the signature check for APOs" [verified].
  - Cannot process ASIO or WASAPI exclusive mode. "E-APO can only process uncompressed audio data" and DRM software may refuse to output sound (2015 maintainer comment) [verified].
  - Configuration commands: Preamp, Filter, Delay, Copy, GraphicEQ, Convolution, Include, Device, Channel, Stage, If/Else, Eval. **No limiter, compressor or gain-ride command** [verified]. Limiting means loading a VST plugin.
  - VST: the config reference has no VSTPlugin page (last edited 2019) [verified]. The stock build loads 64-bit **VST2** only, per a developer reply on itch.io [reported]. VST3 comes from young forks (for example `EqAPO64_with_VST3_support`, whose SourceForge page said "registered 3 days ago" when found) [reported].
  - Tickets: #335 (2025-11-13, 24H2, app "immediately exits"; reporter fixed it with a third-party repair tool) and #347 (2026-04-06, 25H2, microphone VST; reporter later wrote "it was just me being an idiot"). Both are still open. A commenter says the developer "isn't very active" [verified].
  - Config edits "should be effective immediately". Included files must be readable by the audio service. Whether a non-admin program can write to the config folder is not stated [verified].
- **VST limiter plugins** (needed by Equalizer APO)
  - LSP Plugins has a "Limiter" (v1.0.38). Source is LGPL-3. **Windows builds are sold** (VST2/VST3; the store shows "90 $" and "30 $" for the limiter, labels unclear). Linux builds are free [verified].
  - Free Windows limiters seen in search: DSPplug esquire3 (VST2+VST3, true-peak, ceiling listed at -1.0 dBTP, one KVR user calls it open source), W1 Limiter, LoudMax (VST3 in 1.46) [reported]. Licences not verified. A hearing-protection use needs an adjustable ceiling.
- **VB-CABLE** (closed source, donationware, Windows 11 listed as supported) [reported]. Driver signing not confirmed. One forum thread reports it breaking after a 24H2 update and being fixed by updating alongside Voicemeeter [reported].
- **Voicemeeter** (closed, free/donationware [mine]) has compressor controls. Which edition applies them to the output bus is unchecked (HANDOFF option 3).
- **Open-source virtual audio drivers:** the search found nothing recent and Windows 11-ready. `virtual-audio-wire`, `AudioMirror` (Windows 10 only, x64) and Virtual Audio Pipeline (looks dormant) [reported]. Scream and the Microsoft SysVAD sample exist [mine] but were not checked. A driver you build yourself needs a signing path (section 8).
- **Windows Loudness Equalization** is built in but missing on your Realtek driver (HANDOFF section 3).

---

## 5. Architecture options

- **O1. Watchdog ("Sound Guardian").** A user-level tray app. Reads peak meters per app and per device, reads master volume, computes the effective level, compares with your ceiling, notifies, and optionally lowers the offending app's volume then eases it back. No driver, no admin, no audio capture. It can only react after loud audio is already queued. Reaction time is one meter period plus polling plus the audio already in flight, so budget tens of milliseconds (measure in the spike).
- **O2. Equalizer APO plus a VST2 limiter, with our tray app as the control panel.** The limiter is in the audio path, so nothing above the ceiling passes (except exclusive-mode apps). Our app rewrites a small config file for on/off and ceiling, and uses O1-style meters for messages. Costs are in section 4.3: unsigned component, signature check off, ageing project, VST2 only.
- **O3. Virtual cable plus our own limiter.** Windows sends audio to a virtual device (VB-CABLE). Our app captures it, applies a look-ahead limiter, and plays it on the real headphones. Gives exact "limiter pulled down 6.2 dB" numbers and per-app routing. Costs: closed-source cable driver, extra latency (roughly 20 to 40 ms [mine]), clock-drift handling between two devices, and **silence if our app dies** unless a watchdog switches the default device back.
- **O4. Our own APO driver.** The "proper" way, but it needs a signed driver package. Equalizer APO avoids that only by turning Windows' signature check off. Not realistic for a personal tool.
- **O5. Windows Loudness Equalization** (HANDOFF option 2: swap to Microsoft's generic driver). No code. It levels voices (helps R7) but gives no dB ceiling, no messages and no tray icon.
- **O6. Browser extension** (Web Audio compressor on the tab). YouTube only, cannot touch the Zoom desktop app [mine]. Not pursued.

| | R1 set max | R2 many apps | R3 nothing passes | R4 "how much" | R5 toggle | R6 tray | Admin / driver | If it fails | Effort |
|---|---|---|---|---|---|---|---|---|---|
| O1 Watchdog | Yes | Yes, per-app names | Partly (reacts; the first tens of ms of a loud burst leak) | Yes | Yes | Yes | None | Audio plays unprotected | Small |
| O2 Equalizer APO + VST2 | Partly (plugin setting) | Yes, whole device | Yes, except exclusive mode | Partly (needs O1 meters) | Yes (config rewrite) | Yes (our app) | Admin, signature check off | Audio plays unprotected | Medium |
| O3 Cable + own limiter | Yes | Yes, per-app routing | Yes | Yes, exact | Yes (bypass) | Yes | Driver install | **Silence** unless watchdog | Large |
| O4 Own APO | Yes | Yes | Yes | Yes | Yes | Yes | Signed driver needed | n/a | Very large |
| O5 Loudness Equalization | No | Yes | No (leveler) | No | Yes | No | Driver swap | n/a | Tiny |
| O6 Extension | Partly | YouTube only | Partly | Partly | Yes | No | None | n/a | Small |

---

## 6. Recommendation and phases

### Phase 0: zero-code checks (about 15 minutes, all reversible)

**Status 2026-11-09:** the parts that could be done read-only are done; results in section 14. Steps 1 and 6, the exclusive-mode untick (step 4) and opening the HP app (step 5) are still yours.

1. **YouTube:** player gear menu, confirm "Stable volume" is on.
2. **Windows Communications ducking:** `Win+R`, `mmsys.cpl`, Communications tab. Choose "Do nothing" if you do not want Windows lowering YouTube while Zoom is active.
3. **Zoom device:** Zoom, Settings, Audio, Speaker. Note whether it says "Same as System" or a named device, and click Test Speaker. This answers HANDOFF open question 2.
4. **Exclusive mode:** in `mmsys.cpl`, Playback, the device Zoom uses, Properties, Advanced. Untick "Allow applications to take exclusive control of this device" (HANDOFF section 3 shows both boxes ticked). Exclusive streams bypass every protection (F3).
5. **HP Audio Control:** open it and note what it offers (HANDOFF next step 2).
6. **Source-side fix:** ask the quiet and loud participants to turn on Zoom's "Automatically adjust microphone volume" (HANDOFF next step 5).
7. **Optional, your decision:** HANDOFF option 2 (generic driver, then Loudness Equalization). Levels voices, gives no ceiling or messages.

### Spike S1: throwaway measurement script (done 2026-11-09, read-only; results in section 14)

**Status:** run as a throwaway C# file with `dotnet run` (possible once SDK 10 was installed) instead of PowerShell, with no setter calls, then deleted. Because you asked for read-only, the parts that need a volume change (F6, F10, the reaction-time test) and the tone test were not done. Zoom was playing live audio on the Headphones at the time, so live Zoom audio served as the test signal. The list below is the original plan.

A short PowerShell script using `Add-Type` with C# COM declarations (the same trick SoundBrake uses). For 60 seconds it prints, every 10 ms: the device meter, each session's meter with its app name, and the master volume in dB. I would run it while you play the test tones and a Zoom speaker test. It is deleted afterwards. It answers:

- F2: is the device meter really independent of the Windows slider?
- F6: does a session meter move when that app's slider moves?
- F9: how regular are the ticks in a windowless background process?
- Do Zoom and Chrome appear as separate sessions, and under what names?
- Which device does Zoom actually use?
- A first estimate of the reaction time of "see peak, then lower the app's volume".
- F10: after lowering an app and restarting it, is the volume remembered?

Keeping anything the spike produces is a new request and would come back to you first.

### Phase 1: "Sound Guardian" (section 7)

Needs the .NET 10 SDK. This PC has only SDK 6.0.301 (out of support). `winget` lists `Microsoft.DotNet.SDK.10` 10.0.403 today. It installs system-wide, so it needs your OK.

### Decision gate

Run Phase 1 for a week or two. The event log (section 7.6) shows how many events happen, which apps cause them, and how fast they rise. Mostly sustained events (screams, loud ads, hundreds of milliseconds or more) means Phase 1 would be enough on its own. If short bursts matter to you, go to Phase 2.

**Update 2026-11-09:** you asked for a ceiling that holds even with the slider at 100. That is the case where a reactive tool's leak matters most, so plan on Phase 2. The Phase 1 log still tells us how loud and how fast your real events are, which sets the ceiling and the limiter's speed.

### Phase 2: in-path hard limiter (section 8)

### Phase 3: polish

Per-app ceilings, quiet hours, approximate dB SPL calibration, installer, global hotkey, history view, and a "leveler" stage for Zoom (R7) if Phase 2 exists.

---

## 7. Phase 1 specification: "Sound Guardian"

**Working name:** Sound Guardian. Rename freely.

### 7.1 Stack (my call; section 11 Q3 lets you override)

- C# on .NET 10 (the current long-term-support line [mine]), WinForms for the tray app, NAudio 3.x for Core Audio.
- Single-file publish, framework-dependent (small). No admin, no network, no audio capture.
- Suggested layout:

```
Sound Protection/
  HANDOFF.md
  RESEARCH-AND-PLAN.md
  src/
    SoundGuardian.App/     tray, settings, notifications (WinForms)
    SoundGuardian.Core/    pure logic: level math, event merger, duck controller
    SoundGuardian.Audio/   Core Audio wrappers (NAudio): devices, sessions, meters, volumes
  tests/
    SoundGuardian.Core.Tests/   unit tests fed with simulated meter readings
  tools/
    make-test-tones.ps1    the ffmpeg commands from section 10
```

- The pure logic is kept free of Windows calls so it can be unit-tested with simulated meter feeds. The Windows-specific layer is tested by hand with the test tones.
- Python alternative: works today with `pip` packages, but I advise against it for an always-on tool (packaging into an exe often trips antivirus, COM threading quirks) [mine].

### 7.2 What it measures

Every tick (aim for about 10 ms), for each active output device D:

```
master_db = D.masterVolumeDb                  # zero or negative, from the Windows slider
for each session S on D (Zoom, Chrome, ...):
    peak  = S.meter.peak                      # 0..1, previous period
    level = 20*log10(max(peak, 1e-5))         # dBFS
    eff   = level + master_db                 # effective level; 0 = loudest D can play
    over  = eff - ceiling                     # above zero means too loud
```

- Also reads the device-level meter, so apps that do not expose a session still count.
- "Sustained" level = average of the last 1 to 3 seconds of period peaks. A cheap proxy for loudness. Real loudness (LUFS) would need audio capture and is a later option.
- Muted devices are ignored. All active output devices are watched, not just the default (section 11 Q6). Windows lists many old "not present" outputs (30 here); only devices in state Active are used.
- **Measured costs (section 14):** one meter read takes about 70 microseconds; a pass over the device meter plus 5 sessions took 426 microseconds, so polling every session every 10 ms would cost about 4% of one core. Plan: poll only sessions that are Active (usually 1 or 2), re-enumerate sessions about twice a second (1.7 ms each), and slow to a few polls per second when nothing is playing. The meter itself updates every 10 ms, so polling much faster than every 5 ms adds nothing. Use a high-resolution waitable timer for the loop.

### 7.2b Optional: stop the slider itself from going too high (off by default)

You did not ask for this, and it is not the ceiling you described (that one holds at any slider position; see 7.3). It is a cheap extra that came out of the spike (measured numbers in section 14).

- Digital audio cannot go above full scale (0 dBFS), so the loudest possible output of a device is set by its master volume. At your Headphones setting of -16 dB, nothing (a scream, a video ad, a notification) can be louder than about -16 dB re full scale. (Allow a dB or two for inter-sample overshoot [mine].)
- So "never louder than X" can be enforced by keeping the master volume at or below X. It limits the knob, not the content, so it does not depend on how fast a blast arrives.
- Sound Guardian watches the device's volume-change events (no polling). If the master volume goes above the slider cap you chose, it sets it back within milliseconds and tells you ("Volume was raised to -9 dB; put back to your cap of -16 dB").
- This is the only part of the design that writes to a system setting: it lowers the master volume, and only when it is above your cap. It stays off until you confirm a cap. The default offered is your current volume.
- On your Realtek Headphones and Speakers the volume is a hardware control (support mask 0x3), so the cap also holds against exclusive-mode apps. On the two monitor outputs the volume is software-only (mask 0x2) [mine: decoded from the Windows header constants], so exclusive-mode apps bypass both the cap and the meters there.
- What it cannot do: even out loud and quiet voices. At the cap, a quiet participant is still quiet. That needs processing in the audio path (Phase 2).
- The ceiling in 7.3 is the main protection; this slider cap would only be a second, independent guard.

### 7.3 Settings (defaults are my calls)

| Setting | Default | Note |
|---|---|---|
| Slider cap (optional, maximum master volume) | off | section 7.2b |
| Enabled | on | toggled from the tray |
| Ceiling X (peak, in dB below the loudest the device can play) | you choose; "Learn my level" helps | holds at any slider position; see below |
| Sustained ceiling | off | optional second limit |
| When over | notify and turn the app down | or notify only |
| Event merge gap | 500 ms | consecutive overs become one event |
| Notification cooldown | 10 s per app | extra events fold into a summary |
| Turn-down release | 6 dB per second | after a 1.5 s hold |
| Per-app overrides | none | for example a lower ceiling for Zoom |
| Ignore list | empty | apps to leave alone |
| Start with Windows | on | `HKCU\...\Run`, no admin |

**"Learn my level":** play ordinary conversation for 20 seconds. The app measures your median and loudest level and offers a ceiling a few dB above your loudest ordinary moments. (I first planned median + 12 dB. The spike data shows why that is wrong: Zoom's output is already hot, with digital peaks around -4 dBFS and a median of -10.4, so median + 12 dB would be above full scale and never trigger. Section 14.)

### 7.4 Events and messages

- **Event** = effective level above the ceiling for at least one tick. Merge events less than 500 ms apart. Record start time, app, device, peak level, master gain, effective level, overshoot, duration and action taken.
- **Message examples**
  - `Zoom was 8.4 dB over your limit for 1.8 s (about 1.8x as loud). Turned Zoom down.`
  - `Chrome peaked 3.1 dB over your limit at 14:02.` (notify-only mode)
  - Summary when cooling down: `4 events in the last minute. Worst: Zoom +9.7 dB.`
  - "about Nx as loud" is 2 to the power (dB/10), an approximation.
- Buttons later (toast version): "Pause 15 min", "Raise limit 2 dB".
- Windows may suppress toasts under Do Not Disturb or Focus. Fallback: flash the tray icon and keep the event in the log. An always-on-top mini overlay is an option (section 11 Q9).

### 7.5 Tray UX

```
 Tray tooltip:  Sound Guardian: ON, limit -20 dB, last: 14:02 Zoom +8.4 dB

 Right-click menu
   [x] Protection ON                      (left-click the icon toggles this)
       Limit: -20 dB            >  -30 ... -6 in 2 dB steps / Set exactly...
       When over the limit      >  (o) Notify and turn the app down
                                   ( ) Notify only
       Learn my level...
       Pause                    >  15 min / 1 hour / until I turn it on
       Show live meters...                (double-click the icon)
       Recent events (3)        >
       Start with Windows
       Exit
```

- Icon states: ON, OFF (grey), ALERT (red for about 3 s after an event), PAUSED (amber). Light and dark taskbar variants.
- The menu also has an optional "Max slider position" entry next to the limit (section 7.2b).
- New icons start in the hidden-icons flyout (F7), which is what you asked for. The app will not promote itself. The registry flag that does so is undocumented and unnecessary.
- Single instance. Remembers state across restarts.
- Live meters window: bars per app with the ceiling as a line, plus the last 10 events. It is how you tune the ceiling by eye.

### 7.6 Turning an app down, and the event log

- **Attack:** the very next tick, lower that app's session volume by (overshoot + 1 dB).
- **Hold** 1.5 s, then **release** at 6 dB per second back to the app's own setting, stopping if the level nears the ceiling again.
- If you move that app's slider while it is turned down, the app adopts your value and stops fighting you.
- Master volume is **not** touched by default (an optional last resort later).
- **Crash safety (F10):** a small recovery file lists apps currently turned down and their original volumes. It is applied at start and at exit.
- **Event log:** `%LocalAppData%\SoundGuardian\events.csv` with columns `time_local, device, app, pid, peak_dbfs, master_db, effective_db, ceiling_db, over_db, duration_ms, action`. Settings in `%AppData%\SoundGuardian\settings.json`.

### 7.7 Edge cases to handle

Device unplugged or default device changed; Bluetooth connect and disconnect; sleep and resume; audio service restart; Chrome tabs creating and removing sessions all the time; Zoom creating a new session per meeting; several devices at once; exclusive-mode apps (warn once: they are invisible to the meters); timer throttling (use a high-resolution waitable timer if the spike shows jitter).

### 7.8 What Phase 1 cannot do

- Cannot stop the first tens of milliseconds of a sudden loud sound. That needs the limiter in the audio path (Phase 2, section 8). The optional slider cap (7.2b) is exact, but it only limits the slider, not the content.
- Sees sample peaks, not true peaks or loudness.
- Cannot see exclusive-mode apps.
- Cannot separate two people talking at once, and cannot boost a quiet participant (R7). It only turns things down.
- Does not know real dB SPL.

### 7.9 Acceptance criteria (draft)

1. Left-click toggles protection and the state survives a restart.
2. Playing the step test file in Chrome with the ceiling below its loud half produces one message naming Chrome. The reported overshoot is within +/-1 dB of an independent calculation (file level plus master gain).
3. The Zoom speaker test produces an event naming Zoom.
4. Idle CPU under 1% average, memory under 80 MB.
5. At most one message per 10 s per app during a long loud clip.
6. Survives unplug and replug of a device, and sleep and resume.
7. After killing the process while an app is turned down, restart restores that app's volume.
8. Exit leaves all app volumes as found.
9. If the optional slider cap is on, raising the Windows volume above it is put back within about 50 ms, with a message.
10. The same test sound played with the slider at a low and at a high position is held to the same ceiling X (tested quietly, with the headphones off).

---

## 8. Phase 2 detail: the limiter in the audio path

**Decision gate first** (section 6). Then pick one.

**After your clarification (2026-11-09):** you want a ceiling that holds even with the slider at 100. A reactive tool leaks the first tens of milliseconds of each loud burst at full level, which is the worst case exactly when the slider is high. So Phase 2, the limiter in the audio path, is what delivers that promise. It is also what evens out loud and quiet voices (R7): set a low ceiling, then raise the slider so the quiet people are audible. Phase 1 stays useful as the base (tray, toggle, messages) and as a first usable version.

**The ceiling needs no knowledge of the slider:** a limiter in the path works on the digital signal, so "ceiling X" means "never louder than X dB below the loudest the device can play, at slider 100". At lower slider positions everything is quieter by the slider's dB (your 20% is -23.7 dB), so it is still never louder than X.

### O2 route: Equalizer APO plus a VST2 limiter

- **For:** no routing changes; works for every app on the device it is installed on; low latency; reversible by uninstalling.
- **Against (evidence labels as in section 4.3):** unsigned component; needs the signature check disabled system-wide; last release March 2025; VST2 only in the stock build; no built-in limiter; Windows builds of LSP's limiter are paid and other free limiters have unverified licences; open tickets on 24H2/25H2; coexistence with the Realtek audio components is untested [test]; cannot process exclusive mode.
- **How our app would fit:** Guardian stays as the control panel. On/off and ceiling become edits to an Equalizer APO include file. Guardian's meters still produce the "how much" messages, because the per-app meters see what the app tried to send before the limiter.
- HANDOFF rule applies: no system-wide audio software without your explicit go-ahead.

### O3 route: virtual cable plus our own limiter

- **Chain:** apps, then CABLE Input (the virtual playback device), then our app captures CABLE Output, optional leveler, then look-ahead limiter (about 5 ms look-ahead), then the real headphones.
- **Routing:** Windows 11 can pick an output device per app (Settings, System, Sound, Volume mixer) [mine], so only Zoom and the browser need to go through the cable. Zoom's own speaker setting can also name the cable.
- **Hard parts:** clock drift between the cable and the real device (needs adaptive resampling); fail-closed behaviour (a watchdog must restore the real device as default if the app dies); Zoom's echo cancellation if you use speakers rather than headphones, since extra delay can confuse it [mine]; one 24H2 breakage report for VB-CABLE [reported]; closed-source driver.
- **Benefits:** user-level DSP code we fully control, exact gain-reduction numbers for R4, per-app routing, and the same DSP can later add the Zoom leveler (compressor with make-up gain, then limiter) for R7.

My lean if Phase 2 is needed: **O3** for guarantees and exact numbers. **O2** only as a quick, reversible experiment.

### O4: own APO

Not recommended (section 5).

---

## 9. Risks and gotchas

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| First tens of ms of a blast get through | Certain | Medium | Measure in the spike; Phase 2 if unacceptable |
| Exclusive-mode apps invisible and unprotectable | Medium | High | Untick exclusive mode (Phase 0 step 4); warn once in the app |
| Monitor outputs have software-only volume, so exclusive-mode apps bypass the cap and the meters there | Low | Medium | Untick exclusive mode on those devices if you ever use them |
| The cap undoes a volume change you meant to make | Medium | Low | Message with a one-click "raise the cap"; Pause option |
| Zoom plays on a device we are not watching | Medium | High | Watch all active outputs |
| Meter semantics differ from my assumptions (F2, F6) | Medium | Low | Spike S1 before the real build |
| Windows suppresses the messages (Focus, Do Not Disturb) | Medium | Medium | Tray flash, event log, optional overlay |
| App crashes while a program is turned down | Medium | Low to medium | Recovery file; restore at start and exit |
| Timer throttling in a windowless process (F9) | Medium | Low | High-resolution waitable timer; measure |
| Unsigned exe flagged by antivirus or SmartScreen | Low | Low | Build locally; do not distribute |
| Windows or driver update changes behaviour | Low | Medium | Phase 1 touches no driver |
| O2: security (signature check off), 25H2 compatibility, project age | Medium | High | Prefer O3; try O2 only as a reversible experiment |
| O3: silence if app dies, clock drift, cable breakage after Windows updates | Medium | High | Watchdog, tests, keep a way back |
| Setting a ceiling in dBFS is not a dB SPL guarantee | Certain | Medium | Say so in the UI; optional calibration later |

---

## 10. Test plan

### Test tones with exact levels (commands verified on this PC)

ffmpeg's built-in `sine` source is already about -18 dBFS, so use explicit amplitudes: 0.1 is -20 dBFS, 0.0316 is -30 dBFS, 0.708 is -3 dBFS.

```powershell
ffmpeg -f lavfi -i "aevalsrc=0.1*sin(2*PI*1000*t):s=48000:d=5"    tone_m20.wav
ffmpeg -f lavfi -i "aevalsrc=0.0316*sin(2*PI*1000*t):s=48000:d=3" a_m30.wav
ffmpeg -f lavfi -i "aevalsrc=0.708*sin(2*PI*1000*t):s=48000:d=3"  b_m3.wav
ffmpeg -i a_m30.wav -i b_m3.wav -filter_complex "concat=n=2:v=0:a=1" step_m30_to_m3.wav
ffmpeg -i step_m30_to_m3.wav -af volumedetect -f null -      # prints max_volume / mean_volume
```

Checked on 2026-11-09: `tone_m20.wav` gives max_volume -20.0 dB, ffmpeg's default sine gives -18.1 dB, the step file gives max_volume -3.0 dB.

**Safety:** these tones play on your real output. Before T1 to T3, take the headphones off your ears or set the Windows volume to about 10%, and raise it only on purpose. The -3 dBFS half of the step file is meant to be loud.

### Test cases

| # | Test | Expected |
|---|---|---|
| T1 | Play `tone_m20.wav` at Windows volume 100% and at 32% | Device meter reads about -20 dB both times (confirms F2) |
| T2 | Lower one app's slider by 6 dB while it plays the tone | Shows whether the session meter drops 6 dB (F6) |
| T3 | Play the step file with the ceiling at -30 and log the timestamps | Detection and turn-down times; the reaction-time estimate |
| T4 | Step file in Chrome while Zoom's speaker test plays | Separate events, correct app names |
| T5 | Real Zoom call with a second person making a loud noise | Event naming Zoom, plausible overshoot |
| T6 | A 30 s loud clip | At most one message per 10 s per app, plus a summary |
| T7 | Toggle, pause, restart | State persists as designed |
| T8 | Unplug and replug headphones; sleep and resume | Keeps working, no duplicate icons |
| T9 | Kill the process while an app is turned down, then restart | The app's volume is restored |
| T10 | Play from an exclusive-mode app | One-time warning; documented limitation |
| T11 | 8-hour soak | CPU and memory stay within the targets |
| T12 | Do Not Disturb on | Tray flash and log entry still happen |
| T13 | Zoom on a different output device | Events still appear |

---

## 11. Decisions I need from you

1. **Is the in-path limiter the goal?** Your "even at master 100, nothing louder than X" needs it (section 8). OK to build the tray app first as its base, then add the limiter, choosing between a virtual cable and Equalizer APO at that point? *(Most important.)*
2. **Units and behaviour.** Ceiling X in dB below the loudest the device can play (0 = slider at 100 with a full-scale sound), which is my default, or approximate dB SPL after a calibration step? And should X act only when something would really reach X at your ear (slider-aware, pure protection), or always as a digital ceiling (which also evens out loud voices at low slider positions)?
3. **Stack.** C# on .NET 10 (needs the SDK installed, my pick) or Python (works today, weaker for always-on)?
4. **Which output device does Zoom use?** Phase 0 step 3 finds out.
5. **Default action.** Notify and turn the app down, or notify only?
6. **Which devices to watch.** All active outputs (my default) or only the default one?
7. **Spike.** OK to run the one-hour throwaway measurement script (no install)?
8. **Name.** "Sound Guardian" or something else?
9. **If Windows suppresses toasts:** is a tray flash plus log enough, or do you want an always-on-top mini overlay?

**What happens after you answer:** I turn this into a short design document, have an independent reader check it, write the implementation plan, and build test-first. You can also say "just start" and I will begin with the spike using the defaults above.

---

## 12. Unverified and open

- Whether Zoom's "Same as System" follows the default device or the default communications device in general (on this PC both are the Headphones, so it does not matter today).
- The real reaction time of watch-and-react (still an estimate: measuring it needs a volume change, which was off limits). The meter period (10 ms), timer accuracy and poll cost were measured.
- Meter semantics F4 (loopback versus master volume) and F6 (session meter versus session volume) are untested. F9 was measured in a console process, not yet in the real tray app.
- Whether Windows remembers a lowered per-app volume across restarts (F10); needs a write, not done.
- Whether Stable volume is on in your YouTube (a 10-second look in the player's gear menu).
- What the "Audio enhancements" dropdown in Settings offers for the Headphones (HANDOFF section 3 notes it was not opened), and what HP Audio Control's pages offer.
- Which registry value holds the exclusive-mode checkboxes. My first guess was wrong; the checkbox in the Sound dialog is the authority.
- Whether NAudio 3.x still exposes `AudioEndpointVolume` (not in the release notes read).
- EarTrumpet's licence at source; SoundBrake's last-commit date (not shown).
- LSP price labels; whether VB-CABLE's Windows 11 driver is signed and how it behaves on 25H2; licences and ceiling parameters of the free VST limiters; Equalizer APO's licence (from memory).
- WHO/ITU numeric listening limits.
- Wale, WinSoftVol, FxSound, Voicemeeter's compressor behaviour: not inspected.
- Several search summaries were thin or conflicted; where a page was read the page wins, and I note one case in section 3 (YouTube Stable volume).

---

## 13. Sources

**This PC (read-only checks):** `winget search --id Microsoft.DotNet.SDK` lists 10.0.403 and 11.0 preview; .NET SDK 6.0.301 only; Python 3.14.0; Node, git, winget, ffmpeg 8.0.1, VS Code present; Zoom at `%APPDATA%\Zoom\bin\Zoom.exe`; Equalizer APO and VB-Audio software not installed; `UserDuckingPreference` not set. Also `HANDOFF.md` sections 2, 3 and 8.

**Prior art**
- https://github.com/eneswritescode/soundbrake
- https://sourceforge.net/projects/soundbrake/
- https://www.tech2geek.net/soundbrake-a-lightweight-tool-that-protects-your-hearing-by-monitoring-system-volume/
- https://github.com/zadzora/PeakFlow
- https://zadzi.itch.io/peakflow-simple-audio-volume-limiter-for-windows
- https://github.com/e-zijlstra/ez-volume-normalizer
- https://volumelimiter.sourceforge.io/
- https://sourceforge.net/projects/volumelimiter/
- https://horkan.com/?p=7697 (Equalizer APO with a free limiter, walkthrough)
- https://awesome.ecosyste.ms/api/v1/projects/github.com%2Fdechamps%2Fwinsoftvol

**Equalizer APO and plugins**
- https://sourceforge.net/projects/equalizerapo/files/
- https://sourceforge.net/p/equalizerapo/tickets/335
- https://sourceforge.net/p/equalizerapo/tickets/347
- https://sourceforge.net/p/equalizerapo/wiki/Configuration%20reference/
- https://sourceforge.net/p/equalizerapo/wiki/Developer%20documentation/
- https://itch.io/post/11306876 (stock build loads VST2 only)
- https://sourceforge.net/projects/eqapo64-with-vst3-support/
- https://www.kvraudio.com/forum/viewtopic.php?p=9252334
- https://lsp-builds.com/ and https://lsp-builds.com/terms and https://lsp-plug.in/?page=download
- https://www.kvraudio.com/product/dspplug-esquire3-by-dspplug
- https://soundshockaudio.com/loudmax

**VB-Audio**
- https://vb-audio.com/Cable/
- https://shop.vb-audio.com/en/win-apps/12-vb-cable.html
- https://forum.vb-audio.com/viewtopic.php?p=7505

**Microsoft Learn and NAudio**
- https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudiometerinformation
- https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudiometerinformation-getpeakvalue
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording
- https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync
- https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
- https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback
- https://learn.microsoft.com/cs-cz/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
- https://learn.microsoft.com/en-us/windows/apps/design/shell/tiles-and-notifications/send-local-toast-other-apps
- https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.builder
- https://www.nuget.org/packages/NAudio
- https://github.com/naudio/NAudio

**Windows 11 tray, Zoom, YouTube, safe listening**
- https://www.elevenforum.com/t/hide-or-show-notification-icons-on-taskbar-system-tray-in-windows-11.415/latest
- https://techcommunity.microsoft.com/discussions/windows11/why-does-windows-11-keep-hiding-my-system-tray-icons-taskbar-icons-missing/4472629
- https://community.zoom.com/meetings-2/using-windows-default-communication-device-instead-of-speakers-16742
- https://endicott.freshservice.com/support/solutions/articles/10000055830
- https://support.google.com/youtube/answer/14106294
- https://www.itu.int/dms_pubrec/itu-t/rec/h/T-REC-H.870-202203-I!!SUM-HTM-E.htm
- https://sourceforge.net/mirror/eartrumpet/ and https://github.com/File-New-Project/EarTrumpet

**Handoff sources** (Zoom and Windows/Realtek): see `HANDOFF.md` section 8.

---

## 14. Results of Phase 0 and spike S1 (2026-11-09, read-only)

**How it was done.** You asked me to do steps 1 and 2 myself, read-only. The spike was a throwaway C# file run with `dotnet run`. Every "set" method in its COM declarations was an inert placeholder, so it could not change a volume, mute or setting. It read devices, sessions, meters, volumes and timers. It played no sound and stored no audio, only levels. The file, its build cache and a few test WAVs were deleted afterwards. Nothing was changed on the PC.

### What was not done, and why

| Item | Why |
|---|---|
| Tone test (known-level tone through the meters) | Zoom had live audio on the Headphones at that moment. A tone would have disturbed the call and confounded the readings. Live Zoom audio served as the test signal. |
| F6: session meter before or after session volume | Needs a volume change. All session volumes are at 1.00 anyway. |
| F10: is a lowered app volume remembered | Needs a write. |
| Reaction time of "see peak, turn app down" | Needs a write. Only its parts were measured (meter period, timer accuracy, poll cost). My estimate stays at tens of milliseconds [mine]. |
| F4: loopback capture versus master volume | Not needed for Phase 1; would need audio capture. |
| YouTube Stable volume | Checking needs your signed-in YouTube; opening a video would add to your watch history and may play sound. It is a 10-second look for you: the video's gear menu, Stable volume. |

### Phase 0 results

| Step | Result |
|---|---|
| 2 Communications ducking | `UserDuckingPreference` is not set, so Windows' default applies. |
| 3 Zoom device | Zoom's live audio session is on **Headphones (Realtek(R) Audio)**. Headphones is also the default device for the console, multimedia and communications roles, so the communications-default question does not matter today. Zoom runs two processes; the active audio session was in one, the other had an inactive session. |
| 4 Exclusive mode | No override is stored in the registry for the active outputs, consistent with Windows' default (allowed) and with your screenshot (both boxes ticked on Headphones). My registry cross-check could not decode the flag itself, so the checkbox remains the authority. Not changed (read-only). |
| 5 HP Audio Control v2.32.260.0 | Read statically. Its manifest lists Dolby DAX and DTS APO components, Realtek render-effect classes, an equalizer, room correction, speaker test, ear calibration and microphone noise-reduction controls. The word "Loudness" appears in at least three of its DLLs, and "Normaliz" and "Night" in its main DLL. "Limiter", "Compressor" and "Dynamic range" do not appear. That hints at a loudness-type option but proves nothing about the UI. Opening the app is still worth two minutes (HANDOFF next step 2). |
| 1, 6 | Yours (YouTube setting; asking participants to enable Zoom's auto-adjust microphone volume). |

**Side findings**

- Windows lists 34 playback outputs, 30 of them "not present" (old devices). Only 4 are active: Headphones and Speakers (Realtek), C24-40 and VA2405-FHD (NVIDIA monitors). The app must filter by state Active.
- The two monitor outputs carry Microsoft's own effects components (WM LFX and WM GFX APO, the ones behind the Enhancements tab). Headphones and Speakers (Realtek) have no effect component registered in the endpoint's effects key. That is probably why only the Realtek outputs lack Loudness Equalization (HANDOFF section 3). So Loudness Equalization is probably already available on the monitor outputs [mine, inferred; not checked in the UI].
- For Equalizer APO on the Realtek outputs, no other effect component is registered there, so nothing visibly competes. Still untested [test].

### Measurements

| Measurement | Result |
|---|---|
| Mix format, all four active outputs | 48000 Hz, 2 channels, 32-bit float |
| Device period, all four | default 10.00 ms, minimum 3.00 ms |
| Headphones master volume | -16.0 dB (34%); range -65.2 to 0.0 dB in 0.03 dB steps; hardware volume and mute (mask 0x3) |
| Speakers master volume | -6.0 dB (67%); same range; mask 0x3 |
| Monitor outputs | 0.0 dB (100%); range -96 to 0 dB in 1.5 dB steps; mask 0x2 (mute only) [mine: decoding] |
| Sessions on Headphones | system sounds, Chrome, Zoom (active), Zoom's second process, and one video-editing app; every session volume 1.00 |
| Timer: default `Sleep(1)` | median 15.56 ms (min 8.77, max 16.94) |
| Timer: after `timeBeginPeriod(1)` | median 1.58 ms (max 3.16) |
| Timer: high-resolution waitable timer, 10 ms period | median 10.32 ms, 95th percentile 10.67, max 11.19 |
| Poll loop, 2 ms timer plus COM meter reads (15 s, 6865 ticks) | median 2.12 ms, 95th percentile 2.55, max 10.70 |
| Cost of one meter read | about 70 microseconds (426 microseconds for the device meter plus 5 session meters) |
| Re-enumerating sessions | 1.71 ms |
| Device-meter update interval | values change every 8 to 12 ms (10th percentile 8.7 ms, median 11.0 ms); longer gaps are stretches where the peak stayed the same |

### F2 result: is the device meter before or after the master volume?

With live Zoom audio (6282 ticks, no other app audible), the device meter minus Zoom's session meter was a median **+0.10 dB** (10th to 90th percentile -0.18 to +0.57 dB). If the device meter were after the master volume it would read about 16 dB lower. So it is **before** the master volume, as Microsoft's page says [verified, measured].

### Zoom on this PC, one 15-second sample (levels only)

| | digital (dBFS) | effective (master -16 dB) |
|---|---|---|
| median | -10.4 | -26.4 |
| 90th percentile | -5.7 | -21.7 |
| 99th percentile | -4.2 | -20.2 |
| maximum | -4.1 | -20.1 |
| per-second peak: quietest / median / loudest | -9.7 / -6.7 / -4.1 | -25.7 / -22.7 / -20.1 |

The per-second spread was only 5.5 dB, which looks like one steady speaker. A call with a very quiet person and a loud one would show a much larger spread. Treat this as one example, not a profile.

### What this changes in the plan

1. **An optional slider cap** (section 7.2b) is a cheap extra, not the main protection. It was my idea, not your request. Slider mapping measured on your Realtek Headphones: 20% = -23.7 dB, 34% = -16.0 dB, 100% = 0 dB (Speakers: 67% = -6.0 dB).
2. **The default soft ceiling** is no longer "median + 12 dB" (section 7.3).
3. **Polling** is adaptive: active sessions only (section 7.2).
4. **Timers**: use a high-resolution waitable timer (F9 resolved).
5. **Your "even at master 100" request needs Phase 2** (section 8): a reactive tool leaks the first tens of milliseconds at full level.
6. **Event attribution**: Zoom has two processes, so group sessions by process name, not by process ID.
