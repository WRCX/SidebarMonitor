using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SidebarMonitor.UI;

/// <summary>Section states: 0 hidden, 1 folded, 2 expanded.</summary>
public sealed class UiConfig
{
    public int RefreshMs { get; set; } = 1000;
    public bool Topmost { get; set; } = true;

    /// <summary>UI language: "auto" follows the OS culture (Spanish on es-*, else English), "es" and
    /// "en" force it. Applied at startup by <see cref="Loc.Init"/>; changing it relaunches the UI.</summary>
    public string Language { get; set; } = "auto";

    // ── First-run / platform consent ──────────────────────────────────────────────────────────
    /// <summary>The vendor-specific first-run notice (AMD EULA or Intel ring0 info) has been shown.</summary>
    public bool FirstRunNoticeShown { get; set; }
    /// <summary>User accepted AMD's Ryzen Master Monitoring SDK EULA. Gates the elevated SDK sensors.
    /// Mirrored to a marker file the elevated helper reads (it can't see this config's semantics).</summary>
    public bool AmdEulaAccepted { get; set; }
    /// <summary>Intel user acknowledged that deep CPU sensors (temp/power) need a ring0 driver (PawnIO)
    /// which isn't bundled yet — so the app runs in PDH-only mode without nagging again.</summary>
    public bool IntelRing0Ack { get; set; }

    // ── Diagnostics / logging ─────────────────────────────────────────────────────────────────
    /// <summary>Append every snapshot as a row to a CSV under %LOCALAPPDATA%\SidebarMonitor\logs.</summary>
    public bool LogCsv { get; set; }
    /// <summary>Verbose on-screen debug overlay (contract versions, vendor, SDK/helper state, FPS).</summary>
    public bool LogVerbose { get; set; }

    /// <summary>Legacy: false = single total line, true = one faint line per core overlaid. Migrated
    /// into <see cref="CpuGraphMode"/> on load.</summary>
    public bool CpuPerCoreGraph { get; set; }

    /// <summary>CPU usage graph: 0 = single total line, 1 = one faint line per core overlaid,
    /// 2 = a separate mini-graph per core in a grid (like the GPU engines).</summary>
    public int CpuGraphMode { get; set; }

    /// <summary>Columns for the per-core mini-graph grid (mode 2): 4, 3, 2 or 1.</summary>
    public int CpuGraphColumns { get; set; } = 4;

    /// <summary>Per-core grid Y-axis: false = every core shares a fixed 0..100 axis (comparable at a
    /// glance), true = each core autoscales to its own range (detail on idle cores, but not
    /// comparable). Default false — the grid exists to compare cores.</summary>
    public bool CpuGridAutoScale { get; set; }

    /// <summary>Colour palette for the per-core bars/lines/mini-graphs: 0 rainbow, 1 high-contrast,
    /// 2 cool (blues), 3 warm (reds), 4 pastel.</summary>
    public int CorePalette { get; set; }

    /// <summary>Per-core clock / temperature on each core row, and where it sits.</summary>
    public bool ShowCoreFreq { get; set; } = true;
    public bool ShowCoreTemp { get; set; }
    /// <summary>0 inside the fill, 1 overlaid at the bar's end (default), 2 outside before the %.</summary>
    public int CoreMetricPos { get; set; } = 1;

    /// <summary>Dim parked cores (C0≈0) and label them "sleep", like Ryzen Master. Needs the helper.</summary>
    public bool MarkSleepCores { get; set; } = true;
    /// <summary>Legacy: per-core bar used C0 instead of % Utility. Migrated into CoreBarMode.</summary>
    public bool CoreUsageC0 { get; set; }

    /// <summary>Per-core bar: 0 = %Util (work), 1 = C0 residency (awake), 2 = combined (both layered),
    /// 3 = work bar + an "awake" tick.</summary>
    public int CoreBarMode { get; set; }

    /// <summary>Where to show the CPU model name: 0 = off, 1 = after the section title, 2 = inside
    /// the section (a line at the top, visible when expanded).</summary>
    public int CpuNameMode { get; set; }
    /// <summary>Where to show the primary GPU model name: 0 = off, 1 = after the GPU section title,
    /// 2 = inside (each GPU block already shows its own name when expanded).</summary>
    public int GpuNameMode { get; set; }

    /// <summary>RAM module info (from WMI) in the MEMORIA section: 0 = off, 1 = compact summary
    /// ("2× 16 GiB DDR5-6000"), 2 = full per-stick detail as text (slot, size, speed, maker, part).</summary>
    public int RamModulesMode { get; set; } = 1;

    /// <summary>Memory byte units: true = binary (GiB, 1024), false = decimal (GB, 1000).</summary>
    public bool MemUnitsBinary { get; set; } = true;

    /// <summary>Show the GPU's per-engine breakdown (3D/compute/decode/…) and top process.</summary>
    public bool ShowGpuEngines { get; set; } = true;

    /// <summary>Which GPU(s) to show: 0 = discrete (NVIDIA), 1 = integrated (Ryzen iGPU), 2 = both.</summary>
    public int GpuView { get; set; } = 2;

    /// <summary>Columns in the per-engine mini-graph grid: 4, 3, 2 or 1 (4 ≈ a quarter width each).</summary>
    public int GpuEngineColumns { get; set; } = 4;

