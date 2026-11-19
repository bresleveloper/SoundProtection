# Human Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) checkbox syntax for tracking.

**Goal:** A double-click `SoundProtectionSetup.exe` (Inno Setup 6) that a non-technical human runs on **Windows 11**: it checks .NET, offers a one-click guided Microsoft-runtime download when missing, installs Sound Protection on every output device, and adds a tray "Protect this device" button for later-plugged devices.

**Architecture:** The proven `SoundProtection.Install.ps1` stays the engine (one addition: `-Endpoints All`). The setup exe is a face on it: Inno Setup pages + Pascal [Code] for the Win11 check, .NET runtime detection/download/install, running the script elevated once, and an uninstaller that runs `-Action Uninstall`. The tray app gains a button driven by the existing `ProtectionState.WatchedOutputCovered` flag.

**Tech Stack:** Inno Setup 6 (Pascal Script), .NET 10 WinForms tray app, Windows PowerShell 5.1 installer script, plain-PowerShell test harness.

**Spec:** `docs/superpowers/specs/2026-11-19-human-installer-design.md` — read it; this plan argues from it.

## Global Constraints

- **Windows 11 only.** Setup checks `GetWindowsVersion.Build >= 22000` at launch; on fail:
  `Sound Protection works on Windows 11 only. Setup will now close.` — then exit False. Nothing else runs.
- x64 only. The app publishes **framework-dependent** (`--self-contained false -r win-x64`);
  the setup requires the **Microsoft .NET 10 Desktop Runtime x64**.
- Runtime download URL, exact: `https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{V}/windowsdesktop-runtime-{V}-win-x64.exe`,
  `{V}` resolved at **build** time from `https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0/latest.version`
  (verified 2026-11-19: `10.0.15`, installer 57.3 MB) and its SHA-256 pinned via ISCC `/D` defines. The setup verifies the hash.
- Install dir: `C:\Program Files\SoundProtection` (Inno `DefaultDirName={autopf}\SoundProtection`).
- Startup value: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, name `SoundProtection`,
  data `"{app}\SoundProtection.exe"` (quoted). Uninstall deletes it. **Never HKLM.**
- The engine script changes **only** by accepting `-Endpoints All`; journal, rollback, repair, verify,
  ACL, service semantics untouched. The DSP and tray protection logic untouched.
