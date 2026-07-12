using System.Drawing;
using System.Windows.Forms;

namespace SidebarMonitor.UI;

/// <summary>
/// Tray presence, so the panel can be hidden entirely and brought back without a taskbar button
/// (the window is a WS_EX_TOOLWINDOW and deliberately has none).
///
/// The icon is drawn in memory rather than shipped as a .ico: three bars, like the panel itself.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _handle;

    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    public event Action? ConfigRequested;

    public TrayIcon()
    {
        _handle = BuildIcon();
        _icon = new NotifyIcon
        {
            Icon = _handle,
            Text = "SidebarMonitor",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        _icon.ContextMenuStrip.Items.Add(Loc.T("Mostrar / ocultar"), null, (_, _) => ToggleRequested?.Invoke());
        _icon.ContextMenuStrip.Items.Add(Loc.T("Configuración…"), null, (_, _) => ConfigRequested?.Invoke());
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add(Loc.T("Salir"), null, (_, _) => ExitRequested?.Invoke());

        _icon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    public void Notify(string text)
    {
        _icon.BalloonTipTitle = "SidebarMonitor";
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(3000);
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var bar = new SolidBrush(Color.FromArgb(0x39, 0x87, 0xE5));
            g.FillRectangle(bar, 5, 18, 5, 10);
            g.FillRectangle(bar, 13, 10, 5, 18);
            g.FillRectangle(bar, 21, 4, 5, 24);
        }

        // Icon.FromHandle does not own the handle; clone so the bitmap can be disposed.
        IntPtr h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();
        }
        finally { NativeMethods.DestroyIcon(h); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _handle.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
