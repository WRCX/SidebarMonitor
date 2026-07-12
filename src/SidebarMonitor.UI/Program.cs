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

        // Preview the first-run notice in isolation (QA/screenshots), then exit. --firstrun-preview
        // shows the AMD EULA branch; --firstrun-preview=intel forces the Intel notice.
        string? previewArg = args.FirstOrDefault(a => a.StartsWith("--firstrun-preview"));
        if (previewArg is not null)
        {
            var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string which = previewArg.Contains('=') ? previewArg[(previewArg.IndexOf('=') + 1)..] : "amd";
            FirstRunDialog.Preview(which);
            return 0;
        }

        var cfg = UiConfig.Load();

        // Language: config preference ("auto"/"es"/"en"), with a --lang= override for QA/screenshots.
        string? langArg = args.FirstOrDefault(a => a.StartsWith("--lang="))?["--lang=".Length..];
        Loc.Init(langArg ?? cfg.Language);

        // Command-line overrides are for debugging. Applying one makes the config ephemeral, so
        // a throwaway run never rewrites what the user configured through the menu.
        if (IntArg(args, "--monitor=", -1) is var m and >= 0) { cfg.Monitor = m; cfg.Ephemeral = true; }
        if (IntArg(args, "--width=", -1) is var w and > 0) { cfg.Width = w; cfg.Ephemeral = true; }
        if (args.Contains("--left")) { cfg.EdgeLeft = true; cfg.Ephemeral = true; }
        if (args.Contains("--floating")) { cfg.Docked = false; cfg.Ephemeral = true; }
        if (args.Contains("--no-reserve")) { cfg.ReserveSpace = false; cfg.Ephemeral = true; }
        if (args.Contains("--minimized")) { cfg.Minimized = true; cfg.Ephemeral = true; }
        if (args.Contains("--cpu-cores")) { cfg.CpuGraphMode = 1; cfg.Ephemeral = true; }
        if (args.Contains("--cpu-grid")) { cfg.CpuGraphMode = 2; cfg.Ephemeral = true; }
        if (args.Contains("--core-temp")) { cfg.ShowCoreTemp = true; cfg.Ephemeral = true; }
        if (args.Contains("--cpu-process")) { cfg.CpuGraphMode = 0; cfg.Ephemeral = true; }
        if (args.Contains("--show-vid")) { cfg.ShowCpuVid = true; cfg.Ephemeral = true; }
        if (args.Contains("--show-limits")) { cfg.ShowCpuLimits = true; cfg.Ephemeral = true; }
        if (args.Contains("--verbose")) { cfg.LogVerbose = true; cfg.Ephemeral = true; }
        if (args.Contains("--csv")) { cfg.LogCsv = true; cfg.Ephemeral = true; }
        if (IntArg(args, "--cpu-name=", -1) is var cn and >= 0) { cfg.CpuNameMode = cn; cfg.Ephemeral = true; }
        if (IntArg(args, "--gpu-name=", -1) is var gn and >= 0) { cfg.GpuNameMode = gn; cfg.Ephemeral = true; }

        int seconds = IntArg(args, "--seconds=", 0);          // 0 = run until closed
        string? shot = args.FirstOrDefault(a => a.StartsWith("--shot="))?["--shot=".Length..];
        // --frames=DIR dumps one RenderTargetBitmap PNG every ~400 ms for --seconds, for building a
        // demo GIF (assembled separately). Rendering the visual tree needs no visible window.
        string? framesDir = args.FirstOrDefault(a => a.StartsWith("--frames="))?["--frames=".Length..];
        int frameMs = IntArg(args, "--frame-ms=", 400);

        var monitors = Native.EnumerateMonitors();
        if (cfg.Monitor >= monitors.Count) cfg.Monitor = monitors.Count - 1;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // First-run platform notice (AMD EULA / Intel ring0). Skipped for automated/timed runs and
        // for throwaway debug runs so nothing blocks on a modal dialog; --no-firstrun forces skip.
        if (seconds == 0 && !cfg.Ephemeral && !args.Contains("--no-firstrun"))
            FirstRunDialog.EnsureShown(cfg);

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

        if (framesDir is not null && seconds > 0)
        {
            win.Loaded += (_, _) =>
            {
                win.Apply(Split("--collapse="), Split("--expand="), Split("--hide="));
                System.IO.Directory.CreateDirectory(framesDir);
                int n = 0;
                long end = Environment.TickCount64 + seconds * 1000L;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frameMs) };
                timer.Tick += (_, _) =>
                {
                    try { win.SaveScreenshot(System.IO.Path.Combine(framesDir, $"f{n:D3}.png")); } catch { }
                    n++;
                    if (Environment.TickCount64 >= end) { timer.Stop(); Console.WriteLine($"frames: {n} en {framesDir}"); win.Close(); }
                };
                timer.Start();
            };
        }
        else if (seconds > 0)
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

        if (args.Contains("--settings")) win.Loaded += (_, _) => win.OpenSettings();

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
