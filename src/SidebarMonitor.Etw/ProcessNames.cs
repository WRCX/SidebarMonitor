using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace SidebarMonitor.Etw;

/// <summary>
/// PID -> display name, grouped the way the user reads it: every chrome.exe is "chrome".
///
/// Names come from the kernel session's own Process events, which arrive for both the rundown
/// (processes already alive when we started) and anything launched later. Falling back to
/// Process.GetProcessById would race with exit and cost a handle per lookup on the hot path.
/// </summary>
internal sealed class ProcessNames
{
    private readonly Dictionary<int, string> _byPid = new(512);

    public ProcessNames()
    {
        _byPid[0] = "Idle";
        _byPid[4] = "System";
    }

    public void OnStart(ProcessTraceData d) => _byPid[d.ProcessID] = Clean(d.ProcessName, d.ImageFileName);

    /// <summary>Keep exited PIDs: samples for the window we are aggregating may still refer to them.</summary>
    public void OnStop(ProcessTraceData d) { }

    public string Get(int pid)
    {
        if (_byPid.TryGetValue(pid, out string? n)) return n;

        // Missed the rundown (rare). Resolve once, then cache forever.
        string name;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); name = p.ProcessName; }
        catch { name = $"pid {pid}"; }
        return _byPid[pid] = name;
    }

    private static string Clean(string processName, string imageFileName)
    {
        string s = !string.IsNullOrEmpty(processName) ? processName : imageFileName;
        if (string.IsNullOrEmpty(s)) return "?";

        int slash = s.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s;
    }
}
