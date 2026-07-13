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
}
