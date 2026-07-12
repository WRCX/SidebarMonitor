namespace SidebarMonitor.UI;

/// <summary>
/// Spanish → English translations for every user-facing UI string, keyed by the exact Spanish
/// literal passed to <see cref="Loc.T"/>. A key missing here falls back to the Spanish text. Keep
/// composite-format placeholders (<c>{0}</c>, <c>{0:F1}</c>) identical between the two languages.
/// </summary>
internal static class LocStrings
{
    public static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // ── Section titles (main panel) ───────────────────────────────────────────────────────
        ["MEMORIA"] = "MEMORY",
        ["RED"] = "NETWORK",
        ["DISCOS"] = "DISKS",
        ["PROCESOS"] = "PROCESSES",
        ["JUEGO"] = "GAME",
        ["{0} reales · frame-gen"] = "{0} real · frame-gen",
        // CPU / GPU / DOCKER / WSL read the same in both languages.

        // ── Tray + top-level menu ─────────────────────────────────────────────────────────────
        ["Abrir SidebarMonitor"] = "Open SidebarMonitor",
        ["Mostrar / ocultar"] = "Show / hide",
        ["Configuración…"] = "Settings…",
        ["Salir"] = "Quit",
        ["Ajustes…"] = "Settings…",
        ["Secciones"] = "Sections",
        ["Minimizar a pestaña"] = "Minimize to tab",
        ["Ocultar (queda en la bandeja)"] = "Hide (stays in tray)",

        // ── Main panel: status / captions ─────────────────────────────────────────────────────
        ["esperando al agente…"] = "waiting for agent…",
        ["Módulos de memoria"] = "Memory modules",
        ["Versión de SidebarMonitor (UI). Debe coincidir con la del agente/helper instalados."]
            = "SidebarMonitor (UI) version. Must match the installed agent/helper.",
        ["proceso"] = "process",
        ["nombre            CPU  RAM"] = "name              CPU  RAM",
        ["· no disponible / no responde"] = "· unavailable / not responding",
        ["· leyendo…"] = "· reading…",
        ["GHz medio"] = "GHz avg",
        ["GHz mediana"] = "GHz median",
        ["GHz máx"] = "GHz max",
        ["agente desfasado"] = "agent outdated",
        ["agente parado ({0:F0} s)"] = "agent stopped ({0:F0} s)",
        ["sin helper (lanza SidebarMonitor.Etw)"] = "no helper (launch SidebarMonitor.Etw)",
        [" (mejor núcleo)"] = " (best core)",
        ["⚠ throttle térmico"] = "⚠ thermal throttle",
        ["térm {0:F0}%"] = "therm {0:F0}%",
        ["sin interfaz activa"] = "no active interface",
        ["· ETW para ver el tráfico por proceso"] = "· ETW to see per-process traffic",
        ["actividad {0:F0} %"] = "activity {0:F0} %",
        ["R {0,-6} W {1,-6} cola {2:F2}"] = "R {0,-6} W {1,-6} queue {2:F2}",
        ["{0} procesos · {1} threads"] = "{0} processes · {1} threads",
        ["cont."] = "cont.",
        ["proc."] = "proc.",
        ["POT"] = "PWR",
        ["CORR"] = "CUR",
        ["TÉRM"] = "THRM",
        ["throttle: {0}"] = "throttle: {0}",
        ["cerca de throttle: {0}"] = "near throttle: {0}",
        ["sin throttle"] = "no throttle",

        // ── Core rows / tooltips ──────────────────────────────────────────────────────────────
        ["Core {0}"] = "Core {0}",
        ["  {0:F1}% uso"] = "  {0:F1}% usage",
        ["   ★ mejor núcleo"] = "   ★ best core",
        ["   ◆ 2º mejor"] = "   ◆ 2nd best",
        ["C0 (despierto) {0:F0}%"] = "C0 (awake) {0:F0}%",
        ["sin atribución"] = "no attribution",
        ["lanza el helper ETW para ver qué proceso lo ocupa"]
            = "launch the ETW helper to see which process owns it",
        ["ahora"] = "now",
        ["hace {0:F0} s"] = "{0:F0}s ago",
        ["Total {0}%"] = "Total {0}%",
        ["Módulo"] = "Module",

        // ── Core colour palettes ──────────────────────────────────────────────────────────────
        ["Arcoíris"] = "Rainbow",
        ["Contraste"] = "Contrast",
        ["Fríos"] = "Cool",
        ["Cálidos"] = "Warm",
        ["Pastel"] = "Pastel",

