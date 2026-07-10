using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ShellProbe;

/// <summary>
/// De-risks the window, not the data. Every behaviour the sidebar needs is applied and then
/// read back through an API, so the console output is evidence rather than intention.
/// </summary>
internal static class Program
{
    public const string AppId = "SidebarMonitor.ShellProbe";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // The undocumented pinning COM is guessed vtable order: a wrong guess is an access
        // violation, which no catch block can save. So it gets its own process.
        if (args.Contains("--pin-probe"))
        {
            Console.WriteLine(VirtualDesktop.TryPinAppId(AppId));
            return 0;
        }

        int monitorIndex = IntArg(args, "--monitor=", 0);
        int width = IntArg(args, "--width=", 260);
        int seconds = IntArg(args, "--seconds=", 12);
        bool clickThrough = args.Contains("--clickthrough");
        uint edge = args.Contains("--left") ? Native.ABE_LEFT : Native.ABE_RIGHT;

        Native.SetCurrentProcessExplicitAppUserModelID(AppId);

        var monitors = Native.EnumerateMonitors();
        Console.WriteLine($"=== Monitores ({monitors.Count}) ===");
        for (int i = 0; i < monitors.Count; i++)
        {
            var mi = monitors[i].Info;
            Console.WriteLine($"  [{i}] {(mi.dwFlags == 1 ? "primario " : "         ")}bounds {mi.rcMonitor}   workarea {mi.rcWork}");
        }
        if (monitorIndex >= monitors.Count) monitorIndex = 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var win = new SidebarWindow(monitors[monitorIndex].Handle, edge, width, clickThrough);

        // An appbar that outlives its process leaves the user's desktop permanently shrunk.
        // Windows does reclaim it when the hwnd dies, but do not rely on that: unregister on
        // every exit path, including a crash.
        void Rescue(object? _, EventArgs __) => win.RemoveAppBar();
        AppDomain.CurrentDomain.UnhandledException += Rescue;
        AppDomain.CurrentDomain.ProcessExit += Rescue;

        win.Loaded += (_, _) =>
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); win.Close(); };
            timer.Start();
        };

        app.Run(win);

        Console.WriteLine();
        Console.WriteLine("=== Pinning a escritorios virtuales (proceso aislado) ===");
        Console.WriteLine("  " + RunPinProbe());
        return 0;
    }

    private static string RunPinProbe()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                Arguments = "--pin-probe",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return p.ExitCode == 0
                ? output
                : $"el proceso murió (exit 0x{p.ExitCode:X8}) -> vtable incorrecta en esta build. {output}";
        }
        catch (Exception ex) { return $"no se pudo lanzar: {ex.Message}"; }
    }

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}

internal sealed class SidebarWindow : Window
{
    private readonly IntPtr _monitor;
    private readonly uint _edge;
    private readonly int _width;
    private readonly bool _clickThrough;

