# Sound Protection V2: bass and treble caps

2026-11-17. **Installed at 13:05, with your go.** Nothing is committed; everything is staged in git.

## In short

- The panel has two new sliders under the Cap: **Bass** and **Treble**.
- Each one is a cap for one part of the sound:
  - **Treble** is the sharp, high part, where a high voice gets piercing.
  - **Bass** is the deep, boomy part, where a deep voice booms.
- When that part goes over its slider, only that part is turned down, by up to 12 dB. The rest of the voice stays as it is.
- All the way right is **Off**. Both start Off, so V2 sounds exactly like V1 until you move one. This was checked bit for bit.
- While a slider is working, the panel shows **"cutting N.N dB"** next to it.
- It works inside the sound engine, before the volume slider, like the main cap.

The new rows:

```
Cap (dB)  [-20.0]
Bass    ━━━━━━━━━━━━━━━━━━━━━━━━━━●  Off
Treble  ━━━━━━━━━━━●┄┄┄┄┄┄┄┄┄┄┄┄┄┄  -32.0 dB   cutting 4.0 dB
```

The whole panel, with made-up readings: `panel-v2.png` in this folder.

## How to set them

1. Play a voice that sounds too hard.
2. Move **Treble** left a little at a time. When "cutting" shows up, it is working on that voice. Stop when the voice stops sounding hard.
3. For a boomy deep voice, do the same with **Bass**.
4. If voices start to sound dull (Treble) or thin (Bass), you went too far. Move back right a bit.

The first part of each slider may do nothing. The cut starts only once the slider is below the level of that part of the sound, and "cutting" shows you when it starts.

## Installing it

**Done on 2026-11-17 between 13:04 and 13:05, after your "yes".**

- **Plug-in:** the installer's Repair copied the V2 plug-in into Program Files, registered it, and restarted audio. Its own check passed ("VERIFY: PASS").
- **Tray app:** the V1 tray was stopped, the V2 tray was copied into `dist\`, and it started at 13:05.
- **The new file:** the new tray wrote `C:\ProgramData\SoundProtection\bands.bin`, with both caps Off.
- **Live check:** I played a quiet test tone. The plug-in's log shows `bands applied: sequence 1, bass off 0.0 dB, treble off 0.0 dB`, so the plug-in reads what the tray writes.
- **Kept as they were:** your cap (−25 dB), your settings, and everything else from the first install.

The steps, as they were done:

1. **Back up V1** into `C:\Users\Ariel\SoundProtection-V1-backup\`:
   - the plug-in, `C:\Program Files\SoundProtection\SoundProtection.Apo.dll`;
   - the tray app, `dist\SoundProtection.exe`, with its two `.pdb` files;
   - your tray settings (`settings.json`) and the plug-in config (`config.bin`), just in case.
2. **Install the new plug-in.** The project's installer runs with Repair (admin). It copies the new plug-in from `native\out\apo\` and restarts audio.
3. **Swap the tray app.** Exit the V1 tray (right-click its icon, then Exit), copy `dist-staging\` over `dist\`, and start `dist\SoundProtection.exe`.
4. **Check it.**
   - Hover over the tray icon while something plays. It should say "Protected: cap −25.0 dB" (your cap).
   - The panel shows Bass Off and Treble Off.
   - Play a voice and move Treble left until "cutting" appears. That proves the whole chain works on real sound.

## Going back to V1

1. Exit the tray, copy `SoundProtection.exe` from the backup folder into `dist\`, and start it.
2. Run Repair (admin) with the backed-up plug-in. The command is in the table below.

V1 ignores the new `bands.bin` file, so it can stay. To remove everything, the desktop shortcut "Sound Protection - Undo" works as before.

## Commands

Each one runs in PowerShell. Repair needs admin.

| What | Command |
|---|---|
| Install the V2 plug-in | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Users\Ariel\source\MS PlayGround\Sound Protection\installer\SoundProtection.Install.ps1" -Action Repair` |
| Back to the V1 plug-in | the same, plus `-DllSource "C:\Users\Ariel\SoundProtection-V1-backup\SoundProtection.Apo.dll"` |
| Status (read-only) | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\SoundProtection\SoundProtection.Install.ps1" -Action Status` |

## What was tested

Every test ran on this PC. None touched the live sound.