        // ── Settings: window + categories ─────────────────────────────────────────────────────
        ["SidebarMonitor — Ajustes"] = "SidebarMonitor — Settings",
        ["Apariencia"] = "Appearance",
        ["Memoria"] = "Memory",
        ["Red"] = "Network",
        ["Discos"] = "Disks",
        ["Refresco"] = "Refresh",
        ["Colocación"] = "Placement",
        ["Diagnóstico"] = "Diagnostics",

        // ── Settings: Appearance ──────────────────────────────────────────────────────────────
        ["Tamaño de todas las gráficas"] = "Size of all graphs",
        ["Alto por defecto de las gráficas. Cada una puede sobreescribirlo abajo."]
            = "Default graph height. Each one can override it below.",
        ["Pequeñas"] = "Small",
        ["Medianas"] = "Medium",
        ["Grandes"] = "Large",
        ["Enormes"] = "Huge",
        ["Alto por gráfica (sobreescribe el global)"] = "Height per graph (overrides the global)",
        ["Global"] = "Global",
        ["P"] = "S",
        ["G"] = "L",
        ["E"] = "XL",
        ["Auto-escala del eje Y"] = "Y-axis auto-scale",
        ["Ajusta cada eje al mín/máx de su ventana para ver el detalle cuando los valores son bajos."]
            = "Fits each axis to the min/max of its window to see detail when values are low.",
        ["Colores"] = "Colors",
        ["Colores de núcleos"] = "Core colors",
        ["Paleta para las barras, líneas y mini-gráficas por núcleo."]
            = "Palette for the per-core bars, lines and mini-graphs.",
        ["Tooltips"] = "Tooltips",
        ["Opacidad de tooltips"] = "Tooltip opacity",
        ["Transparencia del fondo de los tooltips (1 = opaco)."]
            = "Tooltip background transparency (1 = opaque).",
        ["Idioma"] = "Language",
        ["Idioma de la interfaz. Al cambiarlo se reinicia el panel."]
            = "Interface language. Changing it restarts the panel.",
        ["Automático"] = "Automatic",
        ["Español"] = "Spanish",
        ["English"] = "English",

        // ── Settings: Sections ────────────────────────────────────────────────────────────────
        ["Muestra u oculta cada sección."] = "Show or hide each section.",
        ["Orden (arriba = primera)"] = "Order (top = first)",

        // ── Settings: Refresh ─────────────────────────────────────────────────────────────────
        ["0,5 s"] = "0.5 s",
        ["1 s"] = "1 s",
        ["2 s"] = "2 s",
        ["5 s"] = "5 s",
        ["Ritmo de muestreo por defecto. Reinicia el agente para muestrear al nuevo ritmo."]
            = "Default sampling rate. Restart the agent to sample at the new rate.",
        ["Por sección (sobreescribe el global)"] = "Per section (overrides the global)",
        ["«Global» = seguir el ritmo de arriba. Útil para tener la CPU rápida y los discos lentos."]
            = "«Global» = follow the rate above. Useful for a fast CPU and slow disks.",

