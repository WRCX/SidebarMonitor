# Downloads Intel's PresentMon CLI (the console app) next to this script as PresentMon.exe. It is
# MIT-licensed (GameTechDev/PresentMon) and freely redistributable; the elevated helper spawns it to
# measure game frame-timing from ETW (no injection). Gitignored — fetched on demand, like the SDKs.
#
# ASCII-only (Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'
$dst = Join-Path $PSScriptRoot 'PresentMon.exe'
if (Test-Path $dst) { Write-Host 'PresentMon.exe already present. Delete it to refetch.'; exit 0 }

$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:ProgramFiles\GitHub CLI;$env:PATH"
$gh = (Get-Command gh -ErrorAction SilentlyContinue)?.Source
if ($gh) {
    # gh handles the redirect + picks the x64 console exe asset.
    & gh release download --repo GameTechDev/PresentMon --pattern 'PresentMon-*-x64.exe' --dir $PSScriptRoot --clobber
    $asset = Get-ChildItem (Join-Path $PSScriptRoot 'PresentMon-*-x64.exe') | Select-Object -First 1
    if ($asset) { Move-Item $asset.FullName $dst -Force }
} else {
    # Fallback without gh: query the API and download the console-app asset.
    $rel = Invoke-RestMethod 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest' -Headers @{ 'User-Agent' = 'SidebarMonitor' }
    $url = ($rel.assets | Where-Object { $_.name -like 'PresentMon-*-x64.exe' } | Select-Object -First 1).browser_download_url
    if (-not $url) { throw 'Could not find the PresentMon x64 console asset.' }
    Invoke-WebRequest $url -OutFile $dst
}
if (Test-Path $dst) { Write-Host "Fetched $dst" } else { throw 'PresentMon.exe download failed.' }
