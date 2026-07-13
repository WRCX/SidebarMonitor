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

  Uso:   .\install.ps1                  (o doble clic en install.cmd)
         .\install.ps1 -SelfContained   incrusta el runtime .NET (no requiere .NET instalado)

  ASCII-only a proposito: install.cmd usa Windows PowerShell 5.1, que lee un .ps1 sin BOM como ANSI.
#>
param([switch]$SelfContained)

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
$app  = Join-Path $env:LOCALAPPDATA 'SidebarMonitor\app'
$taskName = 'SidebarMonitor Helper'
$runName  = 'SidebarMonitor'

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
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
# Cerrar la UI CON GRACIA primero: es un AppBar (reserva espacio en pantalla). Un kill duro no corre
# su OnClosing/RemoveAppBar, y DWM deja la franja/superficie vieja pintada hasta que repinta la UI
# nueva (el "rectangulo azul" transitorio). CloseMainWindow envia WM_CLOSE -> cierre limpio. El MSI ya
# lo hace via el Restart Manager de Windows; esto iguala la ruta manual.
Get-Process SidebarMonitor.UI -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Milliseconds 400
Get-Process SidebarMonitor.UI, SidebarMonitor.Agent, SidebarMonitor.Etw -ErrorAction SilentlyContinue |
    Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='wscript.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*run-helper-hidden.vbs*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

# 4. Publicar los tres en la misma carpeta (la UI encuentra el agente y el helper sus DLLs al lado).
Write-Host "[4/6] Publicando (tarda; el agente es AOT)..." -ForegroundColor Cyan
if (Test-Path $app) { Remove-Item "$app\*" -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $app | Out-Null

$sc = if ($SelfContained) { 'true' } else { 'false' }

# El agente es AOT (nativo, siempre self-contained); helper y UI, framework-dependent salvo -SelfContained.
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

# Lanzador oculto: WScript.Run con estilo 0 crea la consola del helper ya oculta (sin parpadeo).
# En VBScript, "" dentro de una cadena es una comilla literal, asi que """ruta""" -> "ruta" (con
# comillas, por si la ruta tiene espacios).
$exe = Join-Path $app 'SidebarMonitor.Etw.exe'
$vbs = Join-Path $app 'run-helper-hidden.vbs'
@(
    'Set sh = CreateObject("WScript.Shell")'
    ('sh.Run """{0}""", 0, False' -f $exe)
) | Set-Content -Path $vbs -Encoding ASCII

# 5. Autostart.
Write-Host "[5/6] Registrando arranque automatico..." -ForegroundColor Cyan
# Helper: tarea al inicio de sesion, elevada (RunLevel Highest no dispara UAC), en la sesion
# interactiva (el espacio de nombres Local\ debe coincidir con el de la UI y el agente).
$action    = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('//B //Nologo "{0}"' -f $vbs)
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

# UI: clave Run del usuario (sin elevar). La UI lanza el agente por su cuenta.
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name $runName -Value "`"$app\SidebarMonitor.UI.exe`""

# 6. Arrancar ya. El helper por la tarea (elevado); la UI de-elevada via el shell (explorer).
Write-Host "[6/6] Arrancando..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Milliseconds 400
Start-Process explorer.exe -ArgumentList "`"$app\SidebarMonitor.UI.exe`""

Write-Host "`n== Instalado ==" -ForegroundColor Green
Write-Host "  App:    $app"
Write-Host "  Helper: tarea '$taskName' (elevada, al inicio de sesion, sin consola)"
Write-Host "  UI:     clave Run de HKCU (arranca el agente)"
Write-Host "  Se reinicia solo en cada inicio de sesion. Desinstalar: .\uninstall.ps1`n"
