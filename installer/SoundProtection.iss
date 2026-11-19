; Sound Protection: the double-click setup for Windows 11.
; Compiled by installer\build.cmd (build.ps1), which stages the payload next to this file and
; passes the pinned Microsoft .NET 10 Desktop Runtime x64 as ISCC defines:
;   ISCC.exe /DRuntimeVersion=10.0.x /DRuntimeSha256=<sha256 of the runtime installer> SoundProtection.iss

#ifndef RuntimeVersion
  #error Compile this file with installer\build.cmd: it defines RuntimeVersion and RuntimeSha256.
#endif

[Setup]
AppId={{D9A7C6F1-52B4-4E17-9E9F-6D51E3B84A10}
AppName=Sound Protection
AppVersion=0.1.0
AppPublisher=Sound Protection
DefaultDirName={autopf}\SoundProtection
DefaultGroupName=Sound Protection
UninstallDisplayName=Sound Protection
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=out
OutputBaseFilename=SoundProtectionSetup
Compression=lzma2/max
SolidCompression=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
CloseApplications=yes
InfoBeforeFile=setup-notice.txt

[Files]
Source: "payload\app\*"; DestDir: "{app}"; Flags: recursesubdirs
Source: "payload\apo\SoundProtection.Apo.dll"; DestDir: "{app}"
Source: "payload\script\SoundProtection.Install.ps1"; DestDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: SoundProtection; ValueData: """{app}\SoundProtection.exe"""; Flags: uninsdeletevalue

[Code]
var
  g_InstallExit: Integer;
  DownloadPage: TDownloadWizardPage;

{ The Windows 11 gate. This is the very first thing Setup executes (InitializeSetup runs before any
  page, download, copy or write): on Windows 10 or older one message, then Setup closes and nothing
  on the machine was touched. }
function InitializeSetup(): Boolean;
var
  W: TWindowsVersion;
begin
  g_InstallExit := -1;
  GetWindowsVersionEx(W);
  Result := True;
  if W.Build < 22000 then begin
    MsgBox('Sound Protection works on Windows 11 only. Setup will now close.', mbError, MB_OK);
    Result := False;
  end;
end;

{ The Microsoft .NET 10 Desktop Runtime x64: any 10.x folder under shared\Microsoft.WindowsDesktop.App. }
function IsDotNetDesktop10Installed(): Boolean;
var
  Dir: String;
  FindRec: TFindRec;
begin
  Result := False;
  Dir := ExpandConstant('{pf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(Dir) then
    Exit;
  if FindFirst(Dir + '\*', FindRec) then begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
          if Pos('10.', FindRec.Name) = 1 then begin
            Result := True;
            Break;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ The download page (created with all the other wizard pages; used by NextButtonClick below). }
function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

{ On the Ready page, when the runtime is missing: download the pinned Microsoft installer with a
  progress bar, then run it silently. The download is rejected when it does not match the pinned
  SHA-256. Any failure stays on this page; pressing Next again is the Try again. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  RCode: Integer;
begin
  Result := True;
  if CurPageID = wpReady then begin
    if not IsDotNetDesktop10Installed() then begin
      DownloadPage.Show;
      DownloadPage.Clear;
      DownloadPage.Add('https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{#RuntimeVersion}/windowsdesktop-runtime-{#RuntimeVersion}-win-x64.exe', 'windowsdesktop-runtime.exe', '{#RuntimeSha256}');
      DownloadPage.SetText('Sound Protection needs a free component from Microsoft (one-time, about 60 MB).', '');
      try
        DownloadPage.Download;
      except
        MsgBox('Could not download it. Connect to the internet once, then choose Try again.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
      if Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, RCode) then begin
        if not IsDotNetDesktop10Installed() then begin
          MsgBox('The component was downloaded, but installing it did not finish. Restart the PC once, then choose Try again.', mbError, MB_OK);
          Result := False;
        end;
      end else begin
        MsgBox('The downloaded component could not be started. Choose Try again, and if it keeps failing, restart the PC once first.', mbError, MB_OK);
        Result := False;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  RCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    { Everything is in place, Setup runs elevated: one run of the proven engine script, on every
      output device that can take the plug-in. -Endpoints All finds them; the script journals
      every value it touches. }
    if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
            '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\SoundProtection.Install.ps1') + '" -Action Install -Endpoints All -DllSource "' + ExpandConstant('{app}\SoundProtection.Apo.dll') + '"',
            ExpandConstant('{app}'),
            SW_SHOWMINNOACTIVE, ewWaitUntilTerminated, RCode) then
      g_InstallExit := RCode
    else
      g_InstallExit := 1;
  end;
  if CurStep = ssDone then begin
    case g_InstallExit of
      0:
        MsgBox('Sound Protection is on for every output device. Your sound will be capped before the volume slider.', mbInformation, MB_OK);
      3:
        MsgBox('Installed. Windows needs a restart of the PC for it to run. Restart, then everything works.', mbInformation, MB_OK);
    else
      MsgBox('Setup failed. Nothing is protected; see install-log.txt in C:\Program Files\SoundProtection\.', mbError, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RCode: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    { This step runs before any file is deleted, so the script is still in its folder.
      Soft-kill the tray first (it does not matter which folder it runs from), then let the
      engine restore every journalled value. Exit 3 only means a restart is still pending. }
    if Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM SoundProtection.exe', '', SW_HIDE, ewWaitUntilTerminated, RCode) then
      Log('taskkill signalled SoundProtection.exe');
    RCode := 1;
    if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
            '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\SoundProtection.Install.ps1') + '" -Action Uninstall',
            '', SW_SHOWMINNOACTIVE, ewWaitUntilTerminated, RCode) then begin
      { RCode now holds the script's exit code }
    end;
    if (RCode <> 0) and (RCode <> 3) then
      MsgBox('Sound Protection was not fully undone. See install-log.txt in C:\Program Files\SoundProtection\, and run the desktop shortcut "Sound Protection - Undo" after a restart.', mbError, MB_OK);
  end;
  if CurUninstallStep = usPostUninstall then begin
    { The [Registry] entry above covers the normal case (uninsdeletevalue); this is the idempotent
      fallback, and a failure is ignored on purpose. Never an HKLM value here. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SoundProtection');
  end;
end;
