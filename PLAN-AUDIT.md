# Audit of PIPELINE-LIMITER-PLAN.md

| | |
|---|---|
| Date | 2026-11-10 |
| Audited | `PIPELINE-LIMITER-PLAN.md`. The text before the audit is saved as `PIPELINE-LIMITER-PLAN.before-audit.md` (SHA-256 `079E05880AC93F8D65DB0307F26EEC432509EE8058CA3CC60A8DB5CABC20856C`, unchanged). |
| What was changed | Targeted edits to the plan; every change is listed with its reason in the plan's section 13. |
| Method | Primary sources (Microsoft Learn, Microsoft's Windows-driver-samples, Equalizer APO's own source), other projects' reports marked as such, and read-only checks on this PC (section 7). |
| Changes on the PC | None. No installs, no registry writes, no service restarts, no plug-in registration, no audio played. Files written: the edited plan, its backup, this file and `GAME-PLAN.html`. |

Tags used below: **VERIFIED** (a source or a command output is given), **REPORTED** (another project or person says so; not checked by Microsoft text or on this PC), **INFERRED** (reasoned from verified facts), **UNVERIFIED** (open; the test that settles it is named).

## 1. Verdict

The core design is right and delivers the ask: a limiter plug-in placed last in the Headphones effect chain, computing its own ceiling T = min(0, X − V, X + R) from the slider's dB value, holds "nothing louder than X at the ear" whatever the slider says. Nothing in the primary sources rules it out.

Before the audit the plan had two design errors that would have moved the ceiling (it cited a volume value that is not dB; it smoothed every change over 100 ms, which lengthens the slider-jump leak), it stated two undocumented things as near-facts (the run order inside a composite effect list; that the test key is required), and it put the riskiest changes first (test key, a third-party install, a reboot) while lacking ear-safety rules in the code and an undo tested before the first registration. All of this is fixed by targeted edits. Four questions can only be settled on the PC; each now has a cheap test placed before the step that depends on it. **With the edits, the plan is fit to approve step by step.**

## 2. Findings by severity

### Critical (could hurt ears or leave the PC in a bad state)

**F1. No ear-safety rules in the plug-in and no safe first-test protocol.** The plan had a safety clipper at T, but no rule that the gain never exceeds 1, no handling of NaN or infinity (a NaN can become a full-scale sample when the driver converts to whole numbers [INFERRED]), no denormal handling, no development clamp, and its first live registration was on the Headphones.
*Fix:* rules in plan 4.2 item 1 (gain ≤ 1; final clamp to ±T that turns NaN or infinity into 0, compiled with `/fp:precise`; denormals flushed with the floating-point mode restored; a -20 dBFS clamp in development builds; a crash-loop guard); the stricter rule in section 6; the first load on a monitor output (M2); Headphones only afterwards, off the head, slider at 10% or less, -20 dBFS tone only (M3); risk rows 11 and 12.

**F2. No undo written and tested before the first registration.** The only rollback was "uninstall reverses every step", first exercised after a real install.
*Fix:* a one-step undo (plan section 7): writes back the exact before-values, removes the registration, deletes the test key only if the plan set it, restarts audio in the right order and compares with the M0 snapshots. It is rehearsed in M1 on a scratch copy of the keys under `HKCU` plus a dry-run on the real keys, and used for real on the monitor at the end of M2, before the Headphones are touched.

**F3. Changes came before the no-change work, and gates were missing.** Old M0, labelled "no installs", started by unticking exclusive mode (a PC change). Old M1 installed Equalizer APO (admin, test key, reboot) before any code was written. The approvals table had no undo column, and restarts were not gates.
*Fix:* M0 and M1 change nothing; the first change is M2 on a harmless output without the key; approvals A1-A10 each say what changes and how it is undone; every audio restart (A5) and the reboot after the key (inside A6) is its own OK; exclusive mode moved to M5 (A9); fault injection in M5 is its own OK.

### High (would make the ceiling wrong, or the plan fail)

