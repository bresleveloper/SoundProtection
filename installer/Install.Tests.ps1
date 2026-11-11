<#
.SYNOPSIS
    Rehearsal tests for SoundProtection.Install.ps1. Plain PowerShell asserts, no Pester.
    Exit code 0 = every test passed, 1 = at least one failed.

.DESCRIPTION
    R1-R7 are the tests named in the plan (Task 3). R5b, R6b, R7b-R7d, R8-R18 and U1-U25 pin the rest
    of the Review Focus: first originals kept, byte-exact restore, rollback, WhatIf writes nothing,
    a tampered journal is not obeyed, and the real-only helpers (ACLs, files, shortcut, reg export,
    argument quoting, Verify parsing) that a rehearsal cannot reach, which are exercised against
    sandboxes under %TEMP%.

    Fix round 1 (rulings 10 and 11): R15, U16, U16b, U16c and U20 pin that a failed Repair puts back only
    its own writes and that the audio services come back; R16, R16b and U17 pin that the journal, the
    .reg backups and the log live in -InstallDir (which only administrators can write) and that nothing
    is ever read back from -DataDir; R17, R17b, U17b and U19 pin the junction/symlink refusal, R18 and
    U21 the rehearsal guard, U18 and U18b the owner change.

    Final-review fix (ruling 27, M7): U20, U20b and U22 to U25 pin that only AudioEndpointBuilder and Audiosrv must
    come back after the restart. Any other recorded dependent that does not reach Running gets one WARN line and the
    run carries on (Install to Verify, no rollback, no exit 1); a core service that does not come back still fails
    the run (Install is rolled back, Repair takes the ruling 10 path, Uninstall reports a problem). U22 to U25 run the
    installer's real Start-SpAudioServices on fake services (Invoke-FakeReal -WontStart).

    U16 to U17b and U22 to U25 run the REAL branch of Install, Repair and Uninstall in this process (Invoke-FakeReal): every system
    call (services, regsvr32, icacls, reg export, shortcut, sound) is a recording stub, and any native
    program, service cmdlet, registry write outside the rehearsal hive or delete outside the sandbox
    is a tripwire that fails the test.

    What this script may touch:
      * READS (never writes) the real HKLM MMDevices and Audio keys: to copy the real Headphones
        endpoint into a rehearsal hive, to read Status, and to prove -WhatIf changed nothing (R6).
      * WRITES only under HKCU:\Software\SoundProtectionRehearsal and %TEMP%\SpRehearsal-<pid>*.
        Both are removed at the end, even on failure.
    It never elevates, never stops or starts a service, never runs regsvr32, never plays sound and
    never creates the desktop shortcut. Every installer run goes through Invoke-Reh (always
    -Rehearsal with an HKCU base) or Invoke-RealReadOnly (only -WhatIf or -Action Status); both
    refuse anything else.

    Run:  powershell -NoProfile -ExecutionPolicy Bypass -File installer\Install.Tests.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Constants (the contract: native\apo\SpShared.h and the plan's Task 3)
# ---------------------------------------------------------------------------------------------
$Installer     = Join-Path $PSScriptRoot 'SoundProtection.Install.ps1'
$PsExe         = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$RehSub        = 'Software\SoundProtectionRehearsal'
$RehBase       = 'HKCU:\' + $RehSub
$RealBase      = 'HKLM:\SOFTWARE'
$DataDir       = Join-Path $env:TEMP ('SpRehearsal-' + $PID)
$InstDirBox    = Join-Path $env:TEMP ('SpRehearsal-' + $PID + '-inst')     # sandbox install folder: a rehearsal puts only the journal and the log here
$UnitBox       = Join-Path $env:TEMP ('SpRehearsal-' + $PID + '-unit')     # sandbox for the U tests
$RenderRel     = 'Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$AudioRel      = 'Microsoft\Windows\CurrentVersion\Audio'
$ClsidLeveler  = '{026BF7A9-ED1A-4C62-ADC6-142802421CBA}'
$ClsidSafety   = '{148D0A3C-621B-40F5-B7FD-13E6CB834BCB}'
$ListLeveler   = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13'
$ListSafety    = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15'
$PropName      = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
$PropExclusive = '{b3f8fa53-0004-438e-9003-51a46e139bfc},3'
$TestKeyName   = 'DisableProtectedAudioDG'
# Golden 1 (SpShared.h): seq 1, enabled, cap -20, margin 12.
$Golden1 = [byte[]](0x53,0x50,0x43,0x46, 0x01,0x00,0x00,0x00, 0x01,0x00,0x00,0x00, 0x01,0x00,0x00,0x00,
                    0x00,0x00,0xA0,0xC1, 0x00,0x00,0x40,0x41, 0x00,0x00,0x00,0x00, 0xEA,0xFA,0xCF,0x1E)

# ---------------------------------------------------------------------------------------------
# Tiny harness
# ---------------------------------------------------------------------------------------------
$script:Pass = 0
$script:Fail = 0
$script:FailedNames = New-Object System.Collections.ArrayList

function Run-Test {
    param([string]$Name, [scriptblock]$Body)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $script:Pass++
        Write-Host ('PASS  {0}  ({1:N1}s)' -f $Name, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    }
    catch {
        $script:Fail++
        [void]$script:FailedNames.Add($Name)
        Write-Host ('FAIL  {0}' -f $Name) -ForegroundColor Red
        $msg = $_.Exception.Message
        foreach ($line in ($msg -split "`r?`n")) { Write-Host ('      ' + $line) -ForegroundColor Red }
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
}

function Format-Seq($Seq) {
    $items = @($Seq)
    return '[' + $items.Count + ']{' + (($items | ForEach-Object { "$_" }) -join ' | ') + '}'
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ("$Actual" -cne "$Expected") { throw "ASSERT FAILED: $Message`n  expected: $Expected`n  actual:   $Actual" }
}

function Assert-SeqEqual($Actual, $Expected, [string]$Message) {
    $a = @($Actual | ForEach-Object { "$_" })
    $e = @($Expected | ForEach-Object { "$_" })
    $same = ($a.Count -eq $e.Count)
    if ($same) { for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -cne $e[$i]) { $same = $false; break } } }
    if (-not $same) { throw "ASSERT FAILED: $Message`n  expected: $(Format-Seq $e)`n  actual:   $(Format-Seq $a)" }
}

function Get-Tail([string]$Text, [int]$Lines) {
    $all = @($Text -split "`r?`n")
    if ($all.Count -le $Lines) { return ($all -join "`n") }
    return (($all | Select-Object -Last $Lines) -join "`n")
}

function Assert-Exit($Run, [int]$Expected, [string]$What) {
    if ($Run.ExitCode -ne $Expected) {
        throw ("ASSERT FAILED: $What exited with $($Run.ExitCode), expected $Expected.`n--- installer output (tail) ---`n" + (Get-Tail $Run.Output 30))
    }
}

# ---------------------------------------------------------------------------------------------
# Registry helpers (read the real HKLM, read/write the HKCU rehearsal hive)
# ---------------------------------------------------------------------------------------------
function Open-RegKey([string]$Path, [bool]$Writable = $false) {
    if ($Path -notmatch '^(HKLM|HKCU):\\(.+)$') { throw "Open-RegKey: unsupported path $Path" }
    $root = [Microsoft.Win32.Registry]::CurrentUser
    if ($Matches[1] -eq 'HKLM') { $root = [Microsoft.Win32.Registry]::LocalMachine }
    return $root.OpenSubKey($Matches[2], $Writable)
}

function Get-RegState([string]$KeyPath, [string]$Name) {
    $k = Open-RegKey $KeyPath
    if ($null -eq $k) { return [pscustomobject]@{ KeyExists = $false; Exists = $false; Kind = $null; Data = $null } }
    try {
        $has = $false
        foreach ($n in $k.GetValueNames()) { if ($n -ieq $Name) { $has = $true; break } }
        if (-not $has) { return [pscustomobject]@{ KeyExists = $true; Exists = $false; Kind = $null; Data = $null } }
        return [pscustomobject]@{
            KeyExists = $true; Exists = $true
            Kind = [string]$k.GetValueKind($Name)
            Data = $k.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
    }
    finally { $k.Close() }
}

function Set-RehValue([string]$KeyPath, [string]$Name, $Data, [string]$Kind) {
    if ($KeyPath -notlike 'HKCU:\Software\SoundProtectionRehearsal*') { throw "Set-RehValue: refusing to write outside the rehearsal hive: $KeyPath" }
    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($KeyPath.Substring(6))
    try { $k.SetValue($Name, $Data, [Microsoft.Win32.RegistryValueKind]$Kind) } finally { $k.Close() }
}

function Remove-RehValue([string]$KeyPath, [string]$Name) {
    if ($KeyPath -notlike 'HKCU:\Software\SoundProtectionRehearsal*') { throw "Remove-RehValue: refusing to write outside the rehearsal hive: $KeyPath" }
    $k = Open-RegKey $KeyPath $true
    if ($null -ne $k) { try { $k.DeleteValue($Name, $false) } finally { $k.Close() } }
}

function Format-RegData($Kind, $Data) {
    $kindText = "$Kind"
    if ($kindText -eq 'Binary' -or $kindText -eq 'None') {
        return 'x:' + ((@($Data) | ForEach-Object { '{0:x2}' -f $_ }) -join '')
    }
    if ($kindText -eq 'MultiString') {
        return 'm:' + @($Data).Count + ':' + (@($Data) -join [string][char]0x1F)
    }
    return 's:' + [string]$Data
}

function Add-KeySnapshot($Key, [string]$Rel, $Lines) {
    [void]$Lines.Add("KEY|$Rel")
    $names = [string[]]$Key.GetValueNames()
    [Array]::Sort($names, [StringComparer]::Ordinal)
    foreach ($n in $names) {
        $kind = $Key.GetValueKind($n)
        $data = $Key.GetValue($n, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        [void]$Lines.Add("VAL|$Rel|$n|$kind|" + (Format-RegData $kind $data))
    }
    $subs = [string[]]$Key.GetSubKeyNames()
    [Array]::Sort($subs, [StringComparer]::Ordinal)
    foreach ($s in $subs) {
        $sk = $Key.OpenSubKey($s)
        if ($null -eq $sk) { [void]$Lines.Add("UNREADABLE|$Rel\$s"); continue }
        try { Add-KeySnapshot $sk "$Rel\$s" $Lines } finally { $sk.Close() }
    }
}

function Get-RegSnapshot([string]$Path) {
    $lines = New-Object System.Collections.ArrayList
    $key = Open-RegKey $Path
    if ($null -eq $key) { [void]$lines.Add("MISSING|$Path") }
    else { try { Add-KeySnapshot $key $Path $lines } finally { $key.Close() } }
    return [string[]]$lines.ToArray()
}

function Assert-SnapshotEqual($Actual, $Expected, [string]$Message) {
    $a = @($Actual); $e = @($Expected)
    $same = ($a.Count -eq $e.Count)
    if ($same) { for ($i = 0; $i -lt $a.Count; $i++) { if ($a[$i] -cne $e[$i]) { $same = $false; break } } }
    if ($same) { return }
    $diff = @(Compare-Object -ReferenceObject $e -DifferenceObject $a | Select-Object -First 8 | ForEach-Object {
        $side = '(now)   '
        if ($_.SideIndicator -eq '<=') { $side = '(before)' }
        $side + ' ' + $_.InputObject
    })
    throw "ASSERT FAILED: $Message`n  snapshot lines before=$($e.Count) now=$($a.Count); first differences:`n    " + ($diff -join "`n    ")
}

function Get-EndpointIds([string]$Base, [string]$Name) {
    $ids = New-Object System.Collections.ArrayList
    $render = Open-RegKey "$Base\$RenderRel"
    if ($null -eq $render) { return @() }
    try {
        foreach ($id in $render.GetSubKeyNames()) {
            $props = $render.OpenSubKey("$id\Properties")
            $fx = $render.OpenSubKey("$id\FxProperties")
            try {
                if ($null -eq $props -or $null -eq $fx) { continue }
                $n = $props.GetValue($PropName)
                if ($n -is [string] -and $n -ieq $Name) {
                    $names = $fx.GetValueNames()
                    if (($names -contains $ListLeveler) -and ($names -contains $ListSafety)) { [void]$ids.Add($id) }
                }
            }
            finally { if ($props) { $props.Close() }; if ($fx) { $fx.Close() } }
        }
    }
    finally { $render.Close() }
    return [string[]]$ids.ToArray()
}

function Copy-KeyTree($Src, $Dst) {
    foreach ($n in $Src.GetValueNames()) {
        $Dst.SetValue($n, $Src.GetValue($n, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames), $Src.GetValueKind($n))
    }
    foreach ($s in $Src.GetSubKeyNames()) {
        $sk = $Src.OpenSubKey($s)
        $dk = $Dst.CreateSubKey($s)
        try { Copy-KeyTree $sk $dk } finally { $sk.Close(); $dk.Close() }
    }
}

# U15 puts a Deny CREATE_SUB_KEY entry for the current user on one key under the rehearsal hive (to behave like the
# real endpoint keys). $On = $true adds it, $false takes it off again. Does nothing if the key is not there.
function Set-CreateSubKeyDeny([string]$Sub, [bool]$On) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Sub, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, [System.Security.AccessControl.RegistryRights]'ReadPermissions, ChangePermissions')
    if ($null -eq $key) { return }
    try {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $rule = New-Object System.Security.AccessControl.RegistryAccessRule($me, [System.Security.AccessControl.RegistryRights]::CreateSubKey, [System.Security.AccessControl.InheritanceFlags]::None, [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Deny)
        $acl = $key.GetAccessControl()
        if ($On) { $acl.AddAccessRule($rule) } else { [void]$acl.RemoveAccessRule($rule) }
        $key.SetAccessControl($acl)
    }
    finally { $key.Close() }
}

function Remove-Rehearsal {
    # a crashed U15 could leave its Deny entry behind, which would make the delete below fail: take it off first
    try { Set-CreateSubKeyDeny "$RehSub\U15" $false } catch { }
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($RehSub, $false) } catch { Write-Host "cleanup warning (registry): $($_.Exception.Message)" -ForegroundColor Yellow }
}

function Remove-TempDir([string]$Path) {
    if (-not $Path.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) { throw "Remove-TempDir: refusing to delete outside TEMP: $Path" }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    # a junction made by a test goes first: rmdir removes only the link, never the folder it points to
    foreach ($l in @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue)) {
        Remove-Junction $l.FullName
    }
    for ($i = 0; $i -lt 5; $i++) {
        try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop; return }
        catch { Start-Sleep -Milliseconds 300 }
    }
    Write-Host "cleanup warning (files): could not delete $Path" -ForegroundColor Yellow
}

# ---- junction helpers (cmd /c mklink /J needs no administrator rights); every path must be inside TEMP
function Invoke-Cmd([string]$CommandLine) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $psi.Arguments = '/c ' + $CommandLine
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    [void]$p.WaitForExit(30000)
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Output = ($so.Result + $se.Result).Trim() }
}

