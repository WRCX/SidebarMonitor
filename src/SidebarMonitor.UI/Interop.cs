using System.Runtime.InteropServices;

namespace SidebarMonitor.UI;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct AppBarData
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public Rect rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int cbSize;
    public Rect rcMonitor;
    public Rect rcWork;
    public uint dwFlags;
}

internal static class Native
{
    // Window styles
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const long WS_THICKFRAME = 0x00040000;   // resizable border (invisible with our NCCALCSIZE)

    // ── DWM: kill the Windows 11 window border ───────────────────────────────────────────────────
    //
    // WS_THICKFRAME (which we need, so Windows runs its resize loop for us) makes DWM draw its own
    // 1 px window border — and it turns WHITE the moment the window looks active (right-click, hover
    // over the strip). Our WM_NCCALCSIZE keeps the CLIENT area borderless, but this border is painted
    // by the compositor OUTSIDE our client area, so no amount of frame recalculation touches it: a
    // white line ran down the panel's edge. DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE removes it
    // (Windows 11 21H2+; older builds just ignore the call, which is why the HRESULT is discarded).
    public const int DWMWA_BORDER_COLOR = 34;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);

    /// <summary>Best-effort: remove DWM's window border. No-op on Windows 10.</summary>
    public static void RemoveDwmBorder(IntPtr hwnd)
    {
        uint none = DWMWA_COLOR_NONE;
        try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint)); }
        catch (DllNotFoundException) { /* ancient Windows: no DWM, no border */ }
    }

    // Resize hit-test + sizing
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_SIZING = 0x0214;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12,
                     HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    public const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
                     WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;   // keeps it out of Alt+Tab
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;   // never becomes foreground

    // Messages
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int WM_DISPLAYCHANGE = 0x007E;   // resolution/topology changed (monitor off/on, dock)
    public const int MA_NOACTIVATE = 3;

    // AppBar
    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const uint ABM_QUERYPOS = 0x2;
    public const uint ABM_SETPOS = 0x3;
    public const uint ABM_WINDOWPOSCHANGED = 0x9;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const int ABN_POSCHANGED = 0x1;

    // SetWindowPos
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct PointI { public int X, Y; }

    /// <summary>Screen coordinates, in physical pixels. The window never activates, so WPF's
    /// DragMove is unavailable and dragging is done from raw cursor positions.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out PointI point);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprc, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// A WinExe owns no console. When it was launched from one, borrow it so the diagnostic
    /// flags still print where the user is looking. Silently does nothing otherwise.
    /// </summary>
    public static void AttachToParentConsole()
    {
        if (!AttachConsole(AttachParentProcess)) return;
        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }

    // DPI awareness, read back to prove the manifest took effect.
    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern uint GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    public static List<(IntPtr Handle, MonitorInfo Info)> EnumerateMonitors()
    {
        var list = new List<(IntPtr, MonitorInfo)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(h, ref mi)) list.Add((h, mi));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo GetMonitorInfoFor(IntPtr hMonitor)
    {
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfoW(hMonitor, ref mi);
        return mi;
    }
}
