@echo off
rem Builds RyzenShim.dll, the flat-C bridge to the AMD Ryzen Master Monitoring SDK.
rem Needs the MSVC C++ toolchain (Desktop development with C++) and the AMD Monitoring SDK headers.
setlocal

set "SDK=%AMDRMMONITORSDKPATH%"
if "%SDK%"=="" set "SDK=C:\Program Files\AMD\RyzenMasterMonitoringSDK\"

for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1

cd /d "%~dp0"
cl /nologo /LD /EHsc /std:c++17 /O2 /I "%SDK%include" RyzenShim.cpp /Fe:RyzenShim.dll /link Advapi32.lib
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
del RyzenShim.obj RyzenShim.exp RyzenShim.lib 2>nul
echo RyzenShim.dll construida.