function Test-IsReparse([string]$Path) {
    $i = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    return (($null -ne $i) -and ((($i.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)))
}

function New-Junction([string]$Link, [string]$Target) {
    foreach ($p in $Link, $Target) {
        if (-not $p.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) { throw "New-Junction: refusing a path outside TEMP: $p" }
    }
    $r = Invoke-Cmd ('mklink /J "' + $Link + '" "' + $Target + '"')
    Assert-True (Test-IsReparse $Link) "the junction could not be created: $($r.Output)"
}

function Remove-Junction([string]$Path) {
    if (-not $Path.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) { throw "Remove-Junction: refusing a path outside TEMP: $Path" }
    if (Test-IsReparse $Path) { [void](Invoke-Cmd ('rmdir "' + $Path + '"')) }
}

# Immediate children of a folder, ordinal-sorted (so an exact content check can be written as a list).
function Get-DirListing([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return @('<missing>') }
    $names = [string[]]@(Get-ChildItem -LiteralPath $Path -Force | ForEach-Object { $_.Name })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    return $names
}

# Copy every real render endpoint that has the ,13 list (Headphones, Speakers: the non-matching ones are decoys
# that must stay untouched) into the rehearsal hive, value by value, with name, kind and data kept. Then an
# empty ...\Audio key, as the plan says. DataDir is left absent so WhatIf and first-run behaviour can be seen.
function New-Rehearsal {
    Remove-Rehearsal
    Remove-TempDir $DataDir
    Remove-TempDir $InstDirBox
    $real = Open-RegKey "$RealBase\$RenderRel"
    Assert-True ($null -ne $real) 'the real Render key must exist on this PC (read-only copy source)'
    $dstRender = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$RehSub\$RenderRel")
    try {
        foreach ($id in $real.GetSubKeyNames()) {
            $fx = $real.OpenSubKey("$id\FxProperties")
            $has13 = ($null -ne $fx) -and ($fx.GetValueNames() -contains $ListLeveler)
            if ($fx) { $fx.Close() }
            if (-not $has13) { continue }
            $src = $real.OpenSubKey($id)
            $dst = $dstRender.CreateSubKey($id)
            try { Copy-KeyTree $src $dst } finally { $src.Close(); $dst.Close() }
        }
    }
    finally { $real.Close(); $dstRender.Close() }
    $audio = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$RehSub\$AudioRel")
    $audio.Close()
    $script:HpIds = @(Get-EndpointIds $RehBase 'Headphones')
    Assert-True ($script:HpIds.Count -ge 1) 'the real PC must have an endpoint named Headphones with the ,13 and ,15 lists'
}

function Get-FxPath([string]$Id)    { return "$RehBase\$RenderRel\$Id\FxProperties" }
function Get-PropsPath([string]$Id) { return "$RehBase\$RenderRel\$Id\Properties" }
$AudioPath = "$RehBase\$AudioRel"

# ---------------------------------------------------------------------------------------------
# Running the installer (always in a child powershell.exe, like the controller will)
# ---------------------------------------------------------------------------------------------
function ConvertTo-Arg([string]$Value) {
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $esc = $Value -replace '(\\*)"', '$1$1\"'
    $esc = $esc -replace '(\\+)$', '$1$1'
    return '"' + $esc + '"'
}

function Test-SafeInstallerArgs([string[]]$A) {
    if ($A -contains '-WhatIf') { return $true }
    $i = [Array]::IndexOf($A, '-Action')
    if ($i -ge 0 -and ($i + 1) -lt $A.Count -and $A[$i + 1] -eq 'Status') { return $true }
    $j = [Array]::IndexOf($A, '-RegistryBase')
    if (($A -contains '-Rehearsal') -and $j -ge 0 -and ($j + 1) -lt $A.Count -and $A[$j + 1] -like 'HKCU:\*') { return $true }
    return $false
}

function Start-Installer([string[]]$ArgList, [int]$TimeoutSec = 180) {
    if (-not (Test-SafeInstallerArgs $ArgList)) {
        throw "SAFETY: refusing to run the installer with these arguments (needs -WhatIf, -Action Status, or -Rehearsal with an HKCU -RegistryBase): $($ArgList -join ' ')"
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $PsExe
    $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (ConvertTo-Arg $Installer) + ' ' + (($ArgList | ForEach-Object { ConvertTo-Arg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        throw "the installer did not finish within $TimeoutSec s: $($ArgList -join ' ')"
    }
    $p.WaitForExit()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $so.Result; StdErr = $se.Result; Output = ($so.Result + $se.Result) }
}

# Rehearsal run: HKCU hive, temp data folder, sandbox install folder (so even a bug could not reach Program Files).
# The journal, the log and the backups live in the install folder, so it is a sandbox too.
function Invoke-Reh([string]$Action, [string[]]$Extra = @()) {
    $a = @('-Action', $Action, '-Rehearsal', '-RegistryBase', $RehBase)
    if ($Extra -notcontains '-DataDir') { $a += @('-DataDir', $DataDir) }
    if ($Extra -notcontains '-InstallDir') { $a += @('-InstallDir', $InstDirBox) }
    return (Start-Installer ($a + $Extra))
}

function Invoke-RealReadOnly([string[]]$ArgList) {
    Assert-True (($ArgList -contains '-WhatIf') -or ($ArgList -contains 'Status')) 'Invoke-RealReadOnly is only for -WhatIf or -Action Status'
    return (Start-Installer $ArgList)
}

# The journal lives in the install folder (ruling 11), never in the user-writable data folder.
function Read-JournalObject([string]$Name = 'install-journal.json') {
    $p = Join-Path $InstDirBox $Name
    Assert-True (Test-Path -LiteralPath $p) "journal file missing: $p"
    return ([IO.File]::ReadAllText($p, [Text.Encoding]::UTF8) | ConvertFrom-Json)
}

function Get-Records($Journal, [string]$Name, [string]$KeyLike = '*') {
    # the unary comma keeps a single match an array (StrictMode: a bare object has no .Count)
    return ,@($Journal.records | Where-Object { $_.name -ieq $Name -and $_.key -like $KeyLike })
}

function Count-Ci($List, [string]$Item) {
    return @(@($List) | Where-Object { $_ -ieq $Item }).Count
}

# ---------------------------------------------------------------------------------------------
# Load the installer's functions (no top-level code runs) for the unit tests (U1-U25)
# ---------------------------------------------------------------------------------------------
$script:FunctionsLoaded = $false
$script:FunctionsError = ''
try {
    if (-not (Test-Path -LiteralPath $Installer)) { throw "installer script not found: $Installer" }
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Installer, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ('installer has parse errors: ' + (($parseErrors | ForEach-Object { $_.Message }) -join '; ')) }
    $fnAsts = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)
    $fnText = ($fnAsts | ForEach-Object { $_.Extent.Text }) -join "`n`n"
    . ([scriptblock]::Create($fnText))
    $script:FunctionsLoaded = $true
}
catch { $script:FunctionsError = $_.Exception.Message }

function Assert-Loaded { Assert-True $script:FunctionsLoaded "installer functions could not be loaded: $($script:FunctionsError)" }

# ---------------------------------------------------------------------------------------------
# The REAL branch of Install/Repair/Uninstall, in this process, with every system call replaced by a recording stub (U16, U17)
# ---------------------------------------------------------------------------------------------
# Rehearsal skips services, files, regsvr32, ACLs, the shortcut and Verify, so a child run can never show what the
# real branch does after a failure. Here Invoke-SpInstallOrRepair (or Invoke-SpUninstallAction for Mode Uninstall)
# runs with Rehearsal = false against the HKCU rehearsal hive and a sandbox install/data folder. The stubs below are
# defined in this function's scope; PowerShell resolves commands through the caller chain, so the installer's own
# functions find them first. Tripwires: a native program, a service cmdlet, a registry write outside the rehearsal
# hive or a delete outside the sandbox is recorded as TRIPWIRE and fails the call. State lives in $script:FakeRun
# (never a plain local: the installer's functions have locals such as $dependents that would shadow it).
# By default Start-SpAudioServices is a recording stub. With -WontStart it is the installer's own function, running on fake
# services (Get-Service and Start-Service are stubs; the services named in -WontStart never come back): that is how a test
# sees what the real function's result does to Install and Repair.
function Invoke-FakeReal {
    param(
        [string]$Mode,
        [string]$Root,
        [string]$FailAfter = '',
        [bool]$ServicesStart = $true,       # what the stubbed Start-SpAudioServices reports
        [bool]$ComWrongPath = $false,       # the stubbed regsvr32 registers a path other than the installed DLL
        [scriptblock]$AfterRegister = $null, # runs right after the stubbed regsvr32: lets a test change the world in the middle of the run
        [string[]]$WontStart = $null        # when given (also as @()): the installer's REAL Start-SpAudioServices runs on fake services
                                            # (events 'SVC-START <name>') and the services named here never come back; ServicesStart is then not used
    )
    Assert-Loaded
    Assert-True ($Root.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) 'Invoke-FakeReal: the sandbox must be inside TEMP'
    $fakeServices = $PSBoundParameters.ContainsKey('WontStart')
    $wont = @()
    if ($fakeServices -and ($null -ne $WontStart)) { $wont = @($WontStart) }
    $instDir = Join-Path $Root 'inst'
    $dataDir = Join-Path $Root 'data'
    $srcDll = Join-Path $Root 'src\SoundProtection.Apo.dll'
    [void][IO.Directory]::CreateDirectory($instDir)
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $srcDll))
    if (-not (Test-Path -LiteralPath $srcDll)) { [IO.File]::WriteAllBytes($srcDll, [byte[]](77, 90, 1, 2, 3)) }
    $script:FakeRun = [pscustomobject]@{
        Root          = $Root
        Events        = (New-Object System.Collections.ArrayList)
        RegAtStart    = @()
        ServicesStart = $ServicesStart
        ComWrongPath  = $ComWrongPath
        AfterRegister = $AfterRegister
        WontStart     = $wont
        Svc           = @{}                                              # the fake services (only used when WontStart is given)
        Deps          = [string[]]@('Audiosrv', 'FakeDependentOne')
    }
    if ($fakeServices) { $script:FakeRun.Deps = [string[]]@('Audiosrv', 'midisrv', 'RtkAudioUniversalService') }   # what this PC reports as running dependents

    function Get-SpRunningAudioDependents { [void]$script:FakeRun.Events.Add('LIST-DEPENDENTS'); return $script:FakeRun.Deps }
    function Stop-SpAudioServices { [void]$script:FakeRun.Events.Add('STOP-SERVICES') }
    if ($fakeServices) {
        # Start-SpAudioServices is the installer's REAL function here, running on fake services. Get-Service and Start-Service
        # are stubs that never reach a real service; Stop-Service, Restart-Service and Set-Service stay tripwires (below).
        # A service that has come back stays Running in this run, as a real one would; one that has not is tried again.
        Add-Type -AssemblyName System.ServiceProcess
        function Get-Service {
            param([string]$Name)
            if (-not $script:FakeRun.Svc.ContainsKey($Name)) {
                $o = [pscustomobject]@{ Name = $Name; Status = 'Stopped' }
                Add-Member -InputObject $o -MemberType ScriptMethod -Name Refresh -Value { }
                Add-Member -InputObject $o -MemberType ScriptMethod -Name WaitForStatus -Value { param($Wanted, $Timeout) if ([string]$this.Status -ne 'Running') { throw ('Timed out waiting for ' + $this.Name) } }
                $script:FakeRun.Svc[$Name] = $o
            }
            return $script:FakeRun.Svc[$Name]
        }
        function Start-Service {
            param([string]$Name)
            [void]$script:FakeRun.Events.Add('SVC-START ' + $Name)
            $script:FakeRun.RegAtStart = @(Get-RegSnapshot $RehBase)     # what the registry looked like at the latest start attempt
            if ($script:FakeRun.WontStart -contains $Name) { throw ('Cannot start service ' + $Name + ' on computer ''.''.') }
            $script:FakeRun.Svc[$Name].Status = 'Running'
        }
    }
    else {
        function Start-SpAudioServices {
            param([string[]]$Dependents = @())
            [void]$script:FakeRun.Events.Add('START-SERVICES ' + (@($Dependents) -join ','))
            $script:FakeRun.RegAtStart = @(Get-RegSnapshot $RehBase)     # what the registry looked like when the services came back
            return $script:FakeRun.ServicesStart
        }
        function Start-Service { [void]$script:FakeRun.Events.Add('TRIPWIRE Start-Service'); throw 'SAFETY: Start-Service inside the fake-real harness' }
    }
    function Register-SpDll {
        param([string]$DllPath)
        [void]$script:FakeRun.Events.Add('REGISTER ' + $DllPath)
        $written = $DllPath
        if ($script:FakeRun.ComWrongPath) { $written = 'C:\Elsewhere\other.dll' }
        foreach ($cl in $ClsidLeveler, $ClsidSafety) {
            Set-RehValue "$RehBase\Classes\CLSID\$cl\InprocServer32" '' $written 'String'
            Set-RehValue "$RehBase\Classes\AudioEngine\AudioProcessingObjects\$cl" 'FriendlyName' 'fake' 'String'
        }
        if ($null -ne $script:FakeRun.AfterRegister) { & $script:FakeRun.AfterRegister }
    }
    function Unregister-SpDll { param([string]$DllPath) [void]$script:FakeRun.Events.Add('UNREGISTER ' + $DllPath) }
    function Set-SpDataAcl { param([string]$DataDir) [void]$script:FakeRun.Events.Add('ACL ' + $DataDir) }
    function Export-SpRegBackup {
        param([string[]]$KeyPaths, [string]$Directory)
        [void]$script:FakeRun.Events.Add('BACKUP ' + $Directory)
        [void][IO.Directory]::CreateDirectory($Directory)
        [IO.File]::WriteAllText((Join-Path $Directory 'fake.reg'), 'Windows Registry Editor Version 5.00')
    }
    function New-SpUndoShortcut { param([string]$LinkPath, [string]$InstallDir) [void]$script:FakeRun.Events.Add('SHORTCUT ' + $LinkPath) }
    function Invoke-SpVerify {
        param([string]$DataDir, [int]$TimeoutSec = 6)
        [void]$script:FakeRun.Events.Add('VERIFY')
        return [pscustomobject]@{ Passed = $true; Missing = @() }
    }
    function Invoke-SpNative {
        param([string]$FilePath, [string[]]$ArgumentList = @(), [int]$TimeoutSec = 120)
        [void]$script:FakeRun.Events.Add('TRIPWIRE native program ' + $FilePath)
        throw 'SAFETY: a native program was started inside the fake-real harness'
    }
    function Stop-Service { [void]$script:FakeRun.Events.Add('TRIPWIRE Stop-Service'); throw 'SAFETY: Stop-Service inside the fake-real harness' }
    function Restart-Service { [void]$script:FakeRun.Events.Add('TRIPWIRE Restart-Service'); throw 'SAFETY: Restart-Service inside the fake-real harness' }
    function Set-Service { [void]$script:FakeRun.Events.Add('TRIPWIRE Set-Service'); throw 'SAFETY: Set-Service inside the fake-real harness' }
    function Start-Process { [void]$script:FakeRun.Events.Add('TRIPWIRE Start-Process'); throw 'SAFETY: Start-Process inside the fake-real harness' }
    function Assert-SpWriteAllowed {
        param([string]$KeyPath)
        if ($KeyPath -notlike 'HKCU:\Software\SoundProtectionRehearsal*') {
            [void]$script:FakeRun.Events.Add('TRIPWIRE registry write ' + $KeyPath)
            throw "SAFETY: a registry write outside the rehearsal hive inside the fake-real harness: $KeyPath"
        }
    }
    function Remove-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [switch]$Force, [switch]$Recurse)
        if (-not $LiteralPath.StartsWith($script:FakeRun.Root, [StringComparison]::OrdinalIgnoreCase)) {
            [void]$script:FakeRun.Events.Add('TRIPWIRE Remove-Item ' + $LiteralPath)
            throw "SAFETY: a delete outside the sandbox inside the fake-real harness: $LiteralPath"
        }
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Force:$Force -Recurse:$Recurse
    }

    $cfg = [pscustomobject]@{
        Base           = $RehBase
        Names          = @('Headphones')
        DataDir        = $dataDir
        InstallDir     = $instDir
        DllSource      = $srcDll
        DllSourceGiven = $true
        ScriptSource   = $Installer
        Rehearsal      = $false
        WhatIf         = $false
        FailAfter      = $FailAfter
    }
    $logPath = Join-Path $instDir 'install-log.txt'
    [IO.File]::AppendAllText($logPath, ('=== fake-real {0} ===' -f $Mode) + "`r`n")
    $script:SpContext = [pscustomobject]@{ WhatIf = $false; Rehearsal = $false; LogPath = $logPath; Quiet = $true }
    $oldErr = [Console]::Error
    $sink = New-Object System.IO.StringWriter
    [Console]::SetError($sink)
    $script:SpExit = 99
    try {
        if ($Mode -eq 'Uninstall') { Invoke-SpUninstallAction -Cfg $cfg }
        else { Invoke-SpInstallOrRepair -Cfg $cfg -Mode $Mode }
    }
    finally {
        [Console]::SetError($oldErr)
        $script:SpContext = $null
    }
    $all = [IO.File]::ReadAllText($logPath)
    $mark = $all.LastIndexOf('=== fake-real ')
    $thisRun = $all
    if ($mark -ge 0) { $thisRun = $all.Substring($mark) }
    return [pscustomobject]@{
        Exit       = [int]$script:SpExit
        Events     = @($script:FakeRun.Events)
        Log        = $thisRun
        Err        = $sink.ToString()
        RegAtStart = @($script:FakeRun.RegAtStart)
        Inst       = $instDir
        Data       = $dataDir
    }
}

function Assert-FakeExit($Run, [int]$Expected, [string]$What) {
    $trip = @($Run.Events | Where-Object { $_ -like 'TRIPWIRE*' })
    if ($trip.Count -gt 0) { throw "ASSERT FAILED: $What hit a tripwire: $($trip -join '; ')" }
    if ($Run.Exit -ne $Expected) {
        throw ("ASSERT FAILED: $What exited with $($Run.Exit), expected $Expected.`n--- log of that run (tail) ---`n" + (Get-Tail $Run.Log 25) + "`n--- stderr ---`n" + (Get-Tail $Run.Err 10))
    }
}

# ---------------------------------------------------------------------------------------------
# Synthetic telemetry (layout from SpShared.h) for U3 and R7c
# ---------------------------------------------------------------------------------------------
function New-FakeTelemetry([uint32]$Magic, [uint64]$Heartbeat, [int64]$HeartbeatFileTime, $Slots) {
    $b = New-Object byte[] 2112
    function Put([byte[]]$Buf, [int]$Offset, [byte[]]$Bytes) { [Array]::Copy($Bytes, 0, $Buf, $Offset, $Bytes.Length) }
    Put $b 0  ([BitConverter]::GetBytes($Magic))
    Put $b 4  ([BitConverter]::GetBytes([uint32]1))
    Put $b 8  ([BitConverter]::GetBytes([uint32]32))
    Put $b 12 ([BitConverter]::GetBytes([uint32]64))
    Put $b 16 ([BitConverter]::GetBytes([uint32]4242))
    Put $b 24 ([BitConverter]::GetBytes($Heartbeat))
    Put $b 32 ([BitConverter]::GetBytes($HeartbeatFileTime))
    Put $b 40 ([BitConverter]::GetBytes([uint32]5))
    $i = 0
    foreach ($s in @($Slots)) {
        $o = 64 + 64 * $i
        Put $b ($o + 0)  ([BitConverter]::GetBytes([uint32]$s.Kind))
        Put $b ($o + 4)  ([BitConverter]::GetBytes([uint32]($i + 1)))
        Put $b ($o + 8)  ([BitConverter]::GetBytes([uint32]48000))
        Put $b ($o + 12) ([BitConverter]::GetBytes([uint32]2))
        Put $b ($o + 16) ([BitConverter]::GetBytes([uint32]1))
        Put $b ($o + 24) ([BitConverter]::GetBytes([uint64]$s.Frames))
        $i++
    }
    return ,$b
}

# =============================================================================================
# Preconditions and clean start
# =============================================================================================
Write-Host ''
Write-Host 'Sound Protection installer tests' -ForegroundColor Cyan
Write-Host ('  installer : ' + $Installer)
Write-Host ('  rehearsal : ' + $RehBase + '  data: ' + $DataDir)
Write-Host ''
if (-not [Environment]::Is64BitProcess) { Write-Host 'These tests need 64-bit Windows PowerShell.' -ForegroundColor Red; exit 1 }
if (-not (Test-Path -LiteralPath $Installer)) { Write-Host 'NOTE  the installer script does not exist yet; every test below is expected to fail.' -ForegroundColor Yellow }

