<#
  Desinstala SidebarMonitor: para el stack, quita la tarea del helper, la clave Run de la UI y,
  por defecto, borra la carpeta de la app. La configuracion (ui.json) se conserva salvo -Purge.

  Uso:  .\uninstall.ps1           quita binarios y autostart, conserva la config
        .\uninstall.ps1 -Purge    ademas borra la configuracion

  ASCII-only a proposito (uninstall.cmd usa Windows PowerShell 5.1).
#>
param([switch]$Purge)

$ErrorActionPreference = 'Continue'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevando (borrar la tarea del helper necesita administrador)..." -ForegroundColor Yellow
    $psi = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Purge) { $psi += ' -Purge' }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $psi -Verb RunAs
    return
}

$base = Join-Path $env:LOCALAPPDATA 'SidebarMonitor'
$app  = Join-Path $base 'app'
$taskName = 'SidebarMonitor Helper'
$runName  = 'SidebarMonitor'

Write-Host "== Desinstalando SidebarMonitor ==" -ForegroundColor Cyan

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $runName -ErrorAction SilentlyContinue

Get-Process SidebarMonitor.UI, SidebarMonitor.Agent, SidebarMonitor.Etw -ErrorAction SilentlyContinue |
    Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*run-helper-hidden.vbs*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 600

if (Test-Path $app) { Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue }
if ($Purge -and (Test-Path $base)) {
    Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Configuracion borrada." -ForegroundColor DarkGray
}

Write-Host "== Desinstalado ==" -ForegroundColor Green
