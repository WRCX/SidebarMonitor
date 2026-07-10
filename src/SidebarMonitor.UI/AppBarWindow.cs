using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SidebarMonitor.UI;

/// <summary>
/// The window chassis validated by ShellProbe on Windows 11 25H2: an AppBar that reserves
/// desktop space so maximized windows never cover it, WS_EX_NOACTIVATE so it never steals
/// focus, WS_EX_TOOLWINDOW so it stays out of Alt+Tab. No virtual-desktop pinning needed:
/// an unassigned appbar window already shows on every desktop (GetWindowDesktopId = GUID_NULL).
/// </summary>
internal abstract class AppBarWindow : Window
{
    private readonly IntPtr _monitor;
    private readonly uint _edge;
    private readonly int _width;

    private IntPtr _hwnd;
    private uint _callbackMsg;
    private bool _registered;

    protected AppBarWindow(IntPtr monitor, uint edge, int width)
    {
        _monitor = monitor;
        _edge = edge;
        _width = width;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        _callbackMsg = Native.RegisterWindowMessage("SidebarMonitor_AppBar");
        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = _hwnd,
            uCallbackMessage = _callbackMsg,
        };
        _registered = Native.SHAppBarMessage(Native.ABM_NEW, ref abd) != IntPtr.Zero;
        PositionOnEdge();
    }

    private void PositionOnEdge()
    {
        if (!_registered) return;

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
            abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height, Native.SWP_NOACTIVATE);
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

        if (msg == (int)_callbackMsg && wParam.ToInt32() == Native.ABN_POSCHANGED)
        {
            PositionOnEdge();
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