try {
    Remove-Rehearsal            # a stale hive from a crashed earlier run
    Remove-TempDir $DataDir
    Remove-TempDir $InstDirBox
    Remove-TempDir $UnitBox
    $script:HpIds = @()
    $script:Snap0 = @()
    $script:Orig = @{}

    # =========================================================================================
    # R1-R3: one sequence (Install, Install again, Uninstall)
    # =========================================================================================
    Run-Test 'R1 Install' {
        New-Rehearsal
        $script:Snap0 = @(Get-RegSnapshot $RehBase)
        $script:Orig = @{}
        foreach ($id in $script:HpIds) {
            $script:Orig[$id] = @{
                L13 = @([string[]](Get-RegState (Get-FxPath $id) $ListLeveler).Data)
                L15 = @([string[]](Get-RegState (Get-FxPath $id) $ListSafety).Data)
            }
        }
        $r = Invoke-Reh 'Install'
        Assert-Exit $r 0 'Install'
        foreach ($id in $script:HpIds) {
            $l13 = Get-RegState (Get-FxPath $id) $ListLeveler
            $l15 = Get-RegState (Get-FxPath $id) $ListSafety
            Assert-Equal $l13.Kind 'MultiString' ",13 on $id keeps its kind"
            Assert-Equal $l15.Kind 'MultiString' ",15 on $id keeps its kind"
            Assert-SeqEqual $l13.Data (@($script:Orig[$id].L13) + $ClsidLeveler) ",13 on $id is the original list with the Leveler CLSID appended at the end"
            Assert-SeqEqual $l15.Data (@($script:Orig[$id].L15) + $ClsidSafety) ",15 on $id is the original list with the Safety Cap CLSID appended at the end"
            $ex = Get-RegState (Get-PropsPath $id) $PropExclusive
            Assert-True ($ex.Exists -and $ex.Kind -eq 'DWord' -and [int]$ex.Data -eq 0) "exclusive mode (,3) on $id is DWORD 0"
        }
        $tk = Get-RegState $AudioPath $TestKeyName
        Assert-True ($tk.Exists -and $tk.Kind -eq 'DWord' -and [int]$tk.Data -eq 1) 'DisableProtectedAudioDG is DWORD 1'
        $j = Read-JournalObject
        Assert-True (@($j.records).Count -ge (3 * $script:HpIds.Count + 1)) 'the journal records every value the run touches'
        $cfgPath = Join-Path $DataDir 'config.bin'
        Assert-True (Test-Path -LiteralPath $cfgPath) 'config.bin was written'
        Assert-SeqEqual ([IO.File]::ReadAllBytes($cfgPath)) $Golden1 'config.bin equals golden 1'
        Assert-True (Test-Path -LiteralPath (Join-Path $DataDir 'telemetry') -PathType Container) 'the telemetry folder exists'
        Assert-True (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-log.txt')) 'install-log.txt exists, in the install folder'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $DataDir 'install-log.txt'))) 'and not in the data folder'
        Assert-SeqEqual @(Get-DirListing $InstDirBox) @('install-journal.json', 'install-log.txt') 'a rehearsal puts only the journal and the log into the install folder (no DLL, no script copy)'
        # Only the expected values changed anywhere in the hive (decoy endpoints and everything else untouched).
        $now = @(Get-RegSnapshot $RehBase)
        $changed = @(Compare-Object -ReferenceObject $script:Snap0 -DifferenceObject $now | ForEach-Object { $_.InputObject })
        $unexpected = @($changed | Where-Object { $_ -notmatch ([regex]::Escape($ListLeveler) + '|' + [regex]::Escape($ListSafety) + '|' + [regex]::Escape($PropExclusive) + '|' + $TestKeyName) })
        Assert-True ($unexpected.Count -eq 0) ("only ,13 ,15 ,3 and the test key may change; unexpected changes:`n    " + ($unexpected -join "`n    "))
    }

    Run-Test 'R2 Idempotent (second Install, journal keeps the first originals)' {
        $journalBefore = [IO.File]::ReadAllText((Join-Path $InstDirBox 'install-journal.json'))
        $r = Invoke-Reh 'Install'
        Assert-Exit $r 0 'second Install'
        foreach ($id in $script:HpIds) {
            $l13 = (Get-RegState (Get-FxPath $id) $ListLeveler).Data
            $l15 = (Get-RegState (Get-FxPath $id) $ListSafety).Data
            Assert-Equal (Count-Ci $l13 $ClsidLeveler) 1 "exactly one Leveler CLSID in ,13 on $id"
            Assert-Equal (Count-Ci $l15 $ClsidSafety) 1 "exactly one Safety Cap CLSID in ,15 on $id"
            Assert-SeqEqual $l13 (@($script:Orig[$id].L13) + $ClsidLeveler) ",13 on $id unchanged by the second run"
            $rec13 = Get-Records (Read-JournalObject) $ListLeveler "*$id*"
            Assert-Equal $rec13.Count 1 "one journal record for ,13 on $id"
            Assert-SeqEqual $rec13[0].data $script:Orig[$id].L13 "the journal still holds the ORIGINAL ,13 list on $id (not the already-modified one)"
        }
        $j = Read-JournalObject
        $tkRecs = Get-Records $j $TestKeyName
        Assert-Equal $tkRecs.Count 1 'one journal record for DisableProtectedAudioDG'
        Assert-True ($tkRecs[0].existed -eq $false) 'the journal still records DisableProtectedAudioDG as NOT existing before'
        $pairs = @($j.records | ForEach-Object { ($_.key + '|' + $_.name).ToLowerInvariant() })
        Assert-Equal @($pairs | Select-Object -Unique).Count $pairs.Count 'no duplicate (key, name) journal records'
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $InstDirBox 'install-journal.json'))) $journalBefore 'a run that adds no records leaves the journal file untouched'
    }

    Run-Test 'R3 Uninstall restores byte-exact' {
        $r = Invoke-Reh 'Uninstall'
        Assert-Exit $r 0 'Uninstall'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $script:Snap0 'every value name, kind and data equals the snapshot taken before R1'
        foreach ($id in $script:HpIds) {
            Assert-True (-not (Get-RegState (Get-PropsPath $id) $PropExclusive).Exists) ",3 is absent again on $id"
        }
        Assert-True (-not (Get-RegState $AudioPath $TestKeyName).Exists) 'DisableProtectedAudioDG is absent again'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-journal.json'))) 'the live journal is gone'
        Assert-True (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-journal.done.json')) 'the journal was renamed to install-journal.done.json (in the install folder)'
        Assert-True (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-log.txt')) 'the log is kept (in the install folder)'
    }

    # =========================================================================================
    # R4 Repair
    # =========================================================================================
    Run-Test 'R4 Repair puts the CLSID back at the end and keeps the true originals' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        $orig = @{}
        foreach ($id in $script:HpIds) { $orig[$id] = @{ L13 = @((Get-RegState (Get-FxPath $id) $ListLeveler).Data); L15 = @((Get-RegState (Get-FxPath $id) $ListSafety).Data) } }
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        $journalAfterInstall = [IO.File]::ReadAllText((Join-Path $InstDirBox 'install-journal.json'))
        foreach ($id in $script:HpIds) {
            # a driver update: ,15 loses our entry, ,13 gets an unrelated effect and loses ours
            $l15 = @((Get-RegState (Get-FxPath $id) $ListSafety).Data | Where-Object { $_ -ine $ClsidSafety })
            Set-RehValue (Get-FxPath $id) $ListSafety ([string[]]$l15) 'MultiString'
            Set-RehValue (Get-FxPath $id) $ListLeveler ([string[]](@($orig[$id].L13) + '{AAAAAAAA-0000-0000-0000-000000000001}')) 'MultiString'
        }
        $r = Invoke-Reh 'Repair'
        Assert-Exit $r 0 'Repair'
        foreach ($id in $script:HpIds) {
            $l13 = (Get-RegState (Get-FxPath $id) $ListLeveler).Data
            $l15 = (Get-RegState (Get-FxPath $id) $ListSafety).Data
            Assert-Equal @($l15)[-1] $ClsidSafety ",15 on $id ends with the Safety Cap CLSID again"
            Assert-Equal (Count-Ci $l15 $ClsidSafety) 1 "one Safety Cap CLSID on $id"
            Assert-SeqEqual $l15 (@($orig[$id].L15) + $ClsidSafety) ",15 on $id is exactly original + ours"
            Assert-Equal @($l13)[-1] $ClsidLeveler ",13 on $id ends with the Leveler CLSID again"
            Assert-Equal (Count-Ci $l13 $ClsidLeveler) 1 "one Leveler CLSID on $id"
        }
        Assert-True ((Get-RegState $AudioPath $TestKeyName).Exists) 'the test key is still set'
        Assert-Equal ([IO.File]::ReadAllText((Join-Path $InstDirBox 'install-journal.json'))) $journalAfterInstall 'Repair keeps the journal records (nothing new to add)'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall after Repair'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'Uninstall after Repair still restores the FIRST originals, byte for byte'
    }

    # =========================================================================================
    # R5 Interrupted
    # =========================================================================================
    Run-Test 'R5 Interrupted install rolls back (fault after the list appends)' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        $r = Invoke-Reh 'Install' @('-FailAfter', 'Lists')
        Assert-Exit $r 1 'Install with -FailAfter Lists'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'the keys equal the snapshot after the rollback'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-journal.json'))) 'the rolled-back run leaves no live journal'
        # and the system is clean enough for a normal install afterwards
        Assert-Exit (Invoke-Reh 'Install') 0 'Install after a rolled-back one'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall after that'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'and it still ends byte-exact'
    }

    Run-Test 'R5b Interrupted install rolls back (fault after every registry write)' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        $r = Invoke-Reh 'Install' @('-FailAfter', 'Exclusive')
        Assert-Exit $r 1 'Install with -FailAfter Exclusive'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'lists, exclusive mode and test key are all restored'
    }

    # =========================================================================================
    # R6 WhatIf on the REAL system (read-only snapshot before and after; no elevation)
    # =========================================================================================
    function Get-RealSnapshot {
        $lines = New-Object System.Collections.ArrayList
        foreach ($id in @(Get-EndpointIds $RealBase 'Headphones')) {
            foreach ($sub in 'FxProperties', 'Properties') {
                foreach ($l in (Get-RegSnapshot "$RealBase\$RenderRel\$id\$sub")) { [void]$lines.Add($l) }
            }
        }
        foreach ($l in (Get-RegSnapshot "$RealBase\$AudioRel")) { [void]$lines.Add($l) }
        foreach ($clsid in $ClsidLeveler, $ClsidSafety) {
            foreach ($l in (Get-RegSnapshot "$RealBase\Classes\CLSID\$clsid")) { [void]$lines.Add($l) }
            foreach ($l in (Get-RegSnapshot "$RealBase\Classes\AudioEngine\AudioProcessingObjects\$clsid")) { [void]$lines.Add($l) }
        }
        # files: names only (the mapped telemetry file legitimately changes size and time while audio plays)
        $realData = Join-Path $env:ProgramData 'SoundProtection'
        $realInst = Join-Path $env:ProgramFiles 'SoundProtection'
        foreach ($d in $realData, $realInst) {
            if (Test-Path -LiteralPath $d) {
                [void]$lines.Add("DIR|$d")
                foreach ($f in (Get-ChildItem -LiteralPath $d -Recurse -Force)) { [void]$lines.Add("FILE|$($f.FullName)") }
            }
            else { [void]$lines.Add("NODIR|$d") }
        }
        foreach ($desk in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory')) {
            [void]$lines.Add('LNK|' + $desk + '|' + (Test-Path -LiteralPath (Join-Path $desk 'Sound Protection - Undo.lnk')))
        }
        return [string[]]$lines.ToArray()
    }

    Run-Test 'R6 WhatIf on the real system changes nothing and names all three changes' {
        $before = @(Get-RealSnapshot)
        Assert-True (@($before | Where-Object { $_ -like 'VAL|*' }).Count -gt 50) 'the real Headphones snapshot has values (sanity)'
        $r = Invoke-RealReadOnly @('-Action', 'Install', '-WhatIf')
        $after = @(Get-RealSnapshot)
        Assert-Exit $r 0 'Install -WhatIf (no elevation)'
        Assert-SnapshotEqual $after $before 'the real Headphones keys, the Audio key, the COM keys, the folders and the shortcut are unchanged'
        $o = $r.Output
        Assert-True ($o -match [regex]::Escape($TestKeyName)) 'the output names the test key change'
        Assert-True (($o -match [regex]::Escape($ClsidLeveler)) -and ($o -match [regex]::Escape($ListLeveler))) 'the output names the Leveler list append (,13)'
        Assert-True (($o -match [regex]::Escape($ClsidSafety)) -and ($o -match [regex]::Escape($ListSafety))) 'the output names the Safety Cap list append (,15)'
        Assert-True ($o -match [regex]::Escape($PropExclusive)) 'the output names the exclusive-mode change'
        Assert-True ($o -notmatch 'Administrator rights are needed') '-WhatIf never relaunches itself elevated (no UAC)'
        # fix round 1: the plan the reader approves shows where the journal and the backups go, and the owner change
        $planInst = Join-Path $env:ProgramFiles 'SoundProtection'
        $planData = Join-Path $env:ProgramData 'SoundProtection'
        Assert-True ($o -match [regex]::Escape('write the journal ' + (Join-Path $planInst 'install-journal.json'))) 'the output puts the journal in the install folder'
        Assert-True ($o -match [regex]::Escape('into ' + (Join-Path $planInst 'backup') + '\')) 'and the .reg backups below it'
        Assert-True ($o -notmatch [regex]::Escape((Join-Path $planData 'install-journal.json'))) 'nothing is journalled in the data folder'
        Assert-True ($o -match ('(?i)Administrators the owner of ' + [regex]::Escape($planData))) 'the output names the owner change on the data folder'
    }

    Run-Test 'R6b WhatIf in the rehearsal hive writes nothing at all (registry, journal, log, folders)' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        $r = Invoke-Reh 'Install' @('-WhatIf')
        Assert-Exit $r 0 'Install -WhatIf'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'the rehearsal hive is unchanged'
        Assert-True (-not (Test-Path -LiteralPath $DataDir)) 'WhatIf did not create the data folder (no config.bin, no telemetry folder)'
        Assert-True (-not (Test-Path -LiteralPath $InstDirBox)) 'WhatIf did not create the install folder (no journal, no log, no backup)'
        Assert-True ($r.Output -match [regex]::Escape($TestKeyName)) 'it still prints the plan'
        foreach ($act in 'Uninstall', 'Repair') {
            $r2 = Invoke-Reh $act @('-WhatIf')
            Assert-Exit $r2 0 "$act -WhatIf"
        }
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'Uninstall and Repair -WhatIf changed nothing either'
        Assert-True (-not (Test-Path -LiteralPath $DataDir)) 'and created no data folder'
        Assert-True (-not (Test-Path -LiteralPath $InstDirBox)) 'and no install folder'
    }

    # =========================================================================================
    # R7 Status
    # =========================================================================================
    Run-Test 'R7 Status returns parseable JSON with the Headphones endpoint' {
        New-Rehearsal
        $r0 = Invoke-Reh 'Status'
        Assert-Exit $r0 0 'Status'
        $j0 = $r0.StdOut | ConvertFrom-Json
        foreach ($p in 'installed', 'endpoints', 'testKey', 'heartbeatAgeMs') {
            Assert-True ($j0.PSObject.Properties.Name -contains $p) "Status JSON has '$p'"
        }
        $hp0 = @($j0.endpoints | Where-Object { $_.name -eq 'Headphones' })
        Assert-Equal $hp0.Count $script:HpIds.Count 'one entry per Headphones endpoint'
        foreach ($e in $hp0) {
            foreach ($p in 'name', 'id', 'levelerListed', 'safetyCapListed', 'effectsDisabled', 'exclusiveAllowed') {
                Assert-True ($e.PSObject.Properties.Name -contains $p) "endpoint entry has '$p'"
            }
            Assert-True (($e.levelerListed -eq $false) -and ($e.safetyCapListed -eq $false)) 'before Install: neither CLSID is listed'
            Assert-True ($e.exclusiveAllowed -eq $true) 'before Install: exclusive mode is allowed (value absent)'
            Assert-True ($e.effectsDisabled -eq $false) 'effects are not disabled'
        }
        Assert-True ($j0.testKey -eq $false) 'before Install: test key off'
        Assert-True ($null -eq $j0.heartbeatAgeMs) 'no telemetry: heartbeatAgeMs is null'
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        $j1 = (Invoke-Reh 'Status').StdOut | ConvertFrom-Json
        foreach ($e in @($j1.endpoints)) {
            Assert-True (($e.levelerListed -eq $true) -and ($e.safetyCapListed -eq $true)) 'after Install: both CLSIDs are listed'
            Assert-True ($e.exclusiveAllowed -eq $false) 'after Install: exclusive mode is off'
        }
        Assert-True ($j1.testKey -eq $true) 'after Install: test key on'
        # effects-disabled flag (FxProperties or Properties = 1)
        Set-RehValue (Get-FxPath $script:HpIds[0]) '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5' 1 'DWord'
        $j2 = (Invoke-Reh 'Status').StdOut | ConvertFrom-Json
        $e2 = @($j2.endpoints | Where-Object { $_.id -eq $script:HpIds[0] })[0]
        Assert-True ($e2.effectsDisabled -eq $true) 'effectsDisabled is true when {1da5d803-...},5 = 1 in FxProperties'
    }

    Run-Test 'R7b Status on the real system (read-only) is parseable and lists Headphones' {
        $r = Invoke-RealReadOnly @('-Action', 'Status')
        Assert-Exit $r 0 'Status (real, read-only)'
        $j = $r.StdOut | ConvertFrom-Json
        foreach ($p in 'installed', 'endpoints', 'testKey', 'heartbeatAgeMs') { Assert-True ($j.PSObject.Properties.Name -contains $p) "Status JSON has '$p'" }
        Assert-True (@($j.endpoints | Where-Object { $_.name -eq 'Headphones' }).Count -ge 1) 'the real Headphones endpoint is listed'
    }

    Run-Test 'R7c Status reports the heartbeat age from telemetry\apo.bin' {
        New-Rehearsal
        $tele = Join-Path $DataDir 'telemetry'
        [void][IO.Directory]::CreateDirectory($tele)
        $ft = [DateTime]::UtcNow.ToFileTimeUtc() - 15000000L          # 1.5 s ago
        [IO.File]::WriteAllBytes((Join-Path $tele 'apo.bin'), (New-FakeTelemetry 0x4D545053 100 $ft @(@{ Kind = 1; Frames = 480 })))
        $j = (Invoke-Reh 'Status').StdOut | ConvertFrom-Json
        Assert-True ($null -ne $j.heartbeatAgeMs) 'heartbeatAgeMs is a number'
        Assert-True (([double]$j.heartbeatAgeMs -ge 1400) -and ([double]$j.heartbeatAgeMs -lt 60000)) "heartbeatAgeMs ($($j.heartbeatAgeMs)) is about 1500 ms plus the process start time"
        [IO.File]::WriteAllBytes((Join-Path $tele 'apo.bin'), (New-FakeTelemetry 0x00000000 100 $ft @()))
        $j2 = (Invoke-Reh 'Status').StdOut | ConvertFrom-Json
        Assert-True ($null -eq $j2.heartbeatAgeMs) 'a file with the wrong magic gives null'
    }

    Run-Test 'R7d Status "installed" needs both files and both COM keys' {
        New-Rehearsal
        $box = Join-Path $UnitBox 'status-inst'
        [void][IO.Directory]::CreateDirectory($box)
        [IO.File]::WriteAllText((Join-Path $box 'SoundProtection.Apo.dll'), 'x')
        [IO.File]::WriteAllText((Join-Path $box 'SoundProtection.Install.ps1'), 'x')
        foreach ($c in $ClsidLeveler, $ClsidSafety) { Set-RehValue "$RehBase\Classes\CLSID\$c\InprocServer32" '' (Join-Path $box 'SoundProtection.Apo.dll') 'String' }
        $j = (Invoke-Reh 'Status' @('-InstallDir', $box)).StdOut | ConvertFrom-Json
        Assert-True ($j.installed -eq $true) 'files + both COM keys present: installed'
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\Classes\CLSID\$ClsidSafety")
        $j2 = (Invoke-Reh 'Status' @('-InstallDir', $box)).StdOut | ConvertFrom-Json
        Assert-True ($j2.installed -eq $false) 'one COM key missing: not installed'
        Remove-Item -LiteralPath (Join-Path $box 'SoundProtection.Apo.dll') -Force
        Set-RehValue "$RehBase\Classes\CLSID\$ClsidSafety\InprocServer32" '' 'x' 'String'
        $j3 = (Invoke-Reh 'Status' @('-InstallDir', $box)).StdOut | ConvertFrom-Json
        Assert-True ($j3.installed -eq $false) 'DLL missing: not installed'
    }

    # =========================================================================================
    # R8-R12: the Review Focus corners
    # =========================================================================================
    Run-Test 'R8a Values that existed before come back with the same kind and data (DWORDs)' {
        New-Rehearsal
        foreach ($id in $script:HpIds) { Set-RehValue (Get-PropsPath $id) $PropExclusive 1 'DWord' }
        Set-RehValue $AudioPath $TestKeyName 0 'DWord'
        $snap = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        Assert-Equal ([int](Get-RegState $AudioPath $TestKeyName).Data) 1 'test key now 1'
        foreach ($id in $script:HpIds) { Assert-Equal ([int](Get-RegState (Get-PropsPath $id) $PropExclusive).Data) 0 ",3 now 0 on $id" }
        $j = Read-JournalObject
        $tk = Get-Records $j $TestKeyName
        Assert-True ($tk.Count -eq 1 -and $tk[0].existed -eq $true -and $tk[0].kind -eq 'DWord' -and [int]$tk[0].data -eq 0) 'the journal holds the original test key (DWord 0)'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'byte-exact, including the pre-existing DWORDs'
    }

    Run-Test 'R8b Values of other kinds come back as their original kind (ExpandString, Binary)' {
        New-Rehearsal
        foreach ($id in $script:HpIds) { Set-RehValue (Get-PropsPath $id) $PropExclusive '%SystemRoot%\sp-test' 'ExpandString' }
        Set-RehValue $AudioPath $TestKeyName ([byte[]]@(1, 2, 255)) 'Binary'
        $snap = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        $tk = Get-RegState $AudioPath $TestKeyName
        Assert-True ($tk.Kind -eq 'DWord' -and [int]$tk.Data -eq 1) 'the test key is now a DWORD 1'
        Assert-Exit (Invoke-Reh 'Install') 0 'second Install'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'byte-exact: unexpanded ExpandString and the Binary bytes are back'
    }

    Run-Test 'R9 A missing Audio key is created for the run and removed again by Uninstall' {
        New-Rehearsal
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\$AudioRel")
        $snap = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        Assert-Equal ([int](Get-RegState $AudioPath $TestKeyName).Data) 1 'test key set (the key was created)'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'the Audio key is gone again, as before'
    }

    Run-Test 'R10 Uninstall without a journal removes our CLSIDs and leaves the test key (with a warning)' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        Remove-Item -LiteralPath (Join-Path $InstDirBox 'install-journal.json') -Force
        $r = Invoke-Reh 'Uninstall'
        Assert-Exit $r 0 'Uninstall without a journal'
        foreach ($id in $script:HpIds) {
            Assert-SeqEqual (Get-RegState (Get-FxPath $id) $ListLeveler).Data $script:Orig[$id].L13 ",13 on $id is the original list again"
            Assert-SeqEqual (Get-RegState (Get-FxPath $id) $ListSafety).Data $script:Orig[$id].L15 ",15 on $id is the original list again"
        }
        Assert-Equal ([int](Get-RegState $AudioPath $TestKeyName).Data) 1 'DisableProtectedAudioDG is left as it is'
        Assert-True ($r.Output -match '(?i)WARN') 'a warning is printed'
        Assert-True ($r.Output -match [regex]::Escape($TestKeyName)) 'the warning names the test key'
    }

    Run-Test 'R11 Repair finds the endpoint by name after its GUID changed' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        $old = $script:HpIds[0]
        $new = '{11111111-2222-3333-4444-555555555555}'
        $realEp = Open-RegKey "$RealBase\$RenderRel\$old"
        Assert-True ($null -ne $realEp) 'the real source endpoint is readable'
        $dst = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$RehSub\$RenderRel\$new")
        try { Copy-KeyTree $realEp $dst } finally { $realEp.Close(); $dst.Close() }
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\$RenderRel\$old")
        $newOrig13 = @((Get-RegState (Get-FxPath $new) $ListLeveler).Data)
        $r = Invoke-Reh 'Repair'
        Assert-Exit $r 0 'Repair'
        Assert-Equal @((Get-RegState (Get-FxPath $new) $ListLeveler).Data)[-1] $ClsidLeveler 'the new endpoint got the Leveler CLSID'
        Assert-Equal @((Get-RegState (Get-FxPath $new) $ListSafety).Data)[-1] $ClsidSafety 'the new endpoint got the Safety Cap CLSID'
        $rec = Get-Records (Read-JournalObject) $ListLeveler "*$new*"
        Assert-True ($rec.Count -eq 1 -and $rec[0].existed -eq $true) 'the journal gained a record for the new endpoint'
        Assert-SeqEqual $rec[0].data $newOrig13 'recorded with its original list'
        $r2 = Invoke-Reh 'Uninstall'
        Assert-Exit $r2 0 'Uninstall (the old endpoint key no longer exists)'
        Assert-SeqEqual (Get-RegState (Get-FxPath $new) $ListLeveler).Data $newOrig13 'the new endpoint is restored'
        Assert-True (-not (Get-RegState (Get-PropsPath $new) $PropExclusive).Exists) ',3 is gone from the new endpoint'
        Assert-True (-not (Get-RegState $AudioPath $TestKeyName).Exists) 'the test key is gone'
        Assert-True ($null -eq (Open-RegKey "$RehBase\$RenderRel\$old")) 'Uninstall did not re-create the vanished endpoint key'
    }

    Run-Test 'R12 Guards: a rehearsal never touches HKLM; -FailAfter is rehearsal-only; bad -Action is refused' {
        $r1 = Invoke-RealReadOnly @('-Action', 'Install', '-Rehearsal', '-RegistryBase', 'HKLM:\SOFTWARE', '-WhatIf')
        Assert-Exit $r1 1 '-Rehearsal with an HKLM base'
        $r2 = Invoke-RealReadOnly @('-Action', 'Install', '-FailAfter', 'Lists', '-WhatIf')
        Assert-Exit $r2 1 '-FailAfter without -Rehearsal'
        $r3 = Invoke-RealReadOnly @('-Action', 'Bogus', '-WhatIf')
        Assert-True ($r3.ExitCode -ne 0) 'an unknown -Action fails'
    }

    Run-Test 'R13 A tampered journal cannot make Uninstall write to a key the installer never changes' {
        New-Rehearsal
        $decoy = "$RehBase\Decoy"
        Set-RehValue $decoy 'Keep' 'original' 'String'
        $snap = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        # add a record that points at the decoy key (outside Render\{id}\FxProperties|Properties and the Audio key)
        $jp = Join-Path $InstDirBox 'install-journal.json'
        $good = [IO.File]::ReadAllText($jp, [Text.Encoding]::UTF8)
        $j = $good | ConvertFrom-Json
        $evil = [pscustomobject]@{ key = $decoy; name = 'Evil'; keyExisted = $true; existed = $true; kind = 'String'; data = 'pwned' }
        $j.records = @(@($j.records) + $evil)
        [IO.File]::WriteAllText($jp, (ConvertTo-Json -InputObject $j -Depth 8), (New-Object Text.UTF8Encoding($false)))
        $r = Invoke-Reh 'Uninstall'
        Assert-Exit $r 1 'Uninstall with a tampered journal'
        Assert-True (-not (Get-RegState $decoy 'Evil').Exists) 'nothing was written to the decoy key'
        Assert-Equal (Get-RegState $decoy 'Keep').Data 'original' 'the decoy value is untouched'
        Assert-True ($r.Output -match '(?i)refused') "the output says the record was refused`n$(Get-Tail $r.Output 12)"
        Assert-True (Test-Path -LiteralPath $jp) 'the journal is kept (not renamed) because a record could not be honoured'
        # the genuine records were still restored, and a clean journal then finishes the job
        [IO.File]::WriteAllText($jp, $good, (New-Object Text.UTF8Encoding($false)))
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall with the genuine journal'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'byte-exact after the genuine journal is used'
    }

    Run-Test 'R14 -DllSource: the default is native\out\apo\ next to installer\; an explicit one is used as given' {
        # Windows PowerShell 5.1 leaves $PSScriptRoot empty inside param() defaults: the default must not turn into C:\native\...
        $expectedDefault = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\native\out\apo\SoundProtection.Apo.dll'))
        $r = Invoke-RealReadOnly @('-Action', 'Install', '-WhatIf')
        Assert-Exit $r 0 'Install -WhatIf'
        Assert-True ($r.Output.IndexOf($expectedDefault, [StringComparison]::OrdinalIgnoreCase) -ge 0) "the default -DllSource is $expectedDefault`n$(Get-Tail $r.Output 25)"
        $fake = Join-Path $UnitBox 'dll source\Fake.Apo.dll'
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $fake))
        [IO.File]::WriteAllText($fake, 'x')
        $r2 = Invoke-RealReadOnly @('-Action', 'Install', '-WhatIf', '-DllSource', $fake)
        Assert-Exit $r2 0 'Install -WhatIf -DllSource <existing file>'
        Assert-True ($r2.Output.IndexOf($fake, [StringComparison]::OrdinalIgnoreCase) -ge 0) 'an explicit -DllSource is the file that would be copied'
        Assert-True ($r2.Output -notmatch 'DLL was not found') 'and no "not found" warning is printed for it'
    }

    # =========================================================================================
    # Fix round 1. Ruling 10: a failed Repair undoes only its own writes (R15, U16, U16b, U16c; U24 for a core service).
    # Ruling 11: the elevated code no longer trusts the user-writable data folder (R16-R18, U17-U19).
    # =========================================================================================
    Run-Test 'R15 A failed Repair puts back only its own writes, leaves the journal alone and exits 1' {
        New-Rehearsal
        $pristine = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        $jp = Join-Path $InstDirBox 'install-journal.json'
        $journalBefore = [IO.File]::ReadAllText($jp)
        foreach ($id in $script:HpIds) {
            # a driver update takes our Safety Cap entry out of ,15: this is what Repair is for
            $l15 = @((Get-RegState (Get-FxPath $id) $ListSafety).Data | Where-Object { $_ -ine $ClsidSafety })
            Set-RehValue (Get-FxPath $id) $ListSafety ([string[]]$l15) 'MultiString'
        }
        $beforeRepair = @(Get-RegSnapshot $RehBase)
        $r = Invoke-Reh 'Repair' @('-FailAfter', 'Lists')
        Assert-Exit $r 1 'Repair with -FailAfter Lists'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $beforeRepair 'the registry is exactly as it was just before the failed Repair (its own ,15 write is undone, nothing else is)'
        foreach ($id in $script:HpIds) {
            Assert-Equal (Count-Ci (Get-RegState (Get-FxPath $id) $ListLeveler).Data $ClsidLeveler) 1 "the Leveler CLSID is still in ,13 on $id (a failed Repair does not strip a working install)"
            Assert-Equal (Count-Ci (Get-RegState (Get-FxPath $id) $ListSafety).Data $ClsidSafety) 0 ",15 on $id is back to what Repair found (without the Safety Cap CLSID)"
            Assert-Equal ([int](Get-RegState (Get-PropsPath $id) $PropExclusive).Data) 0 "exclusive mode stays off on $id"
        }
        Assert-Equal ([int](Get-RegState $AudioPath $TestKeyName).Data) 1 'the test key is still set'
        Assert-True (@(Compare-Object -ReferenceObject $pristine -DifferenceObject @(Get-RegSnapshot $RehBase)).Count -gt 0) 'and it is not the pre-install state: no Uninstall rollback ran'
        Assert-Equal ([IO.File]::ReadAllText($jp)) $journalBefore 'the install journal is byte-identical'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-journal.done.json'))) 'the journal was not renamed to .done'
        Assert-True ($r.Output -match 'Repair failed: the setup was left as it was before the repair.*Injected fault') "the log says it, followed by the error`n$(Get-Tail $r.Output 12)"
        Assert-True ($r.Output -notmatch 'Rolling back from the journal') 'no rollback from the journal was started'
        # the setup is still a working one: Repair works afterwards, and Uninstall still ends at the first originals
        Assert-Exit (Invoke-Reh 'Repair') 0 'Repair again, without the fault'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine 'the untouched journal still restores the first originals, byte for byte'
    }

    Run-Test 'R16 The journal and the log live in the install folder, never in the data folder' {
        New-Rehearsal
        $r0 = Invoke-Reh 'Uninstall'
        Assert-Exit $r0 0 'Uninstall with nothing installed'
        Assert-True (-not (Test-Path -LiteralPath $InstDirBox)) 'an Uninstall of nothing creates no install folder'
        Assert-True (-not (Test-Path -LiteralPath $DataDir)) 'and no data folder'
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        Assert-SeqEqual @(Get-DirListing $DataDir) @('config.bin', 'telemetry') 'after Install the data folder holds only config.bin and telemetry\'
        Assert-SeqEqual @(Get-DirListing $InstDirBox) @('install-journal.json', 'install-log.txt') 'the journal and the log are in the install folder'
        Assert-Exit (Invoke-Reh 'Repair') 0 'Repair'
        Assert-SeqEqual @(Get-DirListing $DataDir) @('config.bin', 'telemetry') 'after Repair the data folder is the same'
        Assert-SeqEqual @(Get-DirListing $InstDirBox) @('install-journal.json', 'install-log.txt') 'and so is the install folder'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall'
        Assert-SeqEqual @(Get-DirListing $DataDir) @('config.bin', 'telemetry') 'after Uninstall the data folder holds nothing new (config.bin is the user''s)'
        Assert-SeqEqual @(Get-DirListing $InstDirBox) @('install-journal.done.json', 'install-log.txt') 'the finished journal and the log stay in the install folder'
        $log = [IO.File]::ReadAllText((Join-Path $InstDirBox 'install-log.txt'))
        Assert-True (($log -match 'Install') -and ($log -match 'Repair') -and ($log -match 'Uninstall')) 'the one log in the install folder holds all three runs'
    }

    Run-Test 'R16b Nothing is read back from the data folder: a journal planted there changes nothing' {
        New-Rehearsal
        $pristine = @(Get-RegSnapshot $RehBase)
        Assert-Exit (Invoke-Reh 'Install') 0 'Install'
        $installed = @(Get-RegSnapshot $RehBase)
        $genuinePath = Join-Path $InstDirBox 'install-journal.json'
        $genuine = [IO.File]::ReadAllText($genuinePath)
        # a record for a key the allow-list accepts (an endpoint's FxProperties) with a value the installer never wrote
        $evil = [pscustomobject]@{
            version = 1; created = '2026-01-01T00:00:00.0000000Z'; registryBase = $RehBase
            records = @([pscustomobject]@{ key = (Get-FxPath $script:HpIds[0]); name = $ListLeveler; keyExisted = $true; existed = $true; kind = 'MultiString'; data = @('{AAAAAAAA-0000-0000-0000-00000000E111}') })
        }
        $json = ConvertTo-Json -InputObject $evil -Depth 8
        $planted = Join-Path $DataDir 'install-journal.json'
        $plantedDone = Join-Path $DataDir 'install-journal.done.json'
        foreach ($p in $planted, $plantedDone) { [IO.File]::WriteAllText($p, $json, (New-Object Text.UTF8Encoding($false))) }
        Assert-Exit (Invoke-Reh 'Repair') 0 'Repair with a planted journal in the data folder'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $installed 'Repair applied nothing from the planted journal'
        Assert-Equal ([IO.File]::ReadAllText($genuinePath)) $genuine 'and the real journal is unchanged'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall with a planted journal in the data folder'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine 'Uninstall restored from the install-folder journal: the planted record was never applied'
        Assert-True ((Test-Path -LiteralPath $planted) -and (Test-Path -LiteralPath $plantedDone)) 'the planted files were neither renamed nor deleted'
        Assert-Equal ([IO.File]::ReadAllText($planted)) $json 'and not changed'
        Assert-Equal ([IO.File]::ReadAllText($plantedDone)) $json 'same for the .done copy'
    }

    Run-Test 'R17 A data folder that is a junction is refused: exit 1, nothing changed (Install, Repair and the dry run)' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        $target = Join-Path $UnitBox 'jdata-target'
        $link = Join-Path $UnitBox 'jdata-link'
        [void][IO.Directory]::CreateDirectory($target)
        New-Junction $link $target
        try {
            foreach ($variant in @(@('Install', @()), @('Repair', @()), @('Install', @('-WhatIf')))) {
                $act = $variant[0]
                $label = $act + ' ' + ($variant[1] -join ' ')
                $r = Invoke-Reh $act (@('-DataDir', $link) + @($variant[1]))
                Assert-Exit $r 1 "$label with a junction as the data folder"
                Assert-True ($r.Output -match '(?i)junction') "the output says why ($label)`n$(Get-Tail $r.Output 10)"
                Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap "$label changed no registry value"
                Assert-SeqEqual @(Get-DirListing $target) @() "$label wrote nothing through the junction"
                Assert-True (Test-IsReparse $link) "$label left the junction as it was"
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-journal.json'))) "$label wrote no journal"
            }
        }
        finally { Remove-Junction $link }
    }

    Run-Test 'R17b A telemetry folder that is a junction is refused too' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        $dd = Join-Path $UnitBox 'jtele-data'
        $target = Join-Path $UnitBox 'jtele-target'
        $link = Join-Path $dd 'telemetry'
        [void][IO.Directory]::CreateDirectory($dd)
        [void][IO.Directory]::CreateDirectory($target)
        New-Junction $link $target
        try {
            $r = Invoke-Reh 'Install' @('-DataDir', $dd)
            Assert-Exit $r 1 'Install with a junction as the telemetry folder'
            Assert-True (($r.Output -match '(?i)junction') -and ($r.Output -match 'telemetry')) "the output names the telemetry folder and the reason`n$(Get-Tail $r.Output 10)"
            Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'no registry value was changed'
            Assert-SeqEqual @(Get-DirListing $target) @() 'nothing was written through the junction'
            Assert-SeqEqual @(Get-DirListing $dd) @('telemetry') 'no config.bin was written into the data folder either'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $InstDirBox 'install-journal.json'))) 'no journal was written'
            $r2 = Invoke-Reh 'Repair' @('-DataDir', $dd)
            Assert-Exit $r2 1 'Repair with a junction as the telemetry folder'
        }
        finally { Remove-Junction $link }
    }

    Run-Test 'R18 A rehearsal refuses the real install folder (it would put a rehearsal journal and log into it)' {
        New-Rehearsal
        $snap = @(Get-RegSnapshot $RehBase)
        # The real %ProgramFiles%\SoundProtection (Windows resets ProgramFiles for every new process, so a child cannot be
        # given a fake one). Every run against it here is a dry run (-WhatIf): it writes nothing even if the guard were
        # missing. U21 pins the same decision on the guard function, with a fake ProgramFiles, for the real-run case.
        $realInst = Join-Path $env:ProgramFiles 'SoundProtection'
        $realBefore = @(Get-DirListing $realInst)
        $base = @('-Action', 'Install', '-Rehearsal', '-WhatIf', '-RegistryBase', $RehBase, '-DataDir', $DataDir)
        $r1 = Start-Installer ($base + @('-InstallDir', $realInst))
        Assert-Exit $r1 1 'rehearsal dry run with -InstallDir = the real install folder'
        Assert-True ($r1.Output -match '(?i)real install folder') "the output says why`n$(Get-Tail $r1.Output 8)"
        $r2 = Start-Installer $base
        Assert-Exit $r2 1 'rehearsal dry run without -InstallDir (the default is the real install folder)'
        $r3 = Start-Installer ($base + @('-InstallDir', ($realInst + '\')))
        Assert-Exit $r3 1 'a trailing backslash does not get around it'
        foreach ($act in 'Repair', 'Uninstall') {
            $ra = Start-Installer @('-Action', $act, '-Rehearsal', '-WhatIf', '-RegistryBase', $RehBase, '-DataDir', $DataDir, '-InstallDir', $realInst)
            Assert-Exit $ra 1 "rehearsal dry run of $act with the real install folder"
        }
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $snap 'no registry value was changed'
        Assert-SeqEqual @(Get-DirListing $realInst) $realBefore 'the real install folder is exactly as it was'
        Assert-True (-not (Test-Path -LiteralPath $DataDir)) 'and no data folder was created'
        # a status report only reads, so it is not refused
        $st = Start-Installer @('-Action', 'Status', '-Rehearsal', '-RegistryBase', $RehBase)
        Assert-Exit $st 0 'Status in a rehearsal, with the default (real) install folder'
        # a sandbox install folder works as before
        $ok = Start-Installer @('-Action', 'Install', '-Rehearsal', '-RegistryBase', $RehBase, '-DataDir', $DataDir, '-InstallDir', $InstDirBox)
        Assert-Exit $ok 0 'the same rehearsal with a sandbox install folder works'
        Assert-Exit (Invoke-Reh 'Uninstall') 0 'Uninstall'
    }

    # =========================================================================================
    # U1-U25: helpers that a rehearsal cannot reach, against sandboxes under %TEMP%
    # =========================================================================================
    [void][IO.Directory]::CreateDirectory($UnitBox)

    Run-Test 'U1 Default config bytes are golden 1 and carry a valid FNV-1a checksum' {
        Assert-Loaded
        $b = Get-SpDefaultConfigBytes
        Assert-SeqEqual $b $Golden1 'the installer writes golden 1'
        $h = [uint64]2166136261
        $mask32 = [uint64]4294967295      # not 0xFFFFFFFF: PowerShell reads that literal as the Int32 -1
        $prime = [uint64]16777619
        for ($i = 0; $i -lt 28; $i++) { $h = (($h -bxor [uint64]$b[$i]) * $prime) -band $mask32 }
        Assert-Equal ([BitConverter]::ToUInt32($b, 28)) $h 'bytes 28..31 are the FNV-1a of bytes 0..27'
    }

    Run-Test 'U2 Verify WAV: 2 s, 48 kHz, 16-bit stereo, 1 kHz, peak 328, 20 ms fades' {
        Assert-Loaded
        $w = New-SpVerifyWavBytes
        $enc = [Text.Encoding]::ASCII
        Assert-Equal $enc.GetString($w, 0, 4) 'RIFF' 'RIFF'
        Assert-Equal $enc.GetString($w, 8, 4) 'WAVE' 'WAVE'
        Assert-Equal $enc.GetString($w, 12, 4) 'fmt ' 'fmt chunk'
        Assert-Equal ([BitConverter]::ToUInt32($w, 16)) 16 'fmt size'
        Assert-Equal ([BitConverter]::ToUInt16($w, 20)) 1 'PCM'
        Assert-Equal ([BitConverter]::ToUInt16($w, 22)) 2 'stereo'
        Assert-Equal ([BitConverter]::ToUInt32($w, 24)) 48000 '48 kHz'
        Assert-Equal ([BitConverter]::ToUInt32($w, 28)) 192000 'byte rate'
        Assert-Equal ([BitConverter]::ToUInt16($w, 32)) 4 'block align'
        Assert-Equal ([BitConverter]::ToUInt16($w, 34)) 16 '16-bit'
        Assert-Equal $enc.GetString($w, 36, 4) 'data' 'data chunk'
        Assert-Equal ([BitConverter]::ToUInt32($w, 40)) 384000 '2 s of stereo 16-bit'
        Assert-Equal $w.Length 384044 'total size'
        Assert-Equal ([BitConverter]::ToUInt32($w, 4)) 384036 'RIFF size'
        $frames = 96000
        $left = New-Object 'int[]' $frames
        $same = $true
        for ($i = 0; $i -lt $frames; $i++) {
            $l = [BitConverter]::ToInt16($w, 44 + 4 * $i); $rr = [BitConverter]::ToInt16($w, 46 + 4 * $i)
            $left[$i] = $l
            if ($l -ne $rr) { $same = $false }
        }
        Assert-True $same 'left and right are identical'
        $max = ($left | Measure-Object -Maximum).Maximum; $min = ($left | Measure-Object -Minimum).Minimum
        Assert-Equal $max 328 'positive peak is 328 (-40 dBFS)'
        Assert-Equal $min -328 'negative peak is -328'
        Assert-Equal $left[0] 0 'starts at zero'
        $headPeak = 0; for ($i = 0; $i -lt 960; $i++) { $headPeak = [Math]::Max($headPeak, [Math]::Abs($left[$i])) }
        $tailPeak = 0; for ($i = $frames - 960; $i -lt $frames; $i++) { $tailPeak = [Math]::Max($tailPeak, [Math]::Abs($left[$i])) }
        Assert-True ($headPeak -lt 328) "the first 20 ms fade in (peak $headPeak)"
        Assert-True ($tailPeak -lt 328) "the last 20 ms fade out (peak $tailPeak)"
        Assert-True ([Math]::Abs($left[$frames - 1]) -le 2) 'ends at (nearly) zero'
        $cross = 0; for ($i = 1; $i -lt $frames; $i++) { if ($left[$i - 1] -le 0 -and $left[$i] -gt 0) { $cross++ } }
        Assert-True (($cross -ge 1995) -and ($cross -le 2005)) "about 2000 rising zero crossings = 1 kHz for 2 s (got $cross)"
    }

    Run-Test 'U3 Verify reads telemetry and names what is missing' {
        Assert-Loaded
        $slots = @(@{ Kind = 1; Frames = 960 }, @{ Kind = 2; Frames = 960 })
        $b1 = New-FakeTelemetry 0x4D545053 100 133000000000000000 $slots
        $b2 = New-FakeTelemetry 0x4D545053 160 133000000100000000 $slots
        $t1 = ConvertFrom-SpTelemetryBytes -Bytes $b1 -Length $b1.Length
        $t2 = ConvertFrom-SpTelemetryBytes -Bytes $b2 -Length $b2.Length
        Assert-True ($t1.MagicOk -eq $true) 'magic recognised'
        Assert-Equal $t1.Heartbeat 100 'heartbeat'
        Assert-Equal $t1.HeartbeatFileTime 133000000000000000 'heartbeat FILETIME'
        Assert-Equal @($t1.Slots | Where-Object { $_.Kind -eq 1 -and $_.Frames -gt 0 }).Count 1 'a Leveler slot with frames'
        Assert-Equal @($t1.Slots | Where-Object { $_.Kind -eq 2 -and $_.Frames -gt 0 }).Count 1 'a Safety Cap slot with frames'
        Assert-Equal @(Get-SpVerifyMissing -Samples @($t1, $t2)).Count 0 'running: magic, rising heartbeat, both slots'
        $same = @(Get-SpVerifyMissing -Samples @($t1, $t1))
        Assert-True ($same.Count -ge 1 -and ($same -join ' ') -match '(?i)heartbeat') "flat heartbeat is reported ($($same -join '; '))"
        $none = @(Get-SpVerifyMissing -Samples @())
        Assert-True ($none.Count -ge 1 -and ($none -join ' ') -match '(?i)telemetry') "no telemetry file is reported ($($none -join '; '))"
        $badBytes = New-FakeTelemetry 0x12345678 100 1 $slots
        $bad = ConvertFrom-SpTelemetryBytes -Bytes $badBytes -Length $badBytes.Length
        Assert-True (@(Get-SpVerifyMissing -Samples @($bad, $bad)).Count -ge 1) 'a wrong magic is reported'
        $noCapBytes = New-FakeTelemetry 0x4D545053 170 133000000200000000 @(@{ Kind = 1; Frames = 960 })
        $noCap = ConvertFrom-SpTelemetryBytes -Bytes $noCapBytes -Length $noCapBytes.Length
        $m1 = @(Get-SpVerifyMissing -Samples @($t1, $noCap))
        Assert-True ($m1.Count -ge 1 -and ($m1 -join ' ') -match '(?i)safety') "a missing Safety Cap slot is reported ($($m1 -join '; '))"
        $noLevBytes = New-FakeTelemetry 0x4D545053 170 133000000200000000 @(@{ Kind = 2; Frames = 960 })
        $noLev = ConvertFrom-SpTelemetryBytes -Bytes $noLevBytes -Length $noLevBytes.Length
        $m2 = @(Get-SpVerifyMissing -Samples @($t1, $noLev))
        Assert-True ($m2.Count -ge 1 -and ($m2 -join ' ') -match '(?i)leveler') "a missing Leveler slot is reported ($($m2 -join '; '))"
        $zeroBytes = New-FakeTelemetry 0x4D545053 170 133000000200000000 @(@{ Kind = 1; Frames = 0 }, @{ Kind = 2; Frames = 0 })
        $zero = ConvertFrom-SpTelemetryBytes -Bytes $zeroBytes -Length $zeroBytes.Length
        Assert-True (@(Get-SpVerifyMissing -Samples @($t1, $zero)).Count -ge 2) 'slots with zero frames count as missing'
        Assert-True ($null -eq (ConvertFrom-SpTelemetryBytes -Bytes (New-Object byte[] 10) -Length 10)) 'a truncated file is not telemetry'
    }

    Run-Test 'U4 Journal data survives JSON for every registry kind' {
        Assert-Loaded
        $cases = @(
            @{ Kind = 'String';       Data = 'hello' },
            @{ Kind = 'String';       Data = '' },
            @{ Kind = 'ExpandString'; Data = '%SystemRoot%\system32\x.dll' },
            @{ Kind = 'MultiString';  Data = [string[]]@() },
            @{ Kind = 'MultiString';  Data = [string[]]@('{DA2C9ECE-7418-4906-B4FA-0A00B3EB88AA}') },
            @{ Kind = 'MultiString';  Data = [string[]]@('{8F3540DF-18B0-4943-B916-D4A79801587D}', '{A29EB043-6CE2-4ee2-B38C-F58719E0D88F}', 'x y') },
            @{ Kind = 'DWord';        Data = [int]0 },
            @{ Kind = 'DWord';        Data = [int]-1 },
            @{ Kind = 'DWord';        Data = [int]2147483647 },
            @{ Kind = 'QWord';        Data = [int64]4611686018427387904 },
            @{ Kind = 'QWord';        Data = [int64]-1 },
            @{ Kind = 'Binary';       Data = [byte[]]@() },
            @{ Kind = 'Binary';       Data = [byte[]]@(0, 1, 127, 128, 255) },
            @{ Kind = 'None';         Data = [byte[]]@(9, 8) }
        )
        $scratch = "$RehBase\U4"
        foreach ($c in $cases) {
            $enc = ConvertTo-SpJournalData -Kind $c.Kind -Data $c.Data
            $json = ConvertTo-Json -InputObject ([pscustomobject]@{ kind = $c.Kind; data = $enc }) -Depth 6
            $back = ConvertFrom-Json $json
            $dec = ConvertFrom-SpJournalData -Kind $back.kind -Data $back.data
            $label = "$($c.Kind) $(Format-RegData $c.Kind $c.Data)"
            Assert-Equal (Format-RegData $c.Kind $dec) (Format-RegData $c.Kind $c.Data) "round trip through JSON: $label"
            Set-RehValue $scratch 'v' $dec $c.Kind
            $st = Get-RegState $scratch 'v'
            Assert-Equal $st.Kind $c.Kind "written back with its kind: $label"
            Assert-Equal (Format-RegData $st.Kind $st.Data) (Format-RegData $c.Kind $c.Data) "read back identical: $label"
        }
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\U4", $false)
    }

    Run-Test 'U5 reg export writes a .reg backup of a key (the same code path the real run uses)' {
        Assert-Loaded
        New-Rehearsal
        $dir = Join-Path $UnitBox 'backup\20260101-000000'
        $key = "$RehBase\$RenderRel\$($script:HpIds[0])"
        Export-SpRegBackup -KeyPaths @($key, $AudioPath) -Directory $dir
        $files = @(Get-ChildItem -LiteralPath $dir -Filter '*.reg')
        Assert-Equal $files.Count 2 'one .reg file per key'
        $epFile = @($files | Where-Object { $_.Name -like "*$($script:HpIds[0])*" })
        Assert-Equal $epFile.Count 1 'the endpoint file is named after its id'
        $text = [IO.File]::ReadAllText($epFile[0].FullName)
        Assert-True ($text.StartsWith('Windows Registry Editor Version 5.00')) 'a registry export header'
        Assert-True ($text -match [regex]::Escape($ListLeveler.Replace('{', '').Replace('}', '')) -or $text -match 'd04e05a6') 'it contains the endpoint values'
        $threw = $false
        try { Export-SpRegBackup -KeyPaths @("$RehBase\No\Such\Key") -Directory $dir } catch { $threw = $true }
        Assert-True $threw 'a missing key is an error, not a silent empty backup'
    }

    Run-Test 'U6 Data-folder ACLs: exactly the grants from the plan (sandbox folder, not a real one)' {
        Assert-Loaded
        # Set-SpDataGrants is the two grant commands of Set-SpDataAcl; the owner change in front of them needs an
        # elevated token (a filtered one gets error 1307), so that command is checked in U18 with a recording stub.
        $d = Join-Path $UnitBox 'acl data'
        [void][IO.Directory]::CreateDirectory((Join-Path $d 'telemetry'))
        [IO.File]::WriteAllBytes((Join-Path $d 'config.bin'), $Golden1)
        Set-SpDataGrants -DataDir $d
        $sec = (New-Object System.IO.DirectoryInfo $d).GetAccessControl()
        Assert-True $sec.AreAccessRulesProtected 'inheritance from the parent is removed'
        $sid = [System.Security.Principal.SecurityIdentifier]
        $rules = @($sec.GetAccessRules($true, $false, $sid))
        function Rights($rs, [string]$S) { $r = @($rs | Where-Object { $_.IdentityReference.Value -eq $S -and $_.AccessControlType -eq 'Allow' }); Assert-True ($r.Count -ge 1) "an Allow rule for $S"; return [int]$r[0].FileSystemRights }
        $FullCtl = [int][System.Security.AccessControl.FileSystemRights]::FullControl
        $Modify = [int][System.Security.AccessControl.FileSystemRights]::Modify
        $RX = [int][System.Security.AccessControl.FileSystemRights]::ReadAndExecute
        $WriteData = [int][System.Security.AccessControl.FileSystemRights]::WriteData
        $WriteDac = [int][System.Security.AccessControl.FileSystemRights]::ChangePermissions
        Assert-Equal (Rights $rules 'S-1-5-18' -band $FullCtl) $FullCtl 'SYSTEM: full control'
        Assert-Equal (Rights $rules 'S-1-5-32-544' -band $FullCtl) $FullCtl 'Administrators: full control'
        $u = Rights $rules 'S-1-5-32-545'
        Assert-True ((($u -band $Modify) -eq $Modify) -and (($u -band $WriteDac) -eq 0)) 'Users: modify, but not change permissions'
        foreach ($s in 'S-1-5-19', 'S-1-15-2-1', 'S-1-15-2-2') {
            $x = Rights $rules $s
            Assert-True ((($x -band $RX) -eq $RX) -and (($x -band $WriteData) -eq 0)) "$s : read and execute only on the data folder"
        }
        foreach ($r in ($rules | Where-Object { $_.IdentityReference.Value -in @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545', 'S-1-5-19', 'S-1-15-2-1', 'S-1-15-2-2') })) {
            Assert-True ($r.InheritanceFlags -eq 'ContainerInherit, ObjectInherit') "the grant for $($r.IdentityReference.Value) is (OI)(CI)"
        }
        $tsec = (New-Object System.IO.DirectoryInfo (Join-Path $d 'telemetry')).GetAccessControl()
        $trules = @($tsec.GetAccessRules($true, $false, $sid))
        foreach ($s in 'S-1-5-19', 'S-1-15-2-1', 'S-1-15-2-2') {
            $x = [int](@($trules | Where-Object { $_.IdentityReference.Value -eq $s -and -not $_.IsInherited })[0].FileSystemRights)
            Assert-True (($x -band $Modify) -eq $Modify) "$s : modify on telemetry\"
        }
        $crules = @(((New-Object System.IO.FileInfo (Join-Path $d 'config.bin')).GetAccessControl()).GetAccessRules($true, $true, $sid))
        $lsRights = [int](@($crules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-19' })[0].FileSystemRights)
        Assert-True (($lsRights -band $RX) -eq $RX) 'an existing config.bin inherits the LocalService read grant'
        Set-SpDataGrants -DataDir $d      # idempotent
    }

    Run-Test 'U7 Files: copy the DLL and the script into the install folder, remove them again (the journal, backups and log stay, and the log says so)' {
        Assert-Loaded
        $script:Logs = New-Object System.Collections.ArrayList
        function Write-SpLog { param([string]$Message, [string]$Level = 'INFO') [void]$script:Logs.Add('[' + $Level + '] ' + $Message) }
        $src = Join-Path $UnitBox 'src'
        [void][IO.Directory]::CreateDirectory($src)
        $dll = Join-Path $src 'SoundProtection.Apo.dll'
        [IO.File]::WriteAllBytes($dll, [byte[]](77, 90, 1, 2, 3))
        $inst = Join-Path $UnitBox 'install dir\SoundProtection'
        Copy-SpInstallFiles -DllSource $dll -ScriptSource $Installer -InstallDir $inst
        $iDll = Join-Path $inst 'SoundProtection.Apo.dll'
        $iScr = Join-Path $inst 'SoundProtection.Install.ps1'
        Assert-True ((Test-Path -LiteralPath $iDll) -and (Test-Path -LiteralPath $iScr)) 'both files are in the install folder'
        Assert-Equal (Get-FileHash -LiteralPath $iDll).Hash (Get-FileHash -LiteralPath $dll).Hash 'the DLL is an exact copy'
        Assert-Equal (Get-FileHash -LiteralPath $iScr).Hash (Get-FileHash -LiteralPath $Installer).Hash 'the script is an exact copy'
        Copy-SpInstallFiles -DllSource $iDll -ScriptSource $iScr -InstallDir $inst        # source == destination (Repair from the installed copy)
        Assert-True (Test-Path -LiteralPath $iDll) 'copying onto itself is harmless'
        [IO.File]::WriteAllText((Join-Path $inst 'unrelated.txt'), 'keep me')
        $removed = Remove-SpInstallFiles -InstallDir $inst
        Assert-True (-not (Test-Path -LiteralPath $iDll) -and -not (Test-Path -LiteralPath $iScr)) 'our two files are deleted'
        Assert-True ($removed -eq $false -and (Test-Path -LiteralPath (Join-Path $inst 'unrelated.txt'))) 'a folder with foreign files is left alone (never a recursive delete)'
        $warn = @($script:Logs | Where-Object { $_ -match '^\[WARN\] .*other files' })
        Assert-Equal $warn.Count 1 "a foreign file is reported as a warning: $($script:Logs -join ' / ')"
        Assert-True ($warn[0] -match 'unrelated\.txt') 'and it is named'
        Remove-Item -LiteralPath (Join-Path $inst 'unrelated.txt') -Force

        # Ruling 11: the journal, the registry backups and the log live in this folder. Uninstall leaves the folder
        # when they remain, and the log says so (it is an expected state, so not a warning).
        $script:Logs.Clear()
        Copy-SpInstallFiles -DllSource $dll -ScriptSource $Installer -InstallDir $inst
        [IO.File]::WriteAllText((Join-Path $inst 'install-journal.done.json'), '{}')
        [IO.File]::WriteAllText((Join-Path $inst 'install-log.txt'), 'log')
        [void][IO.Directory]::CreateDirectory((Join-Path $inst 'backup\20260101-000000'))
        [IO.File]::WriteAllText((Join-Path $inst 'backup\20260101-000000\x.reg'), 'reg')
        $kept = Remove-SpInstallFiles -InstallDir $inst
        Assert-True ($kept -eq $false) 'the folder is not reported as removed'
        Assert-SeqEqual @(Get-DirListing $inst) @('backup', 'install-journal.done.json', 'install-log.txt') 'our two files are gone, the journal, the backups and the log are kept'
        Assert-True (Test-Path -LiteralPath (Join-Path $inst 'backup\20260101-000000\x.reg')) 'the backup files are untouched'
        $note = @($script:Logs | Where-Object { $_ -match '^\[INFO\] .*kept on purpose' })
        Assert-Equal $note.Count 1 "the log says the folder was kept on purpose: $($script:Logs -join ' / ')"
        Assert-True ($note[0] -match 'journal' -and $note[0] -match 'backup' -and $note[0] -match 'log') 'and names what is in it'
        Assert-Equal @($script:Logs | Where-Object { $_ -match '^\[(WARN|ERROR)\]' }).Count 0 'no warning or error for our own files'
        foreach ($n in 'install-journal.done.json', 'install-log.txt') { Remove-Item -LiteralPath (Join-Path $inst $n) -Force }
        Remove-Item -LiteralPath (Join-Path $inst 'backup') -Recurse -Force

        Copy-SpInstallFiles -DllSource $dll -ScriptSource $Installer -InstallDir $inst
        $removed2 = Remove-SpInstallFiles -InstallDir $inst
        Assert-True ($removed2 -eq $true -and -not (Test-Path -LiteralPath $inst)) 'an otherwise empty install folder is removed too'
        Assert-True ((Remove-SpInstallFiles -InstallDir $inst) -eq $true) 'removing what is already gone is fine'
    }

    Run-Test 'U8 The Undo shortcut runs the installed script with -Action Uninstall' {
        Assert-Loaded
        $lnk = Join-Path $UnitBox 'Sound Protection - Undo.lnk'
        New-SpUndoShortcut -LinkPath $lnk -InstallDir 'C:\Program Files\SoundProtection'
        Assert-True (Test-Path -LiteralPath $lnk) 'the .lnk exists'
        $ws = New-Object -ComObject WScript.Shell
        $s = $ws.CreateShortcut($lnk)
        Assert-True ($s.TargetPath -like '*\powershell.exe') "target is powershell.exe ($($s.TargetPath))"
        Assert-Equal $s.Arguments '-NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\SoundProtection\SoundProtection.Install.ps1" -Action Uninstall' 'arguments, exactly as the plan says'
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($s)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ws)
    }

    Run-Test 'U9 External commands: exit code and output are captured (GUI or console)' {
        Assert-Loaded
        $cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
        $a = Invoke-SpNative -FilePath $cmd -ArgumentList @('/c', 'exit 7')
        Assert-Equal $a.ExitCode 7 'exit code 7 is returned'
        $b = Invoke-SpNative -FilePath $cmd -ArgumentList @('/c', 'echo hello-from-cmd')
        Assert-True ($b.ExitCode -eq 0 -and $b.Output -match 'hello-from-cmd') 'output is captured'
    }

    Run-Test 'U10 Self-elevation arguments survive powershell.exe -File intact' {
        Assert-Loaded
        Assert-Equal (ConvertTo-SpQuotedArg -Value 'abc') 'abc' 'plain word'
        Assert-Equal (ConvertTo-SpQuotedArg -Value '') '""' 'empty string'
        Assert-Equal (ConvertTo-SpQuotedArg -Value 'C:\Program Files\x y.ps1') '"C:\Program Files\x y.ps1"' 'path with spaces'
        Assert-Equal (ConvertTo-SpQuotedArg -Value 'C:\a b\') '"C:\a b\\"' 'trailing backslash is doubled'
        $echo = Join-Path $UnitBox 'echo-args.ps1'
        [IO.File]::WriteAllText($echo, 'param([string]$Action, [string]$DataDir, [string]$Endpoints, [switch]$Rehearsal, [switch]$WhatIf)' + "`r`n" + '[pscustomobject]@{ Action = $Action; DataDir = $DataDir; Endpoints = $Endpoints; Rehearsal = $Rehearsal.IsPresent; WhatIf = $WhatIf.IsPresent } | ConvertTo-Json -Compress')
        $bound = [ordered]@{
            Action = 'Repair'
            DataDir = 'C:\Dir With Space\and "quotes" & more\'
            Endpoints = [string[]]@('Headphones', 'Speakers')
            Rehearsal = [switch]$true
            WhatIf = [switch]$false
        }
        $argText = ConvertTo-SpArgumentString -Bound $bound
        Assert-True ($argText -notmatch 'WhatIf') 'an absent switch is not forwarded'
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $PsExe
        $psi.Arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (ConvertTo-Arg $echo) + ' ' + $argText
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
        $o = $out | ConvertFrom-Json
        Assert-Equal $o.Action 'Repair' 'Action arrives'
        Assert-Equal $o.DataDir $bound.DataDir 'a path with spaces, quotes, an ampersand and a trailing backslash arrives unchanged'
        Assert-Equal $o.Endpoints 'Headphones,Speakers' 'the endpoint list arrives as one comma-joined string (the installer splits it)'
        Assert-True ($o.Rehearsal -eq $true -and $o.WhatIf -eq $false) 'switches arrive'
    }

    Run-Test 'U11 COM registration check compares both InprocServer32 defaults with the installed path' {
        Assert-Loaded
        $dll = 'C:\Program Files\SoundProtection\SoundProtection.Apo.dll'
        foreach ($c in $ClsidLeveler, $ClsidSafety) { Set-RehValue "$RehBase\Classes\CLSID\$c\InprocServer32" '' $dll.ToUpperInvariant() 'String' }
        Assert-Equal @(Test-SpComRegistration -Base $RehBase -DllPath $dll).Count 0 'both present and equal (case-insensitive): no problems'
        Set-RehValue "$RehBase\Classes\CLSID\$ClsidSafety\InprocServer32" '' 'C:\Elsewhere\other.dll' 'String'
        $p1 = @(Test-SpComRegistration -Base $RehBase -DllPath $dll)
        Assert-True ($p1.Count -eq 1 -and $p1[0] -match [regex]::Escape($ClsidSafety)) "a wrong path is reported for the Safety Cap ($($p1 -join '; '))"
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\Classes\CLSID\$ClsidLeveler")
        $p2 = @(Test-SpComRegistration -Base $RehBase -DllPath $dll)
        Assert-Equal $p2.Count 2 'a missing key and a wrong path: two problems'
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\Classes", $false)
    }

    Run-Test 'U12 Running audio dependents are listed (read-only service query)' {
        Assert-Loaded
        $names = @(Get-SpRunningAudioDependents)
        Assert-True ($names -notcontains 'AudioEndpointBuilder') 'AudioEndpointBuilder itself is not a dependent'
        if ((Get-Service -Name Audiosrv).Status -eq 'Running') { Assert-True ($names -contains 'Audiosrv') 'Windows Audio (Audiosrv) is a running dependent' }
        foreach ($n in $names) { Assert-True ((Get-Service -Name $n).Status -eq 'Running') "$n is running" }
    }

    Run-Test 'U13 Only keys the installer itself records are accepted from a journal' {
        Assert-Loaded
        $b = 'HKLM:\SOFTWARE'
        $rr = $b + '\' + $RenderRel
        Assert-True (Test-SpJournalKeyAllowed -Base $b -Key "$rr\{abc}\FxProperties") 'an endpoint FxProperties key'
        Assert-True (Test-SpJournalKeyAllowed -Base $b -Key "$rr\{abc}\Properties") 'an endpoint Properties key'
        Assert-True (Test-SpJournalKeyAllowed -Base $b -Key ($b + '\' + $AudioRel)) 'the Audio key'
        Assert-True (Test-SpJournalKeyAllowed -Base $b -Key ($b + '\' + $AudioRel.ToUpperInvariant())) 'the comparison ignores case'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base $b -Key 'HKLM:\SYSTEM\CurrentControlSet\Services\Evil')) 'an unrelated key is refused'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base $b -Key "$rr\{abc}\FxProperties\Sub")) 'a deeper key is refused'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base $b -Key "$rr\{abc}")) 'the endpoint key itself is refused'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base $b -Key "$rr\{abc}\Other")) 'another sub-key is refused'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base $b -Key ('HKCU:\SOFTWARE\' + $AudioRel))) 'the same path in another hive is refused'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base $b -Key '')) 'an empty key is refused'
        Assert-True (-not (Test-SpJournalKeyAllowed -Base '' -Key "$rr\{abc}\FxProperties")) 'an empty base refuses everything'
    }

    Run-Test 'U14 Resolve-SpDllSource: an explicit -DllSource wins; without one, the built DLL, else the installed one' {
        Assert-Loaded
        $inst = Join-Path $UnitBox 'resolve\inst'
        $built = Join-Path $UnitBox 'resolve\built\SoundProtection.Apo.dll'
        [void][IO.Directory]::CreateDirectory($inst)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $built))
        $installedDll = Join-Path $inst 'SoundProtection.Apo.dll'
        [IO.File]::WriteAllText($installedDll, 'installed')
        $cfgDefault = [pscustomobject]@{ DllSource = $built; DllSourceGiven = $false; InstallDir = $inst }
        $cfgExplicit = [pscustomobject]@{ DllSource = $built; DllSourceGiven = $true; InstallDir = $inst }
        Assert-Equal (Resolve-SpDllSource -Cfg $cfgDefault) $installedDll 'no built DLL and none given: the installed one is used (Repair from the installed copy)'
        Assert-True ($null -eq (Resolve-SpDllSource -Cfg $cfgExplicit)) 'an explicit -DllSource that is missing is an error, not a silent fallback'
        [IO.File]::WriteAllText($built, 'built')
        Assert-Equal (Resolve-SpDllSource -Cfg $cfgDefault) $built 'a built DLL wins over the installed one'
        Assert-Equal (Resolve-SpDllSource -Cfg $cfgExplicit) $built 'an explicit existing -DllSource is used'
    }

    Run-Test 'U15 Registry writes ask only for SET_VALUE: a key that refuses CREATE_SUB_KEY (like the real endpoint keys) can still be changed' {
        Assert-Loaded
        # The real MMDevices endpoint keys give Administrators SET_VALUE but not CREATE_SUB_KEY. RegistryKey.OpenSubKey(name, $true)
        # and CreateSubKey both ask for KEY_WRITE, which includes CREATE_SUB_KEY, so on those keys both are refused and a real
        # Install would fail at its first endpoint write. Emulated here by a Deny CREATE_SUB_KEY entry on one rehearsal key.
        $sub = "$RehSub\U15"
        $path = "$RehBase\U15"
        $cu = [Microsoft.Win32.Registry]::CurrentUser
        $k0 = $cu.CreateSubKey($sub)
        try {
            $k0.SetValue('Old', 7, [Microsoft.Win32.RegistryValueKind]::DWord)
            $k0.SetValue('List', [string[]]@('{a}', '{b}'), [Microsoft.Win32.RegistryValueKind]::MultiString)
        }
        finally { $k0.Close() }
        Set-CreateSubKeyDeny $sub $true
        try {
            $naive = $false
            try { $n = $cu.OpenSubKey($sub, $true); if ($null -ne $n) { $n.Close() } } catch { $naive = $true }
            Assert-True $naive 'precondition: OpenSubKey(name, $true) is refused on a key without CREATE_SUB_KEY'
            $naive2 = $false
            try { $n2 = $cu.CreateSubKey($sub); if ($null -ne $n2) { $n2.Close() } } catch { $naive2 = $true }
            Assert-True $naive2 'precondition: CreateSubKey on that existing key is refused too'

            Set-SpValue -KeyPath $path -Name 'Old' -Data 5 -Kind 'DWord' -CreateKey $false
            $st = Get-SpValueState -KeyPath $path -Name 'Old'
            Assert-True ($st.Exists -and ($st.Kind -eq 'DWord') -and ([int]$st.Data -eq 5)) 'an existing DWORD is changed (CreateKey false)'

            Set-SpValue -KeyPath $path -Name 'Fresh' -Data 1 -Kind 'DWord' -CreateKey $true
            $st = Get-SpValueState -KeyPath $path -Name 'Fresh'
            Assert-True ($st.Exists -and ($st.Kind -eq 'DWord') -and ([int]$st.Data -eq 1)) 'a new DWORD is added to the existing key without creating the key (CreateKey true)'

            Set-SpValue -KeyPath $path -Name 'List' -Data ([string[]]@('{a}', '{b}', '{c}')) -Kind 'MultiString' -CreateKey $false
            $st = Get-SpValueState -KeyPath $path -Name 'List'
            Assert-Equal $st.Kind 'MultiString' 'the list keeps its kind'
            Assert-SeqEqual $st.Data @('{a}', '{b}', '{c}') 'a REG_MULTI_SZ list is rewritten'

            Remove-SpValue -KeyPath $path -Name 'Fresh'
            Assert-True (-not (Get-SpValueState -KeyPath $path -Name 'Fresh').Exists) 'a value is deleted'

            $threw = $false
            try { Set-SpValue -KeyPath "$path\NoSuchChild" -Name 'X' -Data 1 -Kind 'DWord' -CreateKey $false } catch { $threw = $true }
            Assert-True $threw 'a missing key is an error when CreateKey is false'
            Assert-True (-not (Get-SpValueState -KeyPath "$path\NoSuchChild" -Name 'X').KeyExists) 'and it was not created'
        }
        finally { Set-CreateSubKeyDeny $sub $false }
        # a key that is really missing is still created when CreateKey is true (the Audio key may not exist yet)
        $fresh = "$RehBase\U15-new\Audio"
        Set-SpValue -KeyPath $fresh -Name 'Flag' -Data 1 -Kind 'DWord' -CreateKey $true
        $st2 = Get-SpValueState -KeyPath $fresh -Name 'Flag'
        Assert-True ($st2.KeyExists -and $st2.Exists -and ([int]$st2.Data -eq 1)) 'a missing key is created for the value when CreateKey is true'
    }

    # =========================================================================================
    # U16-U21: fix round 1 against the REAL branch (Invoke-FakeReal: recording stubs, tripwires, rehearsal hive)
    # U20, U20b, U22-U25: ruling 27 (M7), only AudioEndpointBuilder and Audiosrv must come back
    # =========================================================================================
    function Set-DriverUpdateTamper {
        # a driver update takes our Safety Cap entry out of ,15 on every Headphones endpoint: the reason to run Repair
        foreach ($id in $script:HpIds) {
            $l15 = @((Get-RegState (Get-FxPath $id) $ListSafety).Data | Where-Object { $_ -ine $ClsidSafety })
            Set-RehValue (Get-FxPath $id) $ListSafety ([string[]]$l15) 'MultiString'
        }
    }
    function Remove-FakeComScaffolding {
        # the Register stub creates Classes\CLSID and Classes\AudioEngine; Uninstall removes only our two CLSID keys under them
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$RehSub\Classes", $false)
    }

    Run-Test 'U16 Real branch: a failed Repair keeps COM, files and CLSIDs, undoes only its own writes, then brings audio back' {
        Assert-Loaded
        New-Rehearsal
        $root = Join-Path $UnitBox 'fake-real-u16'
        $pristine = @(Get-RegSnapshot $RehBase)
        $first = Invoke-FakeReal -Mode 'Install' -Root $root
        Assert-FakeExit $first 0 'Install (stubbed system calls)'
        Set-DriverUpdateTamper
        $beforeRepair = @(Get-RegSnapshot $RehBase)
        $jp = Join-Path $first.Inst 'install-journal.json'
        $journalBefore = [IO.File]::ReadAllText($jp)
        $filesBefore = @(Get-DirListing $first.Inst)
        $dllHash = (Get-FileHash -LiteralPath (Join-Path $first.Inst 'SoundProtection.Apo.dll')).Hash

        $r = Invoke-FakeReal -Mode 'Repair' -Root $root -FailAfter 'Lists'
        Assert-FakeExit $r 1 'Repair with an injected fault after the list writes'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $beforeRepair 'the registry (COM keys included) is exactly as it was before the Repair'
        Assert-Equal @($r.Events | Where-Object { $_ -like 'UNREGISTER*' }).Count 0 'the plug-in was not unregistered'
        foreach ($cl in $ClsidLeveler, $ClsidSafety) {
            Assert-True (Get-RegState "$RehBase\Classes\CLSID\$cl\InprocServer32" '').Exists "the COM registration of $cl is still there"
        }
        foreach ($id in $script:HpIds) {
            Assert-Equal (Count-Ci (Get-RegState (Get-FxPath $id) $ListLeveler).Data $ClsidLeveler) 1 "the Leveler CLSID is still in ,13 on $id"
        }
        Assert-SeqEqual @(Get-DirListing $r.Inst) $filesBefore 'no installed file, journal, backup or log was deleted'
        Assert-Equal (Get-FileHash -LiteralPath (Join-Path $r.Inst 'SoundProtection.Apo.dll')).Hash $dllHash 'the installed DLL is as it was'
        Assert-Equal ([IO.File]::ReadAllText($jp)) $journalBefore 'the install journal is byte-identical'
        Assert-True ([Array]::IndexOf($r.Events, 'STOP-SERVICES') -ge 0) 'the audio services had been stopped for the Repair'
        $starts = @($r.Events | Where-Object { $_ -like 'START-SERVICES*' })
        Assert-Equal $starts.Count 1 'and were started once afterwards'
        Assert-Equal $starts[0] 'START-SERVICES Audiosrv,FakeDependentOne' 'together with the dependents that were running'
        Assert-SnapshotEqual $r.RegAtStart $beforeRepair 'the old values were back before the services came up'
        Assert-True ($r.Log -match 'Repair failed: the setup was left as it was before the repair.*Injected fault') "the log says it, followed by the error`n$(Get-Tail $r.Log 8)"
        Assert-True ($r.Log -notmatch 'Rolling back from the journal') 'no rollback from the journal was started'
        # the setup is still a working one
        Assert-FakeExit (Invoke-FakeReal -Mode 'Repair' -Root $root) 0 'Repair again, without the fault'
        Assert-FakeExit (Invoke-FakeReal -Mode 'Uninstall' -Root $root) 0 'Uninstall'
        Assert-True (-not (Get-RegState "$RehBase\Classes\CLSID\$ClsidLeveler" '').KeyExists) 'Uninstall removes the COM registration'
        Remove-FakeComScaffolding
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine 'the journal that the failed Repair left alone restores the first originals, byte for byte'
    }

    Run-Test 'U16b Real branch: a Repair whose core audio services do not come back keeps the install and starts them again after putting its writes back' {
        Assert-Loaded
        New-Rehearsal
        $root = Join-Path $UnitBox 'fake-real-u16b'
        $first = Invoke-FakeReal -Mode 'Install' -Root $root
        Assert-FakeExit $first 0 'Install (stubbed system calls)'
        Set-DriverUpdateTamper
        $beforeRepair = @(Get-RegSnapshot $RehBase)
        $jp = Join-Path $first.Inst 'install-journal.json'
        $journalBefore = [IO.File]::ReadAllText($jp)
        $filesBefore = @(Get-DirListing $first.Inst)

        $r = Invoke-FakeReal -Mode 'Repair' -Root $root -ServicesStart $false
        Assert-FakeExit $r 1 'Repair where the audio services do not come back (the stub reports false)'
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $beforeRepair 'the registry is exactly as it was before the Repair'
        Assert-Equal @($r.Events | Where-Object { $_ -like 'UNREGISTER*' }).Count 0 'the plug-in was not unregistered'
        Assert-SeqEqual @(Get-DirListing $r.Inst) $filesBefore 'no installed file, journal, backup or log was deleted'
        Assert-Equal ([IO.File]::ReadAllText($jp)) $journalBefore 'the install journal is byte-identical'
        Assert-Equal @($r.Events | Where-Object { $_ -like 'START-SERVICES*' }).Count 2 'the services were started in step 8 and tried again after the undo'
        Assert-SnapshotEqual $r.RegAtStart $beforeRepair 'the last attempt came after the old values were back'
        Assert-True ($r.Log -match 'Repair failed: the setup was left as it was before the repair.*AudioEndpointBuilder or Audiosrv did not come back') "the log names the error`n$(Get-Tail $r.Log 8)"
        Assert-True ($r.Log -match 'could not be started') 'and says that the services still need attention'
    }

    Run-Test 'U16c Real branch: a Repair whose COM registration check fails writes nothing into the effect lists' {
        Assert-Loaded
        New-Rehearsal
        $root = Join-Path $UnitBox 'fake-real-u16c'
        $first = Invoke-FakeReal -Mode 'Install' -Root $root
        Assert-FakeExit $first 0 'Install (stubbed system calls)'
        Set-DriverUpdateTamper
        $before = @(Get-RegSnapshot "$RehBase\Microsoft")
        $filesBefore = @(Get-DirListing $first.Inst)
        $r = Invoke-FakeReal -Mode 'Repair' -Root $root -ComWrongPath $true
        Assert-FakeExit $r 1 'Repair where the COM check fails'
        Assert-True ($r.Log -match 'COM registration check failed') "the error is the COM check`n$(Get-Tail $r.Log 8)"
        Assert-SnapshotEqual @(Get-RegSnapshot "$RehBase\Microsoft") $before 'the endpoint and Audio keys were not touched: the lists are written only after the COM check has passed'
        Assert-Equal @($r.Events | Where-Object { $_ -like 'UNREGISTER*' }).Count 0 'the plug-in was not unregistered'
        Assert-SeqEqual @(Get-DirListing $r.Inst) $filesBefore 'no installed file was deleted'
        Assert-Equal @($r.Events | Where-Object { $_ -like 'START-SERVICES*' }).Count 1 'the audio services were started again'
    }

    Run-Test 'U17 Real branch: the journal, the .reg backups and the log are written into the install folder only, and Uninstall keeps them there' {
        Assert-Loaded
        New-Rehearsal
        $root = Join-Path $UnitBox 'fake-real-u17'
        $i = Invoke-FakeReal -Mode 'Install' -Root $root
        Assert-FakeExit $i 0 'Install (stubbed system calls)'
        Assert-SeqEqual @(Get-DirListing $i.Inst) @('SoundProtection.Apo.dll', 'SoundProtection.Install.ps1', 'backup', 'install-journal.json', 'install-log.txt') 'install folder: the two files, backup\, the journal and the log'
        Assert-SeqEqual @(Get-DirListing $i.Data) @('config.bin', 'telemetry') 'data folder: config.bin and telemetry\ only'
        $backups = @(Get-ChildItem -LiteralPath (Join-Path $i.Inst 'backup') -Directory)
        Assert-Equal $backups.Count 1 'one backup folder'
        Assert-True ($backups[0].Name -match '^\d{8}-\d{6}$') "named by date and time ($($backups[0].Name))"
        Assert-True (Test-Path -LiteralPath (Join-Path $backups[0].FullName 'fake.reg')) 'the export went into it'
        $bk = @($i.Events | Where-Object { $_ -like 'BACKUP *' })
        Assert-Equal $bk.Count 1 'one backup was requested'
        Assert-True ($bk[0].StartsWith('BACKUP ' + (Join-Path $i.Inst 'backup') + '\')) "and it is below the install folder ($($bk[0]))"
        Assert-Equal @($i.Events | Where-Object { $_ -like 'ACL *' }).Count 1 'the ACLs were set once'
        Assert-Equal (@($i.Events | Where-Object { $_ -like 'ACL *' })[0]) ('ACL ' + $i.Data) 'on the data folder'

        $u = Invoke-FakeReal -Mode 'Uninstall' -Root $root
        Assert-FakeExit $u 0 'Uninstall (stubbed system calls)'
        Assert-SeqEqual @(Get-DirListing $u.Inst) @('backup', 'install-journal.done.json', 'install-log.txt') 'Uninstall: our two files are gone; the finished journal, the backups and the log stay'
        Assert-SeqEqual @(Get-DirListing $u.Data) @('config.bin', 'telemetry') 'the data folder is as it was'
        Assert-True ($u.Log -match '(?m)\[INFO\] the folder .*kept on purpose') "the log says why the folder is still there`n$(Get-Tail $u.Log 8)"
        Assert-True ($u.Log -notmatch '(?m)\[(WARN|ERROR)\]') 'and there is no warning or error'
        Assert-Equal @($u.Events | Where-Object { $_ -like 'UNREGISTER*' }).Count 1 'Uninstall did unregister the plug-in'
        Remove-FakeComScaffolding
    }

    Run-Test 'U17b Real branch: a data folder that becomes a junction after the first check is caught before it is created or its ACL set' {
        Assert-Loaded
        New-Rehearsal
        $root = Join-Path $UnitBox 'fake-real-u17b'
        $target = Join-Path $root 'elsewhere'
        $dataPath = Join-Path $root 'data'          # Invoke-FakeReal uses <Root>\data as the data folder; it does not exist yet
        [void][IO.Directory]::CreateDirectory($target)
        $pristine = @(Get-RegSnapshot $RehBase)
        try {
            # a standard user swaps in a junction while the services are stopped and the files are being installed
            $r = Invoke-FakeReal -Mode 'Install' -Root $root -AfterRegister { New-Junction $dataPath $target }
            Assert-FakeExit $r 1 'Install where the data folder becomes a junction after the first check'
            Assert-True ($r.Log -match '(?i)turned into a junction') "the log says why`n$(Get-Tail $r.Log 10)"
            Assert-SeqEqual @(Get-DirListing $target) @() 'nothing was created through the junction'
            Assert-Equal @($r.Events | Where-Object { $_ -like 'ACL *' }).Count 0 'and no ACL was set'
            Assert-True ($r.Log -match 'Rollback complete') "the install was rolled back`n$(Get-Tail $r.Log 10)"
            Remove-FakeComScaffolding
            Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine 'the registry is back to the original'
            Assert-Equal @($r.Events | Where-Object { $_ -like 'START-SERVICES*' }).Count 1 'and the audio services were started again'
        }
        finally { Remove-Junction $dataPath }
    }

    Run-Test 'U18 Set-SpDataAcl makes Administrators the owner of the data folder first, then applies the two grants (commands recorded, not run)' {
        Assert-Loaded
        $script:NativeCalls = New-Object System.Collections.ArrayList
        $script:FailOwner = $false
        function Invoke-SpNative {
            param([string]$FilePath, [string[]]$ArgumentList = @(), [int]$TimeoutSec = 120)
            [void]$script:NativeCalls.Add([pscustomobject]@{ File = $FilePath; Args = @($ArgumentList) })
            $code = 0
            if ($script:FailOwner -and ($ArgumentList -contains '/setowner')) { $code = 1307 }
            return [pscustomobject]@{ ExitCode = $code; Output = 'recorded, not run' }
        }
        $d = Join-Path $UnitBox 'owner data'
        Set-SpDataAcl -DataDir $d
        $calls = @($script:NativeCalls)
        Assert-Equal $calls.Count 3 'three icacls commands: owner, grants on the data folder, grants on telemetry\'
        foreach ($c in $calls) { Assert-True ($c.File -like '*\System32\icacls.exe') "icacls is the program ($($c.File))" }
        Assert-SeqEqual $calls[0].Args @($d, '/setowner', '*S-1-5-32-544') 'first: the owner of the data folder becomes Administrators'
        Assert-Equal $calls[1].Args[0] $d 'second: the grants on the data folder'
        Assert-True (($calls[1].Args -contains '/inheritance:r') -and ($calls[1].Args -contains '*S-1-5-32-545:(OI)(CI)M')) '   (inheritance removed, Users: modify, as before)'
        Assert-Equal $calls[2].Args[0] (Join-Path $d 'telemetry') 'third: the grants on telemetry\'
        # a refused owner change stops everything: nothing is granted on a folder that somebody else may own
        $script:NativeCalls.Clear()
        $script:FailOwner = $true
        $msg = ''
        try { Set-SpDataAcl -DataDir $d } catch { $msg = $_.Exception.Message }
        Assert-True ($msg -match '1307') "the failure is reported with the exit code ($msg)"
        Assert-Equal @($script:NativeCalls).Count 1 'and no grant was applied after it'
    }

    Run-Test 'U18b The owner command is understood by the real icacls (sandbox folder): done when elevated, refused with 1307 when not' {
        Assert-Loaded
        $sb = Join-Path $UnitBox 'owner real'
        [void][IO.Directory]::CreateDirectory($sb)
        $msg = $null
        try { Set-SpDataOwner -DataDir $sb } catch { $msg = $_.Exception.Message }
        if (Test-SpAdmin) {
            Assert-True ($null -eq $msg) "elevated: the owner change works ($msg)"
            $owner = (New-Object System.IO.DirectoryInfo $sb).GetAccessControl().GetOwner([System.Security.Principal.SecurityIdentifier]).Value
            Assert-Equal $owner 'S-1-5-32-544' 'and Administrators own the folder'
        }
        else {
            Assert-True (($null -ne $msg) -and ($msg -match '1307')) "not elevated (filtered token): icacls took the command and refused it with 1307 ($msg)"
        }
    }

    Run-Test 'U19 Test-SpReparsePoint: a junction (also a dangling one) is one; a folder, a file and a missing path are not' {
        Assert-Loaded
        $d = Join-Path $UnitBox 'reparse'
        $plain = Join-Path $d 'plain'
        $target = Join-Path $d 'target'
        $link = Join-Path $d 'link'
        $dangT = Join-Path $d 'dangling-target'
        $dangL = Join-Path $d 'dangling-link'
        $file = Join-Path $d 'file.txt'
        foreach ($p in $plain, $target, $dangT) { [void][IO.Directory]::CreateDirectory($p) }
        [IO.File]::WriteAllText($file, 'x')
        New-Junction $link $target
        New-Junction $dangL $dangT
        try {
            Microsoft.PowerShell.Management\Remove-Item -LiteralPath $dangT -Force            # the junction now points nowhere
            Assert-True (Test-SpReparsePoint -Path $link) 'a junction'
            Assert-True (Test-SpReparsePoint -Path $dangL) 'a dangling junction'
            Assert-True (-not (Test-SpReparsePoint -Path $plain)) 'a plain folder'
            Assert-True (-not (Test-SpReparsePoint -Path $file)) 'a file'
            Assert-True (-not (Test-SpReparsePoint -Path (Join-Path $d 'missing'))) 'a missing path'
            Assert-True (-not (Test-SpReparsePoint -Path (Join-Path $d 'missing\deeper'))) 'a path below a missing folder'
            # a directory symbolic link too, if this account may create one (it needs Developer Mode or elevation)
            $sym = Join-Path $d 'symlink'
            [void](Invoke-Cmd ('mklink /D "' + $sym + '" "' + $target + '"'))
            if (Test-IsReparse $sym) {
                try { Assert-True (Test-SpReparsePoint -Path $sym) 'a directory symbolic link' }
                finally { [void](Invoke-Cmd ('rmdir "' + $sym + '"')) }
            }
        }
        finally { Remove-Junction $link; Remove-Junction $dangL }
    }

    # Runs the installer's real Start-SpAudioServices on fake services: Get-Service, Start-Service and Write-SpLog are stubs, so no
    # real service and no log file is reached. -Broken: Start-Service throws for that service. -Stuck: Start-Service returns but the
    # service never reaches Running (the 20 s wait times out). Returns the result, the services in the order they were started, and the log lines.
    function Invoke-FakeServiceStart {
        param([string[]]$Dependents = @(), [string[]]$Broken = @(), [string[]]$Stuck = @())
        Add-Type -AssemblyName System.ServiceProcess
        $script:Logs = New-Object System.Collections.ArrayList
        $script:Svc = @{}
        $script:StartCalls = New-Object System.Collections.ArrayList
        $script:FakeBroken = @($Broken)
        $script:FakeStuck = @($Stuck)
        function Write-SpLog { param([string]$Message, [string]$Level = 'INFO') [void]$script:Logs.Add('[' + $Level + '] ' + $Message) }
        function Get-Service {
            param([string]$Name)
            if (-not $script:Svc.ContainsKey($Name)) {
                $o = [pscustomobject]@{ Name = $Name; Status = 'Stopped' }
                Add-Member -InputObject $o -MemberType ScriptMethod -Name Refresh -Value { }
                Add-Member -InputObject $o -MemberType ScriptMethod -Name WaitForStatus -Value { param($Wanted, $Timeout) if ([string]$this.Status -ne 'Running') { throw ('Timed out waiting for ' + $this.Name) } }
                $script:Svc[$Name] = $o
            }
            return $script:Svc[$Name]
        }
        function Start-Service {
            param([string]$Name)
            [void]$script:StartCalls.Add($Name)
            if ($script:FakeBroken -contains $Name) { throw ('Cannot start service ' + $Name + ' on computer ''.''.') }
            if ($script:FakeStuck -notcontains $Name) { $script:Svc[$Name].Status = 'Running' }
        }
        $ok = Start-SpAudioServices -Dependents $Dependents
        return [pscustomobject]@{ Result = $ok; Calls = @($script:StartCalls); Logs = @($script:Logs) }
    }

    # the text of a WARN line for a dependent that did not come back (ruling 27)
    $WarnTemplate = 'service {0} did not reach Running within 20 s; sound does not need it; start it from services.msc or restart the PC'

    Run-Test 'U20 Start-SpAudioServices: a dependent other than AudioEndpointBuilder and Audiosrv that will not start gets one WARN line, the result stays true and the rest are still started' {
        Assert-Loaded
        $r = Invoke-FakeServiceStart -Dependents @('FakeBroken', 'FakeStuck', 'FakeGood') -Broken @('FakeBroken') -Stuck @('FakeStuck')
        Assert-True ($r.Result -eq $true) 'the result is true: AudioEndpointBuilder and Audiosrv are running, and sound does not need the others'
        Assert-SeqEqual $r.Calls @('AudioEndpointBuilder', 'Audiosrv', 'FakeBroken', 'FakeStuck', 'FakeGood') 'every service is tried, in order, also after a failure'
        foreach ($n in 'FakeBroken', 'FakeStuck') {
            $warn = '[WARN] ' + ($WarnTemplate -f $n)
            Assert-Equal @($r.Logs | Where-Object { $_ -ceq $warn }).Count 1 "$n gets the WARN line, once: $($r.Logs -join ' / ')"
        }
        Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[WARN\]' }).Count 2 'and there is no other WARN line'
        Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[ERROR\]' }).Count 0 'no ERROR line: a service that sound does not need is not an error'
        Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[INFO\] service FakeBroken: Cannot start service FakeBroken' }).Count 1 'the reason Windows gave is kept in the log (INFO), FakeBroken'
        Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[INFO\] service FakeStuck: .*Timed out waiting for FakeStuck' }).Count 1 'and FakeStuck (a timeout reaches the log wrapped in the WaitForStatus call, as the real one would)'
        Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[INFO\] service FakeGood is running' }).Count 1 'FakeGood still came up'
        $r2 = Invoke-FakeServiceStart -Dependents @('FakeGood')
        Assert-True ($r2.Result -eq $true) 'when every service is running the result is true'
        Assert-Equal @($r2.Logs | Where-Object { $_ -match '^\[(WARN|ERROR)\]' }).Count 0 'and nothing is warned about'
    }

    Run-Test 'U20b Start-SpAudioServices: AudioEndpointBuilder or Audiosrv that does not come back makes the result false (ERROR line), and every service is still tried' {
        Assert-Loaded
        $core = @('AudioEndpointBuilder', 'Audiosrv')
        $cases = @(
            @{ Label = 'AudioEndpointBuilder throws';       Broken = @('AudioEndpointBuilder'); Stuck = @() },
            @{ Label = 'Audiosrv never reaches Running';    Broken = @();                       Stuck = @('Audiosrv') },
            @{ Label = 'both core services fail';           Broken = @('AudioEndpointBuilder'); Stuck = @('Audiosrv') },
            @{ Label = 'Audiosrv fails and so do two more'; Broken = @('Audiosrv', 'FakeBroken'); Stuck = @('FakeStuck') }
        )
        foreach ($case in $cases) {
            $r = Invoke-FakeServiceStart -Dependents @('FakeBroken', 'FakeStuck', 'FakeGood') -Broken $case.Broken -Stuck $case.Stuck
            $label = $case.Label
            Assert-True ($r.Result -eq $false) "$label`: the result is false"
            Assert-SeqEqual $r.Calls @('AudioEndpointBuilder', 'Audiosrv', 'FakeBroken', 'FakeStuck', 'FakeGood') "$label`: every service is tried, in order, also after a failure"
            $failedNames = @($case.Broken) + @($case.Stuck)
            foreach ($n in $failedNames) {
                if ($core -contains $n) {
                    Assert-Equal @($r.Logs | Where-Object { $_ -match ('^\[ERROR\] service ' + $n + ' did not reach Running') }).Count 1 "$label`: $n is named in an ERROR line: $($r.Logs -join ' / ')"
                    Assert-Equal @($r.Logs | Where-Object { $_ -match ('^\[WARN\] service ' + $n + ' ') }).Count 0 "$label`: and a core service never gets the soft WARN line"
                }
                else {
                    $warn = '[WARN] ' + ($WarnTemplate -f $n)
                    Assert-Equal @($r.Logs | Where-Object { $_ -ceq $warn }).Count 1 "$label`: $n (not a core service) only gets the WARN line: $($r.Logs -join ' / ')"
                }
            }
            $coreFailed = @($failedNames | Where-Object { $core -contains $_ }).Count
            Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[ERROR\]' }).Count $coreFailed "$label`: one ERROR line for each core service that failed, no more"
            Assert-Equal @($r.Logs | Where-Object { $_ -match '^\[INFO\] service FakeGood is running' }).Count 1 "$label`: FakeGood still came up"
        }
    }

    Run-Test 'U21 Test-SpIsRealInstallFolder: only %ProgramFiles%\SoundProtection counts (any case, with or without a trailing backslash)' {
        Assert-Loaded
        $saved = $env:ProgramFiles
        try {
            # this process only: a child process always gets the real ProgramFiles back from Windows (see R18)
            $env:ProgramFiles = Join-Path $UnitBox 'fake pf'
            $real = Join-Path $env:ProgramFiles 'SoundProtection'
            Assert-True (Test-SpIsRealInstallFolder -Path $real) 'the exact path'
            Assert-True (Test-SpIsRealInstallFolder -Path ($real + '\')) 'with a trailing backslash'
            Assert-True (Test-SpIsRealInstallFolder -Path $real.ToUpperInvariant()) 'in other letter case'
            Assert-True (Test-SpIsRealInstallFolder -Path (Join-Path $env:ProgramFiles 'other\..\SoundProtection')) 'spelled with .. in it'
            Assert-True (-not (Test-SpIsRealInstallFolder -Path ($real + '2'))) 'a sibling that only starts with the same name'
            Assert-True (-not (Test-SpIsRealInstallFolder -Path (Join-Path $real 'sub'))) 'a folder below it'
            Assert-True (-not (Test-SpIsRealInstallFolder -Path $env:ProgramFiles)) 'the Program Files folder itself'
            Assert-True (-not (Test-SpIsRealInstallFolder -Path $InstDirBox)) 'the sandbox install folder of these tests'
            Assert-True (-not (Test-SpIsRealInstallFolder -Path '')) 'an empty path'
        }
        finally { $env:ProgramFiles = $saved }
        Assert-Equal $env:ProgramFiles $saved 'ProgramFiles is back as it was'
    }

    # =========================================================================================
    # U22-U25: ruling 27 (M7) against the REAL branch, with the installer's own Start-SpAudioServices running on fake services
    # (Invoke-FakeReal -WontStart). On this PC the recorded dependents are Audiosrv, midisrv and RtkAudioUniversalService.
    # =========================================================================================
    # The message part of the log lines of one level in a run's log ("yyyy-MM-dd HH:mm:ss [LEVEL] message").
    function Get-LogMessages {
        param([string]$Log, [string]$Level)
        foreach ($line in ($Log -split "`r?`n")) {
            if ($line -match ('^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d \[' + $Level + '\] (.*)$')) { $Matches[1] }
        }
    }
    # The services a fake-real run tried to start, in order.
    function Get-SvcStarts($Run) {
        return @($Run.Events | Where-Object { $_ -like 'SVC-START *' } | ForEach-Object { $_ -replace '^SVC-START ', '' })
    }

    Run-Test 'U22 Real branch: a dependent that does not come back (not AudioEndpointBuilder, not Audiosrv) gets one WARN line; Install and Repair carry on to Verify and nothing is rolled back' {
        Assert-Loaded
        New-Rehearsal
        $root = Join-Path $UnitBox 'fake-real-u22'
        $everyService = @('AudioEndpointBuilder', 'Audiosrv', 'midisrv', 'RtkAudioUniversalService')

        $i = Invoke-FakeReal -Mode 'Install' -Root $root -WontStart @('midisrv', 'RtkAudioUniversalService')
        Assert-FakeExit $i 0 'Install where midisrv and RtkAudioUniversalService do not come back'
        Assert-SeqEqual @(Get-SvcStarts $i) $everyService 'every service got its start attempt, in the usual order, also after the first failure'
        $warns = @(Get-LogMessages $i.Log 'WARN')
        foreach ($n in 'midisrv', 'RtkAudioUniversalService') {
            Assert-Equal @($warns | Where-Object { $_ -ceq ($WarnTemplate -f $n) }).Count 1 "$n gets the WARN line, once`n$(Get-Tail $i.Log 14)"
        }
        Assert-Equal @($warns | Where-Object { $_ -match '^service ' }).Count 2 'and no other service is warned about'
        Assert-Equal @(Get-LogMessages $i.Log 'ERROR').Count 0 'there is no ERROR line'
        Assert-True ($i.Log -notmatch 'FAILED:') 'the run did not fail'
        Assert-True ($i.Log -notmatch 'Rolling back from the journal') 'and was not rolled back'
        Assert-True ($i.Log -notmatch 'could not be started') 'and no second attempt to start the services was needed'
        foreach ($id in $script:HpIds) {
            Assert-Equal (Count-Ci (Get-RegState (Get-FxPath $id) $ListLeveler).Data $ClsidLeveler) 1 "the Leveler CLSID is in ,13 on $id"
            Assert-Equal (Count-Ci (Get-RegState (Get-FxPath $id) $ListSafety).Data $ClsidSafety) 1 "the Safety CLSID is in ,15 on $id"
        }
        Assert-True (Test-Path -LiteralPath (Join-Path $i.Inst 'install-journal.json')) 'the install journal is still there (not renamed by a rollback)'
        $ev = @($i.Events)
        Assert-True ([Array]::IndexOf($ev, 'VERIFY') -gt [Array]::LastIndexOf($ev, 'SVC-START RtkAudioUniversalService')) 'Install went on to Verify after the services'
        Assert-Equal @($ev | Where-Object { $_ -like 'SHORTCUT *' }).Count 1 'and made the shortcut'

        # a Repair does the same
        Set-DriverUpdateTamper
        $r = Invoke-FakeReal -Mode 'Repair' -Root $root -WontStart @('RtkAudioUniversalService')
        Assert-FakeExit $r 0 'Repair where RtkAudioUniversalService does not come back'
        Assert-SeqEqual @(Get-SvcStarts $r) $everyService 'Repair: every service got its start attempt'
        Assert-Equal @(Get-LogMessages $r.Log 'WARN' | Where-Object { $_ -ceq ($WarnTemplate -f 'RtkAudioUniversalService') }).Count 1 'Repair: the WARN line, once'
        Assert-Equal @(Get-LogMessages $r.Log 'ERROR').Count 0 'Repair: there is no ERROR line'
        Assert-True ($r.Log -notmatch 'Repair failed') 'the Repair did not fail'
        foreach ($id in $script:HpIds) {
            Assert-Equal (Count-Ci (Get-RegState (Get-FxPath $id) $ListSafety).Data $ClsidSafety) 1 "the Repair put the Safety CLSID back in ,15 on $id and it stays there"
        }
        Assert-True ([Array]::IndexOf(@($r.Events), 'VERIFY') -ge 0) 'and went on to Verify'
    }

    Run-Test 'U23 Real branch: AudioEndpointBuilder or Audiosrv that does not come back still fails an Install: it is rolled back and the services are tried again' {
        Assert-Loaded
        foreach ($core in 'AudioEndpointBuilder', 'Audiosrv') {
            New-Rehearsal
            $root = Join-Path $UnitBox ('fake-real-u23-' + $core)
            $pristine = @(Get-RegSnapshot $RehBase)
            $r = Invoke-FakeReal -Mode 'Install' -Root $root -WontStart @($core)
            Assert-FakeExit $r 1 "Install where $core does not come back"
            # round one: every service, in order (a core failure does not stop the others); round two (after the rollback): only the one that is not running
            Assert-SeqEqual @(Get-SvcStarts $r) @('AudioEndpointBuilder', 'Audiosrv', 'midisrv', 'RtkAudioUniversalService', $core) "$core`: all four were tried, then $core again after the rollback"
            Assert-True ($r.Log -match 'Rolling back from the journal') "$core`: the Install was rolled back`n$(Get-Tail $r.Log 14)"
            Assert-True ($r.Log -match 'Rollback complete') "$core`: and the rollback finished"
            Remove-FakeComScaffolding
            Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine "$core`: the registry is back to the original"
            Assert-Equal @($r.Events | Where-Object { $_ -eq 'VERIFY' -or $_ -like 'SHORTCUT *' }).Count 0 "$core`: no shortcut and no Verify after a failed Install"
            Assert-Equal @(Get-LogMessages $r.Log 'ERROR' | Where-Object { $_ -match ('^service ' + $core + ' did not reach Running') }).Count 2 "$core`: named in an ERROR line after each attempt"
            Assert-Equal @(Get-LogMessages $r.Log 'WARN' | Where-Object { $_ -match '^service ' }).Count 0 "$core`: never only a warning"
            Assert-True ($r.Log -match 'could not be started; start them from services\.msc') "$core`: the log says the services still need attention"
            Assert-True ($r.Log -match 'FAILED: AudioEndpointBuilder or Audiosrv did not come back') "$core`: and the failure names the two core services`n$(Get-Tail $r.Log 14)"
        }
    }

    Run-Test 'U24 Real branch: AudioEndpointBuilder or Audiosrv that does not come back still fails a Repair the ruling 10 way: its own writes are put back, the install is kept, the services are tried again' {
        Assert-Loaded
        foreach ($core in 'AudioEndpointBuilder', 'Audiosrv') {
            New-Rehearsal
            $root = Join-Path $UnitBox ('fake-real-u24-' + $core)
            $first = Invoke-FakeReal -Mode 'Install' -Root $root
            Assert-FakeExit $first 0 'Install (stubbed system calls)'
            Set-DriverUpdateTamper
            $beforeRepair = @(Get-RegSnapshot $RehBase)
            $jp = Join-Path $first.Inst 'install-journal.json'
            $journalBefore = [IO.File]::ReadAllText($jp)
            $filesBefore = @(Get-DirListing $first.Inst)

            $r = Invoke-FakeReal -Mode 'Repair' -Root $root -WontStart @($core)
            Assert-FakeExit $r 1 "Repair where $core does not come back"
            Assert-SeqEqual @(Get-SvcStarts $r) @('AudioEndpointBuilder', 'Audiosrv', 'midisrv', 'RtkAudioUniversalService', $core) "$core`: all four were tried, then $core again after the undo"
            Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $beforeRepair "$core`: the registry is exactly as it was before the Repair"
            Assert-SnapshotEqual $r.RegAtStart $beforeRepair "$core`: the last start attempt came after the old values were back"
            Assert-Equal @($r.Events | Where-Object { $_ -like 'UNREGISTER*' }).Count 0 "$core`: the plug-in was not unregistered"
            Assert-SeqEqual @(Get-DirListing $r.Inst) $filesBefore "$core`: no installed file, journal, backup or log was deleted"
            Assert-Equal ([IO.File]::ReadAllText($jp)) $journalBefore "$core`: the install journal is byte-identical"
            Assert-True ($r.Log -notmatch 'Rolling back from the journal') "$core`: no rollback from the journal was started"
            Assert-True ($r.Log -match 'could not be started; start them from services\.msc') "$core`: the log says the services still need attention"
            Assert-True ($r.Log -match 'Repair failed: the setup was left as it was before the repair.*AudioEndpointBuilder or Audiosrv did not come back') "$core`: the log names the error`n$(Get-Tail $r.Log 10)"
        }
    }

    Run-Test 'U25 Real branch: Uninstall ends with exit 0 and one WARN line when only a dependent other than AudioEndpointBuilder and Audiosrv does not come back, and with exit 1 when a core service does not' {
        Assert-Loaded
        New-Rehearsal
        $pristine = @(Get-RegSnapshot $RehBase)
        $root = Join-Path $UnitBox 'fake-real-u25-dependent'
        Assert-FakeExit (Invoke-FakeReal -Mode 'Install' -Root $root) 0 'Install (stubbed system calls)'
        $u = Invoke-FakeReal -Mode 'Uninstall' -Root $root -WontStart @('midisrv')
        Assert-FakeExit $u 0 'Uninstall where midisrv does not come back'
        Assert-SeqEqual @(Get-SvcStarts $u) @('AudioEndpointBuilder', 'Audiosrv', 'midisrv', 'RtkAudioUniversalService') 'every service got its start attempt, in order'
        Assert-Equal @(Get-LogMessages $u.Log 'WARN' | Where-Object { $_ -ceq ($WarnTemplate -f 'midisrv') }).Count 1 "midisrv gets the WARN line, once`n$(Get-Tail $u.Log 10)"
        Assert-Equal @(Get-LogMessages $u.Log 'ERROR').Count 0 'there is no ERROR line'
        Assert-True ($u.Log -match 'Uninstall complete') 'and Uninstall says it is complete'
        Remove-FakeComScaffolding
        Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine 'the original settings are back'

        foreach ($core in 'AudioEndpointBuilder', 'Audiosrv') {
            New-Rehearsal
            $pristine = @(Get-RegSnapshot $RehBase)
            $root = Join-Path $UnitBox ('fake-real-u25-' + $core)
            Assert-FakeExit (Invoke-FakeReal -Mode 'Install' -Root $root) 0 'Install (stubbed system calls)'
            $u = Invoke-FakeReal -Mode 'Uninstall' -Root $root -WontStart @($core)
            Assert-FakeExit $u 1 "Uninstall where $core does not come back"
            Assert-SeqEqual @(Get-SvcStarts $u) @('AudioEndpointBuilder', 'Audiosrv', 'midisrv', 'RtkAudioUniversalService') "$core`: every service got its start attempt, in order"
            Assert-Equal @(Get-LogMessages $u.Log 'ERROR' | Where-Object { $_ -match ('^service ' + $core + ' did not reach Running') }).Count 1 "$core`: named in an ERROR line`n$(Get-Tail $u.Log 10)"
            Assert-True ($u.Log -match 'Uninstall finished with 1 problem') "$core`: and Uninstall reports the problem"
            Assert-True ($u.Log -notmatch 'Uninstall complete') "$core`: and does not say it is complete"
            Remove-FakeComScaffolding
            Assert-SnapshotEqual @(Get-RegSnapshot $RehBase) $pristine "$core`: the settings are back all the same (Uninstall is not undone)"
        }
    }
}
finally {
    Remove-Rehearsal
    Remove-TempDir $DataDir
    Remove-TempDir $InstDirBox
    Remove-TempDir $UnitBox
}

Write-Host ''
if ($script:Fail -eq 0) {
    Write-Host ("ALL {0} TESTS PASSED" -f $script:Pass) -ForegroundColor Green
    exit 0
}
Write-Host ("{0} passed, {1} FAILED: {2}" -f $script:Pass, $script:Fail, ($script:FailedNames -join '; ')) -ForegroundColor Red
exit 1
