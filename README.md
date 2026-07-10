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

## Sondas

```
dotnet build
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe          # vuelca todos los sensores
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe --bench  # coste de un snapshot
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe --watch
src/HwiProbe/bin/Release/net10.0-windows/HwiProbe.exe --filter=Potencia
src/NativeProbe/bin/Release/net10.0-windows/NativeProbe.exe    # PDH + RAM + red + procesos + NVML
```

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
la frecuencia real: `% Processor Performance` × frecuencia base.

## Requisitos

- .NET 10 SDK (fijado en `global.json`).
- HWiNFO, para los sensores.
- Para publicar con AOT hace falta el workload **Desktop development with C++** de Visual
  Studio (el linker de MSVC). El código ya es AOT-compatible (`IsAotCompatible`), pero el
  toolchain no está instalado en esta máquina.

Nota para la UI: **WPF no soporta AOT.** O el agente va AOT y la UI en JIT, o la UI se hace
sobre Win2D / DirectComposition.

## Pendiente

- Agente con `ISensorSource` (HWiNFO + hueco para LibreHardwareMonitor).
- La ventana, que es donde está el riesgo real: AppBar (`SHAppBarMessage`) para que las
  ventanas maximizadas no la tapen, `WS_EX_NOACTIVATE` para no robar foco, per-monitor DPI
  v2, y el *pinning* a escritorios virtuales (solo hay COM no documentado).
