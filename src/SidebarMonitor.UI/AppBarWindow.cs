using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SidebarMonitor.UI;

/// <summary>
/// The window chassis, validated by ShellProbe on Windows 11 25H2.
///
/// Two placements. **Docked** registers an AppBar so maximized windows never cover the panel;
/// the shell reserves the strip for us. **Floating** unregisters it and lets the user drag the
/// window anywhere. Either way it keeps WS_EX_NOACTIVATE (never steals focus) and
/// WS_EX_TOOLWINDOW (never appears in Alt+Tab). Topmost is a user choice, not a constant.
///
/// Minimizing collapses the strip to a thin tab rather than hiding it: an AppBar of 18px still
/// reserves 18px, so the panel stays reachable without the desktop jumping around.
/// </summary>
internal abstract class AppBarWindow : Window
{
    public const int MinimizedWidth = 18;

    private IntPtr _hwnd;
    private uint _callbackMsg;
    private bool _registered;
    private bool _placing;

    protected IntPtr MonitorHandle { get; private set; }
    protected bool Docked { get; private set; } = true;
    protected bool EdgeLeft { get; private set; }
    protected int PanelWidth { get; private set; } = 280;
    protected bool Minimized { get; private set; }

    protected AppBarWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        _callbackMsg = Native.RegisterWindowMessage("SidebarMonitor_AppBar");
    }

    /// <summary>Re-applies the whole placement. Safe to call whenever a setting changes.</summary>
    protected void ApplyPlacement(IntPtr monitor, bool docked, bool edgeLeft, int width, bool minimized, bool topmost,
                                 double floatX, double floatY, double floatHeight)
    {
        if (_hwnd == IntPtr.Zero) return;

        MonitorHandle = monitor;
        Docked = docked;
        EdgeLeft = edgeLeft;
        PanelWidth = width;
        Minimized = minimized;

        int strip = minimized ? MinimizedWidth : width;

        _placing = true;
        try
        {
            SetTopmost(topmost);

            if (docked)
            {
                EnsureAppBar();
                PositionOnEdge(strip);
            }
            else
            {
                RemoveAppBar();
                Native.SetWindowPos(_hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                    (int)floatX, (int)floatY, strip, (int)floatHeight, Native.SWP_NOACTIVATE);
            }
        }
        finally { _placing = false; }
    }

    private void SetTopmost(bool topmost)
    {
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = topmost ? ex | Native.WS_EX_TOPMOST : ex & ~Native.WS_EX_TOPMOST;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        Native.SetWindowPos(_hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
    }

    private void EnsureAppBar()
    {
        if (_registered) return;
        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd,
            uCallbackMessage = _callbackMsg,
        };
        _registered = Native.SHAppBarMessage(Native.ABM_NEW, ref abd) != IntPtr.Zero;
    }

    /// <summary>
    /// ABM_QUERYPOS lets the shell push our rectangle around (another appbar may own that edge).
    /// We re-impose our thickness on the axis we care about, then ABM_SETPOS.
    /// </summary>
    private void PositionOnEdge(int strip)
    {
        if (!_registered) return;

        var mon = Native.GetMonitorInfoFor(MonitorHandle).rcMonitor;
        uint edge = EdgeLeft ? Native.ABE_LEFT : Native.ABE_RIGHT;

        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd,
            uEdge = edge,
            rc = EdgeLeft
                ? new Rect { Left = mon.Left, Top = mon.Top, Right = mon.Left + strip, Bottom = mon.Bottom }
                : new Rect { Left = mon.Right - strip, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom },
        };

        Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref abd);
        if (EdgeLeft) abd.rc.Right = abd.rc.Left + strip;
        else abd.rc.Left = abd.rc.Right - strip;
        Native.SHAppBarMessage(Native.ABM_SETPOS, ref abd);

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST,
            abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height, Native.SWP_NOACTIVATE);
    }

    /// <summary>Moves a floating window to an absolute screen position, in physical pixels.</summary>
    protected void MoveFloatingTo(int x, int y) =>
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);

    /// <summary>Current window rectangle in physical pixels.</summary>
    protected Rect WindowRect
    {
        get { Native.GetWindowRect(_hwnd, out Rect r); return r; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }

        if (msg == Native.WM_WINDOWPOSCHANGED && _registered)
        {
            var abd = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = hwnd };
            Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref abd);
        }

        // The taskbar moved, the resolution changed, or another appbar appeared.
        if (msg == (int)_callbackMsg && wParam.ToInt32() == Native.ABN_POSCHANGED && !_placing)
        {
            PositionOnEdge(Minimized ? MinimizedWidth : PanelWidth);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// An appbar that outlives its process leaves the desktop shrunk; Windows reclaims it on
    /// hwnd death but we never rely on that. Called from OnClosing AND process-exit rescues.
    /// </summary>
    public void RemoveAppBar()
    {
        if (!_registered) return;
        var abd = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = _hwnd };
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref abd);
        _registered = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        RemoveAppBar();
        base.OnClosing(e);
    }
}
