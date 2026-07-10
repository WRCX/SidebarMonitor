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
  El estado se guarda en `%LOCALAPPDATA%\SidebarMonitor\ui.json`.
- Una cabecera plegada **sigue informando**: muestra un resumen en vivo (`CPU  7%  37.2W`,
  `RED  ↓14K ↑13K`). Plegar no es perder el dato, es perder el detalle.
- Las secciones plegadas u ocultas **no actualizan su cuerpo**; solo el resumen. Plegar cosas
  abarata la UI de verdad, no solo visualmente.

Gráficas solo donde aportan: sparkline para lo que varía en el tiempo (uso de CPU y GPU,
red, disco), barras por core, y medidores para lo que es una fracción de un total (RAM, VRAM).
El resto son cifras. Nada de ejes ni rejillas: el valor actual va etiquetado al lado.

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
árbol visual con el texto y color de cada `TextBlock`, y `--collapse=`/`--hide=` fuerzan el
estado de las secciones sin tocar el ratón.

> Al capturar, renderiza el visual directamente (`RenderTargetBitmap.Render(root)`). Pasar por
> un `VisualBrush` **descarta silenciosamente los textos**: miden bien y nunca se rasterizan.

## Pendiente

- **«Siempre encima» debe ser una opción, no una constante.** Hoy `WS_EX_TOPMOST` está fijo en
  `AppBarWindow`. Tiene que poder desactivarse desde el menú (y persistirse), para cuando el
  panel esté en la pantalla principal y no quieras que tape nada.
- Bandeja: minimizar / restaurar, y menú de configuración con intervalos.
- Click-through, tooltips al pasar por las sparklines, y elegir monitor desde el menú.
- Reusar los diccionarios de `Processes` entre muestras: hoy reconstruye ~370 entradas con sus
  strings cada 3 ticks. Es la única basura real que genera el agente.
- Arrancar con Windows, e instalador.
- Extraer un `ISensorSource` cuando entre LibreHardwareMonitor como fallback. Hoy el agente
  habla con HWiNFO directamente; no vale la pena la abstracción hasta tener el segundo backend.
- Más sensores de HWiNFO al snapshot (temperaturas de disco, hotspot de GPU, potencia por
  raíl): el mecanismo ya está, es añadir campos e índices.
- Click-through al pasar el ratón (`WS_EX_TRANSPARENT` alternado), que `ShellProbe` ya
  soporta con `--clickthrough` pero no está probado en uso real.