- Unsigned (no signing step anywhere). English copy only. No runtime bundled in the setup file.
- Tray process name for close/kill: `SoundProtection.exe`.
- Inno AppId literal: `{D9A7C6F1-52B4-4E17-9E9F-6D51E3B84A10}`. `AppVersion` = `0.1.0` (matches the app's csproj).

## Review Focus

1. **Win10 or older machine:** double-click → (UAC) → the one message, setup closes, and *nothing was
   checked, downloaded, copied or written* (InitializeSetup False aborts before any [Files] work) —
   pinned in Task 5 step "Win11 gate"; no automated test can run Inno's UI, so the reviewer must
   verify the gate is the first thing in `[Code]` and unskippable.
2. **Runtime download fails mid-way or offline:** the guy sees a clear "connect to the internet once,
   then Try again" page, can retry, and nothing is half-installed — pinned in Task 5's except branch.
3. **Downloaded runtime does not match the pinned SHA-256:** setup refuses it and shows the same
   retry page — pinned by `DownloadPage.Add(url, file, hash)`'s hash enforcement (Task 5).
4. **A new device with no sound playing** (green "Protected (nothing playing)" on an uncovered
   output): the Protect-this-device button must still appear — pinned by a Task 2 test (the button
   keys off `WatchedOutputCovered`, not the amber status).
5. **Windows offers no hook on the device** (script exits 1, e.g. monitor/HDMI outputs): the panel
   says the device cannot be protected, no crash, no spinner forever — pinned by Task 2's
   `ResultMessage(exitCode)` theory test.
6. **Reinstall over a running tray copy from any folder** (incl. a repo `dist\` copy): setup asks to
   close it and force-closes on fallback, then continues — pinned in Task 5's `PrepareToInstall`
   (smoke-verified manually on the next real run).

---

### Task 1: `-Endpoints All` in the engine script

**Files:**
- Modify: `installer/SoundProtection.Install.ps1` (`Find-SpEndpoints`, ~lines 609-647; not-found message, ~lines 1345-1348)
- Test: `installer/Install.Tests.ps1` (append after the rehearsal-run tests)

**Interfaces:**
- Consumes: `Find-SpEndpoints -Base <string> -Names <string[]>` as it is.
- Produces: `Find-SpEndpoints` where a `Names` entry equal to `'All'` (case-insensitive) matches **every** render endpoint that passes the existing capability filter (both `{d04e05a6...},13` and `,15` value names present in `FxProperties`). All other behavior identical. `-'All'` works for `Install`, `Repair` and `Status`, and needs no change elsewhere in the script.

- [ ] **Step 1: Write the failing tests**

Append to `installer/Install.Tests.ps1` (uses the existing `New-Rehearsal`, `Invoke-Reh`, `Get-FxPath`, `Assert-True`, `Run-Test` helpers):

```powershell
# Creates a fake endpoint directly in the rehearsal hive. $PropListNames = the FxProperties value names it gets.
function Add-FakeEndpoint([string]$Name, [string[]]$PropListNames = @($ListLeveler, $ListSafety)) {
    $id = '{' + [guid]::NewGuid().ToString().ToUpper() + '}'
    $props = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$RehSub\$RenderRel\$id\Properties")
    $props.SetValue($PropName, $Name)
    $props.Close()
    $fx = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$RehSub\$RenderRel\$id\FxProperties")
    foreach ($n in $PropListNames) { $fx.SetValue($n, [string[]]@()) }
    $fx.Close()
    return $id
}
```

Three tests (each calls `New-Rehearsal` first, mirroring the existing rehearsal tests):

- `Install with All covers every capable endpoint and skips incapable ones` —
  `Add-FakeEndpoint 'USB Test Device'` (both lists), `Add-FakeEndpoint 'Half Hooked' @($ListLeveler)`
  (only `,13`). Run `Invoke-Reh 'Install' @('-Endpoints', 'All')`. Assert exit code 0; assert
  `"$(Get-FxPath $usbId)" ,13 and ,15 values both contain `$ClsidLeveler` and `$ClsidSafety`
  (read the values as `[string[]]`); assert Half Hooked's `,13` value is still empty (no CLSID added);
  assert every `$script:HpIds` Headphones `FxProperties` contains both CLSIDs.
- `Repair with All attaches a device plugged in later` — run `Invoke-Reh 'Install' @('-Endpoints','All')`;
  then `Add-FakeEndpoint 'Late Usb'`; then `Invoke-Reh 'Repair' @('-Endpoints','All')`. Assert exit 0;
  assert Late Usb's `,13`/`,15` now contain both CLSIDs; assert the install journal (read
  `$InstDirBox\install-journal.json`) gained records keyed under Late Usb's `FxProperties` path, and
  that the Half-Hooked device from the first test (if present) is still untouched.
- `All with no capable endpoint exits 1 and changes nothing` — `New-Rehearsal`; add only
  `Add-FakeEndpoint 'No Hook' @()` (no list values); run `Invoke-Reh 'Install' @('-Endpoints','All')`;
  assert exit code 1; assert the output mentions `No render endpoint`; assert the test key value
  `DisableProtectedAudioDG` does **not** exist under `$RehBase\$AudioRel` (the run stopped before step 7).
- [ ] **Step 2: Run the tests to verify they fail**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File installer\Install.Tests.ps1`
Expected: the three new tests FAIL (All matched nothing / wrong message), all existing tests PASS.

- [ ] **Step 3: Implement**

In `Find-SpEndpoints`: the `$wanted` choice becomes

```powershell
$wanted = $false
foreach ($n in $Names) {
    if ($n -ieq 'All') { $wanted = $true; break }     # All: every endpoint that passes the capability filter
    if ($friendly -ieq $n) { $wanted = $true; break }
}
```

At the not-found error (~line 1346): when `$Cfg.Names -ieq 'All'` the message is
`No render endpoint that can take the plug-in (both composite lists ,13 and ,15) was found under {1}. Nothing was changed.`
— same logging as before, only the text differs. (The `-Endpoints All` value flows through the main
block's comma-splitting unchanged; `Status` picks up All for free.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File installer\Install.Tests.ps1`
Expected: all tests PASS (previous count + 3).

- [ ] **Step 5: Commit**

```bash
git add installer/SoundProtection.Install.ps1 installer/Install.Tests.ps1
git commit -m "Installer: -Endpoints All attaches every capable output device"
```

---

### Task 2: Protect-device decision and result text (Core, test-first)

**Files:**
- Create: `src/SoundProtection.Core/ProtectDevice.cs`
- Test: append class `ProtectDeviceTests` to `tests/SoundProtection.Tests/TrayTests.cs`

**Interfaces:**
- Consumes: `ProtectionState` (`src/SoundProtection.Core/ProtectionEngine.cs:19`) with
  `WatchedOutputCovered` (named arg in constructor: `watchedOutputCovered`) and `Status`
  (`ProtectionStatus`).
- Produces: `static class ProtectDevice` with
  `static bool ShouldShow(ProtectionState state)` and
  `static string ResultMessage(int exitCode)`. Task 3 consumes both names exactly.

- [ ] **Step 1: Write the failing tests** (reuse the `Protection(...)` helper's constructor pattern; plain positional `new(...)` with named `watchedOutputCovered:`)

```csharp
public class ProtectDeviceTests
{
    private static ProtectionState State(ProtectionStatus status = ProtectionStatus.Protected, bool covered = true) =>
        new(status, "any", 0.0, 0.0, 0.0, Array.Empty<AppTurnDown>(), watchedOutputCovered: covered);

    [Fact] Button_shows_when_the_watched_output_is_not_covered_even_while_green: ShouldShow(State(covered: false)) == true;
           ShouldShow(State(ProtectionStatus.NotProtected, false)) == true;
           ShouldShow(State(ProtectionStatus.Idle, false)) == true;
    [Fact] Button_hides_when_the_protection_switch_is_off: ShouldShow(State(ProtectionStatus.Off, false)) == false;
    [Fact] Button_hides_when_the_watched_output_is_covered: ShouldShow(State(ProtectionStatus.Protected, true), NotProtected/covered, Idle/covered) == false;

    [Theory] ResultMessage(exitCode):
        0   -> "Protected on this device."
        3   -> "Sound Protection is on, but Windows needs a restart of the PC before it runs. Restart, then try again."
        1   -> "Not protected: Windows offers no hook on this device, or the run failed. See C:\Program Files\SoundProtection\install-log.txt."
        999 -> the same string as 1.
}
```

(The third test asserts `false` for Protected+covered, NotProtected+covered and Idle+covered.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SoundProtection.Tests --filter ProtectDeviceTests`
Expected: FAIL — `ProtectDevice` does not exist.

- [ ] **Step 3: Implement `ProtectDevice`**

`ShouldShow`: `return !state.WatchedOutputCovered && state.Status is not ProtectionStatus.Off;`
`ResultMessage`: returns the three strings above verbatim; any code other than 0 or 3 gets the
exit-1 string. One `switch`. (UAC decline and missing script never produce a process; Task 3 maps
both to exit code 1.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SoundProtection.Tests --filter ProtectDeviceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SoundProtection.Core/ProtectDevice.cs tests/SoundProtection.Tests/TrayTests.cs
git commit -m "Core: ProtectDevice button visibility and result text"
```

---

### Task 3: Panel — the Protect-this-device button

**Files:**
- Create: `src/SoundProtection.App/ProtectDeviceRunner.cs`
- Modify: `src/SoundProtection.App/PanelForm.cs` (fields near the other controls; wire-up in `ShowStatus`, ~line 413)

**Interfaces:**
- Consumes: `ProtectDevice.ShouldShow/ResultMessage` (Task 2);
  `ProtectionPaths.InstallerPath` (existing, `src/SoundProtection.App/ProtectionPaths.cs:24`);
  the launch pattern of `TrayApp.StartInstaller` (`src/SoundProtection.App/TrayApp.cs:322`).
- Produces: `static class ProtectDeviceRunner` with `static int Run()` — returns the script's exit
  code; **1** when the script file is missing or the administrator prompt was declined
  (`Win32Exception` with `NativeErrorCode == 1223`, the existing `UacDeclined` path). Uses
  `ProcessStartInfo("powershell.exe")` with `UseShellExecute = true`, `Verb = "runas"`,
  `Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ProtectionPaths.InstallerPath}\" -Action Repair -Endpoints All"`
  and `WaitForExit(240_000)`; returns `p.ExitCode` when it exited, otherwise 1. **Repair -Endpoints All** is the
  attach (journal-preserving; no `-DllSource`, the script falls back to the installed DLL).

- [ ] **Step 1: Write `ProtectDeviceRunner`** as above (UI-thread-off: the Panel click calls it inside `Task.Run`, exactly like `TrayApp.RunInstaller` does).
- [ ] **Step 2: Wire the panel** — a small button `Protect this device` (any WinForms control the file already styles; it sits under the status line) and a result label. In `ShowStatus`: after `state` is known, `_protectButton.Visible = ProtectDevice.ShouldShow(state);` and hide the result label when the button is hidden. Click: `Task.Run` → `Run()` → back on the UI thread show `ProtectDevice.ResultMessage(code)` in the result label and re-enable the button (disable it while running; no double-run).
- [ ] **Step 3: Verify the whole suite is green**

Run: `dotnet test tests/SoundProtection.Tests`
Expected: PASS (the previous 497+ count plus Task 2's). The UI wiring itself is not unit-tested (the test project never instantiates PanelForm); it is pinned by Review Focus 4/5 logic tests plus the real-use check at the end of the spec.

- [ ] **Step 4: Commit**

```bash
git add src/SoundProtection.App/ProtectDeviceRunner.cs src/SoundProtection.App/PanelForm.cs
git commit -m "Tray: Protect this device button (Repair -Endpoints All)"
```

---

### Task 4: Build pipeline — payload and pinned runtime

**Files:**
- Create: `installer/build.ps1`, `installer/build.cmd`
- Modify: `.gitignore` (if `installer/payload/`, `installer/out/`, `installer/prerq/` are not already ignored)

**Interfaces:**
- Produces: `installer\payload\app\` (framework-dependent publish incl. `SoundProtection.exe`),
  `installer\payload\apo\SoundProtection.Apo.dll`, `installer\payload\script\SoundProtection.Install.ps1`,
  `installer\prerq\windowsdesktop-runtime-{V}-win-x64.exe`, and a summary line
  `PAYLOAD OK: runtime {V} sha {SHA256}`. Task 5 consumes these paths and the printed values.

- [ ] **Step 1: Write `installer/build.ps1`**

Pipeline (fail loudly on the first missing piece, with a plain message each):

1. `dotnet publish src\SoundProtection.App -c Release -r win-x64 --self-contained false -o payload\app` → assert `payload\app\SoundProtection.exe` exists.
2. If `native\out\apo\SoundProtection.Apo.dll` is missing, run `native\build-apo.cmd`; copy it to `payload\apo\`.
3. Copy `SoundProtection.Install.ps1` → `payload\script\`.
4. Runtime pin: `$v = [string](Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0/latest.version' -UseBasicParsing).Content.Trim()`; must match `^10\.0\.\d+$`. URL = the Global Constraints pattern with `{V}` = `$v`. Download (if `prerq\windowsdesktop-runtime-$v-win-x64.exe` is absent) into `prerq\`; `$sha = (Get-FileHash <file> -Algorithm SHA256).Hash`.
5. Print `PAYLOAD OK: runtime $v sha $sha`. If `payload\..` staging and pin all succeeded but
   `.\SoundProtection.iss` does not exist, print `PAYLOAD STAGE COMPLETE — no .iss yet` and exit 0 (Task 5 replaces this with the real ISCC call).

- [ ] **Step 2: Write `installer/build.cmd`** — the one-liner `@powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*`
- [ ] **Step 3: Add the .gitignore entries** if missing.
- [ ] **Step 4: Run it**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1`
Expected: `PAYLOAD OK: runtime 10.0.x sha …` and the four payload/prerq artifacts exist.

- [ ] **Step 5: Commit**

```bash
git add installer/build.ps1 installer/build.cmd .gitignore
git commit -m "Build: payload staging and pinned .NET runtime download"
```

---

### Task 5: The Inno script — pages, runtime download, install, uninstall

**Files:**
- Create: `installer/SoundProtection.iss`
- Modify: `installer/build.ps1` (replace the "no .iss yet" exit with the real compile)

**Interfaces:**
- Consumes: Task 4's payload paths and pin values (passed as ISCC defines `/DRuntimeVersion={V} /DRuntimeSha256={SHA}`, read in the script as `{#RuntimeVersion}` / `{#RuntimeSha256}`).
- Produces: `installer\out\SoundProtectionSetup.exe` (the deliverable).

Script sections (write in this order; every user-visible string below is copy-exact):

- [ ] **Step 1: Write the `[Setup]` block** — `AppId={{D9A7C6F1-52B4-4E17-9E9F-6D51E3B84A10}`, `AppName=Sound Protection`, `AppVersion=0.1.0`, `AppPublisher=Sound Protection`, `DefaultDirName={autopf}\SoundProtection`, `DefaultGroupName=Sound Protection`, `UninstallDisplayName=Sound Protection`, `ArchitecturesInstallIn64BitMode=x64compatible`, `PrivilegesRequired=admin`, `OutputDir=out`, `OutputBaseFilename=SoundProtectionSetup`, `Compression=lzma2/max`, `SolidCompression=yes`, `DisableDirPage=yes`, `DisableProgramGroupPage=yes`, `CloseApplications=yes` (Inno's own running-app check) — plus `SetupIconFile` omitted (default icon, no icon asset exists in the repo).
- [ ] **Step 2: `[Code]` — the Win11 gate first.** In `InitializeSetup(): Boolean`: `if GetWindowsVersion().Build < 22000 then begin MsgBox('Sound Protection works on Windows 11 only. Setup will now close.', mbError, MB_OK); Result := False; end`. This is the very first thing executed; per Review Focus 1 nothing on any path may precede it.
- [ ] **Step 3: `[Files]`** — paths are relative to the .iss location: `payload\app\*` → `{app}` (recursesubdirs flag); `payload\apo\SoundProtection.Apo.dll` → `{app}`; `payload\script\SoundProtection.Install.ps1` → `{app}`. Nothing else ships; the runtime installer is downloaded at setup time, never packed.
- [ ] **Step 4: the plain-language page** — `[Setup]` gets `InfoBeforeFile=setup-notice.txt`; create `installer/setup-notice.txt` containing the spec's step-4 copy verbatim:

```
Setup will briefly stop all sound (about 5 seconds) and turn Sound Protection
on for every output device that can take it on this PC (on a typical PC: the
headphones and the speakers; HDMI/monitor outputs offer no hook and are skipped).

While it is on, some copy-protected video (Netflix and the like) may play
without sound. The desktop shortcut "Sound Protection - Undo" restores the
original settings at any time.
```

- [ ] **Step 5: `[Registry]`** — `Root: HKCU; Subkey: Software\Microsoft\Windows\CurrentVersion\Run; ValueType: string; ValueName: SoundProtection; ValueData: """{app}\SoundProtection.exe"""; Flags: uninsdeletevalue`
- [ ] **Step 6: `[Code]` — .NET runtime.**
  - `IsDotNetDesktop10Installed(): Boolean` — `{pf}\dotnet\shared\Microsoft.WindowsDesktop.App` exists and any subfolder name starts with `10.` (`FindFirst` walk).
  - In `NextButtonClick(CurPageID)`: on `wpReady`, when `not IsDotNetDesktop10Installed()`:
    `DownloadPage.Add('https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{#RuntimeVersion}/windowsdesktop-runtime-{#RuntimeVersion}-win-x64.exe', 'windowsdesktop-runtime.exe', '{#RuntimeSha256}')`, page text
    `Sound Protection needs a free component from Microsoft (one-time, about 60 MB).`, then `DownloadPage.Download`. On any exception: show `Could not download it. Connect to the internet once, then choose Try again.` and `Result := False` (stay on the page; Next again = Try again). After a successful download: `Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code)`; if afterwards `not IsDotNetDesktop10Installed()` → error message + `Result := False`.
- [ ] **Step 7: `[Run]`/`[Code]` — run the engine once.** In `CurStepChanged(usPostInstall)` (files are in place, admin context): `Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass -File "{app}\SoundProtection.Install.ps1" -Action Install -Endpoints All -DllSource "{app}\SoundProtection.Apo.dll"', ExpandConstant('{app}'), SW_SHOWMINNOACTIVE, ewWaitUntilTerminated, Code)`; store `g_InstallExit := Code`. In `CurStepChanged(usDone)`: `Code = 0` → MsgBox `Sound Protection is on for every output device. Your sound will be capped before the volume slider.`; `Code = 3` → `Installed. Windows needs a restart of the PC for it to run. Restart, then everything works.`; otherwise → `Setup failed. Nothing is protected; see install-log.txt in C:\Program Files\SoundProtection\.` (all `mbInformation`/`mbError` as fits). No `[Run]` section entries.
- [ ] **Step 7: `[Code]` — uninstall.** `CurUninstallStepChanged(usUninstall)` runs **before** file deletion: soft-kill first (`Exec('taskkill.exe', '/IM SoundProtection.exe', '', SW_HIDE, ewWaitUntilTerminated, Code)`), then `Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "{app}\SoundProtection.Install.ps1" -Action Uninstall', '', SW_SHOWMINNOACTIVE, ewWaitUntilTerminated, Code)` — exit 3 is fine (a restart may be pending), anything else gets one MsgBox naming `install-log.txt`. `usPostUninstall` deletes the HKCU startup value via `RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SoundProtection')` (idempotent: ignore failure). The journal, log and registry backups in `C:\Program Files\SoundProtection` are **not** deleted (same as the desktop Undo shortcut leaves them).

  Reinstall-over-running-tray (Review Focus 6): rely on `[Setup]` `CloseApplications=yes` for the tray (matched by process name regardless of folder), and the script's own copy retries; no extra taskkill on install.

- [ ] **Step 8: Wire the compile into `build.ps1`** — replace the "no .iss yet" exit with:
  locate `"$env:(ProgramFiles(x86))"`… in PowerShell: `$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'`; if missing, `winget install --id JRSoftware.InnoSetup -e --source winget --accept-source-agreements --accept-package-agreements` then re-check. Run `& $iscc "/DRuntimeVersion=$v" "/DRuntimeSha256=$sha" "$PSScriptRoot\SoundProtection.iss"`; assert exit code 0 and `installer\out\SoundProtectionSetup.exe` exists; update the summary line to `SETUP OK: installer\out\SoundProtectionSetup.exe (<size> MB, runtime $v)`. Keep the `[InnoSetup]`-less payload steps unchanged.

- [ ] **Step 9: Verify the full build**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1`
Expected: compiles clean; `SoundProtectionSetup.exe` exists, size **1–30 MB**; ISCC log lists the three `[Files]` entries.

- [ ] **Step 10: Commit**

```bash
git add installer/SoundProtection.iss installer/build.ps1
git commit -m "Installer exe: Inno Setup script (Win11 gate, guided .NET, install, uninstall)"
```

---

### Task 6: Send-along note and full verification

**Files:**
- Create: `installer/README-send-with.md`

- [ ] **Step 1: Write the send-along note** (short, copy-exact, plain human):

```markdown
# Sending Sound Protection to someone

Sound Protection caps sound before the volume slider. This installer:

1. Works on **Windows 11** only.
2. Needs one **Yes** on the administrator prompt. It may ask to download
   a free Microsoft component once (about 60 MB) — one click, then continue.
3. **All sound stops for about 5 seconds** in the middle, then comes back.
4. While it is on, some copy-protected video (Netflix and the like) may play
   **without sound**. The desktop shortcut **Sound Protection - Undo** restores
   the original settings at any time; "Installed apps" also has an Uninstall.
5. First time, Windows may say "Windows protected your PC": press
   **More info → Run anyway**.
6. If it finishes and sound is still unprotected, restart the PC once and the
   tray icon will say Protected.
```

- [ ] **Step 2: Full verification** — run, in order, and expect all green:
  `powershell -NoProfile -ExecutionPolicy Bypass -File installer\Install.Tests.ps1` → all PASS;
  `dotnet test tests/SoundProtection.Tests` → all PASS;
  `powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1` → `SETUP OK`.
- [ ] **Step 3: Commit and push**

```bash
git add installer/README-send-with.md
git commit -m "Installer: send-along note"
git push origin master
```

---

**After the plan (Ariel's manual step, not agent work):** run `installer\out\SoundProtectionSetup.exe`
once on this PC — the real-use check. It will attach the Speakers beside the Headphones (a real change
to this machine) and rewrite the startup value to the Program Files copy; write the install report
after it, as with V1 and V2.
