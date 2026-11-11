# SDD ledger: plan docs/superpowers/plans/2026-11-10-sound-protection-plugin.md

- **Spec:** `docs/02/WIN11-BUILD-PLAN.md` (plan 3, Way 1).
- **Shared contract:** `native/apo/SpShared.h`.
- **Workspace:** `docs/superpowers/plans/2026-11-10-sound-protection-plugin.work/`, holding the briefs, the reports, and `baseline/`, a copy of `src` and `tests` taken before Task 4.
- **Kept by hand:** this folder is not a git repository.

Ariel, 2026-11-10: "i like the plan, do the "my thing" with full plugin a-z now, no mid tests. write a good plan and fire parralel (if possible) sonnets to exec."

## Pre-flight (shared interfaces and self-consistency)

| Pair / task | Produces → consumes | Found |
|---|---|---|
| T1 → T2 | The public API of `native/dsp/SpDsp.h` → the APO shell, and T2's own `SpDspStub.h` | Consistent: the same API block is in both briefs |
| T2 → T3 | `native/out/apo/SoundProtection.Apo.dll` and its `DllRegisterServer` → `regsvr32` and the COM key checks | Consistent |
| T2 → T3 | Telemetry layout (header magic, slot kind, frames) → Verify | Consistent, through `SpShared.h` |
| T2 → T4 | Config and telemetry byte layouts → `ProtectionConfig` / `ApoTelemetry` | Consistent: `SpShared.h` and the same golden bytes in both briefs |
| T3 → T4 | Installed script path and `-Action Repair\|Uninstall` → tray menu items | Consistent |
| T3 self | R6 runs `-WhatIf` without elevation; the self-elevation rule says "real Install, Repair or Uninstall" | Ambiguous → Ruling 3 |
| T1 self | 27 tests listed; Step 4 expects 27 | Consistent |
| T2 self | The loudness part of H5 and the reduction part of H9 need the real DSP; the stub run marks them SKIP | Consistent |
| T4 self | `Evaluate(..., capDb, audioActive)` is used by the rule 7 text | Consistent |

## Rulings

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

## Progress

- Wave 1 dispatched, all on Sonnet, running in the background:
  - T1: agent a5d7e8cc6391cc933
  - T2: agent a0e2e2b0d9c0b75a0
  - T3: agent aede769833210b806
  - T4: agent a3d7c1d569c71d1cb
- Reports go to `work/task-N-report.md`.
- `work/make-package.ps1` rewritten: it stages only the task's files, diffs `empty` or `before` against `now`, and git writes with `--output` (no code-page damage).
- Task 4: DONE_WITH_CONCERNS.
  - Implementer's evidence: 265/265 tests, 0 warnings, published to `dist-staging` only.
  - Concerns:
    - The tray was not run end to end; the Task 5 live check covers it.
    - Install the plug-in before swapping in the new tray (otherwise the red "!" shows meanwhile).
    - 15 deviations are listed in report §11; they go to review.
  - Package: `work/task-4-package.diff` (24 files, +3201/−83).
  - Reviewer: Sonnet (agent a7e2ce806c3db34dc), report to `work/task-4-review.md`.
  - Snapshot R0 saved in `work/snap/task-4-R0`, the base for fix-round packages.
- Task 3: DONE_WITH_CONCERNS.
  - Implementer's evidence: 35/35 rehearsal tests; a read-only snapshot of the real system was identical before and after the suite.
  - Concerns (report §9):
    - Verify uses the default output device.
    - The first end-to-end run is the real one.
    - Realtek's service may rewrite `,13`/`,15`.
    - Both "Headphones" endpoints are changed (one is not present).
    - §9.6: a failed Repair rolls back the whole install.
    - §9.8: the journal sits in a folder standard users can write.
  - 15 deviations in report §8.
  - Package: `work/task-3-package.diff` (2 files, +2729); snapshot R0 saved.
  - Reviewer: Opus (agent a02e3607338c95fd0; the installer is what changes the live PC), report to `work/task-3-review.md`.