        // ── Settings: CPU ─────────────────────────────────────────────────────────────────────
        ["Gráfica principal"] = "Main graph",
        ["Una línea de uso total, líneas por núcleo superpuestas, o una mini-gráfica por núcleo en rejilla."]
            = "A single total-usage line, overlaid per-core lines, or one mini-graph per core in a grid.",
        ["Total"] = "Total",
        ["Superpuesta"] = "Overlaid",
        ["Separada"] = "Separate",
        ["Columnas (gráfica separada)"] = "Columns (separate graph)",
        ["Cuántas mini-gráficas por fila en el modo «Separada»."]
            = "How many mini-graphs per row in «Separate» mode.",
        ["Eje Y de las mini-gráficas"] = "Mini-graph Y-axis",
        ["Fijo 0-100 = todos los núcleos comparables de un vistazo. Autoescala = cada núcleo a su rango (detalle en núcleos ociosos, pero no comparable)."]
            = "Fixed 0-100 = all cores comparable at a glance. Auto-scale = each core to its range (detail on idle cores, but not comparable).",
        ["Fijo 0-100"] = "Fixed 0-100",
        ["Autoescala"] = "Auto-scale",
        ["Frecuencia mostrada (GHz)"] = "Frequency shown (GHz)",
        ["Qué agregado del reloj por núcleo se muestra arriba."]
            = "Which aggregate of the per-core clock is shown at the top.",
        ["Mejor"] = "Best",
        ["Media"] = "Mean",
        ["Mediana"] = "Median",
        ["Filas por núcleo"] = "Per-core rows",
        ["Mostrar frecuencia"] = "Show frequency",
        ["Mostrar temperatura"] = "Show temperature",
        ["Del SDK de AMD; colorea hacia rojo cerca del Tjmax."]
            = "From the AMD SDK; shades toward red near Tjmax.",
        ["Posición de la métrica"] = "Metric position",
        ["Dónde va la frecuencia/temperatura en la fila."]
            = "Where the frequency/temperature goes in the row.",
        ["Dentro"] = "Inside",
        ["Al final"] = "At the end",
        ["Fuera"] = "Outside",
        ["Barra por núcleo"] = "Per-core bar",
        ["Uso (%Util), residencia C0 (despierto), ambas superpuestas, o uso + marca de C0."]
            = "Usage (%Util), C0 residency (awake), both overlaid, or usage + C0 tick.",
        ["Uso"] = "Usage",
        ["Combinada"] = "Combined",
        ["Uso+tick"] = "Usage+tick",
        ["Marcar núcleos dormidos"] = "Mark sleeping cores",
        ["Atenúa y etiqueta «sleep» los núcleos aparcados (C0≈0), como Ryzen Master."]
            = "Dims and labels «sleep» the parked cores (C0≈0), like Ryzen Master.",
        ["Modelo e indicadores"] = "Model and indicators",
        ["Modelo de CPU"] = "CPU model",
        ["Dónde mostrar el nombre del procesador."] = "Where to show the processor name.",
        ["No"] = "No",
        ["En título"] = "In title",
        ["Indicador de throttle (POT/CORR/TÉRM)"] = "Throttle indicator (PWR/CUR/THRM)",
        ["Qué tope duro frena el boost ahora. Del SDK de AMD."]
            = "Which hard cap is limiting boost now. From the AMD SDK.",
        ["Boost logrado / pico"] = "Boost achieved / peak",
        ["Frecuencia del mejor núcleo vs su pico de sesión."] = "Best core's frequency vs its session peak.",
        ["Mostrar VID (voltaje)"] = "Show VID (voltage)",
        ["Mostrar límites (PPT/TDC/EDC/térmico)"] = "Show limits (PPT/TDC/EDC/thermal)",

        // ── Settings: Memory ──────────────────────────────────────────────────────────────────
        ["Información de módulos"] = "Module info",
        ["Datos de los módulos de RAM leídos por WMI (SMBIOS), sin elevación."]
            = "RAM module data read via WMI (SMBIOS), no elevation.",
        ["Resumen"] = "Summary",
        ["Detalle"] = "Detail",
        ["Resumen: «2× 16 GiB DDR5-6000». Detalle: una línea por módulo con ranura, tamaño, tipo, frecuencia (MT/s), fabricante y part number. En cualquier caso el detalle está también en el tooltip."]
            = "Summary: «2× 16 GiB DDR5-6000». Detail: one line per module with slot, size, type, frequency (MT/s), manufacturer and part number. Either way the detail is also in the tooltip.",
        ["Unidades del uso"] = "Usage units",
        ["Binario (GiB, 1024) o decimal (GB, 1000) para el uso/total del sistema. El tamaño de los módulos va siempre en GiB (su tamaño real)."]
            = "Binary (GiB, 1024) or decimal (GB, 1000) for system usage/total. Module size is always in GiB (its real size).",
        ["Binario"] = "Binary",
        ["Decimal"] = "Decimal",

        // ── Settings: GPU ─────────────────────────────────────────────────────────────────────
        ["Mostrar"] = "Show",
        ["Qué GPU(s) ver. La iGPU solo da % y motores (sin sensores propios)."]
            = "Which GPU(s) to see. The iGPU only gives % and engines (no sensors of its own).",
        ["Ambas"] = "Both",
        ["Motores (mini-gráficas)"] = "Engines (mini-graphs)",
        ["Una mini-gráfica por motor: 3D, compute/ML, decode/encode…"]
            = "One mini-graph per engine: 3D, compute/ML, decode/encode…",
        ["Columnas de motores"] = "Engine columns",
        ["Cuántas mini-gráficas por fila."] = "How many mini-graphs per row.",
        ["Modelo de GPU"] = "GPU model",
        ["Dónde mostrar el nombre de la GPU primaria."] = "Where to show the primary GPU's name.",

        // ── Settings: Network ─────────────────────────────────────────────────────────────────
        ["Filas de procesos por ancho de banda"] = "Process rows by bandwidth",
        ["Número fijo de filas en la sección RED (fijo a propósito)."]
            = "Fixed number of rows in the NETWORK section (fixed on purpose).",
        ["Ninguna"] = "None",
        ["Unidades"] = "Units",
        ["Binario (KiB/MiB, 1024) o decimal (KB/MB, 1000)."] = "Binary (KiB/MiB, 1024) or decimal (KB/MB, 1000).",

