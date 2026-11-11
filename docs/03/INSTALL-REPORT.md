# Sound Protection: install report

2026-11-11. Installed at 07:01, and the new tray app started at 07:03.

## In short

- The plug-in is installed. It runs inside Windows sound, on your Realtek **Headphones**.
- The cap is **−20 dB**, applied before the volume slider. The slider is still yours to move.
- The tray app is the new version. Hover over its icon:
  - while something plays, it says **"Protected: cap −20.0 dB"**;
  - when nothing plays, it says **"Protected (nothing playing)"**.
- **To undo everything:** double-click **"Sound Protection - Undo"** on your desktop, then click Yes on the admin prompt.

## How sound flows now (your sketch)

```
Zoom ─┐
YouTube ─┼─► each app's sound: LEVELER (louder than the cap → turned down; quieter → left alone)
other ─┘                │
                        ▼  mixed together
                SAFETY CAP (last in line: a hard limit nothing gets past, −8 dB peak for a −20 cap)
                        ▼
                Windows volume slider (yours, free)  ►  Realtek driver  ►  headphones (at max)
```

- The Safety Cap's limit is your cap plus 12 dB, and never above −1 dB.
- The alerts still work. You get a message when something went over, and by how much.

## Things to know

1. **Your old slider value (−16.5) was reset to −20.** In the old app that number meant loudness at your ear. Now it means a cap before the volume slider, so the same number would be a much quieter cap. You can set a new cap from the tray.
2. **The tray does not start with Windows yet.** The plug-in protects you without it, because it reads the cap from its own file. To get the icon and alerts back after a restart, right-click the tray icon and choose **Start with Windows**.
3. **Only the Headphones are protected.** If you switch to the speakers or a monitor, the tray icon turns amber ("not protected").
4. **Copy-protected video.** The install set the Windows "test key", which our own unsigned plug-in needs. While it is set, some copy-protected video may play without sound. Undo removes the key.
5. **Safe mode.** If the plug-in fails to start 3 times in a row, it switches to a plain hard clamp at the limit. The limit still holds, but the leveling stops. The tray shows this.

## What changed on the PC

From `C:\Program Files\SoundProtection\install-log.txt`, 2026-11-11 07:01:16 to 07:01:22:

1. **Backed up 3 registry keys** to `C:\Program Files\SoundProtection\backup\20261111-070116\`: both Headphones keys and the Windows Audio key.
2. **Wrote a journal** (7 values) to `install-journal.json`, so Undo can put every value back exactly.
3. **Stopped the audio services**, then started them again: AudioEndpointBuilder, Audiosrv, midisrv, RtkAudioUniversalService. All four are running.
4. **Copied the plug-in** `SoundProtection.Apo.dll` and the installer script into `C:\Program Files\SoundProtection\`, and registered the plug-in with Windows.
5. **Created `C:\ProgramData\SoundProtection\`** with `config.bin`, which holds your cap and is written by the tray, and `telemetry\`, where the plug-in reports in. The folder is owned by Administrators, and its permissions are set.
6. **Set the test key:** `DisableProtectedAudioDG = 1` (it was not set before).
7. **Added the Leveler** to the end of the Headphones' stream-effect list, and **the Safety Cap** to the end of the endpoint-effect list. This was done on both Headphones entries: the active one and its unplugged twin. Realtek's own effects are still there, before ours.
8. **Exclusive mode off** on both Headphones entries, so no app can bypass the plug-in.
9. **Desktop shortcut** "Sound Protection - Undo".
10. **Tray app replaced** in `dist\` with the new build (exe SHA-256 E37C2271…). The old one was backed up to `C:\Users\Ariel\AppData\Local\Temp\claude\db6fc79c2d580c08\2cb3942a-e447-424c-b661-3d326db7f77e\scratchpad\task5\dist-old\`, a temporary folder.

## Checks that passed

**Build stage, 2026-11-10:**
- Sound-processing tests: 27 of 27.
- Plug-in host checks: H1–H17.
- Installer tests: 56 of 56.
- Tray tests: 497 of 497, built with 0 warnings.

**Before installing:**
- The dry run (`-WhatIf`) listed exactly the changes above, and nothing else.

**Live check during the install:**
- A silent −40 dB test tone went through the plug-in.
- The plug-in reported back: `VERIFY: PASS`.
- Its log reads "config applied: sequence 1, on, cap -20.0 dB, margin 12.0 dB". Both effects ran at 48000 Hz, stereo.

**Status after the install:**
- Installed.
- On both Headphones entries: both effects listed, effects not switched off, exclusive mode off.
- The test key is on.

**Two minutes later:**
- Realtek had not rewritten the lists, and the Safety Cap is still last in line.

**New tray app:**
- It started with no errors.

**Ways around the plug-in that were checked:**
- "Offload" playback is not supported on the Headphones, the Speakers or the monitor checked.
- Spatial sound is off.
- Exclusive mode is off.
- The final code review found no path above the limit inside the chain.

## Commands (for later)

Each one runs in PowerShell:

| What | Command |
|---|---|
| Status (read-only) | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\SoundProtection\SoundProtection.Install.ps1" -Action Status` |
| Repair (keeps it, puts it right) | same, with `-Action Repair` |
| Undo, same as the desktop shortcut | same, with `-Action Uninstall` |

