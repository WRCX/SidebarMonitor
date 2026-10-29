using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Etw;

/// <summary>
/// Frame-timing for the foreground game, from Intel's PresentMon CLI (ETW, no injection — the same
/// class of capture this elevated helper already does). We spawn <c>PresentMon.exe</c> streaming CSV
/// to stdout, keep a ~2 s rolling window of frames per process, and each publish compute FPS,
/// frametime, 1%/0.1% lows, GPU-busy, latency and animation-error (stutter) for whatever app owns the
/// foreground window. Opt-in: only runs while the UI has written the "fps-enabled" marker, so a user
/// who doesn't want it pays nothing.
/// </summary>
internal sealed class FrameStats : IDisposable
{
    private const double WindowSec = 2.0;

    private readonly string _exe;                 // PresentMon.exe path, or "" if not bundled
    private Process? _pm;
    private readonly object _lock = new();
    private readonly Dictionary<int, Buf> _byPid = new();
    private int[] _col = [];                       // header column indices we care about

    private sealed class Buf
    {
        public string App = "";
        public readonly List<(long ms, float frame, float disp, float gpu, float anim, float lat)> F = new(256);
    }

    // Column names in the v2 CSV (matched from the header, so order/version changes don't break us).
    private static readonly string[] Wanted =
        ["Application", "ProcessID", "FrameTime", "DisplayedTime", "GPUBusy", "AnimationError", "ClickToPhotonLatency"];

    public FrameStats(string helperDir)
    {
        string p = Path.Combine(helperDir, "PresentMon.exe");
        _exe = File.Exists(p) ? p : "";
    }

    public bool Available => _exe.Length > 0;

    /// <summary>Starts PresentMon when enabled and not yet running; stops it when disabled.</summary>
    public void SetEnabled(bool enabled)
    {
        if (!Available) return;
        if (enabled)
        {
            if (_pm is { HasExited: false }) return;
            Start();
        }
        else if (_pm is not null)
        {
            StopProcess();
        }
    }

