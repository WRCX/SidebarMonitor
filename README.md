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
| Potencia CPU, temperaturas, ventiladores, voltajes, SMART | HWiNFO (memoria compartida) | Requiere ring0; delegamos en HWiNFO en vez de shippear WinRing0 y pelearnos con HVCI, la blocklist de drivers vulnerables y la firma |
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
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe          # vuelca todos los sensores
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe --bench  # coste de un snapshot
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe --watch
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe --filter=Potencia
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

`HwiProbe` necesita HWiNFO corriendo con *Settings → Main Settings → Shared Memory Support*
activado. **En la versión gratuita la SHM se autodesactiva a las 12 h**; sin límite en Pro.
Y el uso comercial de esa interfaz se negocia con el autor de HWiNFO.

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
- HWiNFO, para los sensores.
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
- Una cabecera plegada **sigue informando**, con lo más útil de cada sección:
  `CPU 27% 4.70GHz 49W` · `GPU 0% 2640MHz 51W` · `DISCOS 1% R4K W1,5M` ·
  `PROCESOS pwsh 12.2% · bdservi… 1.7%`. Plegar pierde el detalle, no el dato.
- Las secciones plegadas u ocultas **no actualizan su cuerpo**; solo el resumen. Y una ventana
  minimizada no actualiza nada. Plegar abarata la UI de verdad, no solo visualmente.

### Colocación, bandeja y minimizado

Todo desde el menú contextual (clic derecho) o desde el icono de bandeja:

- **Anclado** (AppBar, reserva espacio, nada lo tapa) o **flotante**, arrastrable por su
  cabecera. La ventana nunca se activa, así que `DragMove` de WPF no sirve: el arrastre se
  hace con deltas de `GetCursorPos`.
- **Siempre encima** es una opción, no una constante. Borde izquierdo o derecho, monitor y
  ancho, todo en caliente.
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

Un bloque por disco físico, con **su propia gráfica** de lectura/escritura: etiqueta de volumen
(`DATOS12TB`), modelo, HDD/SSD, bus, tamaño, temperatura y **% de actividad**.

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

La temperatura viene de HWiNFO. El único punto de unión es que HWiNFO nombra sus sensores
`S.M.A.R.T.: <modelo> (<serie>)`, y el modelo del IOCTL es subcadena de ese nombre.

### Procesos

Agrupados por nombre y sumando CPU, RAM e hilos (`chrome.exe ×31`, `svchost.exe ×97`), que es
la única forma de que la lista se lea. Se desactiva con `--no-group` en el agente. La cabecera
etiqueta las columnas: sin ella, `116 MB` no dice que sea el working set.

### Red

La sección muestra **solo la interfaz primaria** — la que usaría la ruta por defecto, según
`GetBestInterface(8.8.8.8)`. Adivinar por «tiene puerta de enlace» falla: Tailscale y los
switches de Hyper-V también tienen una. Debajo, el **tráfico por proceso** que da ETW
(`chrome ↓1,2M`), no la lista de adaptadores. Sin el helper, esa lista se sustituye por una
nota. Cruzado: con una descarga, `pwsh ↓29 MiB/s` contra `Ethernet ↓30 MiB/s`.

### Gráfica de CPU

Dos modos, conmutables desde el menú (**CPU: una línea por core**):

- **Total** (por defecto): una sola curva del uso agregado, con relleno.
- **Por core**: las 16 curvas superpuestas, **cada una con su propio color** (una rueda HSL
  espaciada 22,5°, escalonada para el fondo oscuro), con el total en blanco grueso encima para
  que se lea sin ambigüedad. Nada de rellenos — 16 áreas apiladas serían barro. Con este modo
  activo, **el número de cada core toma su mismo color**, así se emparejan línea y número; con
  el modo desactivado, el número vuelve al color del proceso dominante.

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
- `SidebarMonitor.Agent` — muestrea (HWiNFO, PDH, NVML, `NtQuerySystemInformation`,
  `GlobalMemoryStatusEx`) y publica. AOT, sin privilegios.
- `SidebarMonitor.Etw` — helper **opcional y elevado**: quién ocupa cada core y red por proceso.
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
- Arrancar con Windows, e instalador.
- Extraer un `ISensorSource` cuando entre LibreHardwareMonitor como fallback. Hoy el agente
  habla con HWiNFO directamente; no vale la pena la abstracción hasta tener el segundo backend.
- Más sensores de HWiNFO al snapshot (temperaturas de disco, hotspot de GPU, potencia por
  raíl): el mecanismo ya está, es añadir campos e índices.
- Click-through al pasar el ratón (`WS_EX_TRANSPARENT` alternado), que `ShellProbe` ya
  soporta con `--clickthrough` pero no está probado en uso real.
