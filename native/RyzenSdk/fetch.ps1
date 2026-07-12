# Copies the minimal AMD Ryzen Master Monitoring SDK files this project needs into this folder,
# so the published helper is self-contained and does not require the SDK installed at runtime.
#
# The monitoring path (GetPlatform -> Init -> GetDevice(dtCPU) -> GetCPUParameters) pulls in only
# Platform.dll -> Device.dll plus the AMD kernel driver and the VC++ runtime. None of the ~90 MB of
# Qt DLLs the SDK ships (those drive AMD's own GUI) are touched, so we deliberately leave them out.
#
# These are AMD's redistributables and Microsoft's VC runtime -- gitignored, never committed.
# Run this once on a machine that has the SDK installed; after that the build carries them.
#
# ASCII-only on purpose: install.cmd invokes Windows PowerShell 5.1, which reads a BOM-less .ps1 as
# ANSI, so any non-ASCII byte would break parsing.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$sdk = (Get-ItemProperty 'HKLM:\SOFTWARE\AMD\RyzenMasterMonitoringSDK' -ErrorAction SilentlyContinue).InstallationPath
if (-not $sdk) { $sdk = 'C:\Program Files\AMD\RyzenMasterMonitoringSDK\' }
$bin = Join-Path $sdk 'bin'
if (-not (Test-Path $bin)) {
    throw "No encuentro el SDK de AMD en '$bin'. Instala el Ryzen Master Monitoring SDK y reintenta."
}

# From the SDK: the two interface DLLs plus the signed kernel driver (HVCI-compatible).
$fromSdk = @('Platform.dll', 'Device.dll',
             'AMDRyzenMasterDriver.sys', 'AMDRyzenMasterDriver.inf', 'AMDRyzenMasterDriver.cat')
# The VC++ runtime Device.dll links against. Present in System32 wherever the redist is installed;
# bundling it means the helper runs on a machine that never had it.
$fromSys = @('VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'MSVCP140.dll')

$copied = 0
foreach ($f in $fromSdk) {
    $src = Join-Path $bin $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $here $f) -Force; $copied++; Write-Host "  + $f (SDK)" }
    else { Write-Warning "  ! falta en el SDK: $f" }
}
foreach ($f in $fromSys) {
    $src = Join-Path $env:WINDIR "System32\$f"
    if (Test-Path $src) { Copy-Item $src (Join-Path $here $f) -Force; $copied++; Write-Host "  + $f (System32)" }
    else { Write-Warning "  ! falta en System32: $f (instala el VC++ 2015-2022 x64 redist)" }
}

# AMD's EULA (License.rtf, at the SDK root, not bin). We must show it and get the user's acceptance
# before opening the SDK (redistribution condition, Sec. 2). Bundling it lets the first-run dialog
# open the real document even on machines that never had the SDK installed.
$eula = Join-Path $sdk 'License.rtf'
if (Test-Path $eula) { Copy-Item $eula (Join-Path $here 'License.rtf') -Force; $copied++; Write-Host "  + License.rtf (SDK EULA)" }
else { Write-Warning "  ! no encuentro License.rtf en '$sdk' (necesaria para el aviso de 1er arranque)" }

Write-Host "`n$copied archivos en $here. El helper ya no necesita el SDK instalado." -ForegroundColor Green
