# Builds the SidebarMonitor MSI with WiX. Publishes the three apps SELF-CONTAINED into a staging
# folder (so the MSI needs no .NET runtime prerequisite), generates the hidden-helper launcher and
# the scheduled-task XML pinned to the default install path, then runs `wix build`.
#
# Prereqs: .NET 10 SDK, MSVC C++ toolchain + vswhere on PATH (AOT agent), and the WiX CLI
# (`dotnet tool install --global wix`). The AMD SDK DLLs must already be fetched
# (native/RyzenSdk/fetch.ps1) and RyzenShim.dll / AdlxShim.dll built.
#
# ASCII-only (Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI).
param(
    [string]$Version = '1.2.0.0',
    # -Lite builds the "no bundled AMD binaries" variant: the MSI omits AMD's proprietary DLLs +
    # driver, so nothing of AMD's is redistributed. RyzenShim then loads Platform.dll from the AMD SDK
    # the user has installed (Ryzen Master / the Monitoring SDK); without it, CPU sensors fall back to
    # basic mode. ADLX (GPU) is unaffected — its runtime ships with the AMD driver anyway.
    [switch]$Lite,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
# The release this build corresponds to, for the finish page's "see what's new". Uses the version
# exactly as passed, which is the tag ("1.4.8" from v1.4.8) — read it before the 4-part padding
# below rewrites it into something no tag matches.
$releaseUrl = "https://github.com/WRCX/SidebarMonitor/releases/tag/v$Version"
# Normalise to a 4-part MSI ProductVersion (x.y.z.w). A tag like "1.3.0" becomes "1.3.0.0".
while (($Version -split '\.').Count -lt 4) { $Version += '.0' }

$here  = $PSScriptRoot
$root  = Split-Path -Parent $here
$stage = Join-Path $here 'stage'
$out   = Join-Path $here 'out'

# The signed PawnIO module blobs that MUST ride in the MSI. They are gitignored + fetched
# (native/PawnIO/fetch.ps1) and copied to the helper output only under Condition="Exists" in
# SidebarMonitor.Etw.csproj, so a partial fetch silently drops one and the MSI ships without it (a
# missing LpcACPIEC.bin = no laptop fan %). These stay in BOTH the full and -Lite variants (they are
# not AMD's redistributables). Asserted below: present at the source before publish, and in the stage
# that WiX harvests.
$pawnModules = 'RyzenSMU.bin', 'IntelMSR.bin', 'LpcACPIEC.bin'

# The install path is fixed (the MSI does not expose a directory picker), so the launcher and the
# scheduled task can hardcode it.
$installDir = Join-Path ${env:ProgramFiles} 'SidebarMonitor'

# vswhere for the AOT agent publish.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"   # wix CLI

if (-not $SkipPublish) {
    Write-Host '== Publishing the three apps (self-contained) into stage ==' -ForegroundColor Cyan

    # Fail early if fetch.ps1 didn't land every PawnIO module: the csproj Condition="Exists" would
    # skip the missing one WITHOUT error and the MSI would silently ship without it.
    $srcDir = Join-Path $root 'native\PawnIO'
    $missingSrc = $pawnModules | Where-Object { -not (Test-Path (Join-Path $srcDir $_)) }
    if ($missingSrc) {
        throw "PawnIO module(s) missing from native\PawnIO: $($missingSrc -join ', '). Run native\PawnIO\fetch.ps1 first."
    }

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

# Scheduled task: at any user's logon/reconnect, elevated (HighestAvailable, no UAC prompt), in the
# active interactive session. ONE instance per machine (IgnoreNew): the helper publishes to the
# Global\ map every session reads, and owns the machine-unique NT Kernel Logger session. Launched
# via conhost --headless (native hidden console; wscript from a scheduled task trips some AVs).
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
    <!-- Fast user switching: the helper dies with the session that started it (interactive token);
         these bring it back in whichever session becomes active. IgnoreNew keeps it single. -->
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>ConsoleConnect</StateChange></SessionStateChangeTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>RemoteConnect</StateChange></SessionStateChangeTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionUnlock</StateChange></SessionStateChangeTrigger>
    <!-- Watchdog. The events above only cover ARRIVING at a session, so a helper that died in a
         session you never left (it crashed, or you killed it) stayed dead until the next logon —
         and nothing else could revive it: the task is registered by SYSTEM, and an unelevated UI
         asking for `schtasks /Run` gets "access denied", so the app cannot start its own helper
         without a UAC prompt. A minute of repetition is what the sidebar has instead. This is free
         when the helper is healthy: IgnoreNew (above) means the scheduler drops the run without
         launching anything, which is also why the interval can be this short.
         The start boundary is a fixed date in the past so the first repetition is due immediately
         (with StartWhenAvailable, right after install) rather than at some future o'clock. -->
    <TimeTrigger>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
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
      <Command>C:\Windows\System32\conhost.exe</Command>
      <Arguments>--headless "$installDir\SidebarMonitor.Etw.exe"</Arguments>
    </Exec>
  </Actions>
</Task>
"@ | Set-Content -Path $taskXml -Encoding Unicode

# UI task: the HKLM Run key only fires at LOGON. An upgrade has to kill the UI of every OTHER user
# with an open session (their binaries hold the files), and without this they would sit with no
# sidebar until they log off and back on. These session triggers hand it back the moment they return
# to their session. Unelevated (it's the user's own UI), group Users so it covers everybody.
# Safe to coexist with the Run key: the UI holds a PER-SESSION single-instance mutex (Local\), so a
# redundant trigger just exits.
$uiTaskXml = Join-Path $stage 'ui-task.xml'
@"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Restores the SidebarMonitor sidebar in a user's session (reconnect, unlock).</Description>
  </RegistrationInfo>
  <Triggers>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>ConsoleConnect</StateChange></SessionStateChangeTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>RemoteConnect</StateChange></SessionStateChangeTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionUnlock</StateChange></SessionStateChangeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$installDir\SidebarMonitor.UI.exe</Command>
    </Exec>
  </Actions>
</Task>
"@ | Set-Content -Path $uiTaskXml -Encoding Unicode

# License shown by the MSI UI (the app's own MIT license; the AMD SDK EULA is accepted in-app on
# first run). Minimal RTF wrapper around the plain-text LICENSE.
$licRtf = Join-Path $stage 'license.rtf'
$licTxt = (Get-Content (Join-Path $root 'LICENSE') -Raw) -replace '\\','\\' -replace '\{','\{' -replace '\}','\}'
$licTxt = ($licTxt -replace "`r?`n", '\par ')
"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18 $licTxt}" | Set-Content -Path $licRtf -Encoding ASCII

# Lite variant: drop AMD's proprietary redistributables from the stage so the MSI ships none of them.
# Only these are AMD's — RyzenShim.dll/AdlxShim.dll are our own object code and stay. The MS VC runtime
# stays too (it's Microsoft's redistributable, not AMD's).
if ($Lite) {
    Write-Host '== Lite: removing bundled AMD binaries from stage ==' -ForegroundColor Yellow
    foreach ($f in 'Platform.dll','Device.dll','AMDRyzenMasterDriver.sys','AMDRyzenMasterDriver.inf','AMDRyzenMasterDriver.cat') {
        Remove-Item (Join-Path $stage $f) -Force -ErrorAction SilentlyContinue
    }
}

# Last line of defence: whatever WiX is about to harvest from $stage MUST carry every PawnIO module.
# Catches a silently-dropped blob even on a -SkipPublish run over a stale stage.
$missingStage = $pawnModules | Where-Object { -not (Test-Path (Join-Path $stage $_)) }
if ($missingStage) {
    throw "PawnIO module(s) missing from the stage folder: $($missingStage -join ', '). The MSI would ship without them; aborting."
}

Write-Host '== Building MSI with WiX ==' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $out | Out-Null
# No ternary operator: that is PowerShell 7 syntax and this script targets Windows PowerShell 5.1.
if ($Lite) { $msi = Join-Path $out 'SidebarMonitor-lite.msi'; $flavor = 'lite' }
else       { $msi = Join-Path $out 'SidebarMonitor.msi';      $flavor = 'full' }
& wix build (Join-Path $here 'SidebarMonitor.wxs') `
    -ext WixToolset.UI.wixext `
    -d "Stage=$stage" -d "ProjectRoot=$root" -d "Version=$Version" -d "Flavor=$flavor" `
    -d "ReleaseUrl=$releaseUrl" `
    -arch x64 -o $msi
if ($LASTEXITCODE) { throw 'wix build failed' }

Write-Host "`n== Built ==" -ForegroundColor Green
Write-Host "  $msi  ($([math]::Round((Get-Item $msi).Length/1MB,1)) MB)"
