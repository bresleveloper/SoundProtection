@echo off
rem build-apo.cmd [stub] - builds the Sound Protection plug-in DLL and its test host, then runs the host.
rem
rem   build-apo.cmd         real DSP: #include "SpDsp.h" from native\dsp
rem   build-apo.cmd stub    pass-through DSP: native\apo\SpDspStub.h, compiled with /DSP_DSP_STUB
rem
rem   Output:    native\out\apo\SoundProtection.Apo.dll and native\out\apo\ApoHost.exe
rem   Exit code: the host's exit code (0 = every check passed). 1 = compile or link failed. 2 = toolchain not found.
rem
rem The host loads the DLL with LoadLibrary and points SOUNDPROTECTION_DATA_DIR at a temp folder. Nothing here
rem registers the DLL: DllRegisterServer, RegisterAPO and regsvr32 are never called.
rem
rem vcvars64.bat may print "'vswhere.exe' is not recognized". That line is harmless and is hidden here.
setlocal

set "HERE=%~dp0"
set "OUT=%HERE%out\apo"
set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "STUBFLAG="
if /i "%~1"=="stub" set "STUBFLAG=/DSP_DSP_STUB"

if not exist "%VCVARS%" goto :novcvars
call "%VCVARS%" >nul 2>&1
where cl.exe >nul 2>&1
if errorlevel 1 goto :nocl

if not exist "%OUT%" mkdir "%OUT%"
for %%F in (SoundProtection.Apo.dll SoundProtection.Apo.lib SoundProtection.Apo.exp ApoHost.exe) do (
    if exist "%OUT%\%%F" del /q "%OUT%\%%F"
)
if exist "%OUT%\*.obj" del /q "%OUT%\*.obj"

rem Object files go to the current directory, so build from the output folder.
pushd "%OUT%"

cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /fp:precise /MT /DUNICODE /D_UNICODE /wd4815 %STUBFLAG% ^
   /I "%HERE%apo" /I "%HERE%dsp" ^
   /LD "%HERE%apo\*.cpp" ^
   /Fe"SoundProtection.Apo.dll" ^
   /link /MANIFEST:NO /NODEFAULTLIB:atls.lib /DEF:"%HERE%apo\SoundProtection.Apo.def" ^
   audiobaseprocessingobject.lib audiomediatypecrt.lib audioeng.lib ole32.lib advapi32.lib
if errorlevel 1 goto :compilefailed

cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /fp:precise /MT /DUNICODE /D_UNICODE /wd4815 %STUBFLAG% ^
   /I "%HERE%apo" ^
   "%HERE%apohost\ApoHost.cpp" ^
   /Fe"ApoHost.exe" ^
   /link /NODEFAULTLIB:atls.lib audiomediatypecrt.lib ole32.lib advapi32.lib
if errorlevel 1 goto :compilefailed

popd

"%OUT%\ApoHost.exe" "%OUT%\SoundProtection.Apo.dll"
exit /b %errorlevel%

:novcvars
echo build-apo: vcvars64.bat not found at "%VCVARS%"
exit /b 2

:nocl
echo build-apo: cl.exe is not on PATH after vcvars64.bat
exit /b 2

:compilefailed
popd
echo build-apo: COMPILE FAILED
exit /b 1
