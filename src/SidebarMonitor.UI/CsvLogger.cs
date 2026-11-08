using System.Globalization;
using System.IO;
using System.Text;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// One selectable group of CSV columns. <see cref="Label"/> is a deferred <see cref="Loc.T(string)"/>
/// call: deferred so it reads the language chosen at startup, and a literal call so the
/// LocStrings test can see the key is used. <see cref="Columns"/> is the literal header text, shown
/// untranslated because it is what lands in the file.
/// </summary>
internal sealed record CsvMetric(string Key, Func<string> Label, string Columns, bool Default);

/// <summary>
/// Optional CSV recorder — the "log to file" the other monitors offer. One row per snapshot the UI
/// draws, appended to a timestamped file under %LOCALAPPDATA%\SidebarMonitor\logs. Which columns it
/// writes is chosen per metric group (<see cref="Catalog"/>, <see cref="UiConfig.CsvMetrics"/>); the
/// default set is exactly the columns older versions always wrote, so existing scripts keep working.
/// Columns always come out in catalog order, whatever order the user ticked them in, so two files
/// with the same selection share a header. The header is written lazily on the first row, once the
/// real core count is known, so it adapts to any CPU. All numbers use invariant culture (dot decimal)
/// so the file opens cleanly in Excel/pandas anywhere. Failures are swallowed: logging must never
/// disturb monitoring.
/// </summary>
internal sealed class CsvLogger : IDisposable
{
    /// <summary>Every metric group the CSV can record, in column order.</summary>
    public static readonly CsvMetric[] Catalog =
    [
        new("cpu",       () => Loc.T("Uso y reloj de CPU"),            "cpu_pct, cpu_ghz",                                         true),
        new("cpu_power", () => Loc.T("Potencia de CPU"),               "cpu_watts",                                                true),
        new("cpu_temp",  () => Loc.T("Temperatura de CPU"),            "cpu_temp_c, cpu_tjmax_c",                                  true),
        new("limits",    () => Loc.T("Límites de CPU"),                "ppt_pct, tdc_pct, edc_pct, best_core",                     true),
        new("throttle",  () => Loc.T("Limitador activo (throttling)"), "throttle_thermal, throttle_power, throttle_current (0/1)", false),
        new("fan",       () => Loc.T("Ventilador"),                    "fan_pct, fan_rpm",                                         false),
        new("ram",       () => "RAM",                                  "ram_used_gib, ram_total_gib, commit_used_gib",             true),
        new("gpu0",      () => "GPU 0",                                "gpu0_load_pct, gpu0_temp_c, gpu0_watts, gpu0_vram_used_gib", true),
        new("gpu0_fan",  () => Loc.T("Ventilador de GPU 0"),           "gpu0_fan_pct",                                             false),
        new("net",       () => Loc.T("Red (total)"),                   "net_rx_bps, net_tx_bps",                                   true),
        new("disk",      () => Loc.T("Disco (total)"),                 "disk_read_bps, disk_write_bps",                            true),
        new("core_pct",  () => Loc.T("Uso por núcleo"),                "coreN_pct",                                                true),
        new("core_ghz",  () => Loc.T("Reloj por núcleo"),              "coreN_ghz",                                                true),
        new("core_temp", () => Loc.T("Temperatura por núcleo"),        "coreN_temp_c",                                             true),
        new("core_c0",   () => Loc.T("Residencia C0 por núcleo"),      "coreN_c0_pct",                                             true),
        new("battery",   () => Loc.T("Batería"),                       "battery_pct, battery_w",                                   false),
    ];

    /// <summary>The selection used when the config has none (<c>CsvMetrics</c> null): the legacy columns.</summary>
    public static IReadOnlyList<string> DefaultKeys { get; } = Catalog.Where(m => m.Default).Select(m => m.Key).ToArray();

    /// <summary>Normalise a saved selection: null means the default set; unknown keys are dropped;
    /// the result follows catalog order.</summary>
    public static string[] Resolve(IEnumerable<string>? selected)
    {
        if (selected is null) return [.. DefaultKeys];
        var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return Catalog.Where(m => set.Contains(m.Key)).Select(m => m.Key).ToArray();
    }

    private StreamWriter? _writer;
    private int _cores = -1;
    private string[] _keys = [.. DefaultKeys];
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public bool IsRunning => _writer is not null;
    public long RowCount { get; private set; }
    public string? CurrentPath { get; private set; }

    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor", "logs");

