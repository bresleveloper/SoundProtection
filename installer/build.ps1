# Builds the one Sound Protection setup file, stage by stage, and fails loudly at the first
# missing piece. Windows PowerShell 5.1. Run it with installer\build.cmd (or directly:
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1
#
# Stages:
#   1. publish the tray app (framework-dependent, x64) into payload\app
#   2. build (if needed) and copy the plug-in DLL into payload\apo
#   3. copy the engine script into payload\script
#   4. pin the Microsoft .NET 10 Desktop Runtime x64: its version is read from Microsoft,
#      the official installer is downloaded once into prerq\ and its SHA-256 is recorded.
#   5. print the payload summary; when installer\SoundProtection.iss does not exist yet,
#      stop here (the Inno script, once written, is compiled as the final stage).
#
# Output: payload\, prerq\ and (once the .iss exists) out\SoundProtectionSetup.exe.
# Everything in those folders is produced again by this script; nothing in it is tracked by git.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest downloads ~10x faster without the progress bar

$Here = $PSScriptRoot
$Repo = Split-Path $Here

function Fail([string]$Message) {
    Write-Host ('build.ps1: ' + $Message) -ForegroundColor Red
    exit 1
}

# --- 1. the tray app: framework-dependent publish of src\SoundProtection.App into payload\app ---------------
$appProject = Join-Path $Repo 'src\SoundProtection.App'
if (-not (Test-Path -LiteralPath (Join-Path $appProject 'SoundProtection.App.csproj'))) {
    Fail ('the tray app project was not found at ' + $appProject + '. Run build.ps1 from the repository, not from a copy.')
}
$payload = Join-Path $Here 'payload'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
$appOut = Join-Path $payload 'app'
[void][IO.Directory]::CreateDirectory($appOut)

Write-Host 'publishing the tray app (framework-dependent, win-x64)...'
& dotnet publish $appProject -c Release -r win-x64 --self-contained false -o $appOut
if ($LASTEXITCODE -ne 0) { Fail ('dotnet publish exited with ' + $LASTEXITCODE + '.') }
if (-not (Test-Path -LiteralPath (Join-Path $appOut 'SoundProtection.exe'))) {
    Fail ('the publish did not produce payload\app\SoundProtection.exe.')
}

# --- 2. the plug-in DLL: rebuild when it is missing, always copy into payload\apo ----------------------------
$apoBuild = Join-Path $Repo 'native\build-apo.cmd'
$apoSrc = Join-Path $Repo 'native\out\apo\SoundProtection.Apo.dll'
$apoOut = Join-Path $payload 'apo'
[void][IO.Directory]::CreateDirectory($apoOut)
if (-not (Test-Path -LiteralPath $apoSrc)) {
    if (-not (Test-Path -LiteralPath $apoBuild)) {
        Fail ('the plug-in DLL is missing and its build script was not found at ' + $apoBuild + '.')
    }
    Write-Host 'building the plug-in DLL (native\build-apo.cmd)...'
    & $apoBuild
    if ($LASTEXITCODE -ne 0) {
        Fail ('building the plug-in DLL failed: native\build-apo.cmd exited with ' + $LASTEXITCODE + '.')
    }
}
if (-not (Test-Path -LiteralPath $apoSrc)) {
    Fail ('the plug-in DLL was not found after the build: ' + $apoSrc + '.')
}
Copy-Item -LiteralPath $apoSrc -Destination (Join-Path $apoOut 'SoundProtection.Apo.dll')

# --- 3. the engine script into payload\script ----------------------------------------------------------------
$engine = Join-Path $Here 'SoundProtection.Install.ps1'
if (-not (Test-Path -LiteralPath $engine)) {
    Fail ('the installer script was not found next to build.ps1: ' + $engine + '.')
}
$scriptOut = Join-Path $payload 'script'
[void][IO.Directory]::CreateDirectory($scriptOut)
Copy-Item -LiteralPath $engine -Destination (Join-Path $scriptOut 'SoundProtection.Install.ps1')

# --- 4. the runtime pin: version from Microsoft, the installer downloaded once into prerq\, its SHA-256 -----
# Modern TLS for Windows PowerShell 5.1 (harmless on newer engines).
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
$versionUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0/latest.version'
try {
    $v = [string](Invoke-WebRequest $versionUrl -UseBasicParsing).Content.Trim()
} catch {
    Fail ('could not read the current .NET Desktop Runtime version from ' + $versionUrl + ' (' + $_.Exception.Message + '). An internet connection is needed once per version pin.')
}
if ($v -notmatch '^10\.0\.\d+$') {
    Fail ('the version read from Microsoft does not look like a 10.0.x version: "' + $v + '".')
}
$runtimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/' + $v + '/windowsdesktop-runtime-' + $v + '-win-x64.exe'
$prerq = Join-Path $Here 'prerq'
[void][IO.Directory]::CreateDirectory($prerq)
$runtime = Join-Path $prerq ('windowsdesktop-runtime-' + $v + '-win-x64.exe')
if (-not (Test-Path -LiteralPath $runtime)) {
    Write-Host ('downloading the Microsoft .NET Desktop Runtime ' + $v + ' x64 installer (about 60 MB)...')
    try {
        Invoke-WebRequest $runtimeUrl -OutFile $runtime -UseBasicParsing
    } catch {
        Fail ('could not download ' + $runtimeUrl + ' (' + $_.Exception.Message + '). An internet connection is needed once per version pin.')
    }
}
if (-not (Test-Path -LiteralPath $runtime)) {
    Fail ('the runtime installer is not in place after the download: ' + $runtime + '.')
}
$sha = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash

# --- 5. summary, then the Inno Setup compile -------------------------------------------------------------
Write-Host ('PAYLOAD OK: runtime ' + $v + ' sha ' + $sha)

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
if ([string]::IsNullOrEmpty(${env:ProgramFiles(x86)})) {
    Fail 'could not locate Program Files (x86), where Inno Setup 6 lives.'
}
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host 'Inno Setup 6 was not found; installing it with winget...'
    winget install --id JRSoftware.InnoSetup -e --source winget --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { Fail ('winget install of Inno Setup 6 exited with ' + $LASTEXITCODE + '.') }
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $iscc) {
        Fail ('Inno Setup 6 was not found after the winget install: tried ' + ($isccCandidates -join ' and '))
    }
}
$iss = Join-Path $Here 'SoundProtection.iss'
if (-not (Test-Path -LiteralPath $iss)) {
    Fail 'installer\SoundProtection.iss was not found next to build.ps1.'
}

Write-Host 'compiling installer\SoundProtection.iss into out\SoundProtectionSetup.exe...'
& $iscc ('/DRuntimeVersion=' + $v) ('/DRuntimeSha256=' + $sha) $iss
if ($LASTEXITCODE -ne 0) { Fail ('ISCC.exe exited with ' + $LASTEXITCODE + '.') }
$setupExe = Join-Path $Here 'out\SoundProtectionSetup.exe'
if (-not (Test-Path -LiteralPath $setupExe)) {
    Fail 'the compile did not produce installer\out\SoundProtectionSetup.exe.'
}
$mb = [math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 1)
Write-Host ('SETUP OK: installer\out\SoundProtectionSetup.exe (' + $mb + ' MB, runtime ' + $v + ')')
