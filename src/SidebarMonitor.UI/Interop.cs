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
    // Extended window styles
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;   // keeps it out of Alt+Tab
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_NOACTIVATE = 0x08000000;   // never becomes foreground

    // Messages
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
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
