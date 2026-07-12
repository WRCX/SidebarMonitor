# SidebarMonitor — Architecture & internals

> This is the deep technical write-up (design decisions, measurements, and every trap found along
> the way). For the user-facing overview, features, screenshots and install instructions, see the
> [main README](../README.md). This document is kept in Spanish, as written during development.

---

# SidebarMonitor

Sidebar nativa de monitorización para Windows: CPU, cores, vatios, RAM, red, discos,
GPU y top de procesos, siempre visible en el escritorio (pensada para una segunda pantalla).

Sustituye a los *Windows Desktop Gadgets* (`sidebar.exe`, binario de Windows 7 RTM revivido
vía 8GadgetPack / Gadgets Revived), que son la única opción decente que queda en el mercado
porque Microsoft retiró la plataforma en 2012 (Security Advisory 2719662, ejecución remota
de código). Esos gadgets sacan los datos por WMI + Core Temp, corren HTML/JS sin sandbox
sobre el motor de IE, y no reportan la potencia del paquete de CPU en Ryzen.

Estado actual: **fase de viabilidad**. Los dos proyectos de `src/` son sondas que validan
que cada fuente de datos funciona y cuánto cuesta. No hay UI todavía.

## Arquitectura

Dos fuentes, elegidas para **no enviar ningún driver de kernel** y **no requerir admin**:

| Dato | Fuente | Por qué |
|---|---|---|
| Potencia y temperatura de CPU | SDK de AMD Ryzen Master (helper elevado) | Requiere ring0; el driver de Ryzen Master esta firmado y es compatible con HVCI, asi que no shippeamos WinRing0 ni peleamos con la blocklist de drivers vulnerables |
| Temperatura de discos (NVMe / SATA) | IOCTL del log SMART (agente) / WMI `MSFT_StorageReliabilityCounter` (helper elevado) | NVMe sin admin; SATA por el reliability counter del stack de almacenamiento |
| CPU total y por core, frecuencia real, discos, red | PDH + IP Helper | Sin admin |
| RAM | `GlobalMemoryStatusEx` | Sin admin |
| Procesos | `NtQuerySystemInformation` | 1 llamada devuelve todo |
| GPU NVIDIA | NVML (`nvml.dll`, viene con el driver) | Documentada y estable; NVAPI solo para lo que NVML no expone |

La idea es un **agente** que muestrea y publica un snapshot, y una **UI separada** que lo
consume. Así la UI no corre elevada, y se puede añadir LibreHardwareMonitor como fallback
(o monitorizar otra máquina) sin rediseñar nada.

### El canal: memoria compartida con seqlock

`SidebarMonitor.Shared` define el contrato: un `Snapshot` de tamaño fijo (2248 B, todo structs
`[StructLayout(Sequential)]` con `[InlineArray]`, sin punteros ni offsets que reubicar) sobre
un memory-mapped file `Local\SidebarMonitor.Snapshot.v1`.

La sincronización es un **seqlock**, no un mutex: el agente incrementa un contador a impar
antes de escribir y a par después; la UI copia el payload y comprueba que el contador no
cambió. **El lector nunca bloquea al escritor, y no hay ningún mutex que se quede colgado si
un proceso muere.** Si la UI pilla una escritura a medias, reintenta. Medido: **100 ns por
lectura, 0 lecturas rotas en 100.000 intentos, 0 asignaciones.**