    /// <summary>Clicks pass through the panel to whatever is behind it. Most useful when floating.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>Which core-clock aggregate to show: 0 best, 1 mean, 2 median. Best by default.</summary>
    public int CpuFreqMode { get; set; }

    /// <summary>Optional extra CPU readouts from the AMD SDK, off by default.</summary>
    public bool ShowCpuVid { get; set; }
    /// <summary>The HWiNFO-style "Limits" line: frequency ceiling + PPT/TDC/EDC/thermal usage.</summary>
    public bool ShowCpuLimits { get; set; }

    /// <summary>The binding-limiter indicator (POT/CORR/TÉRM): what's holding the boost back now.</summary>
    public bool ShowThrottle { get; set; } = true;

    /// <summary>Best-core boost vs its session peak — shows the temperature-driven boost curve.</summary>
    public bool ShowBoost { get; set; } = true;

    /// <summary>
    /// Per-graph Y-axis auto-scale: fit the axis to the window's min..max so low, flat data still
    /// fills the chart. Keys: cpu, gpu, net, disk. On by default — the whole reason to have it.
    /// </summary>
    public bool CpuGraphAuto { get; set; } = true;
    public bool GpuGraphAuto { get; set; } = true;
    public bool NetGraphAuto { get; set; } = true;
    public bool DiskGraphAuto { get; set; } = true;

    /// <summary>Global graph height multiplier: 1.0 small, 1.5 medium, 2.0 large, 3.0 huge. The default
    /// for any graph without a per-graph override in <see cref="GraphScales"/>.</summary>
    public double GraphScale { get; set; } = 1.0;

    /// <summary>Per-graph height override, by key (cpu/gpu/net/disk). A graph listed here uses its own
    /// multiplier instead of the global <see cref="GraphScale"/>; not listed = follow the global.</summary>
    public Dictionary<string, double> GraphScales { get; set; } = [];

    /// <summary>Tooltip background opacity, 0..1. 1 = opaque, lower = more translucent.</summary>
    public double TooltipOpacity { get; set; } = 0.85;

    /// <summary>
    /// How many per-process rows the RED section always shows, padded with blanks. Fixed on purpose:
    /// letting the row count follow the live process count makes every section below it jump around.
    /// </summary>
    public int NetProcRows { get; set; } = 4;

    /// <summary>Byte units: true = binary (KiB/MiB/GiB, 1024), false = decimal (KB/MB/GB, 1000).
    /// Independent for network and disk.</summary>
    public bool NetUnitsBinary { get; set; } = true;
    public bool DiskUnitsBinary { get; set; } = true;

    /// <summary>Disk kinds to leave out of the list.</summary>
    public bool HideVirtualDisks { get; set; } = true;
    public bool HideRemovableDisks { get; set; }
    public bool HideSystemDisk { get; set; }

    /// <summary>Docked pins the panel to a screen edge. Floating can be dragged anywhere.</summary>
    public bool Docked { get; set; } = true;

    /// <summary>
    /// Only meaningful while docked. On = register an AppBar so the shell reserves the strip and
    /// nothing ever covers it (but maximises/snaps/fullscreen stop at our edge). Off = sit at the
    /// edge as a plain overlay: other windows maximise, snap and go fullscreen across the whole
    /// monitor, drawing under us. Off is what you want if the panel was cramping window management.
    /// </summary>
    public bool ReserveSpace { get; set; } = true;

    public bool EdgeLeft { get; set; }
    public int Monitor { get; set; } = 1;
    public int Width { get; set; } = 280;
    public bool Minimized { get; set; }

    public double FloatX { get; set; } = 100;
    public double FloatY { get; set; } = 100;
    public double FloatHeight { get; set; } = 800;

    public Dictionary<string, int> Sections { get; set; } = [];

    /// <summary>
    /// Per-section refresh override in ms, by section key. A section not listed here redraws at the
    /// global <see cref="RefreshMs"/>. Lets you run CPU fast but disks slow, for instance.
    /// </summary>
    public Dictionary<string, int> SectionRefreshMs { get; set; } = [];

    /// <summary>
    /// Vertical order of the sections by key, top to bottom. Empty = the built-in default order.
    /// Any section missing from the list keeps its default position, appended after the listed ones.
    /// </summary>
    public List<string> SectionOrder { get; set; } = [];

    /// <summary>
    /// Set when command-line flags overrode the saved settings. Debug runs must not rewrite the
    /// user's configuration behind their back, so Save becomes a no-op.
    /// </summary>
    [JsonIgnore]
    public bool Ephemeral { get; set; }

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SidebarMonitor", "ui.json");

    public static UiConfig Load()
    {
        try
        {
            // An older build wrote a bare {"cpu":2,...} map here; deserializing it into UiConfig
            // yields defaults, which is the right outcome.
            if (File.Exists(Path))
                return JsonSerializer.Deserialize(File.ReadAllText(Path), UiConfigContext.Default.UiConfig) ?? new UiConfig();
        }
        catch { /* corrupt or first run */ }
        return new UiConfig();
    }

    public void Save()
    {
        if (Ephemeral) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, UiConfigContext.Default.UiConfig));
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>Source-generated so the UI never needs reflection-based serialization.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiConfig))]
internal partial class UiConfigContext : JsonSerializerContext;
