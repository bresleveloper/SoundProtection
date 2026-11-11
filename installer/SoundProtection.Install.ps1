<#
.SYNOPSIS
    Installs, repairs, reports on and removes the Sound Protection audio plug-in. Windows PowerShell 5.1.

.DESCRIPTION
    Registers SoundProtection.Apo.dll as two audio effects (APOs) on the chosen render endpoints
    (default: "Headphones"), sets the one registry value that lets Windows load it
    (DisableProtectedAudioDG, "the test key"), restarts the audio services, and records every value
    it touches in a journal so that Uninstall puts everything back exactly.

    -Action Install     back up, journal, stop audio, copy files, register, ACLs, registry, start audio, shortcut, verify
    -Action Repair      the same journal-preserving steps again (endpoints are found by name), then verify;
                        if it fails it undoes only its own writes and starts the audio services again
    -Action Uninstall   restore every journalled value (or delete it if it did not exist), unregister, delete files
    -Action Status      read-only JSON report; needs no elevation

    Exit codes: 0 = done and verified, 3 = installed but not running yet (a restart may be needed),
                1 = failed. A failed Install is rolled back completely. A failed Repair puts back only the
                values it wrote itself and leaves the install, its journal, the COM registration and the
                files as they were.
    After the restart only AudioEndpointBuilder and Audiosrv must come back (sound and the plug-in need nothing
    else): if one of them does not, the run fails as described above. Any other service that was running before
    and does not come back within 20 s gets one warning in the log, and the run carries on.

    The journal (install-journal.json), the .reg backups (backup\<date-time>) and the log (install-log.txt)
    live in -InstallDir (default C:\Program Files\SoundProtection), which only administrators can write.
    -DataDir (default C:\ProgramData\SoundProtection) is writable by standard users, so the elevated run
    never reads anything back from it to write into HKLM; it also refuses to run when -DataDir or its
    telemetry folder is a junction or symbolic link.

    -WhatIf prints every change and makes none. -Rehearsal touches only the registry under -RegistryBase
    (which must then be under HKCU:), config.bin and the telemetry folder under -DataDir, and the journal
    and the log under -InstallDir (so give it a temporary -InstallDir: the real install folder is refused);
    it never elevates, never touches services, Program Files, regsvr32, ACLs, the shortcut or the sound.
    A real Install, Repair or Uninstall relaunches itself elevated (UAC) when it is not already.

    Run it as:  powershell -NoProfile -ExecutionPolicy Bypass -File "<path>\SoundProtection.Install.ps1" -Action Install
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall', 'Repair', 'Status')]
    [string]$Action,

    [string[]]$Endpoints = @('Headphones'),

    [string]$RegistryBase = 'HKLM:\SOFTWARE',

    [string]$DataDir = "$env:ProgramData\SoundProtection",

    [string]$InstallDir = "$env:ProgramFiles\SoundProtection",

    # Default: "$PSScriptRoot\..\native\out\apo\SoundProtection.Apo.dll". It is applied in the main block below,
    # because Windows PowerShell 5.1 leaves $PSScriptRoot empty inside param() default values.
    [string]$DllSource,

    [switch]$Rehearsal,

    [switch]$WhatIf,

    # Rehearsal only: inject a fault so the rollback can be tested.
    [ValidateSet('Lists', 'Exclusive')]
    [string]$FailAfter
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# =================================================================================================
# Constants, run context, logging
# =================================================================================================

function Get-SpConst {
    [pscustomobject]@{
        ClsidLeveler   = '{026BF7A9-ED1A-4C62-ADC6-142802421CBA}'
        ClsidSafetyCap = '{148D0A3C-621B-40F5-B7FD-13E6CB834BCB}'
        ListLeveler    = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13'
        ListSafetyCap  = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15'
        PropName       = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
        PropExclusive  = '{b3f8fa53-0004-438e-9003-51a46e139bfc},3'
        PropDisableFx  = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
        RenderRel      = 'Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
        AudioRel       = 'Microsoft\Windows\CurrentVersion\Audio'
        TestKeyName    = 'DisableProtectedAudioDG'
        DllName        = 'SoundProtection.Apo.dll'
        ScriptName     = 'SoundProtection.Install.ps1'
        ShortcutName   = 'Sound Protection - Undo.lnk'
        JournalName    = 'install-journal.json'
        JournalDone    = 'install-journal.done.json'
        LogName        = 'install-log.txt'
        BackupDir      = 'backup'
        ConfigName     = 'config.bin'
        TelemetryDir   = 'telemetry'
        TelemetryFile  = 'apo.bin'
        TelemetryMagic = [uint32]1297371219        # 0x4D545053 "SPTM"
        TelemetryBytes = 2112
    }
}

# The run context ($script:SpContext) is created by the main block at the bottom of this file. Helpers
# read it through this function so that they also work when only the functions are loaded (unit tests).
function Get-SpContext {
    $v = Get-Variable -Name SpContext -Scope Script -ErrorAction SilentlyContinue
    if ($null -eq $v) { return $null }
    return $v.Value
}

function Write-SpLog {
    param(
        [Parameter(Position = 0)][string]$Message,
        [Parameter(Position = 1)][string]$Level = 'INFO'
    )
    $ctx = Get-SpContext
    $quiet = $false
    if ($null -ne $ctx) { $quiet = [bool]$ctx.Quiet }
    if (-not $quiet) {
        if ($Level -eq 'WARN') { Write-Host ('WARNING: ' + $Message) -ForegroundColor Yellow }
        elseif ($Level -eq 'ERROR') { Write-Host ('ERROR: ' + $Message) -ForegroundColor Red }
        else { Write-Host $Message }
    }
    elseif (($Level -eq 'WARN') -or ($Level -eq 'ERROR')) {
        [Console]::Error.WriteLine($Level + ': ' + $Message)
    }
    if ($null -ne $ctx) {
        if ($ctx.LogPath -and (-not $ctx.WhatIf)) {
            try {
                $line = '{0} [{1}] {2}{3}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message, "`r`n"
                [System.IO.File]::AppendAllText([string]$ctx.LogPath, $line, (New-Object System.Text.UTF8Encoding($false)))
            }
            catch { }
        }
    }
}

# Every change goes through here: it says what is about to happen and tells the caller whether to do it.
# Under -WhatIf it prints "WHATIF: ..." and returns $false, so nothing is ever changed.
function Enter-SpChange {
    param([string]$Description)
    $ctx = Get-SpContext
    if (($null -ne $ctx) -and $ctx.WhatIf) {
        Write-SpLog ('WHATIF: ' + $Description)
        return $false
    }
    Write-SpLog ('DO: ' + $Description)
    return $true
}

# =================================================================================================
# Command lines and external programs
# =================================================================================================

# Quotes one argument the way CommandLineToArgvW expects (spaces, quotes, trailing backslashes).
function ConvertTo-SpQuotedArg {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $esc = $Value -replace '(\\*)"', '$1$1\"'
    $esc = $esc -replace '(\\+)$', '$1$1'
    return '"' + $esc + '"'
}

# Rebuilds the command-line tail for a relaunch from the bound parameters. powershell.exe -File does not
# bind comma lists to string[], so an array goes over as one comma-joined token (the script splits it).
function ConvertTo-SpArgumentString {
    param([System.Collections.IDictionary]$Bound)
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Bound.Keys) {
        $v = $Bound[$key]
        if ($v -is [System.Management.Automation.SwitchParameter]) {
            if ($v.IsPresent) { $parts.Add('-' + [string]$key) }
            continue
        }
        if ($null -eq $v) { continue }
        $text = ''
        if ($v -is [System.Array]) { $text = (@($v | ForEach-Object { [string]$_ }) -join ',') }
        else { $text = [string]$v }
        $parts.Add('-' + [string]$key)
        $parts.Add((ConvertTo-SpQuotedArg -Value $text))
    }
    return ($parts.ToArray() -join ' ')
}

# Runs a program without a window, captures stdout+stderr and the exit code. Never throws on a non-zero exit.
function Invoke-SpNative {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSec = 120
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (@($ArgumentList | ForEach-Object { ConvertTo-SpQuotedArg -Value ([string]$_) }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $so = $p.StandardOutput.ReadToEndAsync()
        $se = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($TimeoutSec * 1000)) {
            try { $p.Kill() } catch { }
            throw "The program did not finish within $TimeoutSec s: $FilePath $($psi.Arguments)"
        }
        $p.WaitForExit()
        [void]$so.Wait(10000)
        [void]$se.Wait(10000)
        $out = ''
        if ($so.IsCompleted) { $out += $so.Result }
        if ($se.IsCompleted) { $out += $se.Result }
        return [pscustomobject]@{ ExitCode = [int]$p.ExitCode; Output = $out.Trim() }
    }
    finally { $p.Dispose() }
}

# =================================================================================================
# Registry primitives (.NET RegistryKey, 64-bit view; values are read and written by name, kind and data)
# =================================================================================================

