using System.Globalization;
using System.IO;
using System.Text;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// Optional CSV recorder — the "log to file" the other monitors offer. One row per snapshot the UI
/// draws, appended to a timestamped file under %LOCALAPPDATA%\SidebarMonitor\logs. Columns cover the
/// whole snapshot (CPU aggregate + limits, per-core usage/freq/temp/C0, RAM, GPU0, net and disk
/// totals). The header is written lazily on the first row, once the real core count is known, so it
/// adapts to any CPU. All numbers use invariant culture (dot decimal) so the file opens cleanly in
/// Excel/pandas anywhere. Failures are swallowed: logging must never disturb monitoring.
/// </summary>
internal sealed class CsvLogger : IDisposable
{
    private StreamWriter? _writer;
    private int _cores = -1;
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public bool IsRunning => _writer is not null;
    public long RowCount { get; private set; }
    public string? CurrentPath { get; private set; }

    public static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor", "logs");

    /// <summary>Open a fresh file. <paramref name="stamp"/> is the local time for the filename (the
    /// caller passes DateTime.Now — this class avoids taking the clock itself so it stays testable).</summary>
    public void Start(DateTime stamp)
    {
        Stop();
        try
        {
            Directory.CreateDirectory(LogDir);
            CurrentPath = Path.Combine(LogDir, $"sidebar-{stamp:yyyyMMdd-HHmmss}.csv");
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
                _cores = Math.Clamp(s.Cpu.CoreCount, 0, SnapshotLayout.MaxCores);
                WriteHeader();
            }

            var sb = new StringBuilder(512);
            sb.Append(stampUtc.ToString("o", Ci));
            ref var c = ref s.Cpu;
            N(sb, c.TotalUsagePct); N(sb, Ghz(c.FreqBestMhz)); N(sb, c.PackagePowerW); N(sb, c.TempC);
            N(sb, c.TjMaxC); N(sb, c.PptPct); N(sb, c.TdcPct); N(sb, c.EdcPct); I(sb, c.BestCore);

            // RAM (GiB) + commit.
            N(sb, Gib(s.Mem.PhysUsed)); N(sb, Gib(s.Mem.PhysTotal)); N(sb, Gib(s.Mem.CommitUsed));

            // GPU 0 (the primary, usually the discrete one).
            if (s.GpuCount > 0)
            {
                ref var g = ref s.Gpus[0];
                N(sb, g.LoadPct); N(sb, g.TempC); N(sb, g.PowerW); N(sb, Gib(g.VramUsed));
            }
            else { sb.Append(",,,,"); }

            // Net + disk totals across all interfaces/drives.
            double rx = 0, tx = 0;
            for (int i = 0; i < s.NicCount; i++) { rx += s.Nics[i].RxBytesPerSec; tx += s.Nics[i].TxBytesPerSec; }
            N(sb, rx); N(sb, tx);
            double rd = 0, wr = 0;
            for (int i = 0; i < s.DiskCount; i++) { rd += s.Disks[i].ReadBytesPerSec; wr += s.Disks[i].WriteBytesPerSec; }
            N(sb, rd); N(sb, wr);

            // Per-core block: usage, freq (GHz), temp, C0.
            for (int i = 0; i < _cores; i++) N(sb, c.CoreUsagePct[i]);
            for (int i = 0; i < _cores; i++) N(sb, Ghz(c.CoreFreqMhz[i]));
            for (int i = 0; i < _cores; i++) N(sb, c.CoreTempC[i]);
            for (int i = 0; i < _cores; i++) N(sb, c.CoreC0Pct[i]);

            _writer.WriteLine(sb.ToString());
            RowCount++;
            if (RowCount % 20 == 0) _writer.Flush();   // survive a crash without flushing every row
        }
        catch { /* disk full / locked: stop bothering */ Stop(); }
    }

    private void WriteHeader()
    {
        var h = new StringBuilder(512);
        h.Append("timestamp_utc,cpu_pct,cpu_ghz,cpu_watts,cpu_temp_c,cpu_tjmax_c,ppt_pct,tdc_pct,edc_pct,best_core,");
        h.Append("ram_used_gib,ram_total_gib,commit_used_gib,");
        h.Append("gpu0_load_pct,gpu0_temp_c,gpu0_watts,gpu0_vram_used_gib,");
        h.Append("net_rx_bps,net_tx_bps,disk_read_bps,disk_write_bps");
        for (int i = 0; i < _cores; i++) h.Append(Ci, $",core{i}_pct");
        for (int i = 0; i < _cores; i++) h.Append(Ci, $",core{i}_ghz");
        for (int i = 0; i < _cores; i++) h.Append(Ci, $",core{i}_temp_c");
        for (int i = 0; i < _cores; i++) h.Append(Ci, $",core{i}_c0_pct");
        _writer!.WriteLine(h.ToString());
    }

    // Empty cell for NaN so gaps read as blanks (Excel/pandas treat them as missing), value otherwise.
    private static void N(StringBuilder sb, double v)
    {
        sb.Append(',');
        if (!double.IsNaN(v)) sb.Append(v.ToString("0.###", Ci));
    }
    private static void I(StringBuilder sb, int v) { sb.Append(','); sb.Append(v.ToString(Ci)); }

    private static double Ghz(float mhz) => float.IsNaN(mhz) ? double.NaN : mhz / 1000.0;
    private static double Gib(ulong bytes) => bytes / (1024.0 * 1024 * 1024);

    public void Dispose() => Stop();
}
