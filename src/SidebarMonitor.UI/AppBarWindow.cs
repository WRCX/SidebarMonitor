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

        // WS_THICKFRAME lets Windows run its NC resize loop when we return HT* codes; WM_NCCALCSIZE
        // (below) then keeps the client area = whole window, so it stays borderless.
        long style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        style |= Native.WS_THICKFRAME;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, new IntPtr(style));

        // Commit that style change so Windows re-queries WM_NCCALCSIZE now (our handler returns 0 =>
        // client area = whole window, borderless). Win32 requires SWP_FRAMECHANGED after any frame
        // style change; without it the recalc is deferred and, after rapid relaunch/re-placement
        // sequences, the thick sizing border can stay painted (a visible edge strip) AND the client
        // area stays inset by the frame — which offsets every hit-test, so right-clicks land on the
        // wrong element and the context menu never opens. Forcing the recalc here fixes both.
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        // WS_THICKFRAME also buys us DWM's window border, which lights up WHITE whenever the panel
        // looks active (a right-click, a hover on the strip). It is painted by the compositor outside
        // our client area, so WM_NCCALCSIZE can't hide it — this is the only thing that removes it.
        Native.RemoveDwmBorder(_hwnd);

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Keep the client area = the whole window despite WS_THICKFRAME, so it stays borderless.
        if (msg == Native.WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }

        // Which edge (if any) is a resize handle. Docked: only the inner edge changes the width and
        // the panel stays full-height. Floating: sides + bottom + bottom corners (not the top — that
        // is the drag bar).
        if (msg == Native.WM_NCHITTEST)
        {
            handled = true;

            // Collapsed, there is nothing to resize: the strip's width is a constant. Offering a grip
            // here was actively harmful — a mere click (no drag) on the edge started Win32's modal
            // sizing loop, WM_SIZING below instantly clamped the 18 px strip up to MinPanelWidth, and
            // the panel ballooned to 240 px for as long as the button was held. Worse, the drag's end
            // persisted that phantom 240 as the user's configured width, clobbering their real one.
            if (Minimized) return new IntPtr(Native.HTCLIENT);

            long lp = lParam.ToInt64();
            int sx = unchecked((short)(lp & 0xFFFF)), sy = unchecked((short)((lp >> 16) & 0xFFFF));
            Native.GetWindowRect(_hwnd, out Rect wr);
            bool left = sx <= wr.Left + ResizeGrip, right = sx >= wr.Right - ResizeGrip;
            bool bottom = sy >= wr.Bottom - ResizeGrip;

            if (Docked)
                return new IntPtr(!EdgeLeft && left ? Native.HTLEFT : EdgeLeft && right ? Native.HTRIGHT : Native.HTCLIENT);

            if (bottom && left) return new IntPtr(Native.HTBOTTOMLEFT);
            if (bottom && right) return new IntPtr(Native.HTBOTTOMRIGHT);
            if (left) return new IntPtr(Native.HTLEFT);
            if (right) return new IntPtr(Native.HTRIGHT);
            if (bottom) return new IntPtr(Native.HTBOTTOM);
            return new IntPtr(Native.HTCLIENT);
        }

        // Constrain the drag: minimum width; docked stays full-height with the outer edge pinned.
        if (msg == Native.WM_SIZING)
        {
            var rc = Marshal.PtrToStructure<Rect>(lParam);
            int edge = wParam.ToInt32();
            if (Docked)
            {
                // Keep the height we ALREADY have, not the monitor's. Docked, our height is whatever
                // the shell granted the AppBar (ABM_QUERYPOS/SETPOS), which excludes the taskbar —
                // forcing the full monitor rect here made every drag end 48 px taller than the strip
                // the shell has reserved for us, leaving the panel overhanging the taskbar and the
                // reservation stale until something re-placed it (the "borders" artefact).
                Native.GetWindowRect(_hwnd, out Rect cur);
                var mon = MonitorRect();
                rc.Top = cur.Top; rc.Bottom = cur.Bottom;
                if (EdgeLeft) rc.Left = mon.Left; else rc.Right = mon.Right;
                if (rc.Width < MinPanelWidth) { if (EdgeLeft) rc.Right = rc.Left + MinPanelWidth; else rc.Left = rc.Right - MinPanelWidth; }
            }
            else
            {
                if (rc.Width < MinPanelWidth)
                {
                    if (edge is Native.WMSZ_LEFT or Native.WMSZ_TOPLEFT or Native.WMSZ_BOTTOMLEFT) rc.Left = rc.Right - MinPanelWidth;
                    else rc.Right = rc.Left + MinPanelWidth;
                }
                if (rc.Height < MinPanelHeight)
                {
                    if (edge is Native.WMSZ_TOP or Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT) rc.Top = rc.Bottom - MinPanelHeight;
                    else rc.Bottom = rc.Top + MinPanelHeight;
                }
            }
            Marshal.StructureToPtr(rc, lParam, false);
            handled = true;
            return new IntPtr(1);
        }

        if (msg == Native.WM_EXITSIZEMOVE)
        {
            // Right after a drag-resize, re-assert the borderless client area so the sizing frame can
            // never linger (which would break right-click hit-testing and paint an edge strip). This
            // is the exact path a user hits by widening the panel; belt-and-suspenders with the
            // FRAMECHANGED now on every re-placement below.
            Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

            // Win32's modal sizing loop takes the mouse capture and swallows the WM_LBUTTONUP that
            // ends the drag: WPF never sees the button come back up, so its input stack still believes
            // the left button is down and refuses to open the context menu — right-click looked dead
            // until you clicked somewhere else (which resynced it). Drop any capture and force WPF to
            // re-read the real device state.
            System.Windows.Input.Mouse.Capture(null);
            System.Windows.Input.Mouse.Synchronize();

            Resized?.Invoke();
        }

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