function Split-SpRegPath {
    param([string]$Path)
    if ($Path -notmatch '^(HKLM|HKCU):\\(.*)$') { throw "Unsupported registry path (need HKLM:\ or HKCU:\): $Path" }
    return [pscustomobject]@{ Hive = $Matches[1]; Sub = $Matches[2].Trim('\') }
}

# Returns an open RegistryKey (the caller closes it) or $null when the key does not exist.
# -Writable asks for the least that can change a value (SET_VALUE and QUERY_VALUE), never for KEY_WRITE: on this PC
# the MMDevices endpoint keys give Administrators SET_VALUE but not CREATE_SUB_KEY, and OpenSubKey(name, $true) and
# CreateSubKey both request CREATE_SUB_KEY, so they would be refused there (and a real Install would roll back).
function Open-SpKey {
    param([string]$Path, [bool]$Writable = $false, [bool]$Create = $false)
    $p = Split-SpRegPath -Path $Path
    $hive = [Microsoft.Win32.RegistryHive]::LocalMachine
    if ($p.Hive -eq 'HKCU') { $hive = [Microsoft.Win32.RegistryHive]::CurrentUser }
    $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        if ($Create) { return $root.CreateSubKey($p.Sub) }
        if ($Writable) {
            $rights = [System.Security.AccessControl.RegistryRights]'SetValue, QueryValues'
            return $root.OpenSubKey($p.Sub, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, $rights)
        }
        return $root.OpenSubKey($p.Sub, $false)
    }
    finally { $root.Close() }
}

# One value as { KeyExists, Exists, Kind, Data }. Data is raw: environment variables are not expanded.
function Get-SpValueState {
    param([string]$KeyPath, [string]$Name)
    $k = Open-SpKey -Path $KeyPath
    if ($null -eq $k) { return [pscustomobject]@{ KeyExists = $false; Exists = $false; Kind = $null; Data = $null } }
    try {
        $actual = $null
        foreach ($n in $k.GetValueNames()) { if ($n -ieq $Name) { $actual = $n; break } }
        if ($null -eq $actual) { return [pscustomobject]@{ KeyExists = $true; Exists = $false; Kind = $null; Data = $null } }
        $kind = $k.GetValueKind($actual)
        $data = $k.GetValue($actual, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = [string]$kind; Data = $data }
    }
    finally { $k.Close() }
}

# Defence in depth on top of Enter-SpChange: no write under -WhatIf, and a rehearsal never leaves HKCU.
function Assert-SpWriteAllowed {
    param([string]$KeyPath)
    $ctx = Get-SpContext
    if ($null -eq $ctx) { return }
    if ($ctx.WhatIf) { throw "Internal error: a registry write was attempted during -WhatIf: $KeyPath" }
    if ($ctx.Rehearsal -and ($KeyPath -notlike 'HKCU:\*')) { throw "A rehearsal never writes outside HKCU: $KeyPath" }
}

# An existing key is opened with the minimal write rights; the key is created only when it is really missing
# and -CreateKey is true (the Audio key may not exist yet).
function Set-SpValue {
    param([string]$KeyPath, [string]$Name, $Data, [string]$Kind, [bool]$CreateKey = $true)
    Assert-SpWriteAllowed -KeyPath $KeyPath
    $k = Open-SpKey -Path $KeyPath -Writable $true
    if (($null -eq $k) -and $CreateKey) { $k = Open-SpKey -Path $KeyPath -Create $true }
    if ($null -eq $k) { throw "Registry key not found (not created): $KeyPath" }
    try { $k.SetValue($Name, $Data, [Microsoft.Win32.RegistryValueKind]$Kind) }
    finally { $k.Close() }
}

function Remove-SpValue {
    param([string]$KeyPath, [string]$Name)
    Assert-SpWriteAllowed -KeyPath $KeyPath
    $k = Open-SpKey -Path $KeyPath -Writable $true
    if ($null -eq $k) { return }
    try { $k.DeleteValue($Name, $false) }
    finally { $k.Close() }
}

function Remove-SpKeyTree {
    param([string]$KeyPath)
    Assert-SpWriteAllowed -KeyPath $KeyPath
    $p = Split-SpRegPath -Path $KeyPath
    $hive = [Microsoft.Win32.RegistryHive]::LocalMachine
    if ($p.Hive -eq 'HKCU') { $hive = [Microsoft.Win32.RegistryHive]::CurrentUser }
    $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
    try { $root.DeleteSubKeyTree($p.Sub, $false) }
    finally { $root.Close() }
}

# Deletes a key only if it has no values and no sub-keys (used for the Audio key when this run created it).
function Remove-SpKeyIfEmpty {
    param([string]$KeyPath)
    $k = Open-SpKey -Path $KeyPath
    if ($null -eq $k) { return $false }
    $empty = $false
    try { $empty = (($k.GetValueNames().Length -eq 0) -and ($k.GetSubKeyNames().Length -eq 0)) }
    finally { $k.Close() }
    if (-not $empty) { return $false }
    Remove-SpKeyTree -KeyPath $KeyPath
    return $true
}

# =================================================================================================
# Journal data: registry value <-> JSON-safe value (kind-exact)
# =================================================================================================

function ConvertTo-SpHex {
    param([byte[]]$Bytes)
    if (($null -eq $Bytes) -or ($Bytes.Length -eq 0)) { return '' }
    return ([BitConverter]::ToString($Bytes).Replace('-', '').ToLowerInvariant())
}

function ConvertFrom-SpHex {
    param([string]$Hex)
    if ([string]::IsNullOrEmpty($Hex)) { return ,([byte[]]@()) }
    if (($Hex.Length % 2) -ne 0) { throw "Odd-length hex string in the journal: $Hex" }
    $b = New-Object byte[] ($Hex.Length / 2)
    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($Hex.Substring(2 * $i, 2), 16) }
    return ,$b
}

# Registry value -> value that survives ConvertTo-Json/ConvertFrom-Json unchanged.
function ConvertTo-SpJournalData {
    param([string]$Kind, $Data)
    switch ($Kind) {
        'String'       { return [string]$Data }
        'ExpandString' { return [string]$Data }
        'MultiString'  {
            $list = New-Object System.Collections.Generic.List[string]
            foreach ($s in @($Data)) { if ($null -ne $s) { $list.Add([string]$s) } }
            return ,($list.ToArray())
        }
        'DWord'        { return [int]$Data }
        'QWord'        { return ([int64]$Data).ToString([System.Globalization.CultureInfo]::InvariantCulture) }
        'Binary'       { return (ConvertTo-SpHex -Bytes ([byte[]]$Data)) }
        'None'         { return (ConvertTo-SpHex -Bytes ([byte[]]$Data)) }
        default        { throw "Unsupported registry value kind '$Kind' (cannot journal it)" }
    }
}

# The reverse; the result can be handed straight to RegistryKey.SetValue with the same kind.
function ConvertFrom-SpJournalData {
    param([string]$Kind, $Data)
    switch ($Kind) {
        'String'       { if ($null -eq $Data) { return '' } else { return [string]$Data } }
        'ExpandString' { if ($null -eq $Data) { return '' } else { return [string]$Data } }
        'MultiString'  {
            $list = New-Object System.Collections.Generic.List[string]
            if ($null -ne $Data) { foreach ($s in @($Data)) { $list.Add([string]$s) } }
            return ,($list.ToArray())
        }
        'DWord'        { return [int]$Data }
        'QWord'        { return [int64]::Parse([string]$Data, [System.Globalization.CultureInfo]::InvariantCulture) }
        'Binary'       { return ,(ConvertFrom-SpHex -Hex ([string]$Data)) }
        'None'         { return ,(ConvertFrom-SpHex -Hex ([string]$Data)) }
        default        { throw "Unsupported registry value kind '$Kind' in the journal" }
    }
}

# =================================================================================================
# Default config, verify sound, telemetry
# =================================================================================================

# Golden 1 from native\apo\SpShared.h: seq 1, enabled, cap -20 dB, margin 12 dB, FNV-1a checksum.
function Get-SpDefaultConfigBytes {
    return ,([byte[]](0x53, 0x50, 0x43, 0x46, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                      0x00, 0x00, 0xA0, 0xC1, 0x00, 0x00, 0x40, 0x41, 0x00, 0x00, 0x00, 0x00, 0xEA, 0xFA, 0xCF, 0x1E))
}

# 2 s, 48 kHz, 16-bit stereo, 1 kHz sine at -40 dBFS (amplitude 328) with 20 ms linear fades.
function New-SpVerifyWavBytes {
    $rate = 48000
    $frames = $rate * 2
    $amp = 328.0
    $fade = 960
    $dataBytes = $frames * 4
    $buf = New-Object byte[] (44 + $dataBytes)
    $ascii = [System.Text.Encoding]::ASCII
    [Array]::Copy($ascii.GetBytes('RIFF'), 0, $buf, 0, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32](36 + $dataBytes)), 0, $buf, 4, 4)
    [Array]::Copy($ascii.GetBytes('WAVE'), 0, $buf, 8, 4)
    [Array]::Copy($ascii.GetBytes('fmt '), 0, $buf, 12, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]16), 0, $buf, 16, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint16]1), 0, $buf, 20, 2)
    [Array]::Copy([BitConverter]::GetBytes([uint16]2), 0, $buf, 22, 2)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$rate), 0, $buf, 24, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]($rate * 4)), 0, $buf, 28, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint16]4), 0, $buf, 32, 2)
    [Array]::Copy([BitConverter]::GetBytes([uint16]16), 0, $buf, 34, 2)
    [Array]::Copy($ascii.GetBytes('data'), 0, $buf, 36, 4)
    [Array]::Copy([BitConverter]::GetBytes([uint32]$dataBytes), 0, $buf, 40, 4)
    $w = 2.0 * [Math]::PI * 1000.0 / $rate
    for ($i = 0; $i -lt $frames; $i++) {
        $gain = 1.0
        if ($i -lt $fade) { $gain = $i / $fade }
        $tail = $frames - 1 - $i
        if ($tail -lt $fade) {
            $g2 = $tail / $fade
            if ($g2 -lt $gain) { $gain = $g2 }
        }
        $s = [int][Math]::Round($amp * [Math]::Sin($w * $i) * $gain)
        $lo = [byte]($s -band 0xFF)
        $hi = [byte](($s -shr 8) -band 0xFF)
        $o = 44 + 4 * $i
        $buf[$o] = $lo
        $buf[$o + 1] = $hi
        $buf[$o + 2] = $lo
        $buf[$o + 3] = $hi
    }
    return ,$buf
}

