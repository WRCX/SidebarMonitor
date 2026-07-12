@echo off
rem Builds AdlxShim.dll, the flat-C bridge to AMD's ADLX (GPU telemetry).
rem Needs the MSVC C++ toolchain (Desktop development with C++) and the ADLX SDK headers, which
rem native\AdlxSdk\fetch.ps1 populates (they are gitignored — see AdlxShim.cpp for the licence note).
setlocal

set "SDK=%~dp0..\AdlxSdk"
if not exist "%SDK%\SDK\Include\ADLX.h" (
    echo ADLX SDK headers not found under "%SDK%". Run native\AdlxSdk\fetch.ps1 first.
    exit /b 1
)

for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1

cd /d "%~dp0"
cl /nologo /LD /EHsc /std:c++17 /O2 /I "%SDK%" ^
   AdlxShim.cpp ^
   "%SDK%\SDK\ADLXHelper\Windows\Cpp\ADLXHelper.cpp" ^
   "%SDK%\SDK\Platform\Windows\WinAPIs.cpp" ^
   /Fe:AdlxShim.dll
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
del *.obj AdlxShim.exp AdlxShim.lib 2>nul
echo AdlxShim.dll construida.