        // ── Settings: Disks ───────────────────────────────────────────────────────────────────
        ["Ocultar discos virtuales"] = "Hide virtual disks",
        ["Ocultar discos extraíbles"] = "Hide removable disks",
        ["Ocultar disco del sistema"] = "Hide system disk",
        ["Unidades de las tasas"] = "Rate units",
        ["Binario (KiB/MiB) o decimal (KB/MB) para las velocidades de lectura/escritura. La capacidad va siempre en decimal (como se anuncian los discos)."]
            = "Binary (KiB/MiB) or decimal (KB/MB) for read/write speeds. Capacity is always decimal (as disks are advertised).",

        // ── Settings: Placement ───────────────────────────────────────────────────────────────
        ["Siempre encima"] = "Always on top",
        ["Anclado al borde"] = "Docked to edge",
        ["Anclado lo pega a un borde; flotante se arrastra por su cabecera."]
            = "Docked pins it to an edge; floating is dragged by its header.",
        ["Reservar espacio (empuja ventanas)"] = "Reserve space (pushes windows)",
        ["Solo anclado. Reserva la franja para que nada la tape; maximizar/snap se paran en su borde."]
            = "Docked only. Reserves the strip so nothing covers it; maximize/snap stop at its edge.",
        ["Borde izquierdo"] = "Left edge",
        ["Anclar al borde izquierdo en vez del derecho."] = "Dock to the left edge instead of the right.",
        ["Ignorar clics (pasan a través)"] = "Ignore clicks (pass through)",
        ["Los clics atraviesan el panel. Reactívalo desde la bandeja."]
            = "Clicks pass through the panel. Re-enable it from the tray.",
        ["Monitor"] = "Monitor",
        ["En qué pantalla vive el panel."] = "Which screen the panel lives on.",
        ["Ancho del panel"] = "Panel width",
        ["Ancho en píxeles (también se arrastra el borde interior)."]
            = "Width in pixels (you can also drag the inner edge).",

        // ── Settings: Diagnostics ─────────────────────────────────────────────────────────────
        ["Registrar a CSV"] = "Log to CSV",
        ["Graba una fila por muestra (CPU, límites, por-núcleo, RAM, GPU0, red, disco) a %LOCALAPPDATA%\\SidebarMonitor\\logs."]
            = "Writes one row per sample (CPU, limits, per-core, RAM, GPU0, network, disk) to %LOCALAPPDATA%\\SidebarMonitor\\logs.",
        ["Datos de depuración (overlay)"] = "Debug data (overlay)",
        ["Bajo el título: fabricante/modelo de CPU, versiones del contrato, estado del SDK/helper, cadencia y estado del CSV."]
            = "Under the title: CPU vendor/model, contract versions, SDK/helper status, cadence and CSV status.",
        ["Abrir carpeta de logs"] = "Open logs folder",
        ["FPS de juegos (PresentMon)"] = "Game FPS (PresentMon)",
        ["Mide FPS/frametime/1% low/latencia/stutter del juego en primer plano vía ETW (sin inyección). El helper lanza PresentMon solo cuando está activado."]
            = "Measures the foreground game's FPS/frametime/1% low/latency/stutter via ETW (no injection). The helper runs PresentMon only when enabled.",

        // ── Updates ───────────────────────────────────────────────────────────────────────────
        ["Actualizaciones"] = "Updates",
        ["Buscar actualizaciones automáticamente"] = "Check for updates automatically",
        ["Al arrancar (y a diario) consulta GitHub Releases. Solo se contacta la API pública de GitHub; no se envía nada tuyo."]
            = "On startup (and daily) checks GitHub Releases. Only GitHub's public API is contacted; nothing about you is sent.",
        ["Versión actual: {0}"] = "Current version: {0}",
        ["Buscar ahora"] = "Check now",
        ["Actualizar ahora"] = "Update now",
        ["Actualizar a {0}"] = "Update to {0}",
        ["Hay una versión nueva disponible ({0}). Clic para actualizar."]
            = "A new version is available ({0}). Click to update.",
        ["Buscando…"] = "Checking…",
        ["No se pudo comprobar (sin conexión o sin releases)."] = "Couldn't check (offline or no releases).",
        ["Estás en la última versión."] = "You're on the latest version.",
        ["Disponible {0} — pulsa «Actualizar»."] = "{0} available — click «Update».",
        ["Se descargará e instalará {0}. Windows pedirá permiso de administrador y el panel se reiniciará. ¿Continuar?"]
            = "{0} will be downloaded and installed. Windows will ask for administrator permission and the panel will restart. Continue?",
        ["Descargando {0}…"] = "Downloading {0}…",
        ["No se pudo actualizar. Prueba a descargarlo manualmente."] = "Update failed. Try downloading it manually.",

