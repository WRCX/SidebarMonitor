using System.Windows;
using System.Windows.Threading;

namespace SidebarMonitor.UI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int monitorIndex = IntArg(args, "--monitor=", 1);
        int width = IntArg(args, "--width=", 280);
        int seconds = IntArg(args, "--seconds=", 0);          // 0 = run until closed
        string? shot = args.FirstOrDefault(a => a.StartsWith("--shot="))?["--shot=".Length..];
        uint edge = args.Contains("--left") ? Native.ABE_LEFT : Native.ABE_RIGHT;

        var monitors = Native.EnumerateMonitors();
        if (monitorIndex >= monitors.Count) monitorIndex = monitors.Count - 1;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var win = new MainWindow(monitors[monitorIndex].Handle, edge, width);

        // Rescue the reserved desktop space on every exit path, crash included.
        void Rescue(object? _, EventArgs __) => win.RemoveAppBar();
        AppDomain.CurrentDomain.UnhandledException += Rescue;
        AppDomain.CurrentDomain.ProcessExit += Rescue;

        string[] Split(string prefix) =>
            args.FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..].Split(',') ?? [];

        if (seconds > 0)
        {
            win.Loaded += (_, _) =>
            {
                win.Apply(Split("--collapse="), Split("--hide="));
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
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

        return app.Run(win);
    }

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}