- Task 4 review: ✅ spec compliant (all 15 deviations and 3 unlisted ones accepted). Quality: Needs fixes, from 2 Important items, both plan-mandated (Rulings 8 and 9). Fix round 1/5 goes to the same implementer.
  - Task 4: minor (deferred): `TurnDownDetector` near-duplicates `OverDetector`; extract an episode tracker later.
  - Task 4: minor (deferred): config-write retry also covers permanent errors; access-denied costs about 0.5 s of a pool thread every 2 s.
  - Task 4: minor (deferred): a cap change in the last 100 ms before Exit is not written.
  - Task 4: minor (deferred): the config-sync scheduling and the registry parsing live in App, which `dotnet test` does not cover.
  - Task 4: minor (deferred): timing assertions in the config tests are load-sensitive.
  - Task 4: minor (deferred): `SharedFile`, `ActivityGate` and `ProtectionText` sit in other classes' files; `ProtectionPaths` recomputes folder paths.
  - Task 4: minor (deferred): `Settings.MinCeilingDb` is −80 while the UI and the file stop at −60.
  - Task 4: minor (deferred): Safety Cap activity (`ClipperHits`) is never shown.
  - Task 4: minor (deferred): `.Distinct()` on the warnings has no test.
  - Carry-forwards from the Task 4 review's ⚠️ items:
    - W1 → Task 5 live check: click Protection, Repair and Remove; decline UAC once; cause one turn-down.
    - W2 → Task 5: the registry reader against real installer output.
    - W3 → Task 2 review: the plug-in must apply a config whose sequence differs from the one in use, not only a higher one.
    - W4 → Task 5: retire the old tray before the new one first runs.
    - W5 → Task 2 review: sharing modes for `apo.bin` and `config.bin`.
    - W6 → Task 3 review and final review: the user-writable data folder that elevated and service code write into.
- Task 4: fix round 1/5 done.
  - Implementer's evidence: 319/319 tests, 0 warnings; republished to `dist-staging`, exe SHA-256 B2EF1F01…1D09.
  - The harness refused the implementer's write to the report. The controller saved its text verbatim to `work/task-4-report-fix1.md`.
- Task 4: fix round 2/5 dispatched to the same implementer (Ruling 13). After it comes one scoped re-review of R0 → now.
- Task 4: fix round 2/5 done.
  - Implementer's evidence: 336/336 tests, 0 warnings; exe 3962502C…B99A. Text saved to `work/task-4-report-fix2.md`.
  - It found that short bursts on an uncovered output are dropped.
- Task 4: fix round 3/5 dispatched (Ruling 21). The scoped re-review of R0 → now follows it.
- Task 4: fix round 3/5 done.
  - Implementer's evidence: 389/389 tests, 0 warnings; exe 283CC1F2…9365. Text saved to `work/task-4-report-fix3.md`.
  - Two timing holes were left: a dead plug-in, and Off.
- Task 4: fix round 4/5 dispatched to the same implementer (Rulings 22 and 23). After it comes one scoped re-review of R0 → now.
- Task 4: fix round 4/5 done.
  - Implementer's evidence: 429/429 tests, 0 warnings; exe 091035BD…395A (1,584,670 bytes). Text saved to `work/task-4-report-fix4.md`.
  - Its notes are accepted as built:
    - a dead plug-in's events are dropped for about 5 s (2 s stale plus the 3 s window);
    - Off raises over-events while showing the protection display;
    - "freshness" is the `IsRunning` pair.
- Task 4: scoped re-review of R0 → now (`work/task-4-package-fix1to4.diff`, 17 files, +1941/−54): Sonnet (agent ae5259267277be4ab).
- Task 4 re-review: findings 1 and 2 ADDRESSED under Rulings 8, 9, 13, 14, 21 and 22; no new Critical or Important breakage.
  - Task 4: minor (deferred): on an uncovered output with no sound, the status can read "Protected: cap X" or "Protected (nothing playing)" by the Headphones' slot. It also reads that for the first 600 ms of a sound there.
  - Task 4: minor (deferred): NotInstalled carries no warnings, so a failed-save warning is not shown in that state. This is by contract.
  - Out-of-scope, to the final review: with the switch on and the plug-in reporting itself switched off, the status stays Protected and over-events stay dropped. Only the 3 s "still switched off" warning signals it. `DropsOverEvents` should also require the header's Enabled flag.
  - Out-of-scope, to the final review: if `Sampler.Evaluate` kept throwing, a stale view would keep dropping events.
