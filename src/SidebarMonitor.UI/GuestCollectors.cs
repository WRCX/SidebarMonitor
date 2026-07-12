using System.Diagnostics;
using System.Globalization;

namespace SidebarMonitor.UI;

/// <summary>One guest workload row: a Docker container or a WSL process.</summary>
internal readonly record struct GuestRow(string Name, double CpuPct, ulong MemBytes, double NetRxBps, double NetTxBps, bool HasNet);

/// <summary>
/// Reads per-workload stats that Windows can't see, by asking the guest directly (Docker's CLI, or
/// a WSL command). Windows only exposes the aggregate as vmmemWSL; the breakdown lives inside the
/// Hyper-V VM. Polling spawns an external process, so it runs off the UI thread and self-throttles.
/// </summary>
internal abstract class GuestCollector
{
    private volatile GuestRow[] _latest = [];
    private int _busy;
    private long _lastStart;

    /// <summary>Floor between spawns; the external tools are slow (docker stats ~1-2 s).</summary>
    protected abstract int MinIntervalMs { get; }

    public GuestRow[] Latest => _latest;
    /// <summary>null until the first attempt, then whether the last collect worked.</summary>
    public bool? Available { get; private set; }

    /// <summary>Non-blocking: kicks a background collect if one isn't running and enough time passed.</summary>
    public void Poll()
    {
        if (_lastStart != 0 && Stopwatch.GetElapsedTime(_lastStart).TotalMilliseconds < MinIntervalMs) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _lastStart = Stopwatch.GetTimestamp();
        Task.Run(() =>
        {
            try { _latest = Collect() ?? []; Available = true; }
            catch { _latest = []; Available = false; }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    protected abstract GuestRow[] Collect();

    protected static string Run(string exe, string[] args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("no arrancó");

        // Drain BOTH streams asynchronously. Reading one to the end while the other's buffer fills
        // (the wsl.exe relay is prone to this) deadlocks — the process blocks writing, we block
        // reading, and WaitForExit is never reached.
        var outSb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) outSb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, _) => { };   // drained and discarded
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException(); }
        p.WaitForExit();   // let the async readers flush the tail
        if (p.ExitCode != 0) throw new InvalidOperationException($"exit {p.ExitCode}");
        return outSb.ToString();
    }

    protected static double ParsePct(string s) =>
        double.TryParse(s.Trim().TrimEnd('%').Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>Parses "282.1MiB", "1.139GiB", "38.7MB", "512kB", or a bare KiB number (top's RES).</summary>
    protected static double ParseBytes(string s, bool bareIsKiB = false)
    {
        s = s.Trim();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',')) i++;
        if (i == 0) return 0;
        if (!double.TryParse(s[..i].Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double num)) return 0;
        string u = s[i..].Trim().ToLowerInvariant();
        double mult = u switch
        {
            "b" => 1,
            "kb" => 1000, "kib" => 1024, "k" => 1024,
            "mb" => 1e6, "mib" => 1024.0 * 1024, "m" => 1024.0 * 1024,
            "gb" => 1e9, "gib" => 1024.0 * 1024 * 1024, "g" => 1024.0 * 1024 * 1024,
            "tb" => 1e12, "tib" => 1024.0 * 1024 * 1024 * 1024, "t" => 1024.0 * 1024 * 1024 * 1024,
            "" => bareIsKiB ? 1024 : 1,
            _ => 1,
        };
        return num * mult;
    }
}

/// <summary>Per-container stats from `docker stats`. Net is cumulative, so it's diffed to a rate.</summary>
internal sealed class DockerCollector : GuestCollector
{
    protected override int MinIntervalMs => 2000;
    private readonly Dictionary<string, (double rx, double tx, long stamp)> _prev = new();

    protected override GuestRow[] Collect()
    {
        string outp = Run("docker",
            ["stats", "--no-stream", "--format", "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.NetIO}}"], 6000);

        var rows = new List<GuestRow>();
        long now = Stopwatch.GetTimestamp();
        var seen = new HashSet<string>();
        foreach (var line in outp.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0) continue;
            var f = l.Split('|');
            if (f.Length < 4) continue;

            string name = f[0].Trim();
            double cpu = ParsePct(f[1]);
            double mem = ParseBytes(f[2].Split('/')[0]);

            var net = f[3].Split('/');
            double rx = ParseBytes(net[0]), tx = net.Length > 1 ? ParseBytes(net[1]) : 0;
            double rxBps = 0, txBps = 0; bool hasNet = false;
            if (_prev.TryGetValue(name, out var pv))
            {
                double secs = Stopwatch.GetElapsedTime(pv.stamp).TotalSeconds;
                if (secs > 0.1) { rxBps = Math.Max(0, (rx - pv.rx) / secs); txBps = Math.Max(0, (tx - pv.tx) / secs); hasNet = true; }
            }
            _prev[name] = (rx, tx, now);
            seen.Add(name);
            rows.Add(new GuestRow(name, cpu, (ulong)mem, rxBps, txBps, hasNet));
        }
        // Forget containers that went away, so the diff map doesn't grow forever.
        foreach (var k in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(k);

        rows.Sort((a, b) => b.CpuPct != a.CpuPct ? b.CpuPct.CompareTo(a.CpuPct) : b.MemBytes.CompareTo(a.MemBytes));
        return rows.ToArray();
    }
}

/// <summary>Top WSL processes via `top -bn2` (the 2nd pass gives a real %CPU). Per-process network
/// isn't readily available inside the guest, so those columns are left blank.</summary>
internal sealed class WslCollector : GuestCollector
{
    protected override int MinIntervalMs => 1500;

    protected override GuestRow[] Collect()
    {
        // -bn2: two iterations so the second reports instantaneous %CPU. LC_ALL=C for a stable format.
        // The first call cold-starts the WSL VM (systemd boot, ~20 s here); allow for it. Once warm
        // it answers in ~1 s, and polling while the section is open keeps it warm.
        string outp = Run("wsl.exe",
            ["-e", "sh", "-c", "LC_ALL=C top -bn2 -d 0.4 -w 200 | tail -n 45"], 30000);

        var lines = outp.Split('\n');
        int header = -1;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].TrimStart().StartsWith("PID", StringComparison.Ordinal)) header = i;   // last header = 2nd pass
        if (header < 0) return [];

        var rows = new List<GuestRow>();
        for (int i = header + 1; i < lines.Length; i++)
        {
            var p = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 12) continue;   // PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ COMMAND
            double cpu = ParsePct(p[8]);
            double res = ParseBytes(p[5], bareIsKiB: true);   // RES, KiB by default
            rows.Add(new GuestRow(p[11], cpu, (ulong)res, 0, 0, false));
        }
        rows.Sort((a, b) => b.CpuPct != a.CpuPct ? b.CpuPct.CompareTo(a.CpuPct) : b.MemBytes.CompareTo(a.MemBytes));
        return rows.Take(8).ToArray();
    }
}
