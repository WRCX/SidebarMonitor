# Fetches the ADLX SDK headers + helper/platform source into this folder so native\AdlxShim\build.cmd
# can compile AdlxShim.dll. The ADLX SDK is AMD-proprietary (see the "ADLX SDK License Agreement.pdf"
# it ships): its headers/source may NOT be redistributed, so they are gitignored and pulled on demand.
# Only our compiled AdlxShim.dll (Object Code) is shipped, under the app's first-run AMD EULA.
#
# ASCII-only (Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git'

$sdkDir = Join-Path $root 'SDK'
if (Test-Path (Join-Path $sdkDir 'Include\ADLX.h')) {
    Write-Host 'ADLX SDK headers already present. Delete native\AdlxSdk\SDK to refetch.'
    exit 0
}

$tmp = Join-Path $env:TEMP ("adlx_" + [Guid]::NewGuid().ToString('N'))
try {
    Write-Host "Cloning ADLX SDK into $tmp ..."
    git clone --depth 1 $repo $tmp
    if ($LASTEXITCODE -ne 0) { throw "git clone failed ($LASTEXITCODE)" }

    New-Item -ItemType Directory -Force -Path $sdkDir | Out-Null
    # Only the pieces the build needs: interface headers, and the ADLXHelper + platform sample source.
    Copy-Item (Join-Path $tmp 'SDK\Include')      (Join-Path $sdkDir 'Include')      -Recurse -Force
    Copy-Item (Join-Path $tmp 'SDK\ADLXHelper')   (Join-Path $sdkDir 'ADLXHelper')   -Recurse -Force
    Copy-Item (Join-Path $tmp 'SDK\Platform')     (Join-Path $sdkDir 'Platform')     -Recurse -Force
    Write-Host 'ADLX SDK headers fetched into native\AdlxSdk\SDK.'
}
finally {
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}
