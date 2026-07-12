# Builds the SidebarMonitor MSI with WiX. Publishes the three apps SELF-CONTAINED into a staging
# folder (so the MSI needs no .NET runtime prerequisite), generates the hidden-helper launcher and
# the scheduled-task XML pinned to the default install path, then runs `wix build`.
#
# Prereqs: .NET 10 SDK, MSVC C++ toolchain + vswhere on PATH (AOT agent), and the WiX CLI
# (`dotnet tool install --global wix`). The AMD SDK DLLs must already be fetched
# (native/RyzenSdk/fetch.ps1) and RyzenShim.dll / AdlxShim.dll built.
#
# ASCII-only (Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI).
param([string]$Version = '1.2.0.0', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
# Normalise to a 4-part MSI ProductVersion (x.y.z.w). A tag like "1.3.0" becomes "1.3.0.0".
while (($Version -split '\.').Count -lt 4) { $Version += '.0' }

$here  = $PSScriptRoot
$root  = Split-Path -Parent $here
$stage = Join-Path $here 'stage'
$out   = Join-Path $here 'out'

# The install path is fixed (the MSI does not expose a directory picker), so the launcher and the
# scheduled task can hardcode it.
$installDir = Join-Path ${env:ProgramFiles} 'SidebarMonitor'

# vswhere for the AOT agent publish.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"   # wix CLI

if (-not $SkipPublish) {
    Write-Host '== Publishing the three apps (self-contained) into stage ==' -ForegroundColor Cyan
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null

    # Version stamping: Version/FileVersion track the release (from the tag in CI); AssemblyVersion
    # stays pinned in Directory.Build.props (explicitly set there, so -p:Version can't move it), which
    # keeps a UI-only redeploy binding to Shared.dll. See Directory.Build.props.
    $ver = @("-p:Version=$Version", "-p:FileVersion=$Version")

    # Agent is AOT (always self-contained native). Helper + UI self-contained so the MSI carries the
    # .NET runtime; publishing all three into ONE folder means they share the single runtime copy.
    & dotnet publish (Join-Path $root 'src\SidebarMonitor.Agent\SidebarMonitor.Agent.csproj') `
        -c Release -r win-x64 -o $stage --nologo -v q @ver
    if ($LASTEXITCODE) { throw 'agent publish failed' }
    & dotnet publish (Join-Path $root 'src\SidebarMonitor.Etw\SidebarMonitor.Etw.csproj') `
        -c Release -r win-x64 --self-contained true -o $stage --nologo -v q @ver
    if ($LASTEXITCODE) { throw 'helper publish failed' }
    & dotnet publish (Join-Path $root 'src\SidebarMonitor.UI\SidebarMonitor.UI.csproj') `
        -c Release -r win-x64 --self-contained true -o $stage --nologo -v q @ver
    if ($LASTEXITCODE) { throw 'UI publish failed' }
}

# Hidden-helper launcher: WScript.Run with window style 0 starts the (console) helper with its
# console already hidden. """path""" -> "path" (quoted, in case of spaces).
$exe = Join-Path $installDir 'SidebarMonitor.Etw.exe'
$vbs = Join-Path $stage 'run-helper-hidden.vbs'
@(
    'Set sh = CreateObject("WScript.Shell")'
    ('sh.Run """{0}""", 0, False' -f $exe)
) | Set-Content -Path $vbs -Encoding ASCII

# Scheduled task: at logon, elevated (Highest, no UAC prompt), in the interactive session so the
# Local\ shared-memory namespace matches the UI and agent. Runs the launcher via wscript, hidden.
$taskXml = Join-Path $stage 'helper-task.xml'
@"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts the elevated SidebarMonitor helper (ETW + AMD SDK sensors) at logon.</Description>
    <URI>\SidebarMonitor Helper</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>wscript.exe</Command>
      <Arguments>//B //Nologo "$installDir\run-helper-hidden.vbs"</Arguments>
    </Exec>
  </Actions>
</Task>
"@ | Set-Content -Path $taskXml -Encoding Unicode

# License shown by the MSI UI (the app's own MIT license; the AMD SDK EULA is accepted in-app on
# first run). Minimal RTF wrapper around the plain-text LICENSE.
$licRtf = Join-Path $stage 'license.rtf'
$licTxt = (Get-Content (Join-Path $root 'LICENSE') -Raw) -replace '\\','\\' -replace '\{','\{' -replace '\}','\}'
$licTxt = ($licTxt -replace "`r?`n", '\par ')
"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18 $licTxt}" | Set-Content -Path $licRtf -Encoding ASCII

Write-Host '== Building MSI with WiX ==' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $out | Out-Null
$msi = Join-Path $out 'SidebarMonitor.msi'
& wix build (Join-Path $here 'SidebarMonitor.wxs') `
    -ext WixToolset.UI.wixext `
    -d "Stage=$stage" -d "ProjectRoot=$root" -d "Version=$Version" `
    -arch x64 -o $msi
if ($LASTEXITCODE) { throw 'wix build failed' }

Write-Host "`n== Built ==" -ForegroundColor Green
Write-Host "  $msi  ($([math]::Round((Get-Item $msi).Length/1MB,1)) MB)"
