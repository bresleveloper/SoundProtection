# Design: the human installer for Sound Protection

2026-11-19. Built on the 2026-11-17 code (V2). Companion to `docs/03/INSTALL-REPORT.md`.

## Intent and why

Sound Protection has a proven PowerShell installer (`installer/SoundProtection.Install.ps1`) that
installed V1 and V2, but using it means opening PowerShell — not something to ask of another person.
This adds a **double-click .exe setup** for a non-technical human on their own Windows PC:

- one file, small enough to email or drop into a chat (about 8 MB);
- if their PC lacks the .NET runtime, setup says so and offers **"Click here to install .NET"**,
  downloads the official Microsoft runtime, then resumes the main install automatically;
- it protects **every output device present** (Headphones, Speakers, monitor — not just "Headphones");
- for devices plugged in later, the tray app gets a **"Protect this device"** button.

The existing installer script stays the engine. The setup adds a face and a front door;
it does not re-implement the risky parts (registry, journals, services, rollback).

## Goals

1. A human can go from "received a file" to "protected" by double-clicking, one admin Yes, and
   answering nothing technical.
2. Works on a bare Windows 11 x64 PC with internet; fails politely without it. **Windows 11 only**:
   setup checks the build number at launch and, on anything older, states it plainly and closes
   without changing anything.
3. Re-installing or upgrading with the same file is safe (idempotent, journal-preserving).
4. Undo/Uninstall stays a single desktop shortcut, as today, and an entry in
   Windows "Installed apps".
5. The product's honesty rules carry over: the person is told what happens (sound stops ~5 s,
   the copy-protected-video side effect), and every change stays journalled for Undo.

## Non-goals

- No code signing or certificate purchase (unsigned; see Constraints for the SmartScreen note).
- No new daemon, no background watcher that silently re-attaches devices.
- No localization; English UI only.
- No bundling of the .NET runtime inside the setup file (that is the ~40–70 MB alternative,
  deliberately not picked).
- No changes to the plug-in's DSP or the protection math.

## Anti-goals (a failure even if it technically works)

- Reaching HKLM through anything user-writable (the existing script's rules 11/journal rules hold).
- An install that leaves sound dead, or an uninstall that is harder than Undo is today.
- Setup screens that hide the DRM/test-key side effect.

## Approaches considered

| | File size | Other human needs | Verdict |
|---|---|---|---|
| **A. Self-contained setup** — app built with its .NET runtime packed in | ~40–70 MB | nothing, works offline | right, too big for "email it to a friend" |
| **B. Small setup + guided .NET download** ← picked | ~8 MB | internet once, one click on a Microsoft download | small and self-explaining |
| **C. Small setup, assumes .NET exists** | ~5 MB | .NET 10 Desktop Runtime already present | fails on a bare PC — not for a simple human |

Tooling considered for the setup exe: **Inno Setup 6** (picked: proper wizard UI, elevation,
"Installed apps" entry, Pascal scripting for the .NET check + download with progress, LZMA),
WiX/MSI (poor fit: our install is dynamic device discovery + services + ACLs, not an MSI shape),
NSIS, and a hand-written C# bootstrapper (weeks of UI work for what Inno gives free).

## What the guy sees

1. Double-click `SoundProtectionSetup.exe`. **First, a Windows check** (build ≥ 22000, i.e. Windows
   11): on Windows 10 or older, a single message — "Sound Protection works on Windows 11 only" —
   and setup closes without changing anything. (The admin Yes comes first — the setup is signed
   into elevation by its manifest — so on an out-of-support PC the guy sees UAC once, then this.)
   SmartScreen (first run, unsigned): "Windows protected your PC" → More info → Run anyway. This
   text goes in the README/WhatsApp message sent with the file.
2. Admin Yes (setup is elevated once; everything after is silent).
3. Setup checks for the **Microsoft .NET 10 Desktop Runtime x64** (`shared\Microsoft.WindowsDesktop.App\10.*`).
   - Present → straight to the wizard (mostly Next → Next → Finish).
   - Missing → page: "Sound Protection needs a free component from Microsoft (one-time,
     ~60 MB download). Click Install." Progress bar; the pinned runtime installer runs silently
     (`/install /quiet /norestart` — no second admin prompt); setup resumes. No internet →
     "Connect to the internet once, then press Try again." Nothing half-installed.
4. A plain-language page: "Setup will briefly stop all sound (~5 seconds) and turn Sound Protection
   on for **every output device that can take it** on this PC (on a typical PC: the headphones and
   the speakers; HDMI/monitor outputs offer no hook and are skipped). Some copy-protected video
   (Netflix and the like) may play without sound while it is on; Undo restores the original settings."
5. Install: copies the files, runs the proven script elevated (`-Action Install -Endpoints All`),
   writes the tray's existing per-user startup value so the app starts with Windows (turn-off in the
   tray, as today), creates the "Sound Protection - Undo" desktop shortcut, verifies with the test
   tone, and shows success, or exit-code-3's "restart the PC and run again".