- **Task 4: complete** (review clean after fix rounds 1–4; 429/429 per the implementer, to be re-run by the controller in Task 5).
- Task 3 review (Opus): ✅ spec compliant; all 15 deviations accepted, and F4 (minimal write rights) verified on the live ACLs. Quality: Needs fixes, from 2 Important items (Rulings 10 and 11). Fix round 1/5 goes to the same implementer.
  - Task 3: minor (deferred): a hard kill between the service stop and start leaves the audio services stopped. Note it in the user docs.
  - Task 3: minor (deferred): Verify's rising-heartbeat check needs at least 2 samples, so a late telemetry file gives exit 3.
  - Task 3: minor (deferred): the restore loop reads `$r.kind`/`$r.data` directly under StrictMode, so a malformed record throws instead of giving a clear message.
  - Task 3: minor (deferred): no single-instance guard against concurrent installer runs.
  - Task 3: minor (deferred): `Start-SpAudioServices` always starts Audiosrv.
  - Carry to Task 5: watch the first real run closely; run `-WhatIf` first.
- Task 3: fix round 1/5 done.
  - Implementer's evidence: 51/51 rehearsal tests (35 before, 16 new; first RED was 26 passed, 23 failed). The real-system snapshot is identical before and after.
  - New concerns are in report §10.7:
    - The owner-set command is first exercised for real in the elevated run.
    - `-Rehearsal` refuses the real install folder.
    - A failed Repair keeps a newer DLL it copied (Ruling 10c).
    - The junction check is not a lock (check-then-use) → final review.
    - The plan and brief still name the old journal location; Ruling 11 supersedes them.
  - Package: `work/task-3-package-fix1.diff` (R0 → now, +866/−58).
  - Scoped re-review: Sonnet (agent aab05b681b430eb80).
- Task 3 re-review: both findings ADDRESSED; no new Critical or Important breakage. PS 5.1 parse is clean, and the file hashes match the report.
  - Task 3: minor (deferred): the junction re-check is a path check, not a lock. The window between the re-check and icacls remains; `icacls /L` or a handle-based check would close it.
  - Task 3: minor (deferred): a Repair that fails before its try block says the generic "FAILED", not the "left as it was" sentence.
  - Task 3: minor (deferred): the comment at line 1447 is wrong when step 8 itself failed.
  - Task 3: minor (deferred): no test exercises the DWORD undo (test key, exclusive) in a failed Repair.
  - Task 3: minor (deferred): the install folder and log are created before a junction refusal that says "Nothing was changed".
  - Task 3: minor (deferred): the undo-failure log wording points to the pre-install backup.
  - Task 3: minor (deferred): a Repair can add true-original journal records in step 2 before it later fails.
  - Task 3: minor (deferred): an empty Audio key can remain if SetValue throws right after creating it.
  - Task 3: minor (deferred): `Test-SpIsRealInstallFolder` compares full paths, so an 8.3 short path gets past it.
  - Out-of-scope observations, passed to the final review:
    - `Write-SpDefaultConfigIfMissing` would follow a dangling `config.bin` symlink in an elevated write (planting one needs symlink rights).
    - `icacls /grant:r` keeps entries planted beforehand.
    - A telemetry junction planted after the checks is possible by design.
    - The plan and brief still give the old journal location and rehearsal command. Ruling 11 supersedes them.
- Ruling 24: **the extra rehearsal guard (`-Rehearsal` refuses the real install folder) is accepted.**
  - Why: it stops a rehearsal from ever touching the real Program Files folder.
  - Cost if wrong: rehearsal command lines need `-InstallDir`.
- **Task 3: complete** (review clean after fix round 1; 51/51 rehearsal tests per the implementer, to be re-run by the controller in Task 5).
- Task 1 review (Opus): ❌ 2 spec items, the format limits (I1) and unlisted deviations (Ruling 16). Quality: Needs fixes, from 1 Important item (Ruling 15). All of Rulings 6 and 7 are implemented as ruled. Fix round 1/5 goes to the same implementer.
  - Task 1: minor (deferred): the header breaks under `<windows.h>` min/max without `NOMINMAX`. Today's consumers define it.
  - Task 1: minor (deferred): no test covers the meter's divisor before the window is full, or K-weighting at 44.1 and 96 kHz at high frequencies.
  - Task 1: minor (deferred): report §8 does not list the three deviations of Ruling 16.
  - Task 1: minor (deferred): the format check is written three times.
  - Task 1: minor (deferred): the tests repeat the delayed-copy and bad-frame loops.
  - Task 1: minor (deferred): `SafetyCap::Process` inlines four units that could stand alone.
  - Task 1: minor (deferred): `build-dsp.cmd` lets cl's source-name echo into a green run.
  - ⚠️ items for Task 2, sent to its reviewer as R-f to R-i:
    - an explicit rate check;
    - `bad_alloc` from `Prepare` fails the lock;
    - `SetParams` from atomics on the audio thread, with FTZ set by Task 2;
    - `LatencyFrames()` re-read after each `Prepare`;
    - `ClipperHits` after a tightening is not treated as a fault.