        // ── First-run dialog ──────────────────────────────────────────────────────────────────
        ["SidebarMonitor — activar sensores de tu Ryzen"] = "SidebarMonitor — enable your Ryzen's sensors",
        ["Para leer temperatura, vatios, residencia C0 y el boost por núcleo de tu Ryzen, SidebarMonitor usa el AMD Ryzen Master Monitoring SDK: el driver oficial de AMD, firmado y compatible con Integridad de Memoria (HVCI). No usa WinRing0 ni drivers dudosos."]
            = "To read your Ryzen's temperature, watts, C0 residency and per-core boost, SidebarMonitor uses the AMD Ryzen Master Monitoring SDK: AMD's official driver, signed and compatible with Memory Integrity (HVCI). It does not use WinRing0 or any dubious drivers.",
        ["AMD exige que aceptes la licencia (EULA) de su SDK antes de utilizarlo. Es un software «de evaluación», se ofrece sin garantía y con responsabilidad limitada por parte de AMD."]
            = "AMD requires you to accept its SDK license (EULA) before using it. It is \"evaluation\" software, provided without warranty and with limited liability on AMD's part.",
        ["Si no la aceptas, la app funciona igual pero en modo básico: uso y frecuencia por núcleo vía Windows (PDH), más red, discos y GPU. Podrás cambiar de opinión más adelante."]
            = "If you don't accept it, the app still works but in basic mode: per-core usage and frequency via Windows (PDH), plus network, disks and GPU. You can change your mind later.",
        ["Ver la licencia completa de AMD (License.rtf) »"] = "View AMD's full license (License.rtf) »",
        ["He leído y acepto la licencia del SDK de monitorización de AMD."]
            = "I have read and accept the AMD monitoring SDK license.",
        ["Aceptar y activar sensores"] = "Accept and enable sensors",
        ["Seguir sin el SDK (modo básico)"] = "Continue without the SDK (basic mode)",
        ["SidebarMonitor — nota sobre CPUs Intel"] = "SidebarMonitor — note about Intel CPUs",
        ["Tu CPU es Intel. A diferencia de AMD (que publica un SDK oficial firmado), Intel no ofrece una vía oficial y firmada para leer la temperatura y los vatios del procesador."]
            = "Your CPU is Intel. Unlike AMD (which publishes an official signed SDK), Intel does not offer an official, signed way to read the processor's temperature and watts.",
        ["Esos sensores viven en registros del chip (MSR) a los que solo se llega con un driver a nivel de kernel (ring 0), como PawnIO. SidebarMonitor todavía no lo incluye, así que por ahora el detalle profundo de la CPU (temp/vatios/C0/boost por núcleo) no está disponible."]
            = "Those sensors live in the chip's registers (MSR), reachable only with a kernel-level (ring 0) driver such as PawnIO. SidebarMonitor doesn't include one yet, so for now the deep CPU detail (temp/watts/C0/per-core boost) is unavailable.",
        ["Lo que sí verás con normalidad: uso y frecuencia por núcleo (Windows PDH), procesos, red, discos y su temperatura, y la GPU — incluida temperatura/vatios si el driver de tu GPU (NVIDIA / AMD / Intel) los expone."]
            = "What you will see normally: per-core usage and frequency (Windows PDH), processes, network, disks and their temperature, and the GPU — including temperature/watts if your GPU driver (NVIDIA / AMD / Intel) exposes them.",
        ["El soporte de sensores Intel (vía ring0/PawnIO) está en la hoja de ruta. Cuando llegue, este mismo aviso te pedirá permiso para instalar ese componente."]
            = "Intel sensor support (via ring0/PawnIO) is on the roadmap. When it arrives, this same notice will ask your permission to install that component.",
        ["Entendido, continuar"] = "Got it, continue",
        ["No encuentro License.rtf empaquetada. Está en la instalación del SDK de AMD, normalmente en C:\\Program Files\\AMD\\RyzenMasterMonitoringSDK\\License.rtf."]
            = "I can't find a bundled License.rtf. It's in the AMD SDK installation, usually at C:\\Program Files\\AMD\\RyzenMasterMonitoringSDK\\License.rtf.",
        ["Licencia de AMD"] = "AMD license",
    };
}