    private IntPtr _hwnd;
    private uint _callbackMsg;
    private bool _appBarRegistered;
    private Rect _workAreaBefore;
    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        Margin = new Thickness(10),
        TextWrapping = TextWrapping.Wrap,
    };

    public SidebarWindow(IntPtr monitor, uint edge, int width, bool clickThrough)
    {
        _monitor = monitor;
        _edge = edge;
        _width = width;
        _clickThrough = clickThrough;

        Title = "SidebarMonitor probe";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;   // paired with WS_EX_TOOLWINDOW
        ShowActivated = false;   // do not steal focus on show
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x1E));
        Content = new Border { Child = _text };

        _workAreaBefore = Native.GetMonitorInfoFor(_monitor).rcWork;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd)!;
        source.AddHook(WndProc);

        ApplyExtendedStyles();
        RegisterAppBar();
        PositionOnEdge();

        // Read everything back rather than trusting that the setters worked.
        Dispatcher.BeginInvoke(new Action(Report),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ApplyExtendedStyles()
    {
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
        if (_clickThrough) ex |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void RegisterAppBar()
    {
        _callbackMsg = Native.RegisterWindowMessage("SidebarMonitorProbe_AppBar");

        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd,
            uCallbackMessage = _callbackMsg,
        };
        _appBarRegistered = Native.SHAppBarMessage(Native.ABM_NEW, ref abd) != IntPtr.Zero;
    }

    /// <summary>
    /// ABM_QUERYPOS lets the shell push our rectangle around (another appbar may already own
    /// that edge). We must re-impose our thickness on the axis we care about, then ABM_SETPOS.
    /// </summary>
    private void PositionOnEdge()
    {
        if (!_appBarRegistered) return;

        var mon = Native.GetMonitorInfoFor(_monitor).rcMonitor;

        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd,
            uEdge = _edge,
            rc = _edge == Native.ABE_RIGHT
                ? new Rect { Left = mon.Right - _width, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom }
                : new Rect { Left = mon.Left, Top = mon.Top, Right = mon.Left + _width, Bottom = mon.Bottom },
        };

        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);

        if (_edge == Native.ABE_RIGHT) abd.rc.Left = abd.rc.Right - _width;
        else abd.rc.Right = abd.rc.Left + _width;

        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST,
            abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height,
            Native.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            // Clicking the sidebar must not pull focus away from whatever the user is doing.
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }

        if (msg == Native.WM_WINDOWPOSCHANGED && _appBarRegistered)
        {
            var abd = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = hwnd };
            Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref abd);
        }

        if (msg == (int)_callbackMsg && wParam.ToInt32() == Native.ABN_POSCHANGED)
        {
            PositionOnEdge();   // taskbar moved, resolution changed, another appbar appeared
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void Report()
    {
        var workAfter = Native.GetMonitorInfoFor(_monitor).rcWork;
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        uint dpi = Native.GetDpiForWindow(_hwnd);
        uint awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext());
        IntPtr fg = Native.GetForegroundWindow();

        string awarenessName = awareness switch
        {
            0 => "UNAWARE",
            1 => "SYSTEM_AWARE",
            2 => "PER_MONITOR_AWARE (v2 via manifest)",
            _ => $"?{awareness}",
        };

        bool Has(long bit) => (ex & bit) != 0;
        string Yes(bool b) => b ? "OK  " : "FALLA";

        int reserved = _workAreaBefore.Width - workAfter.Width;

        var lines = new[]
        {
            "SidebarMonitor",
            "shell probe",
            "",
            $"DPI {dpi}",
            $"reserva {reserved}px",
            "",
            "no roba foco",
            "no sale en Alt+Tab",
            "no lo tapan las",
            "ventanas maximizadas",
        };
        _text.Text = string.Join("\n", lines);

        Console.WriteLine();
        Console.WriteLine("=== Comportamiento de la ventana (leido de vuelta por API) ===");
        Console.WriteLine($"  {Yes(awareness == 2)} DPI awareness .......... {awarenessName}, GetDpiForWindow={dpi}");
        Console.WriteLine($"  {Yes(Has(Native.WS_EX_TOOLWINDOW))} WS_EX_TOOLWINDOW ....... fuera de Alt+Tab");
        Console.WriteLine($"  {Yes(Has(Native.WS_EX_NOACTIVATE))} WS_EX_NOACTIVATE ....... nunca pasa a primer plano");
        Console.WriteLine($"  {Yes(Has(Native.WS_EX_TOPMOST))} WS_EX_TOPMOST .......... siempre encima");
        if (_clickThrough)
            Console.WriteLine($"  {Yes(Has(Native.WS_EX_TRANSPARENT))} WS_EX_TRANSPARENT ...... clicks atraviesan");
        Console.WriteLine($"  {Yes(fg != _hwnd)} no robo el foco ........ foreground={(fg == _hwnd ? "NOSOTROS" : "otra ventana")}");
        Console.WriteLine();
        Console.WriteLine($"  {Yes(_appBarRegistered)} AppBar registrado (ABM_NEW)");
        Console.WriteLine($"  {Yes(reserved == _width)} espacio de trabajo reservado: {reserved}px (pedidos {_width}px)");
        Console.WriteLine($"       workarea antes:   {_workAreaBefore}");
        Console.WriteLine($"       workarea despues: {workAfter}");
        Console.WriteLine();
        // GUID_NULL on its own is ambiguous. Compare against an ordinary foreground window:
        // if that one reports a real desktop GUID, our null genuinely means "unassigned".
        Console.WriteLine("=== Escritorios virtuales ===");
        Console.WriteLine($"  nuestra ventana -> {VirtualDesktop.DescribeWindow(_hwnd)}");
        Console.WriteLine($"  ventana normal  -> {VirtualDesktop.DescribeWindow(fg)}  (control)");
    }

    public void RemoveAppBar()
    {
        if (!_appBarRegistered) return;
        var abd = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = _hwnd };
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        _appBarRegistered = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        RemoveAppBar();
        base.OnClosing(e);
    }
}