- **Sound engine:** 40 checks, 27 from V1 and 13 new. They show that:
  - off or under the cap, the output is a bit-for-bit copy;
  - it never boosts and cuts at most 12 dB;
  - it lets go smoothly;
  - it works at every sample rate and channel count.
- **Plug-in, in a test host** (no real sound, no install): 21 checks, 17 from V1 and 4 new.
  - With the caps off, the output is exactly V1's.
  - They also cover a sound format change, a broken or missing settings file, protection switched off, and safe mode.
- **Tray app:** 563 tests, 497 from V1 and 66 new.
- **Side by side with V1:** the same mixed sound went through the V1 plug-in and the V2 plug-in. With the caps off or not reached, the output was identical byte for byte.
- **Final run, after the last fixes:** every test above ran once more and all passed, with 0 build warnings. The plug-in's test host had also passed 3 times in a row just before.

## The files

| What | Where | SHA-256 starts with |
|---|---|---|
| V2 plug-in (installed) | `C:\Program Files\SoundProtection\SoundProtection.Apo.dll`, built from `native\out\apo\` | `460FFDBB` |
| V2 tray app (running) | `dist\SoundProtection.exe`, copied from `dist-staging\` | `49692943` |
| V1 plug-in (backup) | `C:\Users\Ariel\SoundProtection-V1-backup\SoundProtection.Apo.dll` | `221824F0` |
| V1 tray app (backup) | `C:\Users\Ariel\SoundProtection-V1-backup\SoundProtection.exe` | `E37C2271` |

## Not done yet

- **Not committed.** Everything is staged in git. `git status` shows it, and `git reset` would unstage it.
- **The real test is your ears.**

## Decisions I made for you

Each line says what I decided, then what it costs if I got it wrong.

1. **No commits.** You asked. Git's staging area holds the checkpoints instead.
   - If wrong: you see V2 staged rather than committed, and `git reset` unstages it.
2. **I built in the main project folder, not a separate copy.** V1's running files are untouched: the tray in `dist\`, the plug-in in Program Files, your settings in ProgramData.
   - If wrong: V2 sits next to V1's source, and git can always restore V1.
3. **Every helper was Sonnet, the final review included.** You said "use sonnets".
   - If wrong: a weaker final review. I checked its findings myself.
4. **The installer's own tests were not re-run.** The installer did not change.
   - If wrong: an installer problem would show at install time.
5. **I edited the shared contract file (`SpShared.h`) myself,** as its owner.
6. **Inside the sound engine, a cap that is not a number is ignored.** The file reader already turns it into Off first.
   - If wrong: harmless.
7. **The bass test allows 2.5 s to let go, the treble test 1.5 s.** Bass reacts more slowly by design.
   - If wrong: nothing.
8. **Tiny numbers in the level detector are flushed to zero.** This cannot change the sound, and it keeps the CPU fast.
   - If wrong: nothing.
9. **One test first failed while running rather than at compile time.** A process detail.
   - If wrong: nothing.
10. **The plug-in tests go beyond the plan.** Each added check catches a real bug the planned checks missed.
    - If wrong: the test run takes about 10 s longer.
11. **Until the tray writes the new file, the plug-in logs one line per audio start.** V1 does the same for its own file.
    - If wrong: a few extra lines in the plug-in's log.
12. **The plug-in reads the new file with a copy of the code that reads the old one, not shared code.** That keeps V1's proven code untouched.
    - If wrong: a future fix there has to be made twice.
13. **Before the last fixes, I ran the plug-in's full test host 3 times,** to see whether its timing-based checks ever fail at random. They passed 3 out of 3, so no test fix was needed.
    - If wrong: about 3 minutes of test runs.
14. **I kept the build notes folder** (`.superpowers\sdd\2026-11-17-bass-treble-caps\`). Normally it is deleted at the end, but nothing is committed, so it is the only detailed record of the build. Git ignores it.
    - If wrong: a folder of notes stays on disk. Delete it whenever you like.

## Small things left for later

None of these changes the sound. The final review said to leave them until V2 has proven itself:

- Share the file-reading and file-writing code between the old settings file and the new one (decision 12).
- Two more plug-in tests: a switch to 96 kHz sound, and the order of the two bands.
- Tidy-ups in the test code.
