using System.Windows;
using System.Windows.Threading;

namespace SidebarMonitor.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--dump") || args.Any(a => a.StartsWith("--shot=")))
            Native.AttachToParentConsole();

        var cfg = UiConfig.Load();

        // Command-line overrides are for debugging. Applying one makes the config ephemeral, so
        // a throwaway run never rewrites what the user configured through the menu.
        if (IntArg(args, "--monitor=", -1) is var m and >= 0) { cfg.Monitor = m; cfg.Ephemeral = true; }
        if (IntArg(args, "--width=", -1) is var w and > 0) { cfg.Width = w; cfg.Ephemeral = true; }
        if (args.Contains("--left")) { cfg.EdgeLeft = true; cfg.Ephemeral = true; }
        if (args.Contains("--floating")) { cfg.Docked = false; cfg.Ephemeral = true; }
        if (args.Contains("--minimized")) { cfg.Minimized = true; cfg.Ephemeral = true; }
        if (args.Contains("--cpu-cores")) { cfg.CpuPerCoreGraph = true; cfg.Ephemeral = true; }

        int seconds = IntArg(args, "--seconds=", 0);          // 0 = run until closed
        string? shot = args.FirstOrDefault(a => a.StartsWith("--shot="))?["--shot=".Length..];

        var monitors = Native.EnumerateMonitors();
        if (cfg.Monitor >= monitors.Count) cfg.Monitor = monitors.Count - 1;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = new MainWindow(cfg, monitors);

        TrayIcon? tray = null;
        if (!args.Contains("--no-tray"))
        {
            tray = new TrayIcon();
            win.AttachTray(tray);
        }

        // Rescue the reserved desktop space on every exit path, crash included.
        void Rescue(object? _, EventArgs __) { win.RemoveAppBar(); tray?.Dispose(); }
        AppDomain.CurrentDomain.UnhandledException += Rescue;
        AppDomain.CurrentDomain.ProcessExit += Rescue;

        string[] Split(string prefix) =>
            args.FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..].Split(',') ?? [];

        if (seconds > 0)
        {
            win.Loaded += (_, _) =>
            {
                win.Apply(Split("--collapse="), Split("--expand="), Split("--hide="));
                if (args.Contains("--minimized")) win.SetMinimizedForTest(true);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (args.Contains("--hover")) win.ForceHoverForTest();
                    if (args.Contains("--dump")) win.DumpText();
                    if (shot is not null)
                    {
                        try { win.SaveScreenshot(shot); Console.WriteLine($"captura: {shot}"); }
                        catch (Exception ex) { Console.Error.WriteLine($"captura fallo: {ex.Message}"); }
                    }
                    win.Close();
                };
                timer.Start();
            };
        }

        win.Show();
        app.Run();
        return 0;
    }

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}
