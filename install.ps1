<#
  Instalador de SidebarMonitor. Deja el stack instalado y arrancando solo en cada inicio de sesion,
  sin ninguna ventana de consola y sin depender de HWiNFO ni del SDK de AMD instalado.

  Que hace:
    1. Compila RyzenShim.dll (puente al SDK de AMD) si falta.
    2. Empaqueta las DLLs minimas del SDK de AMD + runtime VC (native/RyzenSdk) si faltan.
    3. Publica agente (AOT), helper (elevado) y UI en %LOCALAPPDATA%\SidebarMonitor\app.
    4. Registra el helper como tarea programada elevada al inicio de sesion (sin UAC, sin consola).
    5. Registra la UI en el arranque del usuario (la UI lanza el agente ella misma).
    6. Lo arranca todo ya.

  Necesita administrador solo para crear la tarea elevada: se auto-eleva si hace falta.

  Uso:   .\install.ps1                      (o doble clic en install.cmd) -- self-contained, como el MSI
         .\install.ps1 -FrameworkDependent  publicacion ligera para iterar en desarrollo; EXIGE que el
                                            runtime .NET 10 este instalado en la maquina

  ASCII-only a proposito: install.cmd usa Windows PowerShell 5.1, que lee un .ps1 sin BOM como ANSI.
#>
# Self-contained POR DEFECTO: esto instala por maquina (Program Files) para TODOS los usuarios, igual
# que el MSI, y una instalacion asi no puede depender de que haya un runtime .NET instalado -- si no
# lo hay, la UI muere con "You must install or update .NET". El runtime va incrustado; -FrameworkDependent
# solo para iterar rapido en la maquina de desarrollo (donde el SDK ya esta).
param([switch]$FrameworkDependent)

$ErrorActionPreference = 'Stop'

# --- auto-elevacion: registrar una tarea con RunLevel Highest requiere token de administrador ---
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevando (la tarea del helper necesita administrador)..." -ForegroundColor Yellow
    $psi = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($SelfContained) { $psi += ' -SelfContained' }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $psi -Verb RunAs
    return
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
# Per-MACHINE install (Program Files), like the MSI: one copy for every Windows user, the helper
# task fires for whichever user logs in, and AVs trust the location (LOCALAPPDATA + scheduled-task
# elevation is a malware-persistence pattern some AVs kill on sight). The old per-user location
# (%LOCALAPPDATA%\SidebarMonitor\app) is cleaned up below if present.
$app  = Join-Path $env:ProgramFiles 'SidebarMonitor'
$oldApp = Join-Path $env:LOCALAPPDATA 'SidebarMonitor\app'
$taskName   = 'SidebarMonitor Helper'
$uiTaskName = 'SidebarMonitor UI'
$runName    = 'SidebarMonitor'

# vswhere (para la publicacion AOT del agente) no siempre esta en el PATH.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vsInstaller) { $env:PATH = "$vsInstaller;$env:PATH" }

# dotnet no siempre esta en el PATH (p.ej. instalaciones nuevas). Resolverlo explicitamente.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $cand = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $cand) { $dotnet = $cand } else { throw 'No se encontro dotnet: instala el SDK de .NET 10 (https://dot.net).' }
}

Write-Host "== SidebarMonitor: instalando en $app ==`n" -ForegroundColor Cyan

# 1. Puente nativo al SDK de AMD. OPCIONAL: en un equipo Intel (o sin toolchain C++/SDK de AMD) se
#    omite y el stack se instala igual -- los sensores avanzados de AMD simplemente no estaran, pero
#    los de Intel (MSR) y el ventilador (EC) via PawnIO funcionan sin este puente.
$shim = Join-Path $root 'native\RyzenShim\RyzenShim.dll'
if (-not (Test-Path $shim)) {
    Write-Host "[1/6] Compilando RyzenShim.dll (opcional, AMD)..." -ForegroundColor Cyan
    try { & (Join-Path $root 'native\RyzenShim\build.cmd') } catch { }
    if (-not (Test-Path $shim)) { Write-Host "  Omitido: sin toolchain C++/SDK de AMD. Los sensores CPU de AMD via SDK no estaran (Intel/EC no se ven afectados)." -ForegroundColor Yellow }
} else { Write-Host "[1/6] RyzenShim.dll ya presente." -ForegroundColor DarkGray }