    private void Start()
    {
        try
        {
            var psi = new ProcessStartInfo(_exe)
            {
                Arguments = "--output_stdout --v2_metrics --no_console_stats " +
                            "--stop_existing_session --session_name SidebarMonitorPM",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _pm = Process.Start(psi);
            if (_pm is null) return;
            var t = new Thread(ReadLoop) { IsBackground = true };
            t.Start();
        }
        catch { _pm = null; }
    }

    private void ReadLoop()
    {
        var proc = _pm;
        if (proc is null) return;
        try
        {
            string? header = proc.StandardOutput.ReadLine();
            if (header is not null) MapColumns(header);
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
                ParseRow(line);
        }
        catch { /* process ended */ }
    }

    private void MapColumns(string header)
    {
        var cols = header.Split(',');
        var idx = new int[Wanted.Length];
        for (int i = 0; i < Wanted.Length; i++)
            idx[i] = Array.FindIndex(cols, c => c.Trim().Equals(Wanted[i], StringComparison.OrdinalIgnoreCase));
        lock (_lock) _col = idx;
    }

    private void ParseRow(string line)
    {
        int[] col;
        lock (_lock) col = _col;
        if (!TryParseRow(line, col, out var r)) return;

        long now = Environment.TickCount64;
        var app = line.AsSpan()[r.App];
        lock (_lock)
        {
            if (!_byPid.TryGetValue(r.Pid, out var buf)) { buf = new Buf(); _byPid[r.Pid] = buf; }
            // The name barely ever changes for a pid; only allocate when it does.
            if (!app.SequenceEqual(buf.App)) buf.App = app.ToString();
            buf.F.Add((now, r.Frame, r.Disp, r.Gpu, r.Anim, r.Lat));
        }
    }

    /// <summary>One CSV row's fields, as <see cref="TryParseRow"/> reads them. <see cref="App"/> is a
    /// range into the line, so a row costs no allocation at all.</summary>
    internal struct FrameRow
    {
        public int Pid;
        public Range App;
        public float Frame, Disp, Gpu, Anim, Lat;
    }

    /// <summary>
    /// Parses one PresentMon CSV row with the header-mapped column indices (<see cref="Wanted"/> order).
    /// False for a row that cannot be a frame: no frame-time column, unparseable pid, or a frame time
    /// that is missing or ≤ 0. Other missing or unparseable metrics read NaN.
    /// <para>
    /// A walk over the line's commas rather than <c>Split</c>: this runs for every frame of every
    /// process that presents — a game at 300 fps plus a browser and a chat client at their refresh
    /// rate — and Split built a string per column (~25 of them) plus an array for each one.
    /// </para>
    /// </summary>
    internal static bool TryParseRow(string line, int[] col, out FrameRow row)
    {
        row = default;
        if (col.Length < Wanted.Length || col[0] < 0 || col[2] < 0) return false;

        // Locate the wanted fields in one pass. -1 start = the line has no such column.
        Span<int> start = stackalloc int[Wanted.Length];
        Span<int> len = stackalloc int[Wanted.Length];
        start.Fill(-1);
        int field = 0, pos = 0;
        while (true)
        {
            int comma = line.IndexOf(',', pos);
            int end = comma < 0 ? line.Length : comma;
            for (int k = 0; k < Wanted.Length; k++)
                if (col[k] == field) { start[k] = pos; len[k] = end - pos; }
            if (comma < 0) break;
            pos = comma + 1;
            field++;
        }

        ReadOnlySpan<char> F(Span<int> s, Span<int> l, int k) => s[k] < 0 ? default : line.AsSpan(s[k], l[k]);
        float N(Span<int> s, Span<int> l, int k) =>
            float.TryParse(F(s, l, k), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;

        if (start[2] < 0) return false;   // the row is shorter than the frame-time column
        if (!int.TryParse(F(start, len, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return false;
        float frame = N(start, len, 2);
        if (float.IsNaN(frame) || frame <= 0) return false;

        row.Pid = pid;
        row.App = start[0] < 0 ? new Range(0, 0) : new Range(start[0], start[0] + len[0]);
        row.Frame = frame;
        row.Disp = N(start, len, 3);
        row.Gpu = N(start, len, 4);
        row.Anim = N(start, len, 5);
        row.Lat = N(start, len, 6);
        return true;
    }

    /// <summary>Computes the current metrics for the foreground app. App empty when nothing qualifies.</summary>
    public FrameInfo Poll()
    {
        var result = default(FrameInfo);
        int fgPid = ForegroundPid();
        long now = Environment.TickCount64;
        long cutoff = now - (long)(WindowSec * 1000);

        lock (_lock)
        {
            // Prune stale processes and old frames.
            foreach (var (pid, buf) in _byPid)
            {
                buf.F.RemoveAll(x => x.ms < cutoff);
            }
            foreach (var pid in _byPid.Where(kv => kv.Value.F.Count == 0).Select(kv => kv.Key).ToList())
                _byPid.Remove(pid);

            if (!_byPid.TryGetValue(fgPid, out var b) || b.F.Count < 5) return result;

            // Ignore our own windows and anything presenting slowly (static desktop apps, not a game).
            if (b.App.StartsWith("SidebarMonitor", StringComparison.OrdinalIgnoreCase)) return result;
            var frames = b.F.Select(x => x.frame).OrderBy(x => x).ToArray();
            float meanFrame = frames.Average();
            if (meanFrame <= 0 || 1000f / meanFrame < 10f) return result;   // < 10 fps: not a game

            NameField.Set(ref result.App, b.App);
            result.FrametimeMs = meanFrame;
            result.FpsPresented = 1000f / meanFrame;
            result.Low1PctFps = 1000f / Percentile(frames, 99.0);
            result.Low01PctFps = 1000f / Percentile(frames, 99.9);

            var disp = b.F.Select(x => x.disp).Where(x => x > 0 && !float.IsNaN(x)).ToArray();
            result.FpsDisplayed = disp.Length > 0 ? 1000f / disp.Average() : result.FpsPresented;

            var gpu = b.F.Where(x => !float.IsNaN(x.gpu) && x.frame > 0).ToArray();
            result.GpuBusyPct = gpu.Length > 0 ? (float)Math.Min(100, gpu.Average(x => x.gpu / x.frame * 100)) : float.NaN;

            var anim = b.F.Select(x => x.anim).Where(x => !float.IsNaN(x)).Select(Math.Abs).ToArray();
            result.AnimationErrorMs = anim.Length > 0 ? anim.Average() : float.NaN;

            var lat = b.F.Select(x => x.lat).Where(x => x > 0 && !float.IsNaN(x)).ToArray();
            result.LatencyMs = lat.Length > 0 ? lat.Average() : float.NaN;
        }
        return result;
    }

    /// <summary>The p-th percentile of a pre-sorted ascending array (linear interpolation).</summary>
    private static float Percentile(float[] sorted, double p)
    {
        if (sorted.Length == 0) return float.NaN;
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return (float)(sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]));
    }

    private static int ForegroundPid()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return -1;
        _ = GetWindowThreadProcessId(h, out int pid);
        return pid;
    }

    private void StopProcess()
    {
        try { if (_pm is { HasExited: false }) _pm.Kill(true); } catch { }
        _pm = null;
        lock (_lock) _byPid.Clear();
    }

    public void Dispose() => StopProcess();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
}