**F4. The volume value the plan cited is not dB.** The plan subscribed to the endpoint-volume notification "available from build 22000" [V2][V33]. That notification carries `fMasterVolume`, "normalized to the range from 0.0 to 1.0 ... described by a nonlinear, audio-tapered curve" [V38]; Microsoft's own sample stores exactly that field [V2]. Turning it into dB needs this device's curve, which Microsoft does not publish. On this output the measured slider at 20% is -23.7 dB [M], while 20·log10(0.20) gives -14.0 dB: 9.7 dB apart. Where R does not cap T, such an error goes straight into the ceiling, and another curve could err towards louder.
*Fix:* use `APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME2` with `masterVolumeInDb`, through `IAudioProcessingObjectNotifications2::GetApoNotificationRegistrationInfo2`, available from build 22621 [V36][V37]; this PC is 26200 [M]. Plan sections 2 and 4.2 item 1.

**F5. Smoothing every change widens the slider-jump leak.** Section 2 bounds the leak by the sound already on its way (about 10-30 ms [INFERRED]). But 4.2 item 2 and 4.6 ramped every parameter change over 100 ms, so after a slider jump up, T would reach its new, lower value only after the ramp: up to min(R, jump) dB over X for about 100 ms [INFERRED].
*Fix:* tightening (slider up, X lowered, On) acts at once through the 5 ms look-ahead; only relaxing ramps. Plan sections 2, 4.2 item 2 and 4.6.

**F6. "Route C needs the test key" was assumed, not shown.** The plan cited [V3], which says only that an APO with an embedded manifest fails without the key. Microsoft's list of load failures names "The graph is running protected content, and the APO is not properly signed" [V42], which ties the signature check to DRM-protected playback. On this PC `audiodg` runs with no process protection and with signature, image-load and dynamic-code policies all 0 [M]. One project reports an unsigned plug-in loading with the key at 0 [V46, REPORTED]; others call the key required [V47, REPORTED]; Equalizer APO's installer always sets it [V44], so its wide use proves nothing either way. **UNVERIFIED.**
*Fix:* M2 tries a no-key load on a harmless output first; the key is asked for only if that fails (M2-K, gate A6), with its reboot as a separate OK. Plan sections 1, 3, 4.2 item 5, 5, 8 row 2, 9 D3 and A6.

**F7. The run order inside the composite list was stated as near-fact.** The plan said "[I: list order = run order; verified in M2]", but nothing had verified it, and Microsoft's three composite-key pages describe the list form only, with no word on order [V4][V49]. If a vendor effect with gain ran after ours, the ceiling would not hold.
*Fix:* marked UNVERIFIED; M3 step 3 checks it by switching on an HP Audio Control effect that changes the level and comparing the plug-in's output peak with loopback.