# 2. DLLs del SDK de AMD empaquetadas (opcional, misma logica que el puente).
if (-not (Test-Path (Join-Path $root 'native\RyzenSdk\Platform.dll'))) {
    Write-Host "[2/6] Empaquetando DLLs del SDK de AMD (opcional)..." -ForegroundColor Cyan
    try { & (Join-Path $root 'native\RyzenSdk\fetch.ps1') } catch { Write-Host "  Omitido: no se pudo obtener el SDK de AMD (no necesario en Intel)." -ForegroundColor Yellow }
} else { Write-Host "[2/6] DLLs del SDK ya empaquetadas." -ForegroundColor DarkGray }

# 3. Parar lo que este corriendo y soltar la tarea, para no chocar con los ficheros al publicar.
Write-Host "[3/6] Parando el stack anterior (si lo hay)..." -ForegroundColor Cyan
# Las tareas primero: sus triggers de sesion podrian relanzar helper o UI a media publicacion.
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $uiTaskName -Confirm:$false -ErrorAction SilentlyContinue

# Parada cooperativa del helper: sondea este fichero cada ventana y cierra su sesion ETW de kernel
# limpiamente, en vez de morir a hachazos a media escritura.
$stopFile = Join-Path $env:ProgramData 'SidebarMonitor\helper-stop'
New-Item -ItemType Directory -Force (Split-Path $stopFile) | Out-Null
Set-Content -Path $stopFile -Value "shutdown requested (install.ps1).`n"

# Cerrar la UI CON GRACIA y ANTES que el agente. Dos razones:
#  - Es un AppBar (reserva espacio en pantalla): un kill duro no corre su OnClosing/RemoveAppBar y
#    DWM deja la franja vieja pintada hasta que repinta la UI nueva.
#  - La UI RESUCITA al agente si lo ve muerto (recuperacion de agente caido, backoff ~2 s). Matar el
#    agente con la UI viva lo revive justo cuando estamos borrando su exe -> "fichero en uso".
Get-Process SidebarMonitor.UI -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Milliseconds 600
Get-Process SidebarMonitor.UI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Get-Process SidebarMonitor.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

# Dar al helper una ventana para irse solo; si sigue ahi (colgado), forzarlo.
Start-Sleep -Seconds 2
Get-Process SidebarMonitor.Etw -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $stopFile -Force -ErrorAction SilentlyContinue   # que no mate al helper que arrancaremos
Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*run-helper-hidden.vbs*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