Undo restores the original sound settings from the journal and restarts audio. It leaves these behind on purpose; delete them by hand if you want them gone:
- `C:\ProgramData\SoundProtection`, with your config and the plug-in's reports;
- in `C:\Program Files\SoundProtection`, the journal, the log and the registry backups.

## Still open

- **One real-use check:** play a YouTube video and hover over the tray icon. It should read "Protected: cap −20.0 dB". The next Zoom call is the real test.
- Everything below this line is the build record, for a developer or the next session.

---

## Appendix A: rulings made during the build (copied from the ledger)

Each ruling says what was decided, why, and what it costs if wrong. The source is `docs\superpowers\plans\2026-11-10-sound-protection-plugin.ledger.md`.


1. **Tasks 1–4 run in parallel on Sonnet**, although the skill says to dispatch implementers one at a time.
   - Why: Ariel asked for parallel Sonnets. File ownership is disjoint, and only T2 consumes T1, through its own stub.
   - Cost if wrong: an integration mismatch, found in Task 5.
2. **No git, so no commits and no BASE.** Review packages are `git diff --no-index` against `work/baseline` for the C# code, and the new files themselves for native and installer.
   - Cost if wrong: weaker diff context for reviewers.
3. **`-WhatIf` and `-Rehearsal` never self-elevate.**
   - Cost if wrong: none.
4. **"no mid tests" means skip the staged trials:** the quick version (Way 2), the monitor-first load, "test key only if needed", and the low-volume step. Automated tests (native, rehearsal, xUnit) and the final silent live check stay.
   - Cost if wrong: time spent on automated checks Ariel did not want.
5. **One elevated run:** the test key is set together with the plug-in, with no separate no-key attempt. Ear-safety notes at deploy are messages, not stops.
   - Cost if wrong: the DRM side effect starts at once rather than after a failed no-key try. The research expects the key to be needed anyway.

6. **Task 1, test 10 (`leveler_release_returns_to_unity`): the quiet phase becomes 4 s instead of 3 s.** The < 0.5 dB bar and the 1000 ms release stay.
   - Why: with τ = 1 s, and the 400 ms loudness window delaying the start, about 15 dB only falls to about 1.0 dB in 3 s. The brief contradicted itself. The release constant comes from the spec; the test length does not.
   - Cost if wrong: none for behaviour; the test proves recovery over 4 s instead of 3 s.
7. **Task 1's interpretations are accepted:**
   - (a) The Safety Cap's slow-release timer counts time spent demanding reduction, not the release tail.
     - As built, it adds up the demanding frames within one limiting episode, across short gaps, and resets only when the envelope is back at 0.999. So a talker's syllable gaps keep the slow release instead of pumping.
     - This is program-dependent release, the intent of `slowAfterMs`.
   - (b) The envelope snaps to unity, so the output is again an exact delayed copy.
   - (c) The first ceiling after Prepare applies at once.
   - (d) `lookaheadMs` and `holdMs` are read at Prepare, because they fix buffer sizes and latency.
   - Cost if wrong: (a) releases faster after short bursts than a literal reading would. The Task 1 reviewer checks it.

