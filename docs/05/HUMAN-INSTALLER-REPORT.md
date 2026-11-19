# Sound Protection: the human installer — build + live-install report

2026-11-19 (built; installed on Ariel's own PC at 21:12, tray started at 01:02).

## In short

- `installer\out\SoundProtectionSetup.exe` (2.5 MB) exists and **works**: it installed on this PC
  end to end — the first real run anywhere.
- The plug-in is now on **every output device that can take it here**: Headphones (as before) and
  the Speakers (new). Verified listed on both, test key on, exclusive mode off.
- The tray app now runs from `C:\Program Files\SoundProtection\` and **starts with Windows for the
  first time** (the new-human default; the old dist copy is untouched on disk).
- Uninstall: the desktop **Sound Protection - Undo** shortcut (unchanged) or
  **Installed apps → Sound Protection**.
- Send-along message for other people: `installer\README-send-with.md`.

## What the guy sees (the designed flow, first two steps exercised here)

1. Double-click → one admin Yes (UAC) → on Windows 10 or older: one message, closes, nothing changed.
2. (If their PC lacks .NET 10: a page with a one-click Microsoft download, ~60 MB, then it resumes.
   Not exercised here — this PC has it.)
3. A plain-language notice (sound stops ~5 s; every-capable-device scope; copy-protected-video note).
4. Install, verify, and a message: "…on for every output device" / "restart the PC…" / failure text.
5. First launch on new PCs: Windows may show "Windows protected your PC" → More info → Run anyway.

## What changed on the PC (from `C:\Program Files\SoundProtection\install-log.txt`, 21:12:19–21:12:29)

1. .reg backup of the four capable endpoint keys + the Audio key → `backup\20261119-*`.
2. The journal grew by **6 new values** (the Speakers', active and unplugged twin); the first
   originals from 11-11 are kept, so **Undo still restores the true originals**.
3. Audio services stopped and restarted: AudioEndpointBuilder, Audiosrv, midisrv,
   RtkAudioUniversalService — all running again.
4. The tray app files (SoundProtection.exe + .NET files) copied into `C:\Program Files\SoundProtection\`;
   the existing Apo DLL and script stay in place.
5. `Sound Protection - Undo` desktop shortcut re-created (same as before).
6. **VERIFY: PASS** — the −40 dB test tone went through the plug-in.
7. Inno's own records: `unins000.exe` in the install folder + "Installed apps" entry;
   `HKCU\...\Run\SoundProtection` = the Program Files copy (first time this value has ever been set).

## Checks that passed

- Setup compiled clean (ISCC 6.7.3, warning about HKCU under admin installs reviewed — by design).
- Engine exit code 0; verify tone PASS; heartbeat fresh (59 ms) after the tray started.
- Status (`-Endpoints All`): Speakers ×2 and Headphones ×2 — both effect lists on each; effects
  enabled; exclusive off; test key on.
- The tray app started from Program Files with **no errors** in its log (`%APPDATA%\SoundProtection\log.txt`).
- Silent flags used here: `/SILENT /NORESTART /SUPPRESSMSGBOXES` (second attempt; first attempt was
  cancelled at UAC and exited 5, changing nothing — noted as a normal cancel, not a bug).

## What remains for another PC (the friend)

- The real wizard pages, the guided .NET download, and the SmartScreen "Run anyway" — first seen there.
- After the install the tray icon appears **after the next sign-in** (or a restart); it does not launch
  itself at the end of setup — a follow-up below.
- Ask them for the last lines of `C:\Program Files\SoundProtection\install-log.txt` and whether the
  tray says Protected; that's the install report for that PC.

## Follow-ups

1. Make the 21 installer-suite tests machine-state independent (they now assume an unprotected PC and
   fail on any protected machine — pre-existing, identical before/after this work).
2. Optionally launch the tray at the end of setup so the icon is up immediately on first install.
3. Optionally exclude `*.pdb` from the payload (ships ~0.2 MB of debug info today).
4. Fix the deferred minor M8 (tray action results invisible): the new Protect-this-device button shows
   a result, but the old Repair/Remove menu items still close fast and silently.

## Commands (as before)

| What | Command |
|---|---|
| Status (read-only) | `powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\SoundProtection\SoundProtection.Install.ps1" -Action Status -Endpoints All` |
| Uninstall (same as the shortcut) | same file, `-Action Uninstall` |
| Remove everything incl. app | Installed apps → Sound Protection → Uninstall |