# 4. Publicar los tres en la misma carpeta (la UI encuentra el agente y el helper sus DLLs al lado).
Write-Host "[4/6] Publicando (tarda; el agente es AOT)..." -ForegroundColor Cyan
if (Test-Path $app) { Remove-Item "$app\*" -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $app | Out-Null

$sc = if ($FrameworkDependent) { 'false' } else { 'true' }

# El agente es AOT (nativo, siempre self-contained); helper y UI llevan el runtime incrustado salvo
# -FrameworkDependent.
# El agente publica con NativeAOT, que necesita el toolchain C++ (link.exe). Si no esta, se
# reintenta sin AOT como self-contained (nativo no, pero funciona igual y sin runtime instalado).
$agentCsproj = Join-Path $root 'src\SidebarMonitor.Agent\SidebarMonitor.Agent.csproj'
& $dotnet publish $agentCsproj -c Release -r win-x64 -o $app --nologo -v q
if ($LASTEXITCODE) {
    Write-Host "  AOT fallo (sin toolchain C++). Publicando el agente sin AOT (self-contained)..." -ForegroundColor Yellow
    & $dotnet publish $agentCsproj -c Release -r win-x64 --self-contained true `
        -p:PublishAot=false -p:IsAotCompatible=false -o $app --nologo -v q
    if ($LASTEXITCODE) { throw "Fallo la publicacion del agente (con y sin AOT)." }
}
& $dotnet publish (Join-Path $root 'src\SidebarMonitor.Etw\SidebarMonitor.Etw.csproj') `
    -c Release -r win-x64 --self-contained $sc -o $app --nologo -v q
if ($LASTEXITCODE) { throw "Fallo la publicacion del helper." }
& $dotnet publish (Join-Path $root 'src\SidebarMonitor.UI\SidebarMonitor.UI.csproj') `
    -c Release -r win-x64 --self-contained $sc -o $app --nologo -v q
if ($LASTEXITCODE) { throw "Fallo la publicacion de la UI." }

# 5. Autostart.
Write-Host "[5/6] Registrando arranque automatico..." -ForegroundColor Cyan
# Helper: UNA instancia por maquina para el grupo Users (el kernel logger ETW es unico por maquina
# y el mapa es Global\), elevada sin UAC (HighestAvailable). Dispara en el logon de CUALQUIER
# usuario y ademas al conectar/desbloquear sesion: si el usuario que lo arranco cierra sesion (el
# helper muere con ella), renace en la sesion del siguiente usuario activo. IgnoreNew garantiza la
# instancia unica. Accion via conhost --headless: consola oculta nativa, sin VBS ni wscript (que
# algunos AV matan al venir del programador de tareas).
$exe = Join-Path $app 'SidebarMonitor.Etw.exe'
$taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts the elevated SidebarMonitor helper (ETW + CPU sensors) for whichever user session is active.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled></LogonTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>ConsoleConnect</StateChange></SessionStateChangeTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>RemoteConnect</StateChange></SessionStateChangeTrigger>
    <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionUnlock</StateChange></SessionStateChangeTrigger>
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
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$env:SystemRoot\System32\conhost.exe</Command>
      <Arguments>--headless "$exe"</Arguments>
    </Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskName $taskName -Xml $taskXml -Force | Out-Null

# UI: clave Run por maquina (HKLM) — cada usuario que inicie sesion arranca SU UI, y la UI lanza
# su agente (ambos por sesion via el mapa Local\). Limpiar la clave HKCU de la instalacion
# per-user anterior para no arrancar la UI dos veces.
Set-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name $runName -Value "`"$app\SidebarMonitor.UI.exe`""
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name $runName -ErrorAction SilentlyContinue

# Tarea de la UI: la clave Run solo dispara en el LOGON. Una actualizacion mata la UI de los demas
# usuarios con sesion abierta (sus binarios bloquean los ficheros), y sin esto se quedarian sin barra
# hasta cerrar y reabrir sesion. Con los triggers de conexion/desbloqueo la recuperan al volver a su
# sesion. Sin elevar (es la UI del usuario) y para el grupo Users, asi cubre a todos.
# Es seguro que coexista con la clave Run: la UI tiene guarda de instancia unica POR SESION (mutex
# Local\), asi que un disparo de mas simplemente sale sin hacer nada.
$uiTaskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Restores the SidebarMonitor sidebar in a user's session (logon, reconnect, unlock).</Description>
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
      <Command>$app\SidebarMonitor.UI.exe</Command>
    </Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskName $uiTaskName -Xml $uiTaskXml -Force | Out-Null

# Migracion: borrar los binarios de la instalacion per-user anterior (los markers de consentimiento
# los migra el propio helper a ProgramData en su primer arranque).
if (Test-Path $oldApp) { Remove-Item $oldApp -Recurse -Force -ErrorAction SilentlyContinue }

# 6. Arrancar ya. El helper por la tarea (elevado); la UI de-elevada via el shell (explorer).
Write-Host "[6/6] Arrancando..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Milliseconds 400
Start-Process explorer.exe -ArgumentList "`"$app\SidebarMonitor.UI.exe`""

Write-Host "`n== Instalado (por maquina) ==" -ForegroundColor Green
Write-Host "  App:    $app"
Write-Host "  Helper: tarea '$taskName' (elevada, UNA por maquina, logon/conexion de cualquier usuario)"
Write-Host "  UI:     clave Run de HKLM (una UI+agente por usuario que inicie sesion)"
Write-Host "  Se reinicia solo en cada inicio de sesion. Desinstalar: .\uninstall.ps1`n"