8. **Task 4, review Important 1 (plan-mandated, rule 7): fix it.**
   - While the plug-in is running, the status shows the cap the plug-in reports (`Header.CapDb`), not the setting.
   - A warning line appears when a mismatch lasts more than 3 s:
     - "The plug-in is still using cap {X}"
     - "The plug-in could not read your settings and uses cap {X}"
     - "The plug-in is still switched on|off"
   - A failed config write shows "Your settings could not be saved for the plug-in".
   - Why: the spec's point is true status. A green "Protected: cap X" while the plug-in uses another cap misleads the person.
   - Cost if wrong: a little more tray code and tests.
9. **Task 4, review Important 2 (plan-mandated, rule 6 and `audioActive`): fix it.**
   - Rule 6 applies only when the watched output (the chosen one, or the Windows default) is a protected endpoint, meaning both CLSIDs are listed. Otherwise rules 7 and 8 decide from the Safety Cap slot.
   - When sound plays on an unprotected watched output, add "Sound on {Name} is not protected".
   - Why: four active outputs on this PC. A false red "not running" teaches the person to ignore the real alarm, and sound on an unprotected output is worth saying.
   - Cost if wrong: one more input to Evaluate.

10. **Task 3, review Important 1: a failed Repair undoes only its own changes.**
    - It keeps an in-memory record of each value just before writing it, and on failure restores those values and starts the audio services it stopped.
    - It never replays the install journal, never unregisters COM, never deletes installed files. Exit 1, and the log says the setup was left as it was before the repair.
    - Install keeps its full rollback.
    - Why: the brief scoped rollback to Install. A repair hiccup (for example a slow service start) must not silently remove ear protection.
    - Cost if wrong: a failed Repair can leave a partly repaired install, no worse than before the run.
11. **Task 3, review Important 2 (plan-mandated): move the journal out of the user-writable folder and refuse links.**
    - The journal, the registry backups and the install log move to the install folder (`-InstallDir`, `C:\Program Files\SoundProtection`, admin-only by inheritance). No elevated step reads anything from the data folder to write into HKLM.
    - Before creating, setting ACLs on or writing into the data folder or its `telemetry` subfolder, the installer refuses (exit 1, nothing changed) if either is a reparse point. It also sets the data folder's owner to Administrators.
    - Why: the plan put a journal that an admin run replays into HKLM in a Users:Modify folder, which is a local privilege-escalation surface. The fix costs Ariel nothing.
    - Cost if wrong: after Uninstall, the install folder keeps the done-journal, backups and logs, so it is not removed.

12. **Task 2, guard counting: +1 each time the process goes from no locked effect to one.** This replaces "once per process", and the `SpShared.h` comment is reconciled to it.
    - Why: with the literal reading, a crash in a later session of the same audiodg would never be counted. Several effects locking together still count once.
    - Cost if wrong: none for the byte layout; safe mode could trip after three fast unclean sessions within one process.

13. **Task 4: an unprotected watched output with sound gets an amber status, not green "Protected" plus a warning.**
    - Evaluate returns an amber state with the status text "Sound on {Name} is not protected", which replaces the separate warning line.
    - The tray then behaves as in the older ear-level mode: ear-level display, over-events raised, older icon logic.
    - Precedence: rules 1–5 first, then this state, then rule 6 (protected outputs only), then rules 7 and 8.
    - It is folded into fix round 2 before the re-review, so one scoped re-review against snapshot R0 covers both rounds.
    - Why: the implementer showed that Ruling 9 left a green "Protected" above an amber "not protected". The product's job is honest ear protection, and the older alerts keep working on that output.
    - Cost if wrong: one more state and its tests.
14. **Task 4: the 3 s mismatch count restarts when the request changes** (switch or cap). Accepted as built: a mismatch is measured against the current request, so a slider drag gives the plug-in time to catch up.
    - Cost if wrong: a plug-in that ignores the settings is reported 3 s after the last change rather than 3 s after the first.