- Task 2: DONE_WITH_CONCERNS.
  - Implementer's evidence: H1–H14 and cleanup pass with the real DSP (0 SKIP); the stub build is green; DspTests 27/27; `dumpbin` shows 4 exports and imports only ole32, ADVAPI32 and KERNEL32; nothing was registered.
  - Concerns:
    - The guard reading, resolved as Ruling 12.
    - The DLL never creates folders; the installer owns them.
    - The DLL is built against an older `SpDsp.h`, which has had comment-only edits since. Rebuild in Task 5.
  - 17 deviations in report §8.
  - Package: `work/task-2-package.diff` (13 files, +3961); snapshot R0 saved.
  - Reviewer: Opus (agent ae5d95a81dba0d807), with named risks R-a to R-e: the T1 Prepare risk, W3, W5, real time and lifetime.
- Task 1: fix round 1/5 done (Ruling 15, plus M3 and M5 folded in).
  - Implementer's evidence: ALL 27 PASSED, no warnings; RED was 1 of 27 failed (131 checks).
  - Mutation check: 97 of 99 killed. Both fail-open mutants are killed; the survivors are the speed-only flush and one equivalent mutant.
  - Fuzz: 3600 scenarios, 0 failures. The Task 2 sources compile clean against the new header.
  - Package: `work/task-1-package-fix1.diff` (+492/−57).
  - Scoped re-review: Sonnet (agent a14a8e75690c1a530).
- Task 1 re-review: I1 and M3 ADDRESSED; no new Critical or Important breakage. The reviewer's own probes:
  - Prepared output is bit-identical to R0 over 140 scenarios.
  - No write past a buffer in 272 guard-page runs.
  - No stale state after a failed Prepare in 1020 cases.
  - Task 1: minor (deferred): test 24's out-of-memory thresholds (≥ 4) are tied to the current allocation count in Prepare.
  - Task 1: minor (deferred): the refused-format table is not followed by a good Prepare. Recovery is pinned by other parts.
  - Task 1: minor (deferred): the trailing `Reset()` in `Leveler::Prepare` is redundant.
  - Task 1: minor (deferred): the fix adds two more unparenthesised `std::max(` sites (M1 is still open; `NOMINMAX` guards today).
  - Task 1: minor (deferred): report §8 item 8, §9 and §10 still describe the old fail-open behaviour. §11 supersedes them.
  - Final-review note: after a failed Prepare, `Process` writes frames × that call's channel count. A caller still passing a smaller earlier layout would overrun; Task 2 gates on its own `ready_`, so it does not.
  - Final-review note: `SpDspStub.h`'s Safety Cap stub still passes audio through when unprepared. It is test-only, but stub host runs cannot see fail-closed.
- Ruling 25: **the 65535 bound on the recorded channel count is accepted.**
  - Why: it matches the 16-bit `WAVEFORMATEX` channel range, and without it a bogus call could write billions of samples.
  - Cost if wrong: none for real formats.