    /// <summary>Open a fresh file recording <paramref name="metrics"/> (null = the default set).
    /// <paramref name="stamp"/> is the local time for the filename (the caller passes DateTime.Now —
    /// this class avoids taking the clock itself so it stays testable). Calling it while running
    /// closes the current file and starts a new one: a different selection needs a new header.</summary>
    public void Start(DateTime stamp, IEnumerable<string>? metrics = null)
    {
        Stop();
        try
        {
            _keys = Resolve(metrics);
            Directory.CreateDirectory(LogDir);
            CurrentPath = Path.Combine(LogDir, $"sidebar-{stamp:yyyyMMdd-HHmmss}.csv");
            // A second Start within the same second (e.g. ticking two metrics quickly) must not
            // truncate the file it just opened.
            for (int n = 2; File.Exists(CurrentPath); n++)
                CurrentPath = Path.Combine(LogDir, $"sidebar-{stamp:yyyyMMdd-HHmmss}-{n}.csv");
            _writer = new StreamWriter(CurrentPath, append: false, Encoding.UTF8) { AutoFlush = false };
            _cores = -1;   // header written on first Log, when we know the core count
            RowCount = 0;
        }
        catch { _writer = null; CurrentPath = null; }
    }

    public void Stop()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
    }

    public void Log(ref Snapshot s, DateTime stampUtc)
    {
        if (_writer is null) return;
        try
        {
            if (_cores < 0)
            {
                // Wait for the first populated snapshot: if CoreCount is still 0 (agent read before PDH
                // filled the per-core counter), latching it now would give every row zero core columns.
                if (s.Cpu.CoreCount <= 0) return;
                _cores = Math.Clamp(s.Cpu.CoreCount, 0, SnapshotLayout.MaxCores);
                _writer.WriteLine(BuildHeader(_keys, _cores));
            }

            var sb = new StringBuilder(512);
            sb.Append(stampUtc.ToString("o", Ci));
            foreach (var k in _keys) AppendValues(sb, k, ref s, _cores);

            _writer.WriteLine(sb.ToString());
            RowCount++;
            if (RowCount % 20 == 0) _writer.Flush();   // survive a crash without flushing every row
        }
        catch { /* disk full / locked: stop bothering */ Stop(); }
    }

    /// <summary>The header line for a selection (internal for tests).</summary>
    internal static string BuildHeader(IReadOnlyList<string> keys, int cores)
    {
        var h = new StringBuilder(512);
        h.Append("timestamp_utc");
        foreach (var k in keys)
        {
            switch (k)
            {
                case "cpu":       h.Append(",cpu_pct,cpu_ghz"); break;
                case "cpu_power": h.Append(",cpu_watts"); break;
                case "cpu_temp":  h.Append(",cpu_temp_c,cpu_tjmax_c"); break;
                case "limits":    h.Append(",ppt_pct,tdc_pct,edc_pct,best_core"); break;
                case "throttle":  h.Append(",throttle_thermal,throttle_power,throttle_current"); break;
                case "fan":       h.Append(",fan_pct,fan_rpm"); break;
                case "ram":       h.Append(",ram_used_gib,ram_total_gib,commit_used_gib"); break;
                case "gpu0":      h.Append(",gpu0_load_pct,gpu0_temp_c,gpu0_watts,gpu0_vram_used_gib"); break;
                case "gpu0_fan":  h.Append(",gpu0_fan_pct"); break;
                case "net":       h.Append(",net_rx_bps,net_tx_bps"); break;
                case "disk":      h.Append(",disk_read_bps,disk_write_bps"); break;
                case "core_pct":  for (int i = 0; i < cores; i++) h.Append(Ci, $",core{i}_pct"); break;
                case "core_ghz":  for (int i = 0; i < cores; i++) h.Append(Ci, $",core{i}_ghz"); break;
                case "core_temp": for (int i = 0; i < cores; i++) h.Append(Ci, $",core{i}_temp_c"); break;
                case "core_c0":   for (int i = 0; i < cores; i++) h.Append(Ci, $",core{i}_c0_pct"); break;
                case "battery":   h.Append(",battery_pct,battery_w"); break;
            }
        }
        return h.ToString();
    }

    /// <summary>Append one metric group's cells (internal for tests).</summary>
    internal static void AppendValues(StringBuilder sb, string key, ref Snapshot s, int cores)
    {
        ref var c = ref s.Cpu;
        switch (key)
        {
            case "cpu":       N(sb, c.TotalUsagePct); N(sb, Ghz(c.FreqBestMhz)); break;
            case "cpu_power": N(sb, c.PackagePowerW); break;
            case "cpu_temp":  N(sb, c.TempC); N(sb, c.TjMaxC); break;
            case "limits":    N(sb, c.PptPct); N(sb, c.TdcPct); N(sb, c.EdcPct); I(sb, c.BestCore); break;
            case "throttle":
                // Same "hard cap" rule the panel's limiter indicator uses (MainWindow.ThrottleState):
                // Intel's authoritative status bits, or the AMD limit percentages / Tjmax margin.
                bool haveTemp = c.TjMaxC > 0 && !float.IsNaN(c.TempC);
                B(sb, (c.ThrottleFlags & 1) != 0 || (haveTemp && c.TempC >= c.TjMaxC - 3));
                B(sb, (c.ThrottleFlags & 2) != 0 || c.PptPct >= 99);
                B(sb, (c.ThrottleFlags & 4) != 0 || c.TdcPct >= 99 || c.EdcPct >= 99);
                break;
            case "fan":       N(sb, c.FanPct); N(sb, c.FanRpm); break;
            case "ram":       N(sb, Gib(s.Mem.PhysUsed)); N(sb, Gib(s.Mem.PhysTotal)); N(sb, Gib(s.Mem.CommitUsed)); break;
            case "gpu0":
                // GPU 0 is the primary, usually the discrete one.
                if (s.GpuCount > 0)
                {
                    ref var g = ref s.Gpus[0];
                    N(sb, g.LoadPct); N(sb, g.TempC); N(sb, g.PowerW); N(sb, Gib(g.VramUsed));
                }
                else sb.Append(",,,,");
                break;
            case "gpu0_fan":
                // Only NVML-backed adapters report a fan; the rest leave the field at 0, which would
                // read as "fan stopped" rather than "unknown".
                if (s.GpuCount > 0 && s.Gpus[0].HasDetail != 0) N(sb, s.Gpus[0].FanPct);
                else sb.Append(',');
                break;
            case "net":
            {
                double rx = 0, tx = 0;
                for (int i = 0; i < s.NicCount; i++) { rx += s.Nics[i].RxBytesPerSec; tx += s.Nics[i].TxBytesPerSec; }
                N(sb, rx); N(sb, tx);
                break;
            }
            case "disk":
            {
                double rd = 0, wr = 0;
                for (int i = 0; i < s.DiskCount; i++) { rd += s.Disks[i].ReadBytesPerSec; wr += s.Disks[i].WriteBytesPerSec; }
                N(sb, rd); N(sb, wr);
                break;
            }
            case "core_pct":  for (int i = 0; i < cores; i++) N(sb, c.CoreUsagePct[i]); break;
            case "core_ghz":  for (int i = 0; i < cores; i++) N(sb, Ghz(c.CoreFreqMhz[i])); break;
            case "core_temp": for (int i = 0; i < cores; i++) N(sb, c.CoreTempC[i]); break;
            case "core_c0":   for (int i = 0; i < cores; i++) N(sb, c.CoreC0Pct[i]); break;
            // Blank on a desktop (NaN). Last in the catalog, after the variable-width per-core block.
            case "battery":   N(sb, s.Battery.Pct); N(sb, s.Battery.RateW); break;
        }
    }

    // Empty cell for NaN so gaps read as blanks (Excel/pandas treat them as missing), value otherwise.
    private static void N(StringBuilder sb, double v)
    {
        sb.Append(',');
        if (!double.IsNaN(v)) sb.Append(v.ToString("0.###", Ci));
    }
    private static void I(StringBuilder sb, int v) { sb.Append(','); sb.Append(v.ToString(Ci)); }
    private static void B(StringBuilder sb, bool v) { sb.Append(','); sb.Append(v ? '1' : '0'); }

    private static double Ghz(float mhz) => float.IsNaN(mhz) ? double.NaN : mhz / 1000.0;
    private static double Gib(ulong bytes) => bytes / (1024.0 * 1024 * 1024);

    public void Dispose() => Stop();
}
