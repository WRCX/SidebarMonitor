using System.Management;

namespace SidebarMonitor.Etw;

/// <summary>
/// PID -> parent process and command line, from WMI. The helper is elevated, so it can read the
/// command line of any process (including svchost and other system hosts) — the UI can't. Refreshed
/// on a slow cadence off the sample hot path; command lines don't change over a process's life.
/// </summary>
internal sealed class ProcessDetails
{
    private Dictionary<int, (string Name, int ParentPid, string Cmd)> _map = new();

    public void Refresh()
    {
        var m = new Dictionary<int, (string, int, string)>(512);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                int pid = ToInt(mo["ProcessId"]);
                if (pid <= 0) continue;
                m[pid] = (mo["Name"] as string ?? "", ToInt(mo["ParentProcessId"]), mo["CommandLine"] as string ?? "");
            }
        }
        catch { /* WMI can hiccup; keep the previous map */ return; }
        _map = m;
    }

    /// <summary>"padre: services\n&lt;command line&gt;" for a pid, or "" if unknown.</summary>
    public string Detail(int pid)
    {
        if (pid <= 0 || !_map.TryGetValue(pid, out var d)) return "";

        string parent = d.ParentPid > 0
            ? (_map.TryGetValue(d.ParentPid, out var pd) ? Clean(pd.Name) : $"pid {d.ParentPid}")
            : "";
        string cmd = ShortCmd(d.Cmd);

        var parts = new List<string>(2);
        if (parent.Length > 0) parts.Add($"padre: {parent}");
        if (cmd.Length > 0) parts.Add(cmd);
        return string.Join("\n", parts);
    }

    private static int ToInt(object? o)
    {
        try { return o is null ? 0 : Convert.ToInt32(o); } catch { return 0; }
    }

    private static string Clean(string exe) =>
        exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    /// <summary>Strips the leading full path of the exe so the interesting args stay ("svchost.exe
    /// -k netsvcs -p -s Themes", "rundll32.exe shell32.dll,..."), truncated so it fits the field.</summary>
    private static string ShortCmd(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.Length == 0) return "";
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
        {
            int slash = cmd.LastIndexOf('\\', exe);
            cmd = cmd[(slash >= 0 ? slash + 1 : 0)..].TrimStart('"');
        }
        return cmd.Length > 110 ? cmd[..109] + "…" : cmd;
    }
}
