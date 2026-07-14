# Multi-usuario: diagnóstico y diseño (2026-07-14)

## Qué estaba roto

Con **varios usuarios de Windows con sesión abierta a la vez** (cambio rápido de usuario), el
diseño anterior se rompía por construcción, en tres capas independientes:

1. **El kernel logger es único por máquina.** El helper usa la sesión
   `NT Kernel Logger` (`KernelTraceEventParser.KernelSessionName`), de la que Windows permite
   exactamente **una** instancia machine-wide — y `TraceEventSession` *se apropia* de una sesión
   existente con el mismo nombre. Dos helpers (uno por usuario) no pueden coexistir: el segundo le
   robaba la sesión ETW al primero y sus datos morían en silencio.

2. **Los mapas `Local\` son por sesión.** `Local\SidebarMonitor.Etw` vive en el espacio de nombres
   de la sesión que lo crea: el helper del usuario A era **invisible** para la UI del usuario B,
   cuya barra decía "sin helper" aunque hubiera un helper perfectamente vivo en la máquina.

3. **La tarea del helper (MSI) dispara para el grupo Users con `IgnoreNew`.** Con el helper del
   usuario A vivo, el logon del usuario B disparaba el trigger y `IgnoreNew` lo descartaba: B se
   quedaba sin helper hasta reiniciar (ya anotado en la auditoría 2026-07-13). Y el autostart de la
   UI es por máquina (HKLM Run), así que cada usuario lanzaba su UI + su agente: N usuarios = 2N+1
   procesos y solo una sesión con sensores completos.

Además, la variante `install.ps1` instalaba en `%LOCALAPPDATA%` (por usuario): los demás usuarios
no tenían nada — y algunos AV (Bitdefender ATD) matan al vuelo un exe **sin firmar** en carpeta de
usuario lanzado **elevado por tarea programada** (patrón de persistencia de malware). Ese era el
"sin helper al iniciar sesión" de esta máquina: la tarea moría con `LastTaskResult=1` sin rastro.

## El diseño correcto: por máquina, UN helper global, UI+agente por usuario

La restricción del kernel logger no se puede sortear — se abraza: **un único helper elevado por
máquina** que sirve a todas las sesiones.

| Pieza | Ámbito | Mecanismo |
|---|---|---|
| Helper (Etw) | **máquina** (1 instancia) | mapa **`Global\SidebarMonitor.Etw`** con DACL `Users:read` (el writer elevado lo fija con `SetKernelObjectSecurity`); mutex writer global; retry 30 s al arrancar |
| Agente + UI | **usuario/sesión** | mapa `Local\SidebarMonitor.Snapshot` por sesión, como siempre |
| Tarea del helper | máquina | grupo Users, `HighestAvailable`, **triggers**: logon + ConsoleConnect + RemoteConnect + SessionUnlock, `IgnoreNew` (ahora es la semántica correcta: instancia única) |
| Autostart UI | máquina (HKLM Run) | cada usuario que entra arranca SU UI, que lanza SU agente |
| Consentimientos | **máquina** (`ProgramData\SidebarMonitor`) | el helper es uno: el opt-in es decisión de máquina. El helper elevado crea el dir con ACL `Users:Modify` y migra los markers del `%LOCALAPPDATA%` antiguo (`ConsentMarker.EnsureMachineDir`); lectura con fallback al path legado |
| Binarios (`install.ps1`) | máquina (`%ProgramFiles%\SidebarMonitor`) | igual que el MSI; ubicación de confianza para los AV; limpia la instalación per-user anterior |

### Ciclo de vida con cambio rápido de usuario

- A inicia sesión → tarea dispara → helper (elevado, token interactivo de A) publica en `Global\`.
- B inicia sesión (A sigue dentro) → trigger disparado e ignorado (`IgnoreNew`) — **correcto**,
  porque la UI de B lee el mapa `Global\` del helper de A.
- A cierra sesión → su helper muere con la sesión → al conectar/desbloquear B (o en su próximo
  logon) los triggers de sesión relanzan el helper en la sesión de B; el retry de 30 s absorbe la
  carrera con los lectores que aún retienen el mapa viejo.

## Una sola versión por máquina (y por qué no hay alternativa)

**No es posible que un usuario se quede en 1.4.3 mientras otro usa 1.4.6 en el mismo PC.** No es una
decisión de producto, es la consecuencia forzosa de la restricción de arriba: un único kernel logger
→ un único helper → un único mapa `Global\` → **una única versión de contrato**. Con dos versiones
conviviendo, el helper (que es uno, de una versión) publicaría un contrato que la UI de la otra
rechazaría por versión: esa persona vería «sin helper» y se quedaría sin sensores. Peor que
actualizar.

Lo que sí se controla es **quién puede** cambiar la versión de todos, y **a qué coste**:

| Salvaguarda | Efecto |
|---|---|
| MSI per-machine → `msiexec` pide UAC | Un usuario **estándar no puede actualizar**: no puede imponer una versión a los demás |
| `AutoInstallUpdates` = false por defecto | La actualización es un acto deliberado, no algo que pase solo |
| Auto-instalación **desactivada si hay otra sesión iniciada** | «Silencioso» nunca significa «silencioso para la persona a la que le pasa» |
| El diálogo de actualizar **nombra a los otros usuarios conectados** | Quien actualiza decide con la consecuencia delante (`Updater.OtherLoggedInUsers`, vía WTS) |
| Tarea **«SidebarMonitor UI»** (Users, sin elevar; triggers de conexión/desbloqueo) | Al usuario al que la actualización le cerró la barra se la devuelve al volver a su sesión, sin esperar a un logon |

## Instancia única — por SESIÓN, no por máquina

La UI toma un mutex **`Local\SidebarMonitor.UI.singleton`** al arrancar y, si ya está tomado, sale
en silencio (código 0). El detalle que importa: el espacio de nombres `Local\` **es por sesión**, así
que el guardián es por sesión de manera natural. Cada usuario de Windows tiene su propia barra; el
guardián **nunca cruza sesiones** ni le niega su instancia a un usuario legítimo.

Hace falta porque ahora hay varias rutas apuntando a la misma UI (clave Run de HKLM en el logon, la
tarea de reconexión/desbloqueo, el relanzado tras actualizar, el acceso directo del menú Inicio), y
dos UIs en una misma sesión se pelean: dos AppBars reservando espacio de pantalla, dos agentes
(el segundo muere al no poder ser escritor del mismo mapa `Local\`), dos iconos de bandeja y dos
escritores de `ui.json`. Perder la carrera no es un error: la barra que el usuario quería ya está en
pantalla. Las ejecuciones efímeras de QA (`--seconds`, `--shot=`, `--frames=`, overrides de
depuración) se saltan el guardián, para poder correr junto a la instancia real.

Un cierre a lo bruto no deja el mutex bloqueado: se recoge el `AbandonedMutexException` y la sesión
pasa al nuevo dueño.

### Notas de seguridad

- El mapa `Global\` queda **solo-lectura** para Users (GR → `SECTION_MAP_READ`); escribir sigue
  siendo de SYSTEM/Administradores/dueño. Mismo perfil que la auditoría dio por bueno para el mapa
  del helper (la etiqueta de integridad ya impedía write-up).
- `ProgramData\SidebarMonitor` con `Users:Modify` permite a cualquier usuario consentir/retirar
  opt-ins — deliberado: son toggles de sensores de hardware de la máquina, no secretos. El dump de
  diagnóstico (`DiagBridge`) vive en el mismo dir y no contiene datos personales (versión y floats
  del PM_Table).

### Compatibilidad

`EtwLayout.Version` 14→15 en el mismo ciclo (junto con `CpuLimitMhz`): helper y agente de versiones
distintas simplemente no se emparejan (el lector rechaza la versión) y la UI muestra "sin helper" —
la degradación de siempre. El MSI y `install.ps1` despliegan los tres juntos.