6. Uninstall: "Installed apps → Sound Protection → Uninstall" — removes the tray app, the Run key,
   and runs the script's `-Action Uninstall` (journal restore, as the desktop shortcut does today).

## Payload and layout

```
SoundProtectionSetup.exe (Inno build)
  ├─ app\            framework-dependent publish: SoundProtection.exe (~1.6 MB, 10 files, win-x64)
  ├─ apo\            SoundProtection.Apo.dll (249 KB)
  ├─ script\         SoundProtection.Install.ps1 (the same proven script)
  └─ [Code]          .NET check, pinned download, post-install steps
```

Installed to `C:\Program Files\SoundProtection\`: the tray app files + Apo dll + script, laid out
as the script already expects (`-DllSource` points at the payload copy; the script also copies
itself there, as today). Tray app runs from there; the startup value points at that exe.
`installer/build.cmd` produces the setup: publish the app → payload folder → `ISCC.exe`.

The script's FxProperties-only capability filter is the right contract (registry scan on 2026-11-19:
a name-matched endpoint can take the plug-in exactly when both composite lists sit in FxProperties;
Headphones and Speakers qualify, monitor/HDMI outputs have no lists anywhere, and no endpoint on
this PC keeps them in Properties — the tray's Properties fallback never fires and stays as
defensive code).

## Changes outside the new setup

1. **Script: `-Endpoints All`.** `Find-SpEndpoints` currently matches names. An `All` entry matches
   *every* render endpoint that has both composite lists (the existing capability filter — the same
   one that skips devices the plug-in cannot attach to). Unit-tested in `installer/Install.Tests.ps1`
   alongside the existing cases. Everything else (journal, rollback, repair, verify) untouched.
2. **Tray app: "Protect this device".** Driven by the existing `WatchedOutputCovered` flag (a real
   state the app already tracks, independent of sound — install report Ruling 21), not by the amber
   status: an idle-uncovered output shows green today, and the button must still appear. So: the
   button is shown whenever the watched/current output lacks the plug-in and the protection switch is
   on (the switch, not Repair, fixes Off). Clicking runs the installed script elevated:
   `-Action Repair -Endpoints All`. Repair is journal-preserving and idempotent: it attaches every
   capable device present — the new one included — and left-attached ones are no-ops, so no device
   name needs to travel from the panel to the script.
   If the script answers "no capable endpoint" (exit 1), the panel says plainly: "Windows offers no
   hook on this device, so it cannot be protected." The button
   speaks the outcome ("Protected", "Needs admin — press Yes", "Sound stopped ~5 s").
   One UAC Yes per new device; this is the accepted cost of "always works on the current output device".
3. **Startup at install:** the same HKCU value name the tray's toggle manages
   (`Software\Microsoft\Windows\CurrentVersion\Run\SoundProtection`), written for the installing user
   by the elevated setup (UAC-as-same-user sees that user's hive). Not HKLM: an HKLM entry would
   autostart every user and the tray's toggle — which only owns the HKCU value — could not turn it
   off. Other users of the machine can start the tray and toggle their own entry.
   Current behavior is opt-in; a new human would never find it — and protection itself persists
   without the tray. Marked as a call I made; reversible in the tray.
4. **Installing over anything already running or old.** Setup prompts to close any running
   `SoundProtection.exe` — matched by process name, any path, so an older tray copy running from a
   build folder is found too — with a force-close fallback. It always rewrites the startup value to
   the new exe path, which replaces any stale one (e.g. one that pointed at a repo `dist\` copy).
   Nothing outside `C:\Program Files\SoundProtection` and that HKCU value is deleted.

## Constraints

- **Unsigned SmartScreen** is real friction for "the simple human": first run needs
  More info → Run anyway, and Microsoft's reputation builds with installs. Accepted; documented in
  the message that travels with the file. Signed builds are a later decision, not this project.
- The pinned .NET download must be version-pinned with its SHA-256 (integrity check; the URL
  pattern `builds.dotnet.microsoft.com/dotnet/Runtime/<v>/windowsdesktop-runtime-<v>-win-x64.exe`
  is version-stable). Builder picks the current 10.x version and records it in the Inno script.
- Reinstall-over-running-tray: setup asks to close the running `SoundProtection.exe` (Inno's
  close-prompt), then overwrites.
- 64-bit only; the script itself refuses 32-bit today. **Windows 11 only** (build ≥ 22000) —
  no Windows 10 support, by decision; the launch check enforces it.

## Left to the builder

- The Inno Setup script (wizard pages, [Code] for the .NET check/download/silent-install/resume,
  the Installed apps entry, close-app prompt), `installer/build.cmd`, winget install of Inno Setup 6
  as a build dependency.
- Exact pinned runtime version + SHA-256 (verify the URL returns bytes before committing to it).
- Test strategy in the project's existing style: installer tests for `-Endpoints All`; a payload +
  compile smoke test in `build.cmd`; tray tests for the button states; the full UI/wizard flow gets
  its real-use check on a target PC (documented in an install report, as with V1/V2).
- Wording of the two plain-language setup pages (kept from the "What the guy sees" above).
