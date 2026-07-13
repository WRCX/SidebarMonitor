# Downloads the signed PawnIO modules (namazso/PawnIO.Modules, LGPL-2.1) next to this script:
# RyzenSMU.bin (AMD Tctl via SMN), IntelMSR.bin (Intel temp/power/clock via MSR) and LpcACPIEC.bin
# (ACPI embedded-controller port I/O, for laptop fan speed), plus COPYING (the LGPL text - keep it
# next to any redistributed .bin). The modules only run inside the PawnIO driver, which the USER
# installs from https://pawnio.eu - we never ship the driver itself. Gitignored - fetched on demand,
# like the SDKs.
#
# ASCII-only (Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'
$modules = 'RyzenSMU.bin', 'IntelMSR.bin', 'LpcACPIEC.bin', 'COPYING'

# Nothing to do if every module is already present.
$missing = $modules | Where-Object { -not (Test-Path (Join-Path $PSScriptRoot $_)) }
if (-not $missing) { Write-Host 'All PawnIO modules already present. Delete one to refetch.'; exit 0 }

$rel = Invoke-RestMethod 'https://api.github.com/repos/namazso/PawnIO.Modules/releases/latest' -Headers @{ 'User-Agent' = 'SidebarMonitor' }
$url = ($rel.assets | Where-Object { $_.name -like 'release_*.zip' } | Select-Object -First 1).browser_download_url
if (-not $url) { throw 'Could not find the PawnIO.Modules release zip.' }

$zip = Join-Path $PSScriptRoot 'modules.zip'
Invoke-WebRequest $url -OutFile $zip
$tmp = Join-Path $PSScriptRoot 'unzip'
Expand-Archive $zip -DestinationPath $tmp -Force
foreach ($f in $modules) {
    $src = Get-ChildItem $tmp -Recurse -Filter $f | Select-Object -First 1
    if ($src) { Copy-Item $src.FullName (Join-Path $PSScriptRoot $f) -Force }
}
Remove-Item $zip -Force
Remove-Item $tmp -Recurse -Force

$still = $modules | Where-Object { -not (Test-Path (Join-Path $PSScriptRoot $_)) }
if ($still) { throw "Extraction failed for: $($still -join ', ')" }
Write-Host "Fetched PawnIO modules ($($rel.tag_name)): $($modules -join ', ')"