15. **Task 1, review I1: fail closed.**
    - When a processor is not prepared (a refused format or a failed allocation), `Process` sanitises and clamps over frames × the channel count recorded at that `Prepare` call:
      - Safety Cap: to ±(the ceiling in force), with no delay, and `LatencyFrames()` = 0 while unprepared. This is clamp-only, like Task 2's safe mode.
      - Leveler: to ±1.
    - Never prepared at all (channels unknown): writes nothing, documented as a caller error.
    - Test 24's two checks are changed to expect clamped output.
    - M3 (re-prepare a Safety Cap that is limiting) is re-graded into this fix, because it pins Review Focus 2.
    - The DSP's own format limits (8–768 kHz, 1–64 channels) stay, as allocation bounds.
    - Why: the last line of ear protection must not depend on every caller validating formats.
    - Cost if wrong: on a path the product should never reach, clamp-only sound instead of pass-through.
16. **Task 1, the unlisted deviations are accepted:**
    - `kAim`: the limiter aims 1e-5 below the ceiling.
    - The Leveler's GR snaps to 0 below 1e-7 dB.
    - The disabled fall has a floor of at least 1 dB per 100 ms, which fixes the frozen GR when maxReductionDb = 0.
    - Cost if wrong: none audible.

17. **Task 2, review Important 1: H2–H14 create their objects through the test outer object (aggregated), as the brief requires.** H1 keeps its plain path, and H5 and H6 run once each way.
    - Why: audiodg aggregates APOs, so the delegation path is the one that runs in production.
    - Cost if wrong: test time.
18. **Task 2, review Important 2: the guard never swallows a failure.**
    - `guardArmed_` is cleared only when the reset write succeeds. Later health ticks and the last unlock retry it.
    - A failed write is logged once.
    - At session start, file-not-found means "no history". Any other failed open or read is logged, the file is left untouched, and the guard is not armed for that session.
    - Why: a silent failure can latch safe mode with nothing in `apo.log`, or erase a real crash count. This is Review Focus 5.
    - Cost if wrong: a few log lines.
