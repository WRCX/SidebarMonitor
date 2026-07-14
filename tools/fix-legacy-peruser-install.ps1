<#
  Limpia la instalacion PER-USUARIO antigua (anterior a 1.4.4) que rompe el arranque.

  El problema: hasta 1.4.3, install.ps1 publicaba framework-dependent en
  %LOCALAPPDATA%\SidebarMonitor\app y registraba el autostart de la UI en HKCU\...\Run. Esa copia
  EXIGE tener el runtime .NET 10 instalado; en un equipo sin el, arranca y muere con el dialogo
  "You must install or update .NET to run this application" -- aunque el MSI 1.4.4 (self-contained,
  en Program Files) este perfectamente instalado, porque ambas arrancan en el logon.

  Que hace: para los procesos, borra la clave Run de HKCU y la carpeta de la app antigua. NO toca la
  instalacion por maquina (Program Files) ni tu configuracion (ui.json).

  Uso (por CADA usuario de Windows afectado; no necesita administrador):
      powershell -ExecutionPolicy Bypass -File tools\fix-legacy-peruser-install.ps1

  ASCII-only a proposito.
#>
$ErrorActionPreference = 'Continue'

$oldApp  = Join-Path $env:LOCALAPPDATA 'SidebarMonitor\app'
$runName = 'SidebarMonitor'
$newUi   = Join-Path $env:ProgramFiles 'SidebarMonitor\SidebarMonitor.UI.exe'

Write-Host "== Limpiando la instalacion per-usuario antigua ==" -ForegroundColor Cyan

$run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $runName -ErrorAction SilentlyContinue
if ($run) {
    Write-Host "  Autostart HKCU (viejo): $($run.$runName)" -ForegroundColor DarkGray
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $runName -ErrorAction SilentlyContinue
    Write-Host "  Clave Run de HKCU borrada." -ForegroundColor Green
} else {
    Write-Host "  No hay clave Run en HKCU (ya limpio)." -ForegroundColor DarkGray
}

# Parar solo lo que corre desde la ubicacion antigua; el helper (elevado, por tarea) no se toca.
Get-Process SidebarMonitor.UI, SidebarMonitor.Agent -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($oldApp, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Write-Host "  Parando $($_.ProcessName) (PID $($_.Id)) de la ruta antigua." -ForegroundColor DarkGray; Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 500

if (Test-Path $oldApp) {
    Remove-Item $oldApp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Carpeta antigua borrada: $oldApp" -ForegroundColor Green
} else {
    Write-Host "  No existe la carpeta antigua (ya limpio)." -ForegroundColor DarkGray
}

Write-Host "`n== Comprobando la instalacion por maquina ==" -ForegroundColor Cyan
if (Test-Path $newUi) {
    Write-Host "  OK: $newUi" -ForegroundColor Green
    $hklm = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $runName -ErrorAction SilentlyContinue
    if ($hklm) { Write-Host "  Autostart HKLM: $($hklm.$runName)" -ForegroundColor Green }
    else       { Write-Host "  AVISO: falta el autostart en HKLM; reinstala el MSI." -ForegroundColor Yellow }
    if (-not (Get-Process SidebarMonitor.UI -ErrorAction SilentlyContinue)) {
        Start-Process $newUi
        Write-Host "  UI arrancada desde Program Files." -ForegroundColor Green
    }
} else {
    Write-Host "  AVISO: no hay instalacion en Program Files. Instala el MSI 1.4.4+ desde:" -ForegroundColor Yellow
    Write-Host "  https://github.com/WRCX/SidebarMonitor/releases/latest" -ForegroundColor Yellow
}

Write-Host "`n== Listo ==" -ForegroundColor Green
