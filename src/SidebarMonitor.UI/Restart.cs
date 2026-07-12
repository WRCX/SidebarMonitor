using System.Diagnostics;
using System.Windows;

namespace SidebarMonitor.UI;

/// <summary>
/// Relaunches the UI process. The panel is built once in C# at startup (labels, menus, section
/// titles), so switching language is applied cleanly by restarting rather than re-localising a live
/// visual tree. The elevated helper and the agent are untouched — only this WPF process restarts.
/// </summary>
internal static class Restart
{
    public static void Relaunch()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;

        // The agent is spawned as our child and the AppBar space is released on ProcessExit, so a
        // plain start-then-shutdown hands off cleanly; the new instance re-adopts everything.
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); }
        catch { return; }

        Application.Current?.Shutdown();
    }
}
