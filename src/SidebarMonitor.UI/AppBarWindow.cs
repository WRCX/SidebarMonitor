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
    public const int MinPanelWidth = 240;
    private const int MinPanelHeight = 200;
    private const int ResizeGrip = 8;

    /// <summary>Raised after the user finishes resizing the panel by dragging an edge.</summary>
    public event Action? Resized;

    private IntPtr _hwnd;
    private uint _callbackMsg;
    private bool _registered;
    private bool _placing;

    protected IntPtr MonitorHandle { get; private set; }
    protected bool Docked { get; private set; } = true;
    protected bool ReserveSpace { get; private set; } = true;
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

        // NO WS_THICKFRAME. It used to be here so Windows would run its own modal sizing loop for us
        // when WM_NCHITTEST returned HTLEFT/HTRIGHT — and it cost us both of the panel's worst bugs:
        //
        //  • That modal loop takes the mouse capture and SWALLOWS the WM_LBUTTONUP that ends the drag.
        //    WPF never sees the button come back up, so its input stack stays wedged: the next
        //    right-click opened the context menu and it vanished again ~0.2 s later. Mouse.Synchronize()
        //    did not fix it — the only real fix is never to enter that loop.
        //  • WS_THICKFRAME also makes DWM paint its own window border, which turns WHITE whenever the
        //    panel looks active. The compositor draws it OUTSIDE our client area, so no WM_NCCALCSIZE
        //    trick could hide it: hence the pale strip along the edge in the user's recording.
        //
        // We resize the panel ourselves instead (OnPreviewMouseLeftButtonDown & co. below): ordinary
        // WPF capture, no modal loop, no frame, no DWM border. The window keeps WindowStyle=None, so
        // there is no non-client area left to fight over.
        Native.RemoveDwmBorder(_hwnd);   // belt and braces; nothing should be drawing one now

        _callbackMsg = Native.RegisterWindowMessage("SidebarMonitor_AppBar");
    }

    /// <summary>
    /// WS_EX_TRANSPARENT (plus LAYERED, which it requires) makes every click fall through to the
    /// window behind. A toggle, not a hover: if hovering made the panel solid, you could never
    /// click through the very spot the cursor is over.
    /// </summary>
    protected void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED
                : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Re-applies the whole placement. Safe to call whenever a setting changes.</summary>
    protected void ApplyPlacement(IntPtr monitor, bool docked, bool reserveSpace, bool edgeLeft, int width,
                                 bool minimized, bool topmost, double floatX, double floatY, double floatHeight)
    {
        if (_hwnd == IntPtr.Zero) return;

        MonitorHandle = monitor;
        Docked = docked;
        ReserveSpace = reserveSpace;
        EdgeLeft = edgeLeft;
        PanelWidth = width;
        Minimized = minimized;

        int strip = minimized ? MinimizedWidth : width;

        _placing = true;
        try
        {
            SetTopmost(topmost);

            if (docked && reserveSpace)
            {
                EnsureAppBar();
                // If the shell refuses the AppBar registration (seen when the process is spawned by
                // explorer during logon/first-run, a timing race on the HWND), PositionOnEdge would
                // bail and leave the window at its creation size — the panel then renders too narrow
                // and clips the right-hand columns (per-core temp, combined bars). Fall back to a
                // plain edge placement so it is ALWAYS sized to the configured width; a later
                // ReapplyPlacement re-tries the AppBar once the shell is ready.
                if (_registered) PositionOnEdge(strip);
                else PositionAtEdgeNoReserve(strip, topmost);
            }
            else if (docked)
            {
                // Edge-pinned overlay without an AppBar: no reserved work area, so other windows
                // maximise, snap and go fullscreen across the whole monitor and just draw under us.
                RemoveAppBar();
                PositionAtEdgeNoReserve(strip, topmost);
            }
            else
            {
                RemoveAppBar();
                // SWP_FRAMECHANGED forces WM_NCCALCSIZE (our handler => client area = whole window) on
                // every re-placement, not just the first. Without it, a later placement/resize can let
                // the WS_THICKFRAME sizing border re-appear and re-inset the client, which offsets all
                // hit-testing (dead right-click, wrong element) and paints a visible edge strip.
                Native.SetWindowPos(_hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
                    (int)floatX, (int)floatY, strip, (int)floatHeight, Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            }
        }
        finally { _placing = false; }
    }

    /// <summary>
    /// The chosen monitor's rectangle, in physical pixels — but self-healing. A stored HMONITOR
    /// goes stale the instant its display is powered off (or the topology otherwise changes), and
    /// then GetMonitorInfoW fails and hands back an all-zero rect. Placing against that lands the
    /// panel at (0,0) with zero size on the primary monitor: the "impossible to manage" state.
    /// When we detect it, we fall back to whatever monitor the window is physically on and adopt
    /// that handle, so placement always targets a real display.
    /// </summary>
    private Rect MonitorRect()
    {
        var mi = Native.GetMonitorInfoFor(MonitorHandle);
        if (mi.rcMonitor.Width > 0 && mi.rcMonitor.Height > 0) return mi.rcMonitor;

        MonitorHandle = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        return Native.GetMonitorInfoFor(MonitorHandle).rcMonitor;
    }

    /// <summary>Positions the panel flush against the chosen edge, full monitor height, without
    /// registering an AppBar — so the shell reserves nothing and window management stays whole.</summary>
    private void PositionAtEdgeNoReserve(int strip, bool topmost)
    {
        var mon = MonitorRect();
        int x = EdgeLeft ? mon.Left : mon.Right - strip;
        Native.SetWindowPos(_hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            x, mon.Top, strip, mon.Bottom - mon.Top, Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    private void SetTopmost(bool topmost)
    {
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = topmost ? ex | Native.WS_EX_TOPMOST : ex & ~Native.WS_EX_TOPMOST;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        // FRAMECHANGED re-asserts the borderless client area even on a topmost-only toggle (NOSIZE
        // otherwise skips WM_NCCALCSIZE), so this path can't leave the thick frame painted either.
        Native.SetWindowPos(_hwnd, topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_FRAMECHANGED);
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

        var mon = MonitorRect();
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
            abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height, Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
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

    // ── Edge resize, done by us (no Win32 modal loop) ────────────────────────────────────────────
    //
    // Plain WPF mouse capture, exactly like the floating-window drag above it. Everything Win32's
    // sizing loop used to do — and break — we now do here, in a state we control: no swallowed
    // button-up (so the context menu keeps working), no WS_THICKFRAME (so DWM paints no border), and
    // the geometry is ours (so the panel can never end up taller than the strip the shell reserved).

    private bool _resizing;
    private int _grip;             // which edge is being dragged (HT* code), 0 = none
    private Rect _resizeStart;     // window rect when the drag began

    /// <summary>The resize edge under the cursor right now, or 0. Docked, only the INNER edge
    /// resizes (the outer one is pinned to the monitor); floating, the sides and bottom do.
    /// Collapsed, nothing does: the strip's width is a constant.</summary>
    private int GripUnderCursor()
    {
        if (Minimized || !Native.GetCursorPos(out var p)) return 0;
        Native.GetWindowRect(_hwnd, out Rect wr);
        if (p.X < wr.Left || p.X > wr.Right || p.Y < wr.Top || p.Y > wr.Bottom) return 0;

        bool left = p.X <= wr.Left + ResizeGrip, right = p.X >= wr.Right - ResizeGrip;
        bool bottom = p.Y >= wr.Bottom - ResizeGrip;

        if (Docked)
            return EdgeLeft && right ? Native.HTRIGHT : !EdgeLeft && left ? Native.HTLEFT : 0;

        if (bottom && left) return Native.HTBOTTOMLEFT;
        if (bottom && right) return Native.HTBOTTOMRIGHT;
        if (left) return Native.HTLEFT;
        if (right) return Native.HTRIGHT;
        if (bottom) return Native.HTBOTTOM;
        return 0;
    }

    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        int grip = GripUnderCursor();
        if (grip != 0)
        {
            _grip = grip;
            _resizing = true;
            _resizeStart = WindowRect;
            CaptureMouse();
            e.Handled = true;   // don't let the click through to the panel underneath
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_resizing) { ResizeToCursor(); e.Handled = true; return; }

        // Hover feedback, so the grip is discoverable at all.
        Cursor = GripUnderCursor() switch
        {
            Native.HTLEFT or Native.HTRIGHT => System.Windows.Input.Cursors.SizeWE,
            Native.HTBOTTOM => System.Windows.Input.Cursors.SizeNS,
            Native.HTBOTTOMLEFT => System.Windows.Input.Cursors.SizeNESW,
            Native.HTBOTTOMRIGHT => System.Windows.Input.Cursors.SizeNWSE,
            _ => null,
        };
        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_resizing)
        {
            _resizing = false;
            _grip = 0;
            ReleaseMouseCapture();
            Resized?.Invoke();   // persists the new size and re-snaps the AppBar
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseLeftButtonUp(e);
    }

    /// <summary>Live-resize to the cursor, with the same constraints the old WM_SIZING applied.</summary>
    private void ResizeToCursor()
    {
        if (!Native.GetCursorPos(out var p)) return;
        var r = _resizeStart;

        if (Docked)
        {
            // The outer edge stays pinned to the monitor; only the width follows the cursor. The
            // height is whatever we already have — which docked is the rect the shell granted the
            // AppBar (taskbar excluded), NOT the full monitor.
            var mon = MonitorRect();
            int width = Math.Max(MinPanelWidth, EdgeLeft ? p.X - mon.Left : mon.Right - p.X);
            int x = EdgeLeft ? mon.Left : mon.Right - width;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, r.Top, width, r.Bottom - r.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }

        int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (_grip is Native.HTLEFT or Native.HTBOTTOMLEFT) left = Math.Min(p.X, right - MinPanelWidth);
        if (_grip is Native.HTRIGHT or Native.HTBOTTOMRIGHT) right = Math.Max(p.X, left + MinPanelWidth);
        if (_grip is Native.HTBOTTOM or Native.HTBOTTOMLEFT or Native.HTBOTTOMRIGHT)
            bottom = Math.Max(p.Y, top + MinPanelHeight);

        Native.SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
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

        // A display was powered off/on, the resolution changed, or a monitor was docked/undocked.
        // Every stored HMONITOR is now suspect, so let the subclass re-enumerate and re-place.
        if (msg == Native.WM_DISPLAYCHANGE)
            OnDisplayChanged();

        return IntPtr.Zero;
    }

    /// <summary>
    /// Display topology changed. The subclass re-enumerates monitors and re-applies placement, so
    /// the panel returns to its chosen display (with a fresh handle) instead of being stranded on
    /// the primary at zero size. Base does nothing.
    /// </summary>
    protected virtual void OnDisplayChanged() { }

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