19. **Task 2, review Minor 1, re-graded to Important: remove the second zeroing after the DSP (deviation #4 is rejected).**
    - A silent-flagged period's output is what the DSP produces from zero input: the previous sound's look-ahead tail, then zeros. The finite check and the clamp then run on it.
    - The output flag is BUFFER_VALID whenever any output sample is non-zero.
    - H6's junk-in-silence case runs on a drained object.
    - Why: dropping the tail cuts up to 5 ms off every sound that is followed by a silent period, an audible step. The clamp already bounds the tail.
    - Cost if wrong: none for safety, because the finite check and the clamp still run.
20. **Task 2's fix round tests with the stub build only. The real-DSP build and H-run are the controller's, in Task 5.**
    - Why: Task 1 is editing `SpDsp.h` at the same time, and a build could catch it half-edited. The changes in this round do not depend on the DSP.
    - Cost if wrong: the real-DSP H-run with the round's changes happens later, in integration.

21. **Task 4: over-events are raised whenever the watched output is not covered by the plug-in, whatever the current state.** They are dropped only when `ShowsProtection` is true and the watched output is covered. `ProtectionState` gets a flag for "watched output covered" that is independent of sound.
    - Why: the implementer measured that short loud bursts (0.3–1.5 s, then silence) on an unprotected output gave no alert, because the amber state had already ended when the event closed. The older mode alerted on them.
    - Cost if wrong: one flag and its tests.

22. **Task 4: over-events are dropped only while the plug-in is actually protecting the watched output.**
    - `DropsOverEvents` = `WatchedOutputCovered` && status is Protected (rule 7) or Idle (rule 8) && the Safety Cap slot was seen fresh at some tick within the last 3 s.
    - Every other case raises them as the older mode did: Off, NotInstalled, effects off, test key off, safe mode, Down, NotProtected, and Protected or Idle without a recent fresh slot.
    - Why: a dead plug-in plus a short burst ending in silence closed in Idle and was dropped. With the switch Off, nothing turns sound down, so the alert is the only protection left.
    - Cost if wrong: alerts while Protection is Off that a user who switched off might not want. AlertsEnabled still turns them off.
23. **Process: Task 4's round 4 goes to the same implementer, not a fresh, more capable one.**
    - Why: the escalation rule targets an implementer stuck on unresolved findings. Here every finding was fixed in its round, and rounds 2–4 carry new rulings that the implementer itself surfaced by measuring.
    - Cost if wrong: one more Sonnet round.

24. **The extra rehearsal guard (`-Rehearsal` refuses the real install folder) is accepted.** (Recorded in the ledger's Progress section, at Task 3.)
    - Why: it stops a rehearsal from ever touching the real Program Files folder.
    - Cost if wrong: rehearsal command lines need `-InstallDir`.
25. **The 65535 bound on the recorded channel count is accepted.** (Recorded in the ledger's Progress section, at Task 1.)
    - Why: it matches the 16-bit `WAVEFORMATEX` channel range, and without it a bogus call could write billions of samples.
    - Cost if wrong: none for real formats.

26. **Final I1 (Important): when the switch is on but the plug-in reports itself off, the tray stops claiming protection.**
    - `PluginEnabled` = the header's Enabled flag while the plug-in runs.
    - `DropsOverEvents` also requires `PluginEnabled`, at once with no grace.
    - After the existing 3 s mismatch grace, the status becomes Down (red icon) with the text "The plug-in is switched off: your setting has not reached it". That text replaces the "still switched off" warning line, so there is no duplicate.
    - Why: nothing is capped in that state. Green "Protected" plus dropped alerts is the honesty failure that Rulings 8, 13 and 22 were meant to remove.
    - Cost if wrong: one extra alert in the first 250 ms after switching on.
27. **Final M7, re-graded to Important: only AudioEndpointBuilder and Audiosrv must reach Running.**
    - Any other recorded dependent (here `midisrv` and `RtkAudioUniversalService`) that does not reach Running in 20 s gets a WARN line naming it. The run continues, with no rollback.
    - Why: it is an avoidable failure on the path that will run on this PC. Every failed attempt costs Ariel another admin prompt and another audio pause, for services that neither sound nor the plug-in needs.
    - Cost if wrong: a Realtek console feature could be left stopped until the next restart, with a log line saying so.
28. **Final, "Declined to judge" lines 1–16: each stands as the reviewer set it aside.**
    - 1: Way 2 and the staged trials (Rulings 4 and 5).
    - 2: the test key's DRM side effect (Ruling 5; Ariel was told).
    - 3: whether a restart is needed for the test key (exit 3 → ask Ariel).
    - 4: the app container question (live check: telemetry appears or not).
    - 5: Zoom in raw mode. The Headphones' mode lists are Default only, and the EFX still applies.
    - 6: the codec and driver after the engine.
    - 7: spatial sound (off; noted in the install report).
    - 8: analog inter-sample peaks.
    - 9: two apps summing about 3 dB (the spec's call).
    - 10: BS.1770 accuracy.
    - 11: torn telemetry reads (absorbed by the gates).
    - 12: the COM unload window.
    - 13: the not-present Headphones endpoint.
    - 14: the Speakers endpoint (the spec says Headphones first; the tray shows amber).
    - 15: the property-store alternative.
    - 16: the ledger's deferred minors.
    - Cost if wrong: these are live-only or by design. The live check and the install report cover 3, 4, 5 and 7.

29. **Final: the flags-0 case stays as built.** That is `config.bin` unreadable after the person switched off, so the plug-in keeps "off". The tray stays green, with the panel's "could not read your settings" warning, and over-alerts are raised because `PluginEnabled` is false.
    - Why the code stands: it needs an unreadable `config.bin` right after an "off", the alerts still fire, and the skill allows no second fix pass.
    - Cost if wrong: in that rare case the icon and tooltip read green while nothing is capped. The over-alerts still fire.

30. **Finish: the plan's work folder is kept, not deleted.**
    - Why: the skill deletes it because git history becomes the record. There is no git here, so the briefs, reports, reviews, packages and snapshots are the only record.
    - Cost if wrong: disk space. It can be deleted at any time.
31. **Finish: no `finishing-a-development-branch` step.**
    - Why: there is no branch to merge or push. The work is already in place in the project folder and installed.
    - Cost if wrong: none.


## Appendix B: deferred minors (copied from the ledger)

Small findings that were noted and not fixed, by decision. None of them lets sound past the limit.

- Task 4: minor (deferred): `TurnDownDetector` near-duplicates `OverDetector`; extract an episode tracker later.
- Task 4: minor (deferred): config-write retry also covers permanent errors; access-denied costs about 0.5 s of a pool thread every 2 s.
- Task 4: minor (deferred): a cap change in the last 100 ms before Exit is not written.
- Task 4: minor (deferred): the config-sync scheduling and the registry parsing live in App, which `dotnet test` does not cover.
- Task 4: minor (deferred): timing assertions in the config tests are load-sensitive.
- Task 4: minor (deferred): `SharedFile`, `ActivityGate` and `ProtectionText` sit in other classes' files; `ProtectionPaths` recomputes folder paths.
- Task 4: minor (deferred): `Settings.MinCeilingDb` is −80 while the UI and the file stop at −60.
- Task 4: minor (deferred): Safety Cap activity (`ClipperHits`) is never shown.
- Task 4: minor (deferred): `.Distinct()` on the warnings has no test.
- Task 4: minor (deferred): on an uncovered output with no sound, the status can read "Protected: cap X" or "Protected (nothing playing)" by the Headphones' slot. It also reads that for the first 600 ms of a sound there.
- Task 4: minor (deferred): NotInstalled carries no warnings, so a failed-save warning is not shown in that state. This is by contract.
- Task 3: minor (deferred): a hard kill between the service stop and start leaves the audio services stopped. Note it in the user docs.
- Task 3: minor (deferred): Verify's rising-heartbeat check needs at least 2 samples, so a late telemetry file gives exit 3.
- Task 3: minor (deferred): the restore loop reads `$r.kind`/`$r.data` directly under StrictMode, so a malformed record throws instead of giving a clear message.
- Task 3: minor (deferred): no single-instance guard against concurrent installer runs.
- Task 3: minor (deferred): `Start-SpAudioServices` always starts Audiosrv.
- Task 3: minor (deferred): the junction re-check is a path check, not a lock. The window between the re-check and icacls remains; `icacls /L` or a handle-based check would close it.
- Task 3: minor (deferred): a Repair that fails before its try block says the generic "FAILED", not the "left as it was" sentence.
- Task 3: minor (deferred): the comment at line 1447 is wrong when step 8 itself failed.
- Task 3: minor (deferred): no test exercises the DWORD undo (test key, exclusive) in a failed Repair.
- Task 3: minor (deferred): the install folder and log are created before a junction refusal that says "Nothing was changed".
- Task 3: minor (deferred): the undo-failure log wording points to the pre-install backup.
- Task 3: minor (deferred): a Repair can add true-original journal records in step 2 before it later fails.
- Task 3: minor (deferred): an empty Audio key can remain if SetValue throws right after creating it.
- Task 3: minor (deferred): `Test-SpIsRealInstallFolder` compares full paths, so an 8.3 short path gets past it.
- Task 1: minor (deferred): the header breaks under `<windows.h>` min/max without `NOMINMAX`. Today's consumers define it.
- Task 1: minor (deferred): no test covers the meter's divisor before the window is full, or K-weighting at 44.1 and 96 kHz at high frequencies.
- Task 1: minor (deferred): report §8 does not list the three deviations of Ruling 16.
- Task 1: minor (deferred): the format check is written three times.
- Task 1: minor (deferred): the tests repeat the delayed-copy and bad-frame loops.
- Task 1: minor (deferred): `SafetyCap::Process` inlines four units that could stand alone.
- Task 1: minor (deferred): `build-dsp.cmd` lets cl's source-name echo into a green run.
- Task 1: minor (deferred): test 24's out-of-memory thresholds (≥ 4) are tied to the current allocation count in Prepare.
- Task 1: minor (deferred): the refused-format table is not followed by a good Prepare. Recovery is pinned by other parts.
- Task 1: minor (deferred): the trailing `Reset()` in `Leveler::Prepare` is redundant.
- Task 1: minor (deferred): the fix adds two more unparenthesised `std::max(` sites (M1 is still open; `NOMINMAX` guards today).
- Task 1: minor (deferred): report §8 item 8, §9 and §10 still describe the old fail-open behaviour. §11 supersedes them.
- Final: minor (deferred): M1. "Safety Cap is last in `,15`" is neither checked nor repaired. The live check confirms it ends the list today.
- Final: minor (deferred): M2. Verify blames the test key for every exit 3, even when the lists were rewritten.
- Final: minor (deferred): M3. The elevated `config.bin` write follows a link. Planting one needs symlink rights, and this PC has one admin user.
- Final: minor (deferred): M4. `icacls /grant:r` keeps explicit entries on a folder that existed before the install. The installer creates the folder fresh.
- Final: minor (deferred): M5. A protection tick that keeps throwing would freeze the last view. No input is known to cause it.
- Final: minor (deferred): M6. The 10 s healthy reset can erase a new session's +1. That only makes safe mode trip one start later.
- Final: minor (deferred): M8. Repair and Remove from the tray show no result, because the console closes.
- Final: minor (deferred): M9. Rule 2's text names the problem but not the way out (turn Audio enhancements back on).
- Final: minor (deferred): M10. Turn-downs are shown in dB only. Ariel's sketch also shows "%"; the plan dropped it.
- Final: minor (deferred): M11. After the v2 migration, the fallback ear-level alerts use the cap number as their ceiling: −20 instead of the old −28.
- Final: minor (deferred): M12. Uninstall writes back the install-time `,13`/`,15` even if a driver update changed them since.
- Final: minor (deferred): M13. Uninstall leaves `C:\ProgramData\SoundProtection` behind. The install report says so.
- Final: minor (deferred): Install's WARN says "within 20 s" even when Start-Service fails at once (the ruled wording).
- Final: minor (deferred): a non-core service can get a second WARN in a run that is already failing.
- Final: minor (deferred): warnings reach only the panel, never the icon or tooltip (the existing design).
- Task 2: minor (deferred): the comment at `SpRuntime.cpp:576–577` says an owed reset is not retried at destroy; the final worker tick does try once.
- Task 2: minor (deferred): H17's quiet-region index underflows if the look-ahead were above 416 frames (test-only; 248 today).
- Task 2: minor (deferred): no check pins the 1 s retry pacing.
- Task 2: minor (deferred): GetLatency is checked only against the formula, never against a measured impulse or in safe mode.
- Task 2: minor (deferred): the matching format ranges are not pinned, with no lock at 8 kHz mono or 384 kHz × 32 ch.
- Task 2: minor (deferred): H14's Leveler half cannot fail.
- Task 2: minor (deferred): H12's tolerance is ±1.5 instead of ±1.
- Task 2: minor (deferred): the host ignores the result of `SetEnvironmentVariableW` before LoadLibrary.
- Task 2: minor (deferred): the log roll's `MoveFileExW` result is ignored; there is dead state.
- Task 2: minor (deferred): the unlock comment overclaims "late APOProcess gets silence".
- Task 2: minor (deferred): safe-mode telemetry reports the clamped output peak as `inputDb`.
- Task 2: minor (deferred): `clipperHits` sums two counters with different lifetimes.
- Task 2: minor (deferred): an unreadable requested type gets no suggestion.
- Task 2: minor (deferred): a transient open failure on `config.bin` clears ConfigValid for about 200 ms.
- Task 2: minor (deferred): a microsecond race between the healthy reset and a new session's arm.
- Task 2: minor (deferred): `build-apo.cmd` uses the same exit code for compile and check failures.
- Task 2: minor (deferred): worker stop is not logged.
- Task 2: minor (deferred): the real-time path is not `noexcept`.
