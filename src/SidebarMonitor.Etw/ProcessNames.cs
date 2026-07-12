using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using SidebarMonitor.Shared;

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
    private readonly Dictionary<int, string> _display = new(512);   // base name expanded (svchost:svc)
    private readonly ServiceMap _services = new();

    public ProcessNames()
    {
        _byPid[0] = "Idle";
        _byPid[4] = "System";
        // ETW attributes samples taken in interrupt / DPC context to PID -1 (no owning process).
        // That's the CPU spent in hardware interrupts and driver deferred work — Task Manager's
        // "System interrupts". Naming it here stops it showing up as the raw "pid -1".
        _byPid[-1] = "Interrupciones";
        RefreshServices();
    }

    /// <summary>Re-reads the SCM so svchost PIDs resolve to their service, and drops the display
    /// cache so late-started services get picked up. Cheap; call it periodically.</summary>
    public void RefreshServices()
    {
        _services.Refresh();
        _display.Clear();
    }

    public void OnStart(ProcessTraceData d) => _byPid[d.ProcessID] = Clean(d.ProcessName, d.ImageFileName);
    // Note: no OnStop handler — exited PIDs are intentionally kept, since samples for the window being
    // aggregated may still refer to them.

    public string Get(int pid)
    {
        if (_byPid.TryGetValue(pid, out string? n)) return n;

        // Missed the rundown (rare). Resolve once, then cache forever.
        string name;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); name = p.ProcessName; }
        catch { name = $"pid {pid}"; }
        return _byPid[pid] = name;
    }

    /// <summary>The name to display and group by: like <see cref="Get"/>, but a generic service host
    /// (svchost) is expanded to the service it runs — "svchost:Dhcp" — so different svchosts read as
    /// different owners instead of one anonymous "svchost".</summary>
    public string GetDisplay(int pid)
    {
        if (_display.TryGetValue(pid, out string? d)) return d;
        string name = Get(pid);
        if (name.Equals("svchost", StringComparison.OrdinalIgnoreCase) && _services.Label(pid) is { } svc)
            name = "svchost:" + svc;
        return _display[pid] = name;
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