**F8. Sandbox and file access were unspecific, and the load test depended on files.** The plan said only "the ACL needs the sandbox SIDs [V27]". `C:\ProgramData` has no app-container entries by default [M]. If the heartbeat file were blocked, "loaded but sandboxed" would look like "not loaded".
*Fix:* the DLL goes to `C:\Program Files\SoundCeiling\`, whose ACL already admits Users and both app-package groups [M]; `%ProgramData%\SoundCeiling` gets explicit entries, read-only for audiodg and app containers except a `telemetry` subfolder, so no sandboxed app can raise the ceiling; the M2 load test is measured in loopback (a test build halves the sound, -6.02 dB) and needs none of our files; the effects property store [V40] is the fallback channel. Plan 4.1, 4.2 items 2 and 3, M2.

### Medium

**F9. The Equalizer APO trial did not test our path.** Its installer always sets the key [V44]; it writes only the single slots and never the composite slots our plug-in uses [V45]; it costs two installs, the key and a reboot. Over-building for the ask. *Fix:* now an optional diagnostic, each step with its own OK.

**F10. Toolchain assumptions.** The plan planned Visual Studio 2026 and "the WDK if needed". The SDK installed on this PC holds `baseaudioprocessingobject.h` and the base-class libraries [M], and Microsoft points to the SDK's `Include\um` folder [V42], so the WDK is not needed. ATL is not installed [M], so the COM part is hand-written with aggregation, as Equalizer APO does [V39]. Microsoft's SwapAPO sample uses ATL and a WDK project type [V43]: a model to read, not to build. *Fix:* plan 4.2 item 1, M1, section 12, risk 9.

**F11. "Audio enhancements: Off" was promised to switch off every APO.** Microsoft's text limits that flag to effects installed by the driver's INF [V41]. Whether it bypasses a hand-registered plug-in is UNVERIFIED. *Fix:* tested in M2; the rollback text no longer promises it.

**F12. A crash inside processing can repeat.** The 10-failure switch-off counts CoCreateInstance, IsInputFormatSupported, IsOutputFormatSupported and LockForProcess [V42]; a crash in APOProcess is not on that list [INFERRED], so it may repeat at every audio restart. *Fix:* crash-loop guard; structured-exception guard; one-step undo; risk row 12.

**F13. The SYSTEM repair task was in version 1.** It is a lasting system-wide change that the ask does not need. *Fix:* optional after version 1; version 1 has a Repair button that asks for admin.

**F14. The emergency cap margin was too small.** It lowered the slider to X − 1 dB, but between samples content below 12 kHz can rise up to 3 dB above the digital peak (plan section 2). *Fix:* X − 3 dB, with the caveat that strong content above 12 kHz could overshoot more.

**F15. What stays unprotected was not stated.** *Fix:* plan 4.7 lists it (exclusive mode while allowed, ASIO and kernel streaming, Off, DOWN, DRM playback without the key, gain after the engine) and the panel never shows "Protected" from settings alone; M4 acceptance checks it.

**F16. Restart order.** The plan restarted the two audio services without an order, while another developer found that the endpoint builder caches effect settings and used stop Audiosrv, stop AudioEndpointBuilder, start AudioEndpointBuilder, start Audiosrv [V27, REPORTED]. *Fix:* plan 4.2 item 5; restarts only on OK.

**F17. The monitor test needed two guards.** The monitors' volume is done in software (mask 0x2 [M]), which could blur the loopback reading; and the load test assumes loopback is taken after the plug-in [INFERRED]. *Fix:* the monitor's slider at 100% for the test, and `tasklist /m` (admin, read-only) to tell "not loaded" from "loopback taken before the plug-in".

### Low

**F18. Time and effort estimates** ("9 to 14 working days", the Effort column, milestone durations, "Total", "for two minutes", "one hour", "8-hour", "over months"): all removed. Timings that are part of the design or a test limit (every 2 seconds, within 5 seconds, 100 ms ramps) stay.

**F19. Registration details.** Flags 0xF match the Sound Research EFX on this PC [M]; the SDK's APO_FLAG_DEFAULT is 0xE [M: `audioenginebaseapo.h`]. The plug-in must accept every float format it is offered, because each rejected format counts towards the 10-failure switch-off [V42].

**F20. Evidence hygiene.** "The APO sits before the hardware volume" was [I]; it now rests on the hardware-volume mask [M] and Microsoft's statement that software volume is substituted only where hardware has none [V35], with the decisive check in M3. References to deleted tests (T-M1-2) now point to M2. New evidence rows V35-V49 and an audit measurement row were added.

## 3. The four judgments

### 3.1 Does the plan deliver Ariel's ask?

| Ariel's ask | Plan | Judgment |
|---|---|---|
| "even if master is 100, nothing is more loud than X" | Reading (b): T = min(0, X − V, X + R), computed inside the plug-in from the dB slider value; safety clipper at T | Delivered while the plug-in runs on that output. Bounded leak on a slider jump up: at most min(R, jump) for the sound already on its way [INFERRED], now not lengthened by ramps (F5) |
| Messages when it goes over | Tray notification with the exact overshoot from the plug-in's own input and output meters (plan 4.4) | Delivered |
| On/off | Off ramps T to 0 dBFS; never a bypass, so no click | Delivered |
| Icon in the hidden tray area | The existing tray app, extended (M4) | Delivered; the monitor version already runs |
| Zoom loud vs quiet talkers | Loud peaks pressed to the ceiling, quiet talkers untouched; R lets the slider go up while the ceiling holds | Delivered |

Gaps (now stated in the plan): only outputs that carry the plug-in are protected; exclusive mode (until unticked), ASIO and kernel streaming bypass it; "dB" means dB below the loudest the output can play, not sound-pressure level (plan section 2); DRM playback without the key may run unprotected.

Over-building (now optional): the Equalizer APO trial, the SYSTEM repair task, the emergency cap. Kept on purpose: reading (a) as a choice (D1) and Voicemeeter as Plan B.

### 3.2 Technical correctness, items a to g

**a. Is `DisableProtectedAudioDG = 1` needed on build 26200?** UNVERIFIED. For "not needed for ordinary playback": the only signature-related load failure Microsoft lists is for protected content [V42]; `audiodg` has no protection or signature policy here [M]; one report of an unsigned load at key 0 [V46]; the Vista design ties the protected process to DRM [V48]. For "needed": other projects call it required [V47]; Microsoft's manifest text speaks of a "protected environment" [V42]. Equalizer APO's use is no evidence either way [V44]. The plan now runs the cheapest decisive test first: a no-key load on a harmless monitor output (M2), key only on failure (M2-K).

**b. Where is the slider applied relative to EFX?** Strongly supported, not yet measured with a plug-in. The Headphones do volume in hardware (support mask 0x3 [M]), and Windows substitutes software volume only where the hardware lacks it [V35], so the slider acts after Windows' engine, after every APO. RESEARCH-AND-PLAN F2 is consistent: the device meter matched Zoom's session meter within 0.10 dB at master -16 dB [M]. Still open: whether a vendor effect applies the volume digitally [UNVERIFIED]. Cheapest experiments: a zero-code check in M0 (the tray app's tone and device meter at 10% and 20%), then the decisive one in M3 (the plug-in's own input peak at 10% and 20% must both read -20.0 dBFS). The formula T = min(0, X − V, X + R) is right if M3 passes; if the input reads -20 + V, the plug-in must not subtract V again. On the monitors the volume is in software (mask 0x2 [M]) and its position relative to EFX is UNVERIFIED; the plan's M2 sidesteps it with the slider at 100%.

**c. The composite lists `{d04e05a6…},13/14/15`.** Meaning VERIFIED: `,13` stream (SFX), `,14` mode (MFX), `,15` endpoint (EFX) composite keys, each "a reg multistring ... to allow multiple effects in a single position" [V4][V49]. Type VERIFIED on this PC: REG_MULTI_SZ [M]. Order: not documented on any of the three pages [V4][V49], so "ours last = runs last" is UNVERIFIED; M3 step 3 tests it. Updates: Microsoft says the INF tells the endpoint builder to set these CLSIDs into the effects store [V49], and others report that driver installs and updates rewrite the lists [V28][V47, REPORTED]; the plan's health check detects a lost entry and Repair re-adds it. Users can also write the list on this PC [M: `(A;CIID;CCDC;;;BU)`], which is risk 1.

**d. Is `CBaseAudioProcessingObject` in the SDK?** VERIFIED on disk: `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\baseaudioprocessingobject.h` with `audioenginebaseapo.h`, `audioengineextensionapo.h` and `AudioAPOTypes.h`; libraries `audiobaseprocessingobject.lib`, `AudioBaseProcessingObjectV140.lib`, `audioeng.lib`, `audiomediatypecrt.lib` in `Lib\10.0.26100.0\um\x64\` [M; also checked by the coordinator]. Microsoft names the header and the SDK's `Include\um` folder [V42]. So the WDK is not needed. Linking: REPORTED by the install (a test DLL deriving from the base class linked with `/NODEFAULTLIB:atls.lib`); this audit did not re-run it. ATL is absent [M], so the plan uses hand-written COM with aggregation as in Equalizer APO [V39]; SwapAPO is a model only [V43].

**e. Deployment.** DLL location: `C:\Program Files\SoundCeiling\`, whose ACL already gives read and execute to Users, ALL APPLICATION PACKAGES and ALL RESTRICTED APPLICATION PACKAGES [M]. COM keys under `HKLM\SOFTWARE\Classes` inherit read for app containers and the `registryRead` capability [M], the same ACL the Realtek classes that load today have [M]. Config and telemetry: `C:\ProgramData` lacks app-container entries [M], so the installer sets an explicit, narrow ACL (F8). Identity: Audiosrv runs as LocalService and is `audiodg`'s parent, session 0 [M]; whether `audiodg` runs as LPAC is REPORTED [V27] and consistent with the `lpacMedia` read entry on the effect-list key [M]; its token could not be read without admin (error 5). The tray app runs in the user session and reaches `audiodg` only through files (or the effects property store [V40]); no user-session path is needed for the ceiling itself, because the plug-in computes T from Windows' own notification.

**f. Failure and ear safety.** Covered after the edits: crash or hang (crash-loop guard, heartbeat, DOWN, one-step undo; a hang gives silence, not loudness), NaN and denormals (final clamp, flush with mode restore), format change (accept every float format; tested on the bench), sleep and resume and device switch (M2 and M3 acceptance), full-scale noise (gain ≤ 1, development clamp at -20 dBFS, bench edge cases), one-step undo written and rehearsed before the first registration, first live tests on a monitor output, then the Headphones off the head at 10% or less with a test tone.

**g. What stays unprotected, and the panel.** Listed in plan 4.7: exclusive-mode apps while allowed, ASIO and kernel streaming [V28], anything while DOWN or Off, DRM playback without the key [V42], and gain after Windows' engine. The panel shows "Protected" only from a fresh heartbeat with the plug-in last in the list, otherwise "NOT protected" with the reason.

### 3.3 Order and gates

New order: **M0** prepare (changes nothing) → **M1** build and bench-test, rehearse the undo (changes nothing) → **M2** first real load on a monitor output without the key (A3 copy and register, A4 add to that output, A5 restart) → **M2-K** only if that failed (A6 key, reboot asked separately) → **M3** Headphones at low volume (A7 add, A5 restart) → **M4** tray app as remote control (A8 start with Windows) → **M5** close the gaps (A9 exclusive mode; A10 optional repair task, emergency cap, more outputs). Every gate names what changes and its undo (plan section 9).

### 3.4 Estimates

Every time and effort estimate was removed (F18).

## 4. Verified and unverified claims

| # | Claim | Status | Source or command |
|---|---|---|---|
| 1 | EFX "is always applied, even to raw streams" | VERIFIED | [V1] |
| 2 | Loopback taps the stream "prior to any volume or muting being applied" | VERIFIED | [V2] |
| 3 | Loopback is taken after the APOs | INFERRED; tested in M2 step 4 | [V2][V28] |
| 4 | The build-22000 volume notification gives a 0-1 audio-tapered value, not dB | VERIFIED | [V38]; sample in [V2] |
| 5 | `ENDPOINT_VOLUME2` gives `masterVolumeInDb`; `Notifications2` needs build 22621 | VERIFIED | [V36][V37] |
| 6 | Windows sends the current value when the APO registers | VERIFIED (Microsoft text; one Microsoft sample also queries once) | [V2][V37] |
| 7 | Headphones do volume in hardware (mask 0x3); monitors in software (mask 0x2) | VERIFIED (this PC) | RESEARCH-AND-PLAN §14 [M] |
| 8 | Windows adds software volume only where the hardware has none | VERIFIED | [V35] |
| 9 | No vendor effect applies the slider digitally | UNVERIFIED; M0 step 5, M3 step 2 | F2 consistent [M] |
| 10 | `,13 ,14 ,15` are the SFX, MFX, EFX composite lists, reg multistrings | VERIFIED | [V4][V49]; REG_MULTI_SZ on this PC [M] |
| 11 | The order inside a composite list is the run order | UNVERIFIED; M3 step 3 | Not on any Microsoft page [V4][V49] |
| 12 | Driver installs and updates rewrite the effect lists | REPORTED (Microsoft text implies it) | [V28][V47]; [V49] |
| 13 | Users can set values on the Headphones `FxProperties` key | VERIFIED (this PC) | SDDL `(A;CIID;CCDC;;;BU)` |
| 14 | The key grants read to `lpacMedia` and set-value to `audioDeviceConfiguration` | VERIFIED (this PC) | SDDL plus `DeriveCapabilitySidsFromName` |
| 15 | `audiodg` runs in an LPAC sandbox | REPORTED; consistent with 14 | [V27] |
| 16 | `audiodg`: PS_PROTECTION 0; signature, image-load, dynamic-code policies 0 | VERIFIED (this PC) | `NtQueryInformationProcess`, `GetProcessMitigationPolicy` |
| 17 | Audiosrv runs as LocalService and is `audiodg`'s parent, session 0 | VERIFIED (this PC) | `Win32_Service`, `Win32_Process` |
| 18 | Signature check tied to protected content | VERIFIED (Microsoft text) | [V42] |
| 19 | Ordinary playback on 26200 loads an unsigned plug-in without the key | UNVERIFIED; M2 | [V46] reports yes; [V47] reports no |
| 20 | The key is read only at boot | REPORTED | [V27]; Equalizer APO asks for a reboot [V15] |
| 21 | Equalizer APO's installer always sets the key; uninstall deletes it | VERIFIED | [V44] |
| 22 | Equalizer APO never writes the composite keys | VERIFIED | [V45] |
| 23 | DRM-protected playback may refuse to play with the key set | VERIFIED (Equalizer APO documentation) | [V16] |
| 24 | The base-class header and libraries are in SDK 10.0.26100.0 | VERIFIED (this PC) | File listing; [V42] |
| 25 | A class deriving from the base class links on this PC | REPORTED (install); not re-run | Memory note of the install |
| 26 | No WDK, no ATL (`atlbase.h`) installed | VERIFIED (this PC) | File listing |
| 27 | SwapAPO needs a WDK project type and uses ATL | VERIFIED | [V43] |
| 28 | Equalizer APO's COM class is hand-written, with aggregation | VERIFIED (GitHub copy of its source) | [V39] |
| 29 | APO_FLAG_DEFAULT = 0xE | VERIFIED (this PC) | `audioenginebaseapo.h` |
| 30 | The 10-failure rule counts CoCreateInstance, IsInput/OutputFormatSupported, LockForProcess | VERIFIED | [V42] |
| 31 | A crash inside APOProcess is not counted | INFERRED | From 30 |
| 32 | `Disable_SysFx` covers INF-installed effects | VERIFIED (Microsoft text) | [V41] |
| 33 | "Audio enhancements: Off" also bypasses a hand-registered plug-in | UNVERIFIED; M2 | [V41] |
| 34 | Effects property store is writable by non-admin apps and readable by APOs | VERIFIED (Microsoft text) | [V40] |
| 35 | That store works for a plug-in registered without an INF | UNVERIFIED (only if needed) | [V40] |
| 36 | `C:\Program Files` admits Users and both app-package groups; `C:\ProgramData` has no app-package entries | VERIFIED (this PC) | `Get-Acl` |
| 37 | Classes keys inherit read for app containers and `registryRead` | VERIFIED (this PC) | Registry SDDL |
| 38 | Restart order: stop Audiosrv, stop AEB, start AEB, start Audiosrv | REPORTED | [V27] |
| 39 | Exclusive-mode properties `{b3f8fa53…},3` and `,4` | REPORTED; neither set on this PC | [V47]; endpoint dump [M] |
| 40 | Monitors' EFX `,7` and its mode key are empty | VERIFIED (this PC) | Endpoint dump |
| 41 | Realtek and Sound Research DLLs are WHQL-signed | VERIFIED (this PC) | Signature check |
| 42 | Between samples, content below 12 kHz rises at most 3 dB above the peak sample | INFERRED (formula); measured at 12 kHz | Plan section 2 [M] |

## 5. Open questions for Ariel

1. **First real load (M2):** may the plug-in be tried first on one monitor's audio output, C24-40 or VA2405-FHD, with nothing plugged into its audio jack? Which one, and does it have built-in speakers?
2. **DRM:** do you play protected streams on this PC (for example Netflix in Edge or the Netflix app)? It decides whether the DRM test matters and what the panel must say.
3. **Start values:** reading (b), X = -28 dB at the ear and R = 10, then tuned from your own "loud talker is bearable" moment?

Later decisions (plan section 9, D1-D9) and every gate are asked when their turn comes.

## 6. Sources

Microsoft:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture [V1]
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/windows-11-apis-for-audio-processing-objects [V2][V40]
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects [V3][V42]
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-endpointeffectclsid [V4]
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-streameffectclsid [V49]
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/pkey-compositefx-modeeffectclsid [V49]
- https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/nn-audioengineextensionapo-iaudioprocessingobjectnotifications [V33]
- https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-queryhardwaresupport [V35]
- https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/ne-audioengineextensionapo-apo_notification_type [V36]
- https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/ns-audioengineextensionapo-audio_volume_notification_data2 [V36]
- https://learn.microsoft.com/en-us/windows/win32/api/audioengineextensionapo/nf-audioengineextensionapo-iaudioprocessingobjectnotifications2-getaponotificationregistrationinfo2 [V37]
- https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/ns-endpointvolume-audio_volume_notification_data [V38]
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-disable-sysfx [V41]
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/protected-user-mode-audio--puma- [V48]
- https://learn.microsoft.com/en-us/archive/blogs/larryosterman/what-is-audiodg-exe [V48]
- https://github.com/microsoft/Windows-driver-samples (SwapAPO: `audio/sysvad/APO/SwapAPO/SwapAPO.vcxproj`, `SwapAPO.h`) [V30][V43]

Equalizer APO:
- https://sourceforge.net/p/equalizerapo/code/ci/main/tree/Setup/Setup.nsi [V10][V44]
- https://sourceforge.net/p/equalizerapo/code/ci/main/tree/DeviceAPOInfo.cpp [V11][V45]
- https://sourceforge.net/p/equalizerapo/wiki/Developer%20documentation/ [V16]
- https://sourceforge.net/p/equalizerapo/tickets/341/ , https://sourceforge.net/p/equalizerapo/tickets/364/ [V17]
- https://raw.githubusercontent.com/TheFireKahuna/equalizerAPO64/main/EqualizerAPO/EqualizerAPO.h (a GitHub copy of the official source) [V39]

Other projects (REPORTED):
- https://github.com/li589/AuraDSP/issues/5 [V27]
- https://github.com/dechamps/APO [V28]
- https://github.com/SilvunDev/goxlr-hub/pull/9 [V46]
- https://github.com/ferhad24/VolumeX , https://github.com/denoso1993/soniclift , https://techcommunity.microsoft.com/discussions/windowspowershell/disable-exclusive-mode-for-all-audio-devices-via-ps/3653408 [V47]

## 7. Read-only checks run on this PC

None of these changes anything; all ran without admin rights.

- Endpoint dump: for each active render endpoint under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render`, every value in `Properties` and `FxProperties` with its registry type (`Get-ItemProperty`, `RegistryKey.GetValueKind`).
- Effect DLLs: `InprocServer32` paths of the Realtek and Sound Research CLSIDs, and their signatures.
- `FxProperties` security: `[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(<key>, $false).GetAccessControl()` and its SDDL (the `Get-Acl` provider path failed on the braces).
- Capability SIDs: `DeriveCapabilitySidsFromName` (kernelbase.dll, through `Add-Type`) for candidate capability names, matched against that SDDL: `lpacMedia` and `audioDeviceConfiguration` matched.
- `audiodg` protection: `OpenProcess` with query-limited rights, `NtQueryInformationProcess` (ProcessProtectionInformation) and `GetProcessMitigationPolicy` (Signature, ImageLoad, DynamicCode); Explorer queried the same way as a comparison. `OpenProcessToken` was refused (error 5), so the token was not read.
- Service and process facts: `Get-CimInstance Win32_Service` (Audiosrv, AudioEndpointBuilder) and `Win32_Process` (audiodg's parent and session); OS build from `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`.
- Toolchain: listings of `C:\Program Files (x86)\Windows Kits\10\Include\*\um` and `Lib\*\um\x64`, of the Build Tools' `VC\Tools\MSVC` (with `cl.exe`) and a search for `atlbase.h`; `audioenginebaseapo.h` read for the APO_FLAG values.
- Folder security: `Get-Acl` on `C:\Program Files`, `C:\ProgramData` and the Realtek effect DLL folder in the DriverStore.
- Classes security: SDDL of `HKLM\SOFTWARE\Classes\CLSID`, a Realtek CLSID with its `InprocServer32`, and `AudioEngine\AudioProcessingObjects`.
- Backup and diff: `Copy-Item` and `Get-FileHash` for the backup; `git diff --no-index` (no repository created) to list the changes for section 13.

## 8. What this audit did not do

- No test on the PC: whether the key is needed, where the slider sits relative to the plug-in, the order inside the list and the sandbox behaviour stay open until M0, M2 and M3.
- It did not re-run the install's link test.
- It did not read `audiodg`'s token, so LPAC is reported, not measured.