# Parses telemetry\apo.bin (layout in SpShared.h). $null = too short to be telemetry.
function ConvertFrom-SpTelemetryBytes {
    param([byte[]]$Bytes, [int]$Length)
    if (($null -eq $Bytes) -or ($Length -lt 64) -or ($Bytes.Length -lt 64)) { return $null }
    $c = Get-SpConst
    $magic = [BitConverter]::ToUInt32($Bytes, 0)
    $version = [BitConverter]::ToUInt32($Bytes, 4)
    $slotCount = [BitConverter]::ToUInt32($Bytes, 8)
    $slotSize = [BitConverter]::ToUInt32($Bytes, 12)
    if (($magic -ne $c.TelemetryMagic) -or ($version -ne 1) -or ($slotSize -ne 64)) {
        return [pscustomobject]@{ MagicOk = $false; Heartbeat = [uint64]0; HeartbeatFileTime = [int64]0; Slots = @() }
    }
    $room = [int][Math]::Floor(($Length - 64) / 64)
    $count = [Math]::Min([Math]::Min([int]$slotCount, 32), $room)
    $slots = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $count; $i++) {
        $o = 64 + 64 * $i
        [void]$slots.Add([pscustomobject]@{
            Kind       = [BitConverter]::ToUInt32($Bytes, $o)
            InstanceId = [BitConverter]::ToUInt32($Bytes, $o + 4)
            Frames     = [BitConverter]::ToUInt64($Bytes, $o + 24)
        })
    }
    return [pscustomobject]@{
        MagicOk           = $true
        Heartbeat         = [BitConverter]::ToUInt64($Bytes, 24)
        HeartbeatFileTime = [BitConverter]::ToInt64($Bytes, 32)
        Slots             = $slots.ToArray()
    }
}

# What is still missing for "running", given the telemetry samples read so far (oldest first).
# Returns plain strings (nothing at all when everything is in place).
function Get-SpVerifyMissing {
    param([object[]]$Samples = @())
    $missing = New-Object System.Collections.Generic.List[string]
    $present = @($Samples | Where-Object { $null -ne $_ })
    if ($present.Count -eq 0) {
        $missing.Add('telemetry file apo.bin not found or unreadable')
        return $missing.ToArray()
    }
    $good = @($present | Where-Object { $_.MagicOk })
    if ($good.Count -eq 0) {
        $missing.Add('telemetry file apo.bin has the wrong magic')
        return $missing.ToArray()
    }
    $first = $good[0]
    $last = $good[$good.Count - 1]
    if (-not ($last.Heartbeat -gt $first.Heartbeat)) { $missing.Add('heartbeat is not increasing') }
    $lev = @($last.Slots | Where-Object { ($_.Kind -eq 1) -and ($_.Frames -gt 0) })
    if ($lev.Count -eq 0) { $missing.Add('no Leveler slot with frames processed') }
    $cap = @($last.Slots | Where-Object { ($_.Kind -eq 2) -and ($_.Frames -gt 0) })
    if ($cap.Count -eq 0) { $missing.Add('no Safety Cap slot with frames processed') }
    return $missing.ToArray()
}

# Reads <DataDir>\telemetry\apo.bin while the plug-in has it mapped (FileShare ReadWrite). $null if unavailable.
function Read-SpTelemetry {
    param([string]$DataDir)
    $c = Get-SpConst
    $path = Join-Path (Join-Path $DataDir $c.TelemetryDir) $c.TelemetryFile
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $buf = New-Object byte[] $c.TelemetryBytes
            $n = 0
            while ($n -lt $buf.Length) {
                $r = $fs.Read($buf, $n, $buf.Length - $n)
                if ($r -le 0) { break }
                $n += $r
            }
            return (ConvertFrom-SpTelemetryBytes -Bytes $buf -Length $n)
        }
        finally { $fs.Dispose() }
    }
    catch { return $null }
}

# =================================================================================================
# Journal: the original value of everything a run touches (first originals are kept forever)
# =================================================================================================

# Optional property of a parsed JSON object (scalar values only).
function Get-SpOpt {
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $Default }
    return $p.Value
}

function New-SpJournal {
    param([string]$Base)
    return [pscustomobject]@{
        version      = 1
        created      = (Get-Date).ToUniversalTime().ToString('o')
        registryBase = $Base
        records      = (New-Object System.Collections.ArrayList)
    }
}

# $null when there is no journal. A damaged journal is an error: it is never silently replaced.
function Read-SpJournal {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $raw = $null
    try { $raw = $text | ConvertFrom-Json }
    catch { throw "The journal '$Path' cannot be read ($($_.Exception.Message)). It was left untouched; the .reg backups in the backup folder next to it hold the originals." }
    if (($null -eq $raw) -or ($null -eq $raw.PSObject.Properties['records'])) { throw "The journal '$Path' has no records list. It was left untouched." }
    $list = New-Object System.Collections.ArrayList
    foreach ($r in @($raw.records)) { if ($null -ne $r) { [void]$list.Add($r) } }
    return [pscustomobject]@{
        version      = (Get-SpOpt $raw 'version' 1)
        created      = (Get-SpOpt $raw 'created' '')
        registryBase = (Get-SpOpt $raw 'registryBase' '')
        records      = $list
    }
}

# Written through a temp file in the same folder, so a crash never leaves half a journal.
function Save-SpJournal {
    param($Journal, [string]$Path)
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path))
    $obj = [pscustomobject]@{
        version      = $Journal.version
        created      = $Journal.created
        registryBase = $Journal.registryBase
        records      = $Journal.records.ToArray()
    }
    $json = ConvertTo-Json -InputObject $obj -Depth 8
    $tmp = $Path + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    # [NullString]::Value, not $null: PowerShell turns a plain $null into "" for a string argument, and Replace rejects "".
    if (Test-Path -LiteralPath $Path -PathType Leaf) { [System.IO.File]::Replace($tmp, $Path, [NullString]::Value) }
    else { [System.IO.File]::Move($tmp, $Path) }
}

# Adds a record only for a (key, name) the journal does not have yet; returns how many were added.
# For the two effect lists the recorded original never contains our own CLSID (it cannot have been
# there before us), so a lost journal followed by a re-install still records the true original.
function Add-SpJournalRecords {
    param($Journal, [object[]]$Intents)
    $added = 0
    foreach ($it in $Intents) {
        $known = $false
        foreach ($r in $Journal.records) {
            if (([string]$r.key -ieq [string]$it.Key) -and ([string]$r.name -ieq [string]$it.Name)) { $known = $true; break }
        }
        if ($known) { continue }
        $st = Get-SpValueState -KeyPath $it.Key -Name $it.Name
        $kind = $null
        $data = $null
        if ($st.Exists) {
            $kind = [string]$st.Kind
            $value = $st.Data
            if (($it.Op -eq 'AppendClsid') -and ($kind -eq 'MultiString')) {
                $value = [string[]]@(@($value) | Where-Object { $_ -ine $it.Clsid })
            }
            $data = ConvertTo-SpJournalData -Kind $kind -Data $value
        }
        [void]$Journal.records.Add([pscustomobject]@{
            key        = [string]$it.Key
            name       = [string]$it.Name
            keyExisted = [bool]$st.KeyExists
            existed    = [bool]$st.Exists
            kind       = $kind
            data       = $data
        })
        $added++
    }
    return $added
}

# =================================================================================================
# Endpoints and the registry changes (the "intents")
# =================================================================================================

# Render endpoints whose friendly name equals one of $Names (case-insensitive) and that have both
# composite lists (,13 and ,15). Found by name every time: the GUID can change after a driver reinstall.
function Find-SpEndpoints {
    param([string]$Base, [string[]]$Names)
    $c = Get-SpConst
    $renderPath = $Base + '\' + $c.RenderRel
    $render = Open-SpKey -Path $renderPath
    if ($null -eq $render) { return }
    try {
        foreach ($id in $render.GetSubKeyNames()) {
            $ep = $render.OpenSubKey($id)
            $fx = $render.OpenSubKey($id + '\FxProperties')
            $pr = $render.OpenSubKey($id + '\Properties')
            try {
                if (($null -eq $fx) -or ($null -eq $pr)) { continue }
                $friendly = $pr.GetValue($c.PropName)
                if ($friendly -isnot [string]) { continue }
                $wanted = $false
                foreach ($n in $Names) { if ($friendly -ieq $n) { $wanted = $true; break } }
                if (-not $wanted) { continue }
                $fxNames = $fx.GetValueNames()
                if (($fxNames -notcontains $c.ListLeveler) -or ($fxNames -notcontains $c.ListSafetyCap)) { continue }
                $state = $null
                if ($null -ne $ep) { $state = $ep.GetValue('DeviceState') }
                [pscustomobject]@{
                    Id        = $id
                    Name      = [string]$friendly
                    State     = $state
                    Path      = $renderPath + '\' + $id
                    FxPath    = $renderPath + '\' + $id + '\FxProperties'
                    PropsPath = $renderPath + '\' + $id + '\Properties'
                }
            }
            finally {
                if ($null -ne $ep) { $ep.Close() }
                if ($null -ne $fx) { $fx.Close() }
                if ($null -ne $pr) { $pr.Close() }
            }
        }
    }
    finally { $render.Close() }
}