- **Task 1: complete** (review clean after fix round 1; ALL 27 PASSED per the implementer, to be re-run by the controller in Task 5).
- Task 5 integration, controller runs (logs in `scratchpad\task5\`):
  - `cmd /c native\build-dsp.cmd` → exit 0, ALL 27 PASSED, no warning or error lines.
  - `installer\Install.Tests.ps1` → exit 0, ALL 51 TESTS PASSED.
  - `cmd /c native\build-apo.cmd` (real DSP) → exit 0, "built with the REAL DSP", H1–H17 and cleanup all PASS. No SKIP, FAIL or warning lines.
  - dumpbin: 4 exports; dependencies ole32, ADVAPI32 and KERNEL32 only.
  - The DLL to deploy: `native\out\apo\SoundProtection.Apo.dll`, 245,248 bytes, 21:52:05, SHA-256 221824F0…D116.
  - `dotnet build SoundProtection.sln --no-incremental` → exit 0, 0 warnings, 0 errors.
  - `dotnet test SoundProtection.sln --no-build` → 429 passed, 0 failed.
- Final whole-project review dispatched: Opus (agent a2b9949b41840bdd2), report to `work/final-review.md`.
  - Inputs: the production files plus `work/task-5-package-final-src.diff` (C# src vs baseline, 19 files, +1813/−80), the Review Focus verbatim, and 9 named items carried from the task reviews.
- Pre-deploy, read-only (22:12):
  - The old tray, PID 17848, is still running from `dist\`.
  - The default output is "Headphones (Realtek(R) Audio)" at 34 % (−16.0 dB), idle. Probe output: `scratchpad\task5\probe-default.txt`.
  - The installer's command lines and exit codes are in `task-3-report.md` §10.6.
  - The `-WhatIf` dry run (exit 0, `scratchpad\task5\whatif-1.txt`) lists exactly the planned changes. There are 7 journal values, and the lists are changed on both Headphones keys.
- Final review (Opus): **With fixes.**
  - Critical: none.
  - Important: 1 (I1, Ruling 26).
  - Minor: 13, of which M7 is re-graded to Important (Ruling 27).
  - Declined to judge: 16 lines (Ruling 28).
  - No path above the ceiling was found inside the chain, and every seam matches.
  - Offload probe: `IsOffloadCapable` is false on Headphones, Speakers and a monitor. This goes in the install report.
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
- Final fix pass, one round, dispatched in parallel:
  - Ruling 26 → the Task 4 implementer.
  - Ruling 27 → the Task 3 implementer.
  - Snapshots F0 are saved (`work/snap/task-3-F0`, `task-4-F0`). One scoped re-review of F0 → now follows.
  - The native DLL is unchanged (221824F0…D116).
- Final fix, Task 4 (Ruling 26): done.
  - Implementer's evidence: 497/497 tests, 0 warnings. RED came in 3 stages (44 compile errors, then 53 and 17 failing of 497). Mutants: 96 of 99 killed, 3 equivalent. The Sampler harness passes.
  - Published: `dist-staging\SoundProtection.exe` 1,585,694 bytes, SHA-256 E37C2271…34DF.
  - Its concerns are accepted as built:
    - ConfigNotRead outranks SwitchDiffers.
      - Correction from the final re-review: this was wrong as first written. The plug-in keeps its last good values, not the defaults (`SpRuntime.cpp:439–451, 515–516`). Flags 0 (config not read, Enabled clear) therefore means "off and unreadable", and nothing is capped.
      - See Ruling 29.
    - Down shows only while the plug-in counts as running. Alerts are raised in between.
    - A plug-in slower than 3 s to switch on shows red until it does.
- Final fix, Task 3 (Ruling 27): done.
  - Implementer's evidence: ALL 56 TESTS PASSED. RED was 7 of 56 failing (U22 showed Install rolling back over `midisrv`). Nine mutants were caught, and the real-system snapshot is identical before and after.
  - Its added INFO line after each WARN, giving the reason Windows reported, is accepted.
- Scoped re-review of the final fix pass (F0 → now, `task-3-package-finalfix.diff` and `task-4-package-finalfix.diff`): Sonnet (agent a010492169f4767e9).
- Controller re-runs on the final code:
  - installer suite: exit 0, ALL 56 TESTS PASSED, 0 FAIL lines;
  - `dotnet build --no-incremental`: 0 warnings, 0 errors;
  - `dotnet test`: 497/497.
  - `dist-staging\SoundProtection.exe` hash E37C2271… was published at 23:05:11, after the newest source edit (22:48:51).
  - The `-WhatIf` run on the final installer exits 0 with 18 WHATIF lines (`whatif-2.txt`).
- Final fix re-review: I1 and M7 ADDRESSED; no new Critical or Important breakage.
  - Final: minor (deferred): Install's WARN says "within 20 s" even when Start-Service fails at once (the ruled wording).
  - Final: minor (deferred): a non-core service can get a second WARN in a run that is already failing.
  - Final: minor (deferred): warnings reach only the panel, never the icon or tooltip (the existing design).
- **Final review clean.** Deploy starts: Task 5 step 3.
- Deploy attempt 1 (23:24:41–23:26:44): exit 1. The UAC prompt was "canceled by the user", either declined or timed out after about 2 minutes. The elevated run never started (`scratchpad\task5\install-run.txt`).
  - Verified unchanged: no `Program Files` or `ProgramData` folders, no test key, the Headphones' `,15` is still Realtek's two entries, no shortcut, the old tray PID 17848 still runs, and the audio services are Running.
  - Not retried without asking Ariel.
- Deploy attempt 2, 2026-11-11 07:01:12–07:01:23, after Ariel chose "Run it again now": **exit 0, VERIFY: PASS** (`tasks\bgq5we0io.output`; log `C:\Program Files\SoundProtection\install-log.txt`).
  - Status:
    - installed true and the test key on;
    - both Headphones keys have the Leveler and the Safety Cap listed, effects not disabled, exclusive off;
    - heartbeat 18 s old, because nothing was playing.
  - Registry, read at 07:01 and again at 07:03 (no Realtek rewrite): `{ab14dbc4…}` `,13` ends with the Leveler and `,15` ends with the Safety Cap.
  - `apo.log`: "config applied: sequence 1, on, cap -20.0 dB, margin 12.0 dB". Both effects locked at 48000 Hz, 2 ch, then unlocked after the tone.
  - Services: AudioEndpointBuilder, Audiosrv, midisrv and RtkAudioUniversalService are Running. The Undo shortcut exists.
  - Tray swap at 07:03:03:
    - the old `dist\` files were backed up to `scratchpad\task5\dist-old\`;
    - PID 17848 was stopped;
    - `dist-staging\*` was copied in (exe SHA-256 matches E37C2271…);
    - the new tray PID 31000 started and logged "Started" with no errors.
  - The old tray logged one expected error at 07:01:17, "The Windows audio service is not running", during the service restart.
  - Settings: the old `settings.json` has no version and `ceilingDb` −16.5. The new build resets it to −20 by design (`SettingsStore.Load`: the old number meant a level at the ear), so `config.bin` already matched and was not rewritten.
  - Startup: no Run value. The tray menu's "Start with Windows" is off, and it is Ariel's choice. The plug-in protects without the tray.
  - Declined-to-judge items 3 and 4 (Ruling 28) are answered live: no restart was needed, and the engine writes telemetry.
- Deploy: complete. The report is `docs/03/INSTALL-REPORT.md`.
- Deploy order decided:
  1. Run the installer first, in the background, waiting for Ariel's UAC click. The old tray keeps watching in the meantime.
  2. Only after the install's exit, stop PID 17848.
  3. Copy `dist-staging` into `dist`.
  4. Start the new tray.
- Task 2: fix round 1/5 done on the stub build (Rulings 17–20).
  - Implementer's evidence: stub H1–H17 pass with 2 expected SKIPs. RED is shown for H15, H16 and H17, and through mutants for aggregation. The report is §12 of `task-2-report.md`.
  - Package: `work/task-2-package-fix1.diff` (5 files, +654/−58).
  - Scoped re-review: Sonnet (agent a61325f4e3b26db65), given the controller's real-DSP evidence.
- Task 2 re-review: findings 1–3 ADDRESSED; no new Critical or Important breakage. Real time, lock order and log-once were checked.
  - Task 2: minor (deferred): the comment at `SpRuntime.cpp:576–577` says an owed reset is not retried at destroy; the final worker tick does try once.
  - Task 2: minor (deferred): H17's quiet-region index underflows if the look-ahead were above 416 frames (test-only; 248 today).
  - Task 2: minor (deferred): no check pins the 1 s retry pacing.
  - Out-of-scope, to the final review: the not-ready or null-buffer path (`SpApoBase.cpp:379–383`) returns BUFFER_SILENT and leaves the output buffer unwritten.
  - Out-of-scope, to the final review: the 10 s-mark reset can race a new session's start (Minor 13 of the first review).
- **Task 2: complete** (review clean after fix round 1; real-DSP H1–H17 PASS in the controller's run).
- Task 2 review (Opus): ❌ 1 spec item (aggregation, Ruling 17). R-a to R-d and R-f to R-h ✅; R-e and R-i ⚠️, left for the live check and the final review. Quality: Needs fixes, from 2 Important items plus 1 re-graded (Rulings 17–19). Fix round 1/5 goes to the same implementer, on the stub build (Ruling 20).
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
- Task 1: NEEDS_CONTEXT.
  - 26/27 tests pass; test 10 is impossible as written. Ruled in Rulings 6 and 7; the same agent was resumed to apply option A.
  - Carry to the Task 2 review as a named risk: the DSP's Prepare returns no status, so a refused format leaves Process a no-op.
    - Task 2 must never call DSP Process on an object that was not prepared.
    - Its accepted formats (float32, 1–32 channels, 8–384 kHz) must stay inside the DSP's range (1–64 channels, 8–768 kHz).
- Task 1, after option A: DONE_WITH_CONCERNS.
  - ALL 27 PASSED, with no warnings under /W4 /WX; test 10 reads 0.370 dB; mutations 57/58 killed (the survivor affects speed only).
  - Package: `work/task-1-package.diff` (3 files, +2325); snapshot R0 saved.
  - Reviewer: Opus (agent a2af4530d93d389c7).
