using System.IO;

namespace SidebarMonitor.Shared;

/// <summary>
/// File bridge for the on-demand PM_Table diagnostics dump, same %LOCALAPPDATA% folder the consent
/// markers use. The unelevated UI can't touch PawnIO, so it drops a request file; the elevated
/// helper sees it on its next publish window, writes the dump and removes the request. The UI polls
/// briefly for the result and copies it to the clipboard for the "support my CPU" GitHub issue.
/// </summary>
public static class DiagBridge
{
    private static string RequestPath => Path.Combine(ConsentMarker.Dir, "pm-dump-request");
    private static string DumpPath => Path.Combine(ConsentMarker.Dir, "pmtable-dump.txt");

    /// <summary>UI: ask the helper for a fresh dump (clears any stale result first).</summary>
    public static void RequestDump()
    {
        try
        {
            Directory.CreateDirectory(ConsentMarker.Dir);
            if (File.Exists(DumpPath)) File.Delete(DumpPath);
            File.WriteAllText(RequestPath, "PM_Table dump requested by the UI diagnostics button.\n");
        }
        catch { /* non-fatal: the UI report just says the dump is missing */ }
    }

    /// <summary>Helper: a dump was requested and not yet served.</summary>
    public static bool DumpRequested => File.Exists(RequestPath);

    /// <summary>Helper: publish the dump and consume the request.</summary>
    public static void WriteDump(string text)
    {
        try
        {
            File.WriteAllText(DumpPath, text);
            if (File.Exists(RequestPath)) File.Delete(RequestPath);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>UI: the dump text, or null while the helper hasn't answered yet.</summary>
    public static string? TryReadDump()
    {
        try { return File.Exists(DumpPath) ? File.ReadAllText(DumpPath) : null; }
        catch { return null; }
    }

    // ── Cooperative shutdown ─────────────────────────────────────────────────────────────────────
    //
    // The helper is elevated and windowless, so nothing unelevated can close it: an installer can't
    // TerminateProcess a high-integrity process, and the Restart Manager can't ask a console app
    // with no message loop to quit — it can only force-kill it, mid-write, taking the kernel ETW
    // session down dirty. So the helper polls for this file each window and exits the same clean way
    // Ctrl+C does. The directory is machine-wide with a Users-modify ACL, so ANY user's installer or
    // script can request the stop without elevation. Absence = keep running; the file is consumed by
    // whoever honours it.

    private static string StopPath => Path.Combine(ConsentMarker.Dir, "helper-stop");

    /// <summary>Installer/scripts: ask the elevated helper to shut down cleanly. Returns immediately;
    /// the helper notices within one publish window (~1 s).</summary>
    public static void RequestStop()
    {
        try
        {
            Directory.CreateDirectory(ConsentMarker.Dir);
            File.WriteAllText(StopPath, "shutdown requested (installer/uninstaller).\n");
        }
        catch { /* non-fatal: the caller falls back to force-killing */ }
    }

    /// <summary>Helper: a shutdown was requested. Consumes the request so a stale file can't kill
    /// the next helper the scheduled task starts.</summary>
    public static bool StopRequested()
    {
        try
        {
            if (!File.Exists(StopPath)) return false;
            File.Delete(StopPath);
            return true;
        }
        catch { return false; }
    }
}
