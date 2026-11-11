@echo off
rem build-dsp.cmd - builds and runs the native DSP tests (native\dsp\tests\DspTests.cpp).
rem
rem   Output:    native\out\dsp\DspTests.exe
rem   Exit code: the tests' exit code (0 = every test passed). 1 = compile failed. 2 = toolchain not found.
rem
rem vcvars64.bat may print "'vswhere.exe' is not recognized". That line is harmless and is hidden here.
setlocal

set "HERE=%~dp0"
set "OUT=%HERE%out\dsp"
set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

if not exist "%VCVARS%" goto :novcvars
call "%VCVARS%" >nul 2>&1
where cl.exe >nul 2>&1
if errorlevel 1 goto :nocl

if not exist "%OUT%" mkdir "%OUT%"
if exist "%OUT%\DspTests.exe" del /q "%OUT%\DspTests.exe"

cl /nologo /std:c++17 /EHsc /W4 /WX /O2 /fp:precise /MT /DUNICODE /D_UNICODE ^
   /I "%HERE%dsp" ^
   "%HERE%dsp\tests\DspTests.cpp" ^
   /Fo"%OUT%\DspTests.obj" /Fe"%OUT%\DspTests.exe"
if errorlevel 1 goto :compilefailed

"%OUT%\DspTests.exe"
exit /b %errorlevel%

:novcvars
echo build-dsp: vcvars64.bat not found at "%VCVARS%"
exit /b 2

:nocl
echo build-dsp: cl.exe is not on PATH after vcvars64.bat
exit /b 2

:compilefailed
echo build-dsp: COMPILE FAILED
exit /b 1