# Everything step 7 changes, in the order it is changed: test key, the lists, exclusive mode.
function Get-SpIntents {
    param([string]$Base, [object[]]$Endpoints)
    $c = Get-SpConst
    $list = New-Object System.Collections.ArrayList
    [void]$list.Add([pscustomobject]@{ Op = 'SetDword'; Group = 'TestKey'; Key = ($Base + '\' + $c.AudioRel); Name = $c.TestKeyName; Value = 1; Clsid = $null; Label = 'test key' })
    foreach ($ep in $Endpoints) {
        [void]$list.Add([pscustomobject]@{ Op = 'AppendClsid'; Group = 'Lists'; Key = $ep.FxPath; Name = $c.ListLeveler; Value = $null; Clsid = $c.ClsidLeveler; Label = 'Leveler' })
        [void]$list.Add([pscustomobject]@{ Op = 'AppendClsid'; Group = 'Lists'; Key = $ep.FxPath; Name = $c.ListSafetyCap; Value = $null; Clsid = $c.ClsidSafetyCap; Label = 'Safety Cap' })
    }
    foreach ($ep in $Endpoints) {
        [void]$list.Add([pscustomobject]@{ Op = 'SetDword'; Group = 'Exclusive'; Key = $ep.PropsPath; Name = $c.PropExclusive; Value = 0; Clsid = $null; Label = 'exclusive mode off' })
    }
    return $list.ToArray()
}

# One undo record, the same shape as a journal record, from the state a value had just before this run wrote it.
# A failed Repair puts these back (and only these); it never goes back to the install journal.
function Add-SpUndoRecord {
    param([System.Collections.ArrayList]$UndoLog, [string]$Key, [string]$Name, $State)
    $kind = $null
    $data = $null
    if ($State.Exists) {
        $kind = [string]$State.Kind
        $data = ConvertTo-SpJournalData -Kind $kind -Data $State.Data
    }
    [void]$UndoLog.Add([pscustomobject]@{
        key        = [string]$Key
        name       = [string]$Name
        keyExisted = [bool]$State.KeyExists
        existed    = [bool]$State.Exists
        kind       = $kind
        data       = $data
    })
}

# Makes one change. Idempotent: if the value is already as wanted, nothing is written.
# With -UndoLog (a Repair), the value as it was just before the write is noted there once the write has gone through.
function Invoke-SpIntent {
    param($Intent, [System.Collections.ArrayList]$UndoLog = $null)
    $st = Get-SpValueState -KeyPath $Intent.Key -Name $Intent.Name
    if ($Intent.Op -eq 'AppendClsid') {
        if (-not $st.KeyExists) { throw "Registry key not found: $($Intent.Key)" }
        if ($st.Exists -and ($st.Kind -ne 'MultiString')) {
            throw "Unexpected registry kind $($st.Kind) for '$($Intent.Name)' in $($Intent.Key) (expected MultiString); left untouched."
        }
        $current = @()
        if ($st.Exists) { $current = @($st.Data) }
        $already = @($current | Where-Object { $_ -ieq $Intent.Clsid })
        if ($already.Count -gt 0) {
            Write-SpLog ('{0}: {1} is already in ''{2}''; nothing to add.' -f $Intent.Label, $Intent.Clsid, $Intent.Name)
            return
        }
        $desc = 'append {0} ({1}) to the end of ''{2}'' (REG_MULTI_SZ) in {3}' -f $Intent.Clsid, $Intent.Label, $Intent.Name, $Intent.Key
        if (Enter-SpChange $desc) {
            $new = [string[]]($current + $Intent.Clsid)
            Set-SpValue -KeyPath $Intent.Key -Name $Intent.Name -Data $new -Kind 'MultiString' -CreateKey $false
            if ($null -ne $UndoLog) { Add-SpUndoRecord -UndoLog $UndoLog -Key $Intent.Key -Name $Intent.Name -State $st }
        }
        return
    }
    $want = [int]$Intent.Value
    if ($st.Exists -and ($st.Kind -eq 'DWord') -and ([int]$st.Data -eq $want)) {
        Write-SpLog ('{0}: ''{1}'' is already DWORD {2}; nothing to change.' -f $Intent.Label, $Intent.Name, $want)
        return
    }
    $was = 'not set'
    if ($st.Exists) { $was = '{0} {1}' -f $st.Kind, (ConvertTo-SpJournalData -Kind $st.Kind -Data $st.Data) }
    $desc2 = 'set ''{0}'' = DWORD {1} ({2}) in {3} (was: {4})' -f $Intent.Name, $want, $Intent.Label, $Intent.Key, $was
    if (Enter-SpChange $desc2) {
        Set-SpValue -KeyPath $Intent.Key -Name $Intent.Name -Data $want -Kind 'DWord' -CreateKey $true
        if ($null -ne $UndoLog) { Add-SpUndoRecord -UndoLog $UndoLog -Key $Intent.Key -Name $Intent.Name -State $st }
    }
}

# The journal is a plain file that an elevated Uninstall acts on. It sits in the install folder, which only
# administrators can write (ruling 11), so a standard user cannot plant records any more; this check stays as a
# second layer. A record is honoured only for a key this installer itself records:
# an endpoint's FxProperties or Properties key under Render, or the Audio key, below the current base.
function Test-SpJournalKeyAllowed {
    param([string]$Base, [string]$Key)
    if ([string]::IsNullOrWhiteSpace($Key) -or [string]::IsNullOrWhiteSpace($Base)) { return $false }
    $c = Get-SpConst
    if ($Key -ieq ($Base + '\' + $c.AudioRel)) { return $true }
    $render = $Base + '\' + $c.RenderRel + '\'
    if (-not $Key.StartsWith($render, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    return [bool]($Key.Substring($render.Length) -match '^[^\\]+\\(FxProperties|Properties)$')
}

# Puts every journalled value back (original kind and data), or deletes it if it did not exist.
# Newest record first. Best effort: one failure is logged and counted, the rest still run.
# Keys that vanished since (a driver reinstall changed the endpoint GUID) are skipped, never re-created.
# A record for a key this installer never changes is refused (counted as a problem, nothing written).
function Restore-SpJournalRecords {
    param($Journal, [string]$Base)
    $problems = 0
    $records = @($Journal.records)
    for ($i = $records.Count - 1; $i -ge 0; $i--) {
        $r = $records[$i]
        $rKey = [string](Get-SpOpt $r 'key' '')
        if (-not (Test-SpJournalKeyAllowed -Base $Base -Key $rKey)) {
            $problems++
            Write-SpLog ('refused a journal record: ''{0}'' in ''{1}'' is not a key this installer changes; nothing was written there.' -f (Get-SpOpt $r 'name' ''), $rKey) 'ERROR'
            continue
        }
        try {
            $st = Get-SpValueState -KeyPath ([string]$r.key) -Name ([string]$r.name)
            if (-not $st.KeyExists) {
                Write-SpLog ('skip ''{0}'': the key no longer exists ({1})' -f $r.name, $r.key)
                continue
            }
            if ([bool](Get-SpOpt $r 'existed' $false)) {
                $kind = [string]$r.kind
                $data = ConvertFrom-SpJournalData -Kind $kind -Data $r.data
                if (Enter-SpChange ('restore ''{0}'' to its original {1} value in {2}' -f $r.name, $kind, $r.key)) {
                    Set-SpValue -KeyPath ([string]$r.key) -Name ([string]$r.name) -Data $data -Kind $kind -CreateKey $false
                }
            }
            elseif ($st.Exists) {
                if (Enter-SpChange ('delete ''{0}'' from {1} (it did not exist before)' -f $r.name, $r.key)) {
                    Remove-SpValue -KeyPath ([string]$r.key) -Name ([string]$r.name)
                }
            }
        }
        catch {
            $problems++
            Write-SpLog ('could not restore ''{0}'' in {1}: {2}' -f $r.name, $r.key, $_.Exception.Message) 'ERROR'
        }
    }
    $seen = @{}
    foreach ($r in $records) {
        if ([bool](Get-SpOpt $r 'keyExisted' $true)) { continue }
        $k = [string](Get-SpOpt $r 'key' '')
        if (-not (Test-SpJournalKeyAllowed -Base $Base -Key $k)) { continue }   # refused and counted above
        if ($seen.ContainsKey($k.ToLowerInvariant())) { continue }
        $seen[$k.ToLowerInvariant()] = $true
        try {
            if (Enter-SpChange ('remove the key {0} if it is empty (this install created it)' -f $k)) {
                if (Remove-SpKeyIfEmpty -KeyPath $k) { Write-SpLog ('removed the empty key ' + $k) }
            }
        }
        catch {
            $problems++
            Write-SpLog ('could not remove the key {0}: {1}' -f $k, $_.Exception.Message) 'ERROR'
        }
    }
    return $problems
}

# No journal: take our two CLSIDs out of the lists on every render endpoint. Nothing else is touched.
function Remove-SpOurClsidsFromLists {
    param([string]$Base)
    $c = Get-SpConst
    $problems = 0
    $renderPath = $Base + '\' + $c.RenderRel
    $render = Open-SpKey -Path $renderPath
    if ($null -eq $render) { return 0 }
    $ids = @($render.GetSubKeyNames())
    $render.Close()
    foreach ($id in $ids) {
        $fxPath = $renderPath + '\' + $id + '\FxProperties'
        foreach ($pair in @(@($c.ListLeveler, $c.ClsidLeveler), @($c.ListSafetyCap, $c.ClsidSafetyCap))) {
            try {
                $st = Get-SpValueState -KeyPath $fxPath -Name $pair[0]
                if ((-not $st.Exists) -or ($st.Kind -ne 'MultiString')) { continue }
                $all = @($st.Data)
                $keep = @($all | Where-Object { $_ -ine $pair[1] })
                if ($keep.Count -eq $all.Count) { continue }
                if (Enter-SpChange ('remove {0} from ''{1}'' in {2}' -f $pair[1], $pair[0], $fxPath)) {
                    Set-SpValue -KeyPath $fxPath -Name $pair[0] -Data ([string[]]$keep) -Kind 'MultiString' -CreateKey $false
                }
            }
            catch {
                $problems++
                Write-SpLog ('could not clean {0} in {1}: {2}' -f $pair[0], $fxPath, $_.Exception.Message) 'ERROR'
            }
        }
    }
    return $problems
}

# =================================================================================================
# Audio services (real runs only)
# =================================================================================================

# Names of the services that are running now and depend (directly or not) on AudioEndpointBuilder.
function Get-SpRunningAudioDependents {
    $found = New-Object System.Collections.Generic.List[string]
    $seen = @{ 'audioendpointbuilder' = $true }
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue((Get-Service -Name 'AudioEndpointBuilder'))
    while ($queue.Count -gt 0) {
        $svc = $queue.Dequeue()
        foreach ($d in $svc.DependentServices) {
            $key = $d.Name.ToLowerInvariant()
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            $queue.Enqueue($d)
            if ($d.Status -eq 'Running') { $found.Add($d.Name) }
        }
    }
    return $found.ToArray()
}

# Stops AudioEndpointBuilder; -Force stops the services that depend on it first.
function Stop-SpAudioServices {
    $svc = Get-Service -Name 'AudioEndpointBuilder'
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name 'AudioEndpointBuilder' -Force
        $svc.Refresh()
        $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
    }
}

# AudioEndpointBuilder, then Audiosrv, then the recorded dependents; each waited for up to 20 s, and every one gets its
# start attempt, also after a failure. Only AudioEndpointBuilder and Audiosrv are needed for sound and for the plug-in, so
# the result is $true when those two are running. Any other dependent (midisrv, a vendor audio service) that does not
# reach Running gets one WARN line and does not change the result: it is no reason to fail or roll back a run.
function Start-SpAudioServices {
    param([string[]]$Dependents = @())
    $core = @('AudioEndpointBuilder', 'Audiosrv')
    $order = New-Object System.Collections.Generic.List[string]
    foreach ($c in $core) { $order.Add($c) }
    foreach ($d in $Dependents) {
        if ($core -notcontains $d) { $order.Add($d) }
    }
    $coreFailed = 0
    foreach ($name in $order) {
        try {
            $svc = Get-Service -Name $name
            if ($svc.Status -ne 'Running') { Start-Service -Name $name }
            $svc.Refresh()
            $svc.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(20))
            Write-SpLog ('service {0} is running' -f $name)
        }
        catch {
            if ($core -contains $name) {
                $coreFailed++
                Write-SpLog ('service {0} did not reach Running: {1}' -f $name, $_.Exception.Message) 'ERROR'
            }
            else {
                Write-SpLog ('service {0} did not reach Running within 20 s; sound does not need it; start it from services.msc or restart the PC' -f $name) 'WARN'
                Write-SpLog ('service {0}: {1}' -f $name, $_.Exception.Message)
            }
        }
    }
    return ($coreFailed -eq 0)
}

# =================================================================================================
# Files, ACLs, shortcut, backup, COM registration (real runs only)
# =================================================================================================

# $true when the path exists and is a reparse point (a junction or a symbolic link, a dangling one too). Get-Item -Force
# looks at the link itself and does not follow it. A path that does not exist is not one; any other failure to look at
# it is an error, so a caller that is guarding something refuses instead of going on.
function Test-SpReparsePoint {
    param([string]$Path)
    $item = $null
    try { $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop }
    catch {
        if ($_.CategoryInfo.Category -eq [System.Management.Automation.ErrorCategory]::ObjectNotFound) { return $false }
        throw
    }
    return [bool](($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Copy-SpFileWithRetry {
    param([string]$Source, [string]$Destination)
    if ([System.IO.Path]::GetFullPath($Source) -ieq [System.IO.Path]::GetFullPath($Destination)) { return }
    for ($i = 1; $i -le 10; $i++) {
        try { Copy-Item -LiteralPath $Source -Destination $Destination -Force; return }
        catch {
            if ($i -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

# Copies the DLL and the script itself into the install folder (a copy onto itself is skipped).
function Copy-SpInstallFiles {
    param([string]$DllSource, [string]$ScriptSource, [string]$InstallDir)
    $c = Get-SpConst
    [void][System.IO.Directory]::CreateDirectory($InstallDir)
    Copy-SpFileWithRetry -Source $DllSource -Destination (Join-Path $InstallDir $c.DllName)
    Copy-SpFileWithRetry -Source $ScriptSource -Destination (Join-Path $InstallDir $c.ScriptName)
}

# Deletes our two files, then the folder if nothing else is in it (never a recursive delete of a path
# the caller supplied). The install journal, the .reg backups and the log live in this folder as well (only
# administrators can write here, unlike the data folder): they are kept on purpose, and the log says so.
# Returns $true when the folder is gone at the end.
function Remove-SpInstallFiles {
    param([string]$InstallDir)
    $c = Get-SpConst
    if (-not (Test-Path -LiteralPath $InstallDir -PathType Container)) { return $true }
    foreach ($n in @($c.DllName, $c.ScriptName)) {
        $p = Join-Path $InstallDir $n
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { continue }
        for ($i = 1; $i -le 10; $i++) {
            try { Remove-Item -LiteralPath $p -Force; break }
            catch {
                if ($i -eq 10) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
    }
    $left = @(Get-ChildItem -LiteralPath $InstallDir -Force)
    if ($left.Count -eq 0) {
        Remove-Item -LiteralPath $InstallDir -Force
        return $true
    }
    $ours = @($c.JournalName, $c.JournalDone, $c.LogName, $c.BackupDir)
    $foreign = @($left | Where-Object { $ours -notcontains $_.Name })
    if ($foreign.Count -eq 0) {
        Write-SpLog ('the folder {0} was kept on purpose: it still holds the install journal, the registry backups and the log ({1})' -f $InstallDir, (@($left | ForEach-Object { $_.Name }) -join ', '))
    }
    else {
        Write-SpLog ('the folder {0} still holds other files and was left in place: {1}' -f $InstallDir, (@($foreign | ForEach-Object { $_.Name }) -join ', ')) 'WARN'
    }
    return $false
}

# Makes Administrators the owner of the data folder. A standard user can create a folder under ProgramData before the
# installer does and would stay its owner (an owner can always change the permissions), so the owner is set first.
# Needs an elevated token: a filtered one gets error 1307, which is fine because only the elevated run gets here.
function Set-SpDataOwner {
    param([string]$DataDir)
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    $r = Invoke-SpNative -FilePath $icacls -ArgumentList @($DataDir, '/setowner', '*S-1-5-32-544')
    if ($r.ExitCode -ne 0) { throw "icacls could not make Administrators the owner of '$DataDir' (exit code $($r.ExitCode)): $($r.Output)" }
}

# The two icacls grant commands from the plan, run on the data folder and its telemetry folder.
function Set-SpDataGrants {
    param([string]$DataDir)
    $c = Get-SpConst
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    $r1 = Invoke-SpNative -FilePath $icacls -ArgumentList @(
        $DataDir, '/inheritance:r', '/grant:r',
        '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-32-545:(OI)(CI)M',
        '*S-1-5-19:(OI)(CI)RX', '*S-1-15-2-1:(OI)(CI)RX', '*S-1-15-2-2:(OI)(CI)RX')
    if ($r1.ExitCode -ne 0) { throw "icacls failed on '$DataDir' (exit code $($r1.ExitCode)): $($r1.Output)" }
    $r2 = Invoke-SpNative -FilePath $icacls -ArgumentList @(
        (Join-Path $DataDir $c.TelemetryDir), '/grant',
        '*S-1-5-19:(OI)(CI)M', '*S-1-15-2-1:(OI)(CI)M', '*S-1-15-2-2:(OI)(CI)M')
    if ($r2.ExitCode -ne 0) { throw "icacls failed on the telemetry folder in '$DataDir' (exit code $($r2.ExitCode)): $($r2.Output)" }
}

# The owner first, then the grants. A refused owner change stops here: nothing is granted on a folder somebody else may own.
function Set-SpDataAcl {
    param([string]$DataDir)
    Set-SpDataOwner -DataDir $DataDir
    Set-SpDataGrants -DataDir $DataDir
}

# config.bin = golden 1, only if it is missing. Returns $true when it was written.
function Write-SpDefaultConfigIfMissing {
    param([string]$DataDir)
    $c = Get-SpConst
    $p = Join-Path $DataDir $c.ConfigName
    if (Test-Path -LiteralPath $p -PathType Leaf) { return $false }
    [System.IO.File]::WriteAllBytes($p, (Get-SpDefaultConfigBytes))
    return $true
}

function New-SpUndoShortcut {
    param([string]$LinkPath, [string]$InstallDir)
    $c = Get-SpConst
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $script = Join-Path $InstallDir $c.ScriptName
    $ws = New-Object -ComObject WScript.Shell
    try {
        $s = $ws.CreateShortcut($LinkPath)
        try {
            $s.TargetPath = $psExe
            $s.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $script + '" -Action Uninstall'
            $s.WorkingDirectory = $env:SystemRoot
            $s.Description = 'Undo Sound Protection: restores the original sound settings and removes the plug-in'
            $s.Save()
        }
        finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($s) }
    }
    finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($ws) }
}

# reg export of each key into <Directory>\<last path segment>.reg. A key that cannot be exported is an error.
function Export-SpRegBackup {
    param([string[]]$KeyPaths, [string]$Directory)
    [void][System.IO.Directory]::CreateDirectory($Directory)
    $reg = Join-Path $env:SystemRoot 'System32\reg.exe'
    foreach ($kp in $KeyPaths) {
        $native = $kp -replace '^(HKLM|HKCU):\\', '$1\'
        $leaf = ($kp.TrimEnd('\') -split '\\')[-1]
        $file = Join-Path $Directory ($leaf + '.reg')
        $n = 1
        while (Test-Path -LiteralPath $file) {
            $n++
            $file = Join-Path $Directory ('{0}-{1}.reg' -f $leaf, $n)
        }
        $r = Invoke-SpNative -FilePath $reg -ArgumentList @('export', $native, $file, '/y')
        if ($r.ExitCode -ne 0) { throw "reg export of '$kp' failed (exit code $($r.ExitCode)): $($r.Output)" }
        Write-SpLog ('backed up {0} to {1}' -f $kp, $file)
    }
}

function Test-SpKeyExists {
    param([string]$KeyPath)
    $k = Open-SpKey -Path $KeyPath
    if ($null -eq $k) { return $false }
    $k.Close()
    return $true
}

# Problems with the COM registration (empty = fine): each of our two CLSIDs must have an
# InprocServer32 default value equal to the installed DLL path (case-insensitive).
function Test-SpComRegistration {
    param([string]$Base, [string]$DllPath)
    $c = Get-SpConst
    foreach ($clsid in @($c.ClsidLeveler, $c.ClsidSafetyCap)) {
        $kp = $Base + '\Classes\CLSID\' + $clsid + '\InprocServer32'
        $st = Get-SpValueState -KeyPath $kp -Name ''
        if (-not $st.KeyExists) { "$clsid : the COM key is missing ($kp)"; continue }
        if (-not $st.Exists) { "$clsid : InprocServer32 has no default value ($kp)"; continue }
        if (([string]$st.Data) -ine $DllPath) { "$clsid : InprocServer32 points to '$($st.Data)' instead of '$DllPath'" }
    }
}

# regsvr32 /s through a process wrapper (it is a GUI program: '&' would not wait for it or give its exit code).
function Register-SpDll {
    param([string]$DllPath)
    $regsvr = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
    $r = Invoke-SpNative -FilePath $regsvr -ArgumentList @('/s', $DllPath)
    if ($r.ExitCode -ne 0) { throw "regsvr32 /s failed for '$DllPath' (exit code $($r.ExitCode)). $($r.Output)" }
}

function Unregister-SpDll {
    param([string]$DllPath)
    if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) { Write-SpLog ('regsvr32 /u skipped: {0} is not there' -f $DllPath); return }
    $regsvr = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
    $r = Invoke-SpNative -FilePath $regsvr -ArgumentList @('/u', '/s', $DllPath)
    if ($r.ExitCode -ne 0) { Write-SpLog ('regsvr32 /u returned exit code {0}; the keys are removed directly next.' -f $r.ExitCode) 'WARN' }
}

# Whatever regsvr32 /u left behind of our two classes.
function Remove-SpComRegistration {
    param([string]$Base)
    $c = Get-SpConst
    foreach ($clsid in @($c.ClsidLeveler, $c.ClsidSafetyCap)) {
        foreach ($rel in @('Classes\CLSID\', 'Classes\AudioEngine\AudioProcessingObjects\')) {
            $kp = $Base + '\' + $rel + $clsid
            if (Test-SpKeyExists -KeyPath $kp) {
                if (Enter-SpChange ('delete the key {0}' -f $kp)) { Remove-SpKeyTree -KeyPath $kp }
            }
        }
    }
}

# =================================================================================================
# Verify and Status
# =================================================================================================

# Plays the quiet test tone and watches telemetry\apo.bin for up to $TimeoutSec seconds.
# Returns { Passed, Missing }.
function Invoke-SpVerify {
    param([string]$DataDir, [int]$TimeoutSec = 6)
    # The audio engine needs a moment after its services are back before it can play anything.
    Start-Sleep -Milliseconds 2000
    $wav = Join-Path $env:TEMP ('SoundProtection-verify-' + [guid]::NewGuid().ToString('N') + '.wav')
    [System.IO.File]::WriteAllBytes($wav, (New-SpVerifyWavBytes))
    $player = $null
    $samples = New-Object System.Collections.ArrayList
    $missing = @('telemetry not read yet')
    try {
        $player = New-Object System.Media.SoundPlayer $wav
        $player.Play()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
        do {
            Start-Sleep -Milliseconds 250
            $s = Read-SpTelemetry -DataDir $DataDir
            if ($null -ne $s) { [void]$samples.Add($s) }
            $missing = @(Get-SpVerifyMissing -Samples $samples.ToArray())
            if ($missing.Count -eq 0) { break }
        } while ([DateTime]::UtcNow -lt $deadline)
    }
    finally {
        if ($null -ne $player) {
            try { $player.Stop() } catch { }
            $player.Dispose()
        }
        Remove-Item -LiteralPath $wav -Force -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ Passed = ($missing.Count -eq 0); Missing = $missing }
}

# The read-only report printed by -Action Status.
function Get-SpStatus {
    param([string]$Base, [string[]]$Names, [string]$DataDir, [string]$InstallDir)
    $c = Get-SpConst
    $rows = New-Object System.Collections.ArrayList
    foreach ($ep in @(Find-SpEndpoints -Base $Base -Names $Names)) {
        $lev = Get-SpValueState -KeyPath $ep.FxPath -Name $c.ListLeveler
        $cap = Get-SpValueState -KeyPath $ep.FxPath -Name $c.ListSafetyCap
        $levListed = $lev.Exists -and ($lev.Kind -eq 'MultiString') -and (@(@($lev.Data) | Where-Object { $_ -ieq $c.ClsidLeveler }).Count -gt 0)
        $capListed = $cap.Exists -and ($cap.Kind -eq 'MultiString') -and (@(@($cap.Data) | Where-Object { $_ -ieq $c.ClsidSafetyCap }).Count -gt 0)
        $fxOff = Get-SpValueState -KeyPath $ep.FxPath -Name $c.PropDisableFx
        $prOff = Get-SpValueState -KeyPath $ep.PropsPath -Name $c.PropDisableFx
        $disabled = ($fxOff.Exists -and ($fxOff.Kind -eq 'DWord') -and ([int]$fxOff.Data -eq 1)) -or ($prOff.Exists -and ($prOff.Kind -eq 'DWord') -and ([int]$prOff.Data -eq 1))
        $ex = Get-SpValueState -KeyPath $ep.PropsPath -Name $c.PropExclusive
        $exclusive = $true
        if ($ex.Exists -and ($ex.Kind -eq 'DWord') -and ([int]$ex.Data -eq 0)) { $exclusive = $false }
        [void]$rows.Add([pscustomobject][ordered]@{
            name             = $ep.Name
            id               = $ep.Id
            state            = $ep.State
            levelerListed    = [bool]$levListed
            safetyCapListed  = [bool]$capListed
            effectsDisabled  = [bool]$disabled
            exclusiveAllowed = [bool]$exclusive
        })
    }
    $dll = Join-Path $InstallDir $c.DllName
    $scr = Join-Path $InstallDir $c.ScriptName
    $filesOk = (Test-Path -LiteralPath $dll -PathType Leaf) -and (Test-Path -LiteralPath $scr -PathType Leaf)
    $comProblems = @(Test-SpComRegistration -Base $Base -DllPath $dll)
    $tk = Get-SpValueState -KeyPath ($Base + '\' + $c.AudioRel) -Name $c.TestKeyName
    $testKey = $tk.Exists -and ($tk.Kind -eq 'DWord') -and ([int]$tk.Data -eq 1)
    $age = $null
    $t = Read-SpTelemetry -DataDir $DataDir
    if (($null -ne $t) -and $t.MagicOk -and ($t.HeartbeatFileTime -gt 0)) {
        $nowFt = [DateTime]::UtcNow.ToFileTimeUtc()
        $age = [int64][Math]::Max(0.0, [Math]::Round(($nowFt - $t.HeartbeatFileTime) / 10000.0))
    }
    return [pscustomobject][ordered]@{
        installed      = [bool]($filesOk -and ($comProblems.Count -eq 0))
        endpoints      = $rows.ToArray()
        testKey        = [bool]$testKey
        heartbeatAgeMs = $age
    }
}

# =================================================================================================
# Elevation
# =================================================================================================

function Test-SpAdmin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object System.Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Relaunches this script elevated (UAC), waits, prints the tail of the log (it lives in the install folder), returns the child's exit code.
function Invoke-SpSelfElevate {
    param([string]$ScriptPath, [System.Collections.IDictionary]$Bound, [string]$InstallDir)
    $c = Get-SpConst
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argText = '-NoProfile -ExecutionPolicy Bypass -File ' + (ConvertTo-SpQuotedArg -Value $ScriptPath) + ' ' + (ConvertTo-SpArgumentString -Bound $Bound)
    Write-Host 'Administrator rights are needed. Windows will now ask for permission (UAC)...'
    $proc = $null
    try { $proc = Start-Process -FilePath $psExe -ArgumentList $argText -Verb RunAs -Wait -PassThru }
    catch {
        Write-Host ('ERROR: the elevated run could not be started (was the UAC prompt cancelled?): ' + $_.Exception.Message) -ForegroundColor Red
        return 1
    }
    $log = Join-Path $InstallDir $c.LogName
    if (Test-Path -LiteralPath $log -PathType Leaf) {
        Write-Host ('--- last lines of ' + $log + ' ---')
        foreach ($line in @(Get-Content -LiteralPath $log -Tail 40)) { Write-Host $line }
    }
    if ($null -eq $proc.ExitCode) { return 1 }
    return [int]$proc.ExitCode
}

# =================================================================================================
# The actions
# =================================================================================================

function Resolve-SpUserPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    $p = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    return [System.IO.Path]::GetFullPath($p)
}

# $true when the path is the real install folder, %ProgramFiles%\SoundProtection (any case, with or without a trailing backslash).
function Test-SpIsRealInstallFolder {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try {
        $real = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'SoundProtection')).TrimEnd('\')
        return ([System.IO.Path]::GetFullPath($Path).TrimEnd('\') -ieq $real)
    }
    catch { return $false }
}

# The DLL to install: -DllSource if it exists, else (only when -DllSource was not given) the one already installed.
function Resolve-SpDllSource {
    param($Cfg)
    $c = Get-SpConst
    if (Test-Path -LiteralPath $Cfg.DllSource -PathType Leaf) { return $Cfg.DllSource }
    $installed = Join-Path $Cfg.InstallDir $c.DllName
    if ((-not $Cfg.DllSourceGiven) -and (Test-Path -LiteralPath $installed -PathType Leaf)) { return $installed }
    return $null
}

# Uninstall without the service handling: restore the journal (or the no-journal minimum), unregister,
# delete files and shortcut, keep the journal as install-journal.done.json. Returns the number of problems.
# Also the rollback of a failed Install or Repair.
function Invoke-SpUninstallCore {
    param($Cfg)
    $c = Get-SpConst
    $real = -not $Cfg.Rehearsal
    $problems = 0
    # The journal is read only from the install folder (administrators only). The data folder is writable by standard
    # users, so nothing that is written into HKLM from here may come from it.
    $journalPath = Join-Path $Cfg.InstallDir $c.JournalName
    $donePath = Join-Path $Cfg.InstallDir $c.JournalDone
    $journal = $null
    try { $journal = Read-SpJournal -Path $journalPath }
    catch {
        $problems++
        Write-SpLog $_.Exception.Message 'ERROR'
    }
    if ($null -ne $journal) {
        Write-SpLog ('restoring {0} journalled values (journal created {1})' -f $journal.records.Count, $journal.created)
        $problems += (Restore-SpJournalRecords -Journal $journal -Base $Cfg.Base)
    }
    else {
        Write-SpLog 'No usable journal: only our two CLSIDs are removed from the effect lists.' 'WARN'
        $problems += (Remove-SpOurClsidsFromLists -Base $Cfg.Base)
        Write-SpLog ('{0} and the exclusive-mode value were left as they are: without a journal their original values are unknown.' -f $c.TestKeyName) 'WARN'
    }
    if ($real) {
        $dll = Join-Path $Cfg.InstallDir $c.DllName
        try {
            if (Enter-SpChange ('unregister the plug-in: regsvr32 /u /s "{0}"' -f $dll)) { Unregister-SpDll -DllPath $dll }
            Remove-SpComRegistration -Base $Cfg.Base
        }
        catch {
            $problems++
            Write-SpLog ('could not remove the COM registration: ' + $_.Exception.Message) 'ERROR'
        }
        try {
            if (Enter-SpChange ('delete the plug-in files and the folder {0}' -f $Cfg.InstallDir)) { [void](Remove-SpInstallFiles -InstallDir $Cfg.InstallDir) }
        }
        catch {
            $problems++
            Write-SpLog ('could not delete the files in {0}: {1}' -f $Cfg.InstallDir, $_.Exception.Message) 'ERROR'
        }
        foreach ($desk in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
            if ([string]::IsNullOrEmpty($desk)) { continue }
            $lnk = Join-Path $desk $c.ShortcutName
            if (Test-Path -LiteralPath $lnk -PathType Leaf) {
                try { if (Enter-SpChange ('delete the shortcut {0}' -f $lnk)) { Remove-Item -LiteralPath $lnk -Force } }
                catch { Write-SpLog ('could not delete the shortcut {0}: {1}' -f $lnk, $_.Exception.Message) 'WARN' }
            }
        }
    }
    else {
        Write-SpLog 'rehearsal: regsvr32, files and shortcut are not touched.'
    }
    if (($null -ne $journal) -and (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
        if ($problems -eq 0) {
            if (Enter-SpChange ('rename the journal to {0}' -f $donePath)) {
                if (Test-Path -LiteralPath $donePath) { Remove-Item -LiteralPath $donePath -Force }
                Move-Item -LiteralPath $journalPath -Destination $donePath
            }
        }
        else { Write-SpLog 'The journal was kept (not renamed) because something could not be restored: run Uninstall again after fixing the errors above.' 'WARN' }
    }
    return $problems
}

# Install and Repair share one path: back up, journal, stop audio, files, register, ACLs, registry,
# start audio, shortcut, verify. Sets $script:SpExit: 0 verified, 3 not running yet, 1 failed (a failed Install is rolled
# back from the journal; a failed Repair puts back only the values it wrote and leaves everything else as it was).
function Invoke-SpInstallOrRepair {
    param($Cfg, [string]$Mode)
    $c = Get-SpConst
    $real = -not $Cfg.Rehearsal
    # The journal lives in the install folder (administrators only), never in the data folder that standard users can write.
    $journalPath = Join-Path $Cfg.InstallDir $c.JournalName
    $installedDll = Join-Path $Cfg.InstallDir $c.DllName
    $tele = Join-Path $Cfg.DataDir $c.TelemetryDir
    $script:SpExit = 1

    # ---- read-only preparation: every check that can fail happens before anything is changed
    # The data folder and the telemetry folder below it are writable by standard users. A junction or a symbolic link in
    # either place would send our writes (and the icacls commands) somewhere else, so both are refused up front.
    foreach ($guarded in @($Cfg.DataDir, $tele)) {
        if (Test-SpReparsePoint -Path $guarded) {
            Write-SpLog ('{0} is a junction or symbolic link. The installer writes into this folder and sets its permissions, and a link could send that somewhere else. Remove the link (or pass another -DataDir) and run again. Nothing was changed.' -f $guarded) 'ERROR'
            return
        }
    }
    $eps = @(Find-SpEndpoints -Base $Cfg.Base -Names $Cfg.Names)
    if ($eps.Count -eq 0) {
        Write-SpLog ('No render endpoint named {0} with the composite lists ,13 and ,15 was found under {1}. Nothing was changed.' -f ($Cfg.Names -join ', '), $Cfg.Base) 'ERROR'
        return
    }
    foreach ($ep in $eps) { Write-SpLog ('endpoint: {0}   id {1}   DeviceState {2}' -f $ep.Name, $ep.Id, $ep.State) }
    $intents = @(Get-SpIntents -Base $Cfg.Base -Endpoints $eps)
    $srcDll = $null
    if ($real) {
        $srcDll = Resolve-SpDllSource -Cfg $Cfg
        if ($null -eq $srcDll) {
            $m = 'The plug-in DLL was not found: ' + $Cfg.DllSource + ' (build it first, or pass -DllSource).'
            if ($Cfg.WhatIf) { Write-SpLog $m 'WARN' ; $srcDll = $Cfg.DllSource }
            else { Write-SpLog ($m + ' Nothing was changed.') 'ERROR'; return }
        }
    }
    $journal = Read-SpJournal -Path $journalPath
    $isNew = ($null -eq $journal)
    if ($isNew) { $journal = New-SpJournal -Base $Cfg.Base }

    # ---- 1. back up (real runs; a Repair backs up only when it is also the first to record originals)
    if ($real -and ($Mode -eq 'Install' -or $isNew)) {
        $backupDir = Join-Path (Join-Path $Cfg.InstallDir $c.BackupDir) (Get-Date).ToString('yyyyMMdd-HHmmss')
        $keys = New-Object System.Collections.Generic.List[string]
        foreach ($ep in $eps) { $keys.Add($ep.Path) }
        $audioKey = $Cfg.Base + '\' + $c.AudioRel
        if (Test-SpKeyExists -KeyPath $audioKey) { $keys.Add($audioKey) }
        if (Enter-SpChange ('back up {0} registry keys with reg export into {1}' -f $keys.Count, $backupDir)) {
            Export-SpRegBackup -KeyPaths $keys.ToArray() -Directory $backupDir
        }
    }
    else { Write-SpLog 'no .reg backup in this run' }

    # ---- 2. journal, before any change; an existing journal keeps its (first) originals
    $added = Add-SpJournalRecords -Journal $journal -Intents $intents
    if ($isNew -or ($added -gt 0)) {
        if (Enter-SpChange ('write the journal {0} ({1} values recorded, {2} new in this run)' -f $journalPath, $journal.records.Count, $added)) {
            Save-SpJournal -Journal $journal -Path $journalPath
        }
    }
    else { Write-SpLog 'journal: every value is already recorded; the first originals are kept.' }

    # ---- steps 3 to 8. A failed Install rolls everything back from the journal (the same as Uninstall). A failed Repair
    # goes back to the state it found, not to the first originals: it puts back only the values it wrote itself (the undo
    # list, filled by Invoke-SpIntent just before each write) and never touches the journal, the COM registration or the files.
    $undo = $null
    if ($Mode -eq 'Repair') { $undo = New-Object System.Collections.ArrayList }
    $stopped = $false
    $dependents = @()
    $failure = $null
    try {
        try {
            if ($real) {
                # 3. stop the audio services
                $dependents = @(Get-SpRunningAudioDependents)
                if (Enter-SpChange ('stop the audio services: AudioEndpointBuilder with its running dependents ({0})' -f ($dependents -join ', '))) {
                    $stopped = $true
                    Stop-SpAudioServices
                }
                # 4. files
                if (Enter-SpChange ('copy {0} and this script into {1}' -f $srcDll, $Cfg.InstallDir)) {
                    Copy-SpInstallFiles -DllSource $srcDll -ScriptSource $Cfg.ScriptSource -InstallDir $Cfg.InstallDir
                }
                # 5. register
                if (Enter-SpChange ('register the plug-in: regsvr32 /s "{0}"' -f $installedDll)) {
                    Register-SpDll -DllPath $installedDll
                    $comProblems = @(Test-SpComRegistration -Base $Cfg.Base -DllPath $installedDll)
                    if ($comProblems.Count -gt 0) { throw ('The COM registration check failed: ' + ($comProblems -join '; ')) }
                }
            }
            else { Write-SpLog 'rehearsal: no services, no files, no regsvr32' }

            # 6. data folder, telemetry folder, ACLs (owner first), config.bin
            # The links were checked before anything was changed. Once more right before the folders are created and
            # their ACLs are set: the services have been stopped and the files copied since, and standard users can write here.
            foreach ($guarded in @($Cfg.DataDir, $tele)) {
                if (Test-SpReparsePoint -Path $guarded) { throw ('{0} turned into a junction or symbolic link while this run was going. Nothing was created or changed in it.' -f $guarded) }
            }
            if (Enter-SpChange ('create the folders {0} and {1}' -f $Cfg.DataDir, $tele)) {
                [void][System.IO.Directory]::CreateDirectory($Cfg.DataDir)
                [void][System.IO.Directory]::CreateDirectory($tele)
            }
            if ($real) {
                if (Enter-SpChange ('make Administrators the owner of {0}, then set the ACLs on it and on {1} (icacls)' -f $Cfg.DataDir, $tele)) { Set-SpDataAcl -DataDir $Cfg.DataDir }
            }
            if (Enter-SpChange ('write {0} (golden 1) if it is missing' -f (Join-Path $Cfg.DataDir $c.ConfigName))) {
                if (Write-SpDefaultConfigIfMissing -DataDir $Cfg.DataDir) { Write-SpLog 'config.bin written (golden 1)' }
                else { Write-SpLog 'config.bin already exists; kept as it is' }
            }

            # 7. registry: test key, the two list appends, exclusive mode
            foreach ($it in @($intents | Where-Object { $_.Group -eq 'TestKey' })) { Invoke-SpIntent -Intent $it -UndoLog $undo }
            foreach ($it in @($intents | Where-Object { $_.Group -eq 'Lists' })) { Invoke-SpIntent -Intent $it -UndoLog $undo }
            if ((-not $Cfg.WhatIf) -and ($Cfg.FailAfter -eq 'Lists')) { throw 'Injected fault after the list appends (-FailAfter Lists).' }
            foreach ($it in @($intents | Where-Object { $_.Group -eq 'Exclusive' })) {
                try { Invoke-SpIntent -Intent $it -UndoLog $undo }
                catch { Write-SpLog ('exclusive mode could not be turned off (continuing): ' + $_.Exception.Message) 'WARN' }
            }
            if ((-not $Cfg.WhatIf) -and ($Cfg.FailAfter -eq 'Exclusive')) { throw 'Injected fault after exclusive mode (-FailAfter Exclusive).' }

            # 8. start the audio services again
            if ($real) {
                if (Enter-SpChange 'start the audio services again: AudioEndpointBuilder, Audiosrv, then the stopped dependents') {
                    if (-not (Start-SpAudioServices -Dependents $dependents)) { throw 'AudioEndpointBuilder or Audiosrv did not come back (see above).' }
                    $stopped = $false
                }
            }
        }
        catch {
            $failure = $_.Exception.Message
        }
        if ($null -ne $failure) {
            Write-SpLog ('FAILED: ' + $failure) 'ERROR'
            if ($Mode -eq 'Repair') {
                # The audio services are still stopped here (the finally below starts them), so nothing rewrites the lists meanwhile.
                Write-SpLog ('Putting back the {0} value(s) this repair wrote, newest first. The install journal, the COM registration and the installed files are left as they are.' -f $undo.Count)
                $rb = Restore-SpJournalRecords -Journal ([pscustomobject]@{ records = $undo }) -Base $Cfg.Base
                if ($rb -gt 0) { Write-SpLog ('Repair failed and {0} of its values could not be put back (see above). The .reg backups are in {1}. Error: {2}' -f $rb, (Join-Path $Cfg.InstallDir $c.BackupDir), $failure) 'ERROR' }
                else { Write-SpLog ('Repair failed: the setup was left as it was before the repair. Error: {0}' -f $failure) 'ERROR' }
            }
            else {
                Write-SpLog 'Rolling back from the journal (same as Uninstall)...'
                $rb = Invoke-SpUninstallCore -Cfg $Cfg
                if ($rb -gt 0) { Write-SpLog ('The rollback had {0} problem(s); see above. The .reg backups are in {1}.' -f $rb, (Join-Path $Cfg.InstallDir $c.BackupDir)) 'ERROR' }
                else { Write-SpLog 'Rollback complete: the system is as it was before this run.' }
            }
        }
    }
    finally {
        if ($stopped) {
            Write-SpLog 'starting the audio services again'
            if (-not (Start-SpAudioServices -Dependents $dependents)) { Write-SpLog 'The audio services could not be started; start them from services.msc or restart the PC.' 'ERROR' }
        }
    }
    if ($null -ne $failure) { $script:SpExit = 1; return }

    # ---- 9. shortcut (a failure here is only a warning)
    if ($real) {
        $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) $c.ShortcutName
        if (Enter-SpChange ('create the desktop shortcut {0}' -f $lnk)) {
            try { New-SpUndoShortcut -LinkPath $lnk -InstallDir $Cfg.InstallDir }
            catch { Write-SpLog ('the shortcut could not be created: ' + $_.Exception.Message) 'WARN' }
        }
    }

    # ---- 10. verify: a quiet test tone, then the plug-in's own telemetry
    $script:SpExit = 0
    if ($real) {
        if (Enter-SpChange 'verify: play a 2 s test tone at -40 dBFS and read telemetry\apo.bin for up to 6 s') {
            $v = Invoke-SpVerify -DataDir $Cfg.DataDir
            if ($v.Passed) { Write-SpLog 'VERIFY: PASS' }
            else {
                Write-SpLog ('VERIFY: NOT RUNNING ({0}). A restart of the PC may be needed for the test key.' -f (@($v.Missing) -join '; '))
                $script:SpExit = 3
            }
        }
    }
    else { Write-SpLog 'rehearsal: no sound, no verify' }
}

function Invoke-SpUninstallAction {
    param($Cfg)
    $real = -not $Cfg.Rehearsal
    $script:SpExit = 1
    $stopped = $false
    $dependents = @()
    $problems = 0
    try {
        if ($real) {
            $dependents = @(Get-SpRunningAudioDependents)
            if (Enter-SpChange ('stop the audio services: AudioEndpointBuilder with its running dependents ({0})' -f ($dependents -join ', '))) {
                $stopped = $true
                Stop-SpAudioServices
            }
        }
        $problems += (Invoke-SpUninstallCore -Cfg $Cfg)
    }
    catch {
        $problems++
        Write-SpLog ('Uninstall failed: ' + $_.Exception.Message) 'ERROR'
    }
    finally {
        if ($stopped) {
            Write-SpLog 'starting the audio services again'
            if (-not (Start-SpAudioServices -Dependents $dependents)) { $problems++ }
        }
    }
    if ($problems -gt 0) {
        Write-SpLog ('Uninstall finished with {0} problem(s); see above.' -f $problems) 'ERROR'
        $script:SpExit = 1
        return
    }
    if ($Cfg.WhatIf) { Write-SpLog 'WHATIF: nothing was changed.' }
    else { Write-SpLog 'Uninstall complete: the original sound settings are back.' }
    $script:SpExit = 0
}

# =================================================================================================
# Main
# =================================================================================================

$script:SpExit = 1
$script:SpContext = $null
try {
    $c = Get-SpConst

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($Endpoints)) {
        foreach ($part in ([string]$e -split ',')) {
            $t = $part.Trim()
            if ($t.Length -gt 0) { $names.Add($t) }
        }
    }
    if ($names.Count -eq 0) { throw '-Endpoints is empty.' }
    $base = $RegistryBase.TrimEnd('\')
    if ($base -notmatch '^(HKLM|HKCU):\\') { throw "-RegistryBase must start with HKLM:\ or HKCU:\ (got '$RegistryBase')." }
    if ($Rehearsal -and ($base -notlike 'HKCU:\*')) { throw "-Rehearsal refuses a registry base outside HKCU: (got '$RegistryBase'). A rehearsal never touches HKLM." }
    $failAfterValue = ''
    if ($PSBoundParameters.ContainsKey('FailAfter')) {
        if (-not $Rehearsal) { throw '-FailAfter works only together with -Rehearsal.' }
        $failAfterValue = $FailAfter
    }
    if (-not [Environment]::Is64BitProcess) { throw 'Run this in 64-bit Windows PowerShell: the audio registry keys are the 64-bit ones.' }

    $dllGiven = -not [string]::IsNullOrWhiteSpace($DllSource)
    $dllRaw = $DllSource
    if (-not $dllGiven) { $dllRaw = Join-Path $PSScriptRoot '..\native\out\apo\SoundProtection.Apo.dll' }
    $dataFull = Resolve-SpUserPath $DataDir
    $installFull = Resolve-SpUserPath $InstallDir
    $dllFull = Resolve-SpUserPath $dllRaw
    # A rehearsal now writes its journal and its log into -InstallDir. Pointed at the real install folder (which is also the
    # default when -InstallDir is left out) it would leave a rehearsal journal there, so that folder is refused.
    if ($Rehearsal -and ($Action -ne 'Status') -and (Test-SpIsRealInstallFolder -Path $installFull)) {
        throw "-Rehearsal refuses the real install folder ($installFull): a rehearsal keeps its journal and log in -InstallDir. Give it a temporary folder with -InstallDir."
    }
    $cfg = [pscustomobject]@{
        Base           = $base
        Names          = $names.ToArray()
        DataDir        = $dataFull
        InstallDir     = $installFull
        DllSource      = $dllFull
        DllSourceGiven = $dllGiven
        ScriptSource   = $PSCommandPath
        Rehearsal      = [bool]$Rehearsal
        WhatIf         = [bool]$WhatIf
        FailAfter      = $failAfterValue
    }
    $script:SpContext = [pscustomobject]@{
        WhatIf    = [bool]$WhatIf
        Rehearsal = [bool]$Rehearsal
        LogPath   = $null
        Quiet     = ($Action -eq 'Status')
    }

    # A real Install, Repair or Uninstall needs administrator rights: relaunch elevated and report its result.
    $needsAdmin = ($Action -ne 'Status') -and (-not $Rehearsal) -and (-not $WhatIf)
    if ($needsAdmin -and (-not (Test-SpAdmin))) {
        $script:SpExit = Invoke-SpSelfElevate -ScriptPath $PSCommandPath -Bound $PSBoundParameters -InstallDir $installFull
    }
    else {
        if (($Action -ne 'Status') -and (-not $WhatIf)) {
            # The log is kept next to the journal, in the install folder. An Uninstall of nothing creates no folder.
            $logHere = $true
            if (($Action -eq 'Uninstall') -and (-not (Test-Path -LiteralPath $installFull -PathType Container))) { $logHere = $false }
            if ($logHere) {
                [void][System.IO.Directory]::CreateDirectory($installFull)
                $script:SpContext.LogPath = Join-Path $installFull $c.LogName
            }
        }
        if ($Action -ne 'Status') {
            $mode = 'real'
            if ($Rehearsal) { $mode = 'REHEARSAL' }
            if ($WhatIf) { $mode = $mode + ', WHATIF (nothing will be changed)' }
            Write-SpLog ('=== Sound Protection installer: {0} ({1}) ===' -f $Action, $mode)
            Write-SpLog ('user {0}, elevated: {1}; registry base {2}; data {3}; install {4}' -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, (Test-SpAdmin), $base, $dataFull, $installFull)
        }
        if ((-not $Rehearsal) -and (-not $WhatIf) -and ($Action -ne 'Status')) {
            # Never stand inside the folder that Uninstall is about to delete.
            try { Set-Location -LiteralPath $env:SystemRoot; [System.Environment]::CurrentDirectory = $env:SystemRoot } catch { }
        }
        switch ($Action) {
            'Status' {
                $report = Get-SpStatus -Base $base -Names $names.ToArray() -DataDir $dataFull -InstallDir $installFull
                Write-Output (ConvertTo-Json -InputObject $report -Depth 8)
                $script:SpExit = 0
            }
            'Install'   { Invoke-SpInstallOrRepair -Cfg $cfg -Mode 'Install' }
            'Repair'    { Invoke-SpInstallOrRepair -Cfg $cfg -Mode 'Repair' }
            'Uninstall' { Invoke-SpUninstallAction -Cfg $cfg }
        }
    }
}
catch {
    $script:SpExit = 1
    $m = $_.Exception.Message
    try { Write-SpLog ('FAILED: ' + $m) 'ERROR' }
    catch { [Console]::Error.WriteLine('ERROR: ' + $m) }
}
exit ([int]$script:SpExit)