`Local\`, no `Global\`: crear un objeto de kernel `Global\` necesita `SeCreateGlobalPrivilege`,
que un proceso sin elevar no tiene. HWiNFO usa `Global\` porque corre elevado; nosotros no lo
necesitamos, agente y UI comparten sesión.

### Cadencia

Los procesos (`NtQuerySystemInformation`) son el coste dominante (~16 ms) y el dato menos
urgente, así que se muestrean **1 de cada 3 ticks** (`--proc-every=`). El efecto es nítido: un
tick con procesos cuesta ~16 ms, uno sin ellos **~1.2 ms** (JIT; menos con AOT). En los ticks
saltados, el último top se queda en el snapshot sin tocar.

## Sondas

```
dotnet build
src/NativeProbe/bin/Release/net10.0-windows/NativeProbe.exe    # PDH + RAM + red + procesos + NVML
src/ShellProbe/bin/Release/net10.0-windows/ShellProbe.exe --monitor=1 --seconds=10
```

El agente, la UI y el cliente de consola:

```
SidebarMonitor.Agent.exe  --interval=1000 --proc-every=3 --verbose   # muestrea y publica
SidebarMonitor.UI.exe     --monitor=1 --width=280                    # el panel
SidebarMonitor.Client.exe --watch                                    # el snapshot en texto
SidebarMonitor.Client.exe --bench                                    # coste de leer el snapshot
```

La UI arranca el agente ella sola si no lo encuentra publicando.

`ShellProbe` aplica cada comportamiento de ventana y luego **lo lee de vuelta por API**, así
que su salida es evidencia, no intención. Acepta `--monitor=N`, `--width=`, `--seconds=`,
`--left` y `--clickthrough`.

## Instalación y arranque automático

```
install.cmd                 # doble clic: publica, empaqueta y registra el arranque (se auto-eleva)
install.ps1 -SelfContained  # incrusta el runtime .NET (para máquinas sin .NET instalado)
uninstall.cmd               # quita binarios y autostart (uninstall.ps1 -Purge borra también la config)
```

El instalador:

1. Compila `RyzenShim.dll` si falta y **empaqueta las DLLs mínimas del SDK de AMD** con
   `native/RyzenSdk/fetch.ps1`: solo `Platform.dll`, `Device.dll`, el driver `AMDRyzenMasterDriver.*`
   y el runtime VC (`VCRUNTIME140`, `VCRUNTIME140_1`, `MSVCP140`) — **~1.5 MB**, nada de las ~90 MB
   de DLLs de Qt que el SDK trae para su propia GUI. `RyzenShim` carga `Platform.dll` de la carpeta
   donde vive el propio shim (empaquetado) antes que del SDK instalado, así que **en runtime el SDK
   no hace falta**. Las DLLs empaquetadas viven en `native/RyzenSdk/` (gitignored).
2. Publica agente (AOT), helper y UI en `%LOCALAPPDATA%\SidebarMonitor\app`.
3. Registra el **helper** (`SidebarMonitor.Etw`, que necesita admin) como **tarea programada** al
   inicio de sesión, `RunLevel Highest` (elevada, **sin UAC**), en la sesión interactiva. Se lanza
   por un `run-helper-hidden.vbs` (`WScript.Shell.Run` con estilo 0) para que la consola **nazca
   oculta, sin parpadeo**. La **UI** va en la clave `HKCU\...\Run` (sin elevar); ella lanza el agente.

Se auto-eleva porque crear la tarea necesita administrador. Todo arranca solo en cada inicio de
sesión, **sin ninguna ventana de consola**.

## Números medidos

Ryzen 7 7800X3D + RTX 4070 Ti SUPER, Windows 11, sin elevación:

| Fuente | Contenido | Coste |
|---|---|---|
| HWiNFO SHM | 23 sensores, 346 lecturas, 164 KiB | **~185 µs** |
| PDH | CPU total + 16 cores, frecuencia, discos | ~500 µs |
| `GlobalMemoryStatusEx` | RAM | ~100 µs |
| NVML | Load, VRAM, temp, potencia, relojes, fan, PCIe | ~1.4 ms |
| `NtQuerySystemInformation` | ~350 procesos, ~7500 threads | 6–16 ms |

Un snapshot completo ronda los **8–18 ms**, o sea **~0.05 % de CPU a 1 Hz**. La línea base a
batir, `sidebar.exe`, consume **2.18 % de CPU, 158 MiB, 134 threads y 1369 handles**.

### El camino caliente

Los 185 µs de HWiNFO son el coste de reconstruir *todo*, cadenas incluidas. Pero las
etiquetas y unidades no cambian mientras HWiNFO esté arriba: se leen una vez y se cachean.
Cada tick solo necesita los `double`. Medido con `--bench`:

| | Snapshot completo | Solo valores |
|---|---|---|
| JIT | 201.2 µs, 78.3 KiB | **6.2 µs, 0 KiB** |
| AOT | 116.4 µs, 78.3 KiB | **4.3 µs, 0 KiB** |

**4.3 µs por tick y cero asignaciones**, o sea 0.0004 % de un core a 1 Hz. El GC nunca
tiene motivo para despertarse.

### AOT vs JIT

| | AOT | JIT (framework-dependent) |
|---|---|---|
| Arranque en frío (mín / medio) | **59.6 / 62.7 ms** | 77.4 / 80.0 ms |
| Pico de working set | **30.3 MiB** | 43.5 MiB |
| En disco | 2.3 MB, un solo `.exe` | 0.2 MB + runtime .NET 10 aparte |

El pico de working set está inflado por el propio benchmark; el agente real, que no
reconstruye cadenas, se quedará muy por debajo.

`NtQuerySystemInformation` es el único punto caliente y el que más varía. Los procesos se
pueden muestrear a menor frecuencia que los sensores.

Validación cruzada: la potencia de la GPU sale **49.69 W por HWiNFO** y **49.76 W por NVML**,
por caminos independientes.

## Trampas descubiertas (leer antes de tocar nada)

**1. El layout de la SHM de HWiNFO no es el que documenta el SDK.** Los structs están
`pack(1)` (así que `poll_time` cae en el offset 12, sin padding de alineación) y las builds
actuales añaden gemelos UTF-8 de las cadenas de usuario que la documentación no lista. Eso
lleva los elementos a **392 y 460 bytes**, no a los 264 y 320 de la doc. Verificado a mano
sobre HWiNFO 7.72 con un volcado hexadecimal.

`HwiProbe` **valida los tamaños contra los que el propio header declara** y aborta si no
cuadran, en vez de escupir basura. Mantener esa comprobación: este layout cambia entre
versiones y falla en silencio. La pista que lo confirma es que
`sensorOffset + sensorCount × sensorElemSize == readingOffset`.

**2. Las etiquetas vienen localizadas.** `szLabelUser` está en el idioma de HWiNFO y en ANSI
("Memoria virtual comprometida"). Para hacer *matching* usar `szLabelOrig` (inglés, estable)
o `dwReadingID`; para mostrar, `szLabelUserUTF8`.

**3. Los contadores PDH por nombre localizado fallan** en un Windows en español
(`\Información del procesador\...`). Hay que usar `PdhAddEnglishCounterW`, que acepta los
nombres en inglés en cualquier idioma.

**4. Usar `% Processor Utility`, no `% Processor Time`**, que subcuenta con turbo/boost. Para
la frecuencia real: `% Processor Performance` × frecuencia nominal.

**5. `% Processor Utility` pasa de 100 y PDH no lo capa.** Se mide contra la frecuencia
nominal, así que un core en turbo reporta 105 % legítimamente. Quitar `PDH_FMT_NOCAP100` **no
cambia nada** (comprobado). Una barra de uso no puede estar más que llena: el agente hace el
`Clamp(0,100)`. Cuidado de no capar `% Processor Performance`, que es justo donde el turbo
tiene que verse.

## La ventana: verificado en Windows 11 25H2 (build 26200)

Todo lo que se temía que fuera frágil funciona. Medido con `ShellProbe` sobre el monitor
secundario:

| Comportamiento | Mecanismo | Resultado |
|---|---|---|
| Que las ventanas maximizadas no la tapen | AppBar (`ABM_NEW` + `ABM_QUERYPOS` + `ABM_SETPOS`) | Reservó los 260 px pedidos: la work area pasó de 1920 a 1660 px |
| No robar el foco | `WS_EX_NOACTIVATE` + `WM_MOUSEACTIVATE` → `MA_NOACTIVATE` | El foreground siguió siendo otra ventana |
| No salir en Alt+Tab | `WS_EX_TOOLWINDOW` + `ShowInTaskbar=false` | OK |
| Siempre encima | `WS_EX_TOPMOST` | OK |
| DPI por monitor | `dpiAwareness=PerMonitorV2` en el manifest | `PER_MONITOR_AWARE` |
| Visible en todos los escritorios virtuales | — | **Ya lo está**, sin hacer nada |

Ese último punto es el hallazgo importante. `GetWindowDesktopId` devuelve `GUID_NULL` para
nuestra ventana mientras que una ventana normal devuelve un GUID real: la ventana no está
asociada a ningún escritorio, o sea que aparece en todos. **No hace falta el pinning.**

Y si algún día hiciera falta, también funciona: el COM no documentado (`IServiceProvider` del
Immersive Shell → `IVirtualDesktopPinnedApps` → `PinAppID`) responde en esta build. Pero es
un contrato que Microsoft rota entre versiones de Windows, y una vtable mal adivinada es un
*access violation*, no una excepción. Por eso `ShellProbe` lo prueba **en un proceso aparte**
(`--pin-probe`): si revienta, no se lleva por delante el resto del diagnóstico.

Dos cosas que hay que respetar sí o sí:

- **Desregistrar el AppBar en todas las salidas**, incluida la caída. Un appbar huérfano deja
  el escritorio del usuario encogido. Windows lo recupera al morir el `hwnd` (comprobado),
  pero no dependemos de eso: hay handler en `UnhandledException` y `ProcessExit`.
- **`InvariantGlobalization=true` rompe WPF.** El font cache construye `CultureInfo` para los
  idiomas mayoritarios y lanza `CultureNotFoundException` en el primer layout de texto. El
  valor por defecto del repo es `true` (interesa para el agente); `ShellProbe` lo desactiva.

## Requisitos

- .NET 10 SDK (fijado en `global.json`).
- Para la temperatura/potencia de CPU: el **SDK de AMD Ryzen Master Monitoring** (aporta las DLLs
  y el driver firmado). El instalador empaqueta las DLLs mínimas, así que **en runtime no hace
  falta tenerlo instalado** — solo para compilar/empaquetar la primera vez. Ya **no se usa HWiNFO**.
- Para publicar con AOT, el workload **Desktop development with C++** de Visual Studio
  (el linker de MSVC). Verificado con Visual Studio Community 2026 (18.7).

```powershell
# ILCompiler invoca vswhere.exe esperandolo en el PATH. Si no esta, el texto del error
# acaba incrustado dentro del comando del linker y falla con un MSB3073 incomprensible.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/SidebarMonitor.Agent -c Release -r win-x64 -p:PublishAot=true -o artifacts/agent
```

El agente AOT es **un `.exe` de 1.83 MB** sin runtime. Un cliente JIT lee sin problema el
snapshot que publica un agente AOT: el header valida `PayloadSize`, y coincide.

### El agente en régimen (AOT, medido)

| | Agente AOT | `sidebar.exe` (gadgets) |
|---|---|---|
| CPU | **0.94 % de un core** = 0.06 % de la máquina | 2.18 % de la máquina |
| Working set privado | **28.3 MiB** | 62.6 MiB |
| Threads | **6** | 135 |

Cuidado al leer el working set *total* (43 MiB): **casi nada es nuestro.** `RTSSHooks64.dll`
(RivaTuner) se inyecta con 52 MiB en todos los procesos de esta máquina, `nvml.dll` aporta 18
y el hook de Bitdefender otro tanto. Nuestro binario son 1.89 MiB. Por eso la cifra a mirar es
el **working set privado**, y por eso tocar el GC no movió la aguja (probado: idéntico).

Nota para la UI: **WPF no soporta AOT.** O el agente va AOT y la UI en JIT, o la UI se hace
sobre Win2D / DirectComposition.

## La UI

**Un solo panel**, no cinco ventanas. Si fueran ventanas sueltas ya tendríamos la de HWiNFO.
Seis secciones — CPU, MEMORIA, GPU, RED, DISCOS, PROCESOS — en un AppBar de 280 px.

- **Plegar** una sección: clic en su cabecera. **Ocultarla** del todo: menú contextual.
  Todo se guarda en `%LOCALAPPDATA%\SidebarMonitor\ui.json`.
- **Reordenar** las secciones: menú contextual → **«Orden de secciones»**, con «▲ Subir» /
  «▼ Bajar» por sección. El orden se guarda en `ui.json` (`SectionOrder`).
- Una cabecera plegada **sigue informando**, con lo más útil de cada sección:
  `CPU 27% 4.70GHz 49W` · `GPU 0% 2640MHz 51W` · `DISCOS 1% R4K W1,5M` ·
  `PROCESOS pwsh 12.2% · bdservi… 1.7%`. Plegar pierde el detalle, no el dato.
- Las secciones plegadas u ocultas **no actualizan su cuerpo**; solo el resumen. Y una ventana
  minimizada no actualiza nada. Plegar abarata la UI de verdad, no solo visualmente.

### Colocación, bandeja y minimizado

Todo desde el menú contextual (clic derecho) o desde el icono de bandeja:

- **Anclado** (pegado a un borde) o **flotante**, arrastrable por su cabecera. La ventana nunca
  se activa, así que `DragMove` de WPF no sirve: el arrastre se hace con deltas de `GetCursorPos`,
  capturando el ratón en el propio elemento de la cabecera (capturar un ancestro rompe el arrastre).
- **Reservar espacio** (solo anclado) es un toggle aparte. Activado: registra un AppBar y el
  escritorio reserva la franja, nada lo tapa — pero maximizar, snap y pantalla completa de otras
  apps se paran en su borde. Desactivado: es un overlay pegado al borde, las demás ventanas usan
  todo el monitor y se dibujan por debajo. Es lo que quieres si el panel te cortaba el manejo de
  ventanas. Ojo: **reservar espacio es independiente de «siempre encima»** (eso es solo z-order).
- **Siempre encima** es una opción, no una constante. Borde izquierdo o derecho, monitor y
  ancho, todo en caliente.
- El **menú contextual** se cierra al clicar fuera aunque el panel no tome foco: al abrirse, su
  popup se trae al foreground (`SetForegroundWindow`), que es lo que le da la señal de «clic fuera».
- **Minimizar** colapsa el panel a una pestaña de 18 px con una flecha, en vez de ocultarlo:
  un AppBar de 18 px sigue reservando 18 px, así que el escritorio no pega saltos y el panel
  queda a un clic. Verificado: la work area pasa de 1920 a 1902 px.
- **Ocultar** lo quita de la vista; vuelve con doble clic en la bandeja. La ventana es
  `WS_EX_TOOLWINDOW` y no tiene botón en la barra de tareas, así que sin bandeja no habría
  forma de recuperarlo.

Los flags de línea de comandos (`--floating`, `--width=`, …) marcan la config como **efímera**:
una ejecución de prueba nunca reescribe lo que el usuario configuró por el menú.

Gráficas solo donde aportan: sparkline para lo que varía en el tiempo (uso de CPU y GPU,
red, disco), filas por core, y medidores para lo que es una fracción de un total (RAM, VRAM).
El resto son cifras. Nada de ejes ni rejillas: el valor actual va etiquetado al lado.

### Filas de core

Una fila por core: índice, barra horizontal segmentada por los procesos que lo ocupan, el `%`,
y el proceso dominante **nombrado en texto**. Verticales no caben: 16 barras en 280 px son
16,5 px cada una, y «100» no entra.

- **La longitud de la barra viene de PDH. Los segmentos solo la subdividen**, con las cuotas de
  muestras de ETW. Derivar la longitud de las muestras pintaría todos los cores en reposo al
  95 % (ver la trampa del C-state, más arriba).
- Color estable por **entidad**, vía FNV-1a del nombre: un proceso conserva su tono aunque
  cambie de puesto en el ranking, entre cores y entre reinicios. Si se coloreara por posición,
  las barras parpadearían cada segundo.
- El **azul está excluido** de la paleta de procesos: es el color de la serie CPU de la
  sparkline justo encima, y un proceso azul se leería como «la serie CPU».
- `System`/kernel usa un neutro reservado, nunca un slot de la paleta. Lo que queda fuera del
  top-3 va a un gris «otros», elegido para no confundirse con el track vacío (1.9:1 de
  contraste, comprobado) ni con el neutro del kernel.
- Separador de 1 px entre segmentos, para que dos tonos contiguos no se fundan.
- El nombre del dominante va en texto: **la identidad nunca depende solo del color.** Y el
  tooltip de cada fila desglosa el top-3.
- El **número de core** y el **nombre del dominante** se pintan con el color de ese proceso, para
  enlazar de un vistazo el índice, la barra y la etiqueta.

Sin el helper ETW, cada barra es un único segmento azul — exactamente el dibujo anterior — y
la barra de estado avisa con `sin ETW`.

### Discos

Un bloque por **disco físico**, con **su propia gráfica** de lectura/escritura. La cabecera es
el **modelo** (la identidad física), y debajo van sus **particiones con espacio usado/total**:

```
FIKWOT FN501 Pro 2TB                    42 °C
C: 226G/293G · juegos(E:) 947G/1,6T
SSD · NVMe · 2,0 TB
actividad 1 %
```

Esto es deliberado: un disco con dos particiones (aquí C: y juegos, ambas en el mismo NVMe de
2 TB) **no debe leerse como dos discos**. Antes se mostraba `C: / juegos` como si fuera un
nombre y confundía. El espacio por volumen sale de `GetDiskFreeSpaceEx`, y la
temperatura/actividad son del disco físico (compartidas por sus particiones, que es lo correcto).

El **título** es la etiqueta del volumen cuando el disco tiene **una sola partición**
(`DATOS12TB`), y el **modelo** cuando tiene varias (`FIKWOT FN501 Pro 2TB`), porque entonces
ninguna etiqueta nombra al disco entero.

### Sin HWiNFO: sensores de CPU por el SDK de AMD

De HWiNFO solo salían tres cosas: potencia de CPU, temperatura de CPU y temperaturas de disco.
Todas se sacan ya sin él:

- **Temp de disco NVMe**: nuestra, sin admin (`DiskTemps.cs`, IOCTL del log SMART).
- **Temp de disco SATA**: del helper elevado (`DiskTempsWmi.cs`), por el reliability counter del
  stack de almacenamiento (`MSFT_StorageReliabilityCounter` vía WMI — lo mismo que
  `Get-StorageReliabilityCounter`). Necesita admin, que el helper ya tiene. Poll cada 10 s (la
  temp cambia despacio) y keyed por número de disco físico. El ATA pass-through directo se probó
  antes pero los controladores SATA daban error; WMI funciona fiable.
- **Temp y potencia de CPU**: del **AMD Ryzen Master Monitoring SDK**, vía el helper elevado.
  La temp real (Tctl) y la potencia (PPT) necesitan ring0, pero AMD tiene un driver **firmado y
  compatible con HVCI** (el de Ryzen Master). WinRing0/LibreHardwareMonitor está bloqueado por la
  blocklist de drivers vulnerables + HVCI; el de AMD no.
- **Nombre de CPU**: del registro (`ProcessorNameString`), unelevated.

`native/RyzenShim/` es un puente C plano a la SDK C++ de AMD (`RmOpen`/`RmRead`/`RmClose`): el
compilador C++ maneja las vtables, el helper C# solo ve funciones planas y un struct POD — nada
de interop de vtables a mano. Se construye con `native/RyzenShim/build.cmd` (necesita el workload
de C++ y la SDK de AMD instalada) y se copia junto al helper. Da temp, PPT, Fmax y de regalo
temp/frecuencia por core. Consulta bajo demanda: **sin el límite de 12 h, sin el ~6 % de overhead
de HWiNFO**. Verificado con HWiNFO muerto: 43 W / 77 °C / 4.84 GHz, cambiando en vivo.

**HWiNFO se ha eliminado por completo.** No queda ni fallback: `HwiSensors` y el proyecto
`HwiProbe` están borrados. CPU temp/potencia del SDK de AMD, temp NVMe del agente, temp SATA del
helper — todo propio. Con el helper elevado corriendo (que el instalador arranca solo), la app es
100 % standalone. Cuando el helper no corre, la barra de estado dice **«sin helper (lanza
SidebarMonitor.Etw)»** y CPU temp/potencia salen como `—`; el resto lo cubre el agente sin elevar.
El antiguo problema del límite de 12 h y el overhead de tener HWiNFO midiendo todo desaparecieron
con él.

### Actividad de disco

Ese porcentaje es el «tiempo activo» del Administrador de tareas, que es `100 - % Idle Time`.
No es `% Disk Time`: ese cuenta peticiones encoladas y pasa de 100 % con toda normalidad.

Desde el menú **Discos** se pueden ocultar por tipo: **virtuales** (el vHD de WSL/Hyper-V,
oculto por defecto), **extraíbles** (USB/SD) y el **disco del sistema**. La clasificación sale
del bus (`IOCTL_STORAGE_QUERY_PROPERTY`) y de comprobar si alguna de sus letras es la unidad de
Windows (`%SystemDrive%`).

La identidad sale de `IOCTL_STORAGE_QUERY_PROPERTY`, no de WMI: WMI necesita COM y reflexión, y
le costaría al agente su build AOT. **Abrir `\\.\PhysicalDriveN` con un acceso deseado de 0
basta** para consultar propiedades, así que tampoco hace falta elevación. El tamaño viene de
`IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`; `GET_LENGTH_INFO` **no vale**, exige acceso de lectura al
dispositivo crudo y falla sin admin.

SSD vs HDD sale de `StorageDeviceSeekPenaltyProperty`: un disco que «incurre en penalización de
búsqueda» es mecánico. Los USB no lo reportan y quedan como `Unknown`, correctamente.

La temperatura de disco es propia: NVMe por IOCTL sin elevar (agente), SATA por
`MSFT_StorageReliabilityCounter` vía WMI (helper elevado), casadas por número de disco físico.

### Procesos

Agrupados por nombre y sumando CPU, RAM e hilos (`chrome.exe ×31`, `svchost.exe ×97`), que es
la única forma de que la lista se lea. Se desactiva con `--no-group` en el agente. La cabecera
etiqueta las columnas: sin ella, `116 MB` no dice que sea el working set.

### Docker y WSL (opcionales)

Windows solo ve el agregado de todo lo que corre en la VM de WSL2/Docker como un único proceso
(`vmmemWSL`); el desglose vive dentro del invitado. Dos secciones **opcionales** (ocultas por
defecto; se activan en «Secciones») lo leen preguntando al invitado:

- **DOCKER** — `docker stats` por contenedor: CPU %, RAM y **red** (la I/O de docker es acumulada,
  se diferencia a tasa). Ej.: `immich_server 0% 1.1G ↓0K ↑0K`.
- **WSL** — `top -bn2` dentro de la distro por defecto (la 2ª pasada da el %CPU instantáneo). Los
  procesos con su CPU y RAM. La red por proceso no la expone el invitado fácilmente, así que ahí va vacía.

Cada colector corre en segundo plano (no bloquea la UI) y **solo se lanza cuando su sección está
visible** — nunca invocamos `docker`/`wsl` si no se muestran. Aviso: la **primera** carga de WSL
puede tardar ~20 s si tu distro tarda en arrancar la sesión de usuario de systemd; en caliente
responde en ~1 s y el propio sondeo la mantiene despierta mientras la sección esté abierta.

### Unidades de red y disco

Binario (KiB/MiB/GiB, 1024) o decimal (KB/MB/GB, 1000), **independiente** para red y disco, desde
«Red → Unidades» y «Discos → Unidades».

### Red

La sección muestra **solo la interfaz primaria** — la que usaría la ruta por defecto, según
`GetBestInterface(8.8.8.8)`. Adivinar por «tiene puerta de enlace» falla: Tailscale y los
switches de Hyper-V también tienen una. Debajo, el **tráfico por proceso** que da ETW
(`chrome ↓1,2M`), no la lista de adaptadores. Sin el helper, esa lista se sustituye por una
nota. Cruzado: con una descarga, `pwsh ↓29 MiB/s` contra `Ethernet ↓30 MiB/s`.

### CPU: vista por proceso o vista por core

El menú **CPU: vista por core** conmuta el bloque entero entre dos vistas, **con un solo
lenguaje de color cada una** — nunca los dos a la vez, que era lo confuso.

**Vista por proceso** (por defecto):
- Gráfica: una curva del uso total, con relleno.
- Barras: segmentadas por los procesos que ocupan el core.
- Número de core y nombre del dominante: en el color del proceso dominante.
- Todo el color significa *proceso*.

**Vista por core**:
- Gráfica: las 16 curvas superpuestas, **cada una con su color** (rueda HSL espaciada 22,5°,
  escalonada para el fondo oscuro), con el total en blanco grueso encima. Nada de rellenos —
  16 áreas apiladas serían barro.
- Barras: un relleno sólido del **color del core** (la longitud es el uso).
- Número de core: el mismo color del core, así se emparejan línea y barra.
- Nombre del proceso dominante: en gris neutro, para no competir; el detalle por proceso
  (top-3) está en el tooltip.
- Todo el color significa *core*.

16 cores son 16 categorías forzadas que no se pueden plegar, así que la rueda generada es la
opción legítima para ese caso.

**Estrella del mejor núcleo.** Con el helper elevado, una **★ dorada** marca el core que más
boostea y un **◆ plateado** el segundo, delante del número de la fila (como los preferentes de
Ryzen Master). El SDK de AMD no expone el ranking CPPC, así que se deduce siguiendo el pico de
frecuencia por core físico (converge a los mismos cores en cuanto la CPU hace boost). El SDK da
cores **físicos** (8) y la UI muestra **lógicos** (16, con SMT): se marcan ambos hilos del físico.

### CPU: temperatura, voltaje y throttle (SDK de AMD)

Con el helper corriendo, la **temperatura cambia de color según se acerca al Tjmax real** que
reporta el SDK (`fcHTCLimit`, el límite cHTC ≈ 89 °C en el 7800X3D): normal en verde, **ámbar** a
menos de 12 °C del límite, **rojo** a menos de 4 °C. Sin SDK, cae a un genérico 80/90 °C.

Dos lecturas más del SDK, **opcionales** (menú, off por defecto), en una línea bajo la gráfica:
- **VID** — voltaje medio de núcleo (`dAvgCoreVoltage`).
- **Límites** — el grupo "Limits" de HWiNFO en una línea: cuánto se acerca a los límites de **PPT**
  (potencia), **TDC** y **EDC** (corriente) y **térmico** (temp/Tjmax), en %. Avisa con **⚠ throttle
  térmico** al tocar el Tjmax. Ej.: `PPT 51% · TDC 22% · EDC 35% · térm 63%`.

  > El **"Límite de frecuencia: Global"** de HWiNFO (que baja a ~4.8 GHz bajo carga all-core) **no
  > lo expone el SDK** de AMD: lo lee HWiNFO del SMU crudo. Los campos de frecuencia del SDK son
  > `fCCLK_Fmax` (5.10 GHz, techo rated **estático**) y `dPeakSpeed` (dinámico pero *sube* con la
  > carga — es la velocidad real de los cores activos, no un límite). Ninguno replica ese valor, así
  > que no se muestra un número de frecuencia aquí.

El SDK da aún más (voltaje SOC, Infinity Fabric FCLK, temp por core); el mecanismo ya está para
añadirlo. PROCHOT no lo expone el SDK — solo el throttle térmico (HTC) es derivable.

### Frecuencia de CPU: mejor núcleo, media o mediana

`% Processor Performance` es un porcentaje de la frecuencia nominal (base), así que el reloj
efectivo de un core es `nominal × perf / 100` — y **no se capa a 100**, porque 120 % es
exactamente cómo se ve un boost de 5.05 GHz sobre una base de 4.2. El agente calcula las tres
agregaciones sobre los cores (`\Processor Information(*)\% Processor Performance`) y las envía;
la UI elige desde el menú **Frecuencia CPU**:

- **Mejor núcleo** (por defecto): el bin de boost que alcanza el core más rápido. Es lo que
  quieres para ver si el Ryzen llega a sus 5.05 GHz en un juego mono-hilo.
- **Media** — la que muestra el `_Total`, el conjunto del paquete.
- **Mediana** — robusta al core que va disparado.

La etiqueta bajo la cifra dice cuál es (`GHz máx` / `GHz medio` / `GHz mediana`).

### Auto-escala del eje Y

Cada sparkline ajusta su eje Y al **min..max de la ventana visible** (con un margen), levantando
la base del cero. Así, tráfico de red bajo y plano, o cores de CPU al 5–15 %, **llenan la
gráfica** en vez de quedar pegados a la base — que es lo que las hacía ilegibles a 36 px. El eje
va suavizado (ease ~4 ticks) para que no dé saltos bruscos en un pico. Cada gráfica tiene un
suelo de rango (`MinRange`) para no hacer zoom sobre el ruido de un sensor plano.

Cada gráfica lleva el **mín y máx del eje** en las esquinas (arriba-izq y abajo-izq), con un
fondo tenue para leerse sobre la línea. Se actualizan cada frame, así que un máximo que salta de
`20 %` a `50 %` es la señal visible de que el eje escaló hacia arriba — sin eso no sabrías en qué
rango estás.

Es configurable por gráfica desde el menú **Gráficas → Auto-escala** (CPU, GPU, Red, Discos).
Con auto desactivado: las de % vuelven a 0..100 fijo; las de bytes, a base-cero hasta el pico.

Y **Gráficas → Tamaño** (Pequeñas/Medianas/Grandes/Enormes) multiplica la altura de todas, para
cambiar espacio de escritorio por detalle visible.

### Sparklines interactivas

Al pasar el ratón por cualquier gráfica: cruceta vertical, un punto en cada serie y un tooltip
con el valor en ese instante y cuánto hace (`hace 12 s · DL 1,3 MiB/s`). El intervalo entre
muestras es el de refresco.

### Click-through

Opción del menú (**Ignorar clics**): activa `WS_EX_TRANSPARENT` y los clics atraviesan el panel
hacia la ventana de detrás. Es un **toggle**, no un hover: si pasar el ratón lo hiciera sólido,
nunca podrías hacer clic justo donde está el cursor. Se recupera desde la bandeja. Útil sobre
todo en flotante.

Color por entidad, nunca por posición: CPU azul, GPU violeta, entrada aqua, salida naranja —
los slots de la paleta de referencia de `dataviz` en su superficie oscura. Los medidores viran
a estado (naranja al 80 %, rojo al 90 %) siempre junto a la cifra, nunca solo por color. Las
dos series de red y disco van direct-labeled con su leyenda, así que la identidad jamás
depende del color.

## ETW: lo que Windows no expone de otra forma

Dos datos no los da ninguna API normal, y ambos necesitan una sesión ETW de kernel, que exige
elevación (`SeSystemProfilePrivilege`; comprobado: sin elevar, *acceso denegado*, aunque el
usuario esté en «Usuarios del registro de rendimiento»):

1. **Qué proceso ocupa cada core.** Ni Task Manager ni Process Explorer lo muestran.
2. **Ancho de banda por proceso.** El Monitor de Recursos lo hace, y corre elevado.

`EtwProbe` valida ambos. Usa el **profiler muestreado** (`Keywords.Profile`), no context
switches: CSwitch dispara en cada decisión del planificador (~100k eventos/s) mientras que
Profile dispara a ritmo fijo por core. Para «quién es dueño de este core» los conteos de
muestras *son* la respuesta, y el volumen queda acotado.

Coste medido en reposo: **1.24 % de un core (0.078 % de la máquina), 38.5 MiB**, con ~100
muestras/s por core (~290/s bajo carga: el ritmo sube con la actividad).

> **Trampa importante.** El profiler **no emite muestras mientras un core duerme** en C-state
> profundo, así que Idle sale infrarrepresentado: un core en reposo puede parecer «95 %
> ocupado» por conteo de muestras. Las muestras sirven para **atribuir quién**, nunca **cuánto**.
> La altura de la barra sigue viniendo de PDH; ETW solo colorea los segmentos.

Y confirma que hay que **agrupar por nombre**: sin agrupar, un core sale como
`powershell 19% powershell 16% powershell 10%` (tres PIDs) y la red lista `chrome` dos veces.

### Dos fallos de red que costó cazar

**1. La captura de red de ETW se atasca tras muchas horas.** El proveedor clásico
`NetworkTCPIP` deja de entregar eventos TcpIp/UdpIp mientras el profiling sigue, así que el
ancho de banda por proceso se congela. El helper corre la sesión de kernel dentro de un bucle
con un **watchdog**: compara su contador de eventos contra los **bytes reales de la interfaz**
(iphlpapi) — si la NIC mueve datos y ETW no emite eventos durante 20 s, recrea la sesión. Usar
los bytes de la NIC evita falsos positivos con la red ociosa (sin tráfico no hay atasco). Las
publicaciones no se cortan durante el reinicio, así que la UI no ve hueco.

**2. El agente no podía reiniciarse con la UI abierta.** El writer usaba `CreateNew`, que falla
si el mapa ya existe — y la UI, como lector, mantiene el mapa vivo aunque el agente muera. Un
agente nuevo no arrancaba («ya hay un agente») y los lectores se quedaban con datos viejos para
siempre. Ahora la instancia única se controla con un **mutex con nombre** (se libera al morir el
proceso, aunque crashee) y el mapa usa `CreateOrOpen` para reutilizar el que sujeta la UI.

**Limitación honesta: el tráfico de WSL2 no se atribuye a un proceso.** Sus sockets viven en el
invitado Linux; en el host el tráfico lo reenvía el vSwitch de Hyper-V sin un PID de proceso
normal dueño del socket. Así que una descarga dentro de WSL sale en el total de la interfaz
pero no bajo ningún proceso de la lista. Es intrínseco a cómo funciona ETW, no un bug.

### Diseño híbrido, implementado

`SidebarMonitor.Etw` es el proceso auxiliar **opcional y elevado** (su manifest pide UAC).
Publica en `Local\SidebarMonitor.Etw.v1` con el mismo seqlock. El agente sigue **sin
privilegios y AOT**: abre ese mapa si existe, lo fusiona en su snapshot y expone
`EtwAvailable`. Si el helper no está, todo funciona igual, sin colores por proceso ni red por
proceso. (`TraceEvent` usa reflexión y no puede ir AOT — otra razón para separarlo.)

Verificado: **un proceso sin elevar puede abrir para lectura el mapa creado por uno elevado**
(el nivel de integridad por defecto de un objeto es medio, no el del creador). El agente
re-sondea cada 5 s por si lanzas el helper después, y detecta que ha muerto porque el
timestamp del mapa envejece — un mapa huérfano conserva sus últimos valores para siempre.

Validación cruzada de la red: con una descarga en curso, ETW atribuye `powershell ↓1279 KiB/s`
mientras el contador de la interfaz marca `Ethernet ↓1310 KiB/s`. Dos caminos independientes,
el mismo número.

## Estructura

- `SidebarMonitor.Shared` — el contrato: `Snapshot` + lector/escritor de memoria compartida.
- `SidebarMonitor.Agent` — muestrea (PDH, NVML, `NtQuerySystemInformation`,
  `GlobalMemoryStatusEx`, IOCTL de disco) y publica. AOT, sin privilegios.
- `SidebarMonitor.Etw` — helper **opcional y elevado**: SDK de AMD (CPU temp/potencia), temp SATA
  por WMI, y quién ocupa cada core y la red por proceso.
- `SidebarMonitor.UI` — el panel (WPF). Reusa el chasis de ventana de `ShellProbe`.
- `SidebarMonitor.Client` — el snapshot en texto; útil para depurar sin abrir la UI.
- `src/*Probe` — las sondas de viabilidad, se pueden borrar cuando estorben.

Para depurar la UI: `--seconds=N` la cierra sola, `--shot=x.png` captura, `--dump` vuelca el
árbol visual con el texto y color de cada `TextBlock`, y `--collapse=` / `--expand=` / `--hide=`
fuerzan el estado de las secciones sin tocar el ratón (útil porque el estado guardado en
`ui.json` se restaura y puede sorprenderte).

La UI es `WinExe`: **no abre consola**. Con los flags de depuración se engancha a la consola
del padre, si la hay. Ojo: en PowerShell, `& app.exe` **no espera** a un WinExe — usa
`Start-Process -Wait` o creerás que ha fallado.

> Al capturar, renderiza el visual directamente (`RenderTargetBitmap.Render(root)`). Pasar por
> un `VisualBrush` **descarta silenciosamente los textos**: miden bien y nunca se rasterizan.

## Pendiente

- Tooltips con puntos sobre las sparklines: pasar el ratón y ver el valor en ese instante.
  Las filas de core ya tienen tooltip con el desglose del top-3.
- Click-through al pasar el ratón (`WS_EX_TRANSPARENT` alternado).
- El menú «Refresco» cambia el ritmo de la UI y reinicia el agente **solo si la UI lo lanzó**.
  Si el agente lo arrancaste tú, la UI solo redibuja más o menos a menudo.
- Reusar los diccionarios de `Processes` entre muestras: hoy reconstruye ~370 entradas con sus
  strings cada 3 ticks. Es la única basura real que genera el agente.
- Click-through al pasar el ratón (`WS_EX_TRANSPARENT` alternado), que `ShellProbe` ya
  soporta con `--clickthrough` pero no está probado en uso real.

**Hecho:** arranque con Windows e instalador (`install.cmd`), HWiNFO eliminado por completo
(sensores propios: SDK de AMD + IOCTL/WMI), y secciones reordenables desde el menú.
