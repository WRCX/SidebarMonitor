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
    public event Action? UpdateRequested;
    public event Action? ClickThroughRequested;

    private ToolStripItem? _updateItem;
    private readonly ToolStripMenuItem _clickThroughItem;

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
        // Same name as the panel's own menu entry, and it opens the same window — it used to pop the
        // panel's context menu instead, one more click away from the settings it was named after.
        _icon.ContextMenuStrip.Items.Add(Loc.T("Ajustes…"), null, (_, _) => ConfigRequested?.Invoke());
        // With click-through on, the panel takes no clicks at all — its context menu included — so the
        // tray is the one place left to turn it back off. Settings' description has always said so.
        _clickThroughItem = new ToolStripMenuItem(Loc.T("Ignorar clics (pasan a través)"), null, (_, _) => ClickThroughRequested?.Invoke());
        _icon.ContextMenuStrip.Items.Add(_clickThroughItem);
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add(Loc.T("Salir"), null, (_, _) => ExitRequested?.Invoke());

        _icon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
        // Only the update offer's balloon applies the update when clicked. The handler used to be hooked
        // up the first time an update was found and then fire for every later balloon — clicking a
        // temperature alert, or the "Descargando…" notice itself, started an update.
        _icon.BalloonTipClicked += (_, _) => { if (_balloonOffersUpdate) UpdateRequested?.Invoke(); };
    }

    private bool _balloonOffersUpdate;
    private string? _announcedVersion;

    /// <summary>Mirrors the click-through state in the tray item's check mark.</summary>
    public void SetClickThrough(bool on) => _clickThroughItem.Checked = on;

    /// <summary>A Windows notification — with its sound. For events worth interrupting someone for, once
    /// each; anything that ticks (download progress) goes to <see cref="SetStatus"/> instead.</summary>
    public void Notify(string text)
    {
        _balloonOffersUpdate = false;
        _icon.BalloonTipTitle = "SidebarMonitor";
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ToolTipIcon.None;
        _icon.ShowBalloonTip(3000);
    }

    /// <summary>A line under the icon's hover text ("SidebarMonitor — Descargando… 42%"), silent. Null
    /// clears it. Windows refuses hover text over 127 characters, so it is cut to fit.</summary>
    public void SetStatus(string? text)
    {
        string s = string.IsNullOrEmpty(text) ? "SidebarMonitor" : "SidebarMonitor — " + text;
        _icon.Text = s.Length <= 127 ? s : s[..126] + "…";
    }

    /// <summary>An alert: its own title and the warning icon, so it doesn't read like an update notice.</summary>
    public void Alert(string title, string text)
    {
        _balloonOffersUpdate = false;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ToolTipIcon.Warning;
        _icon.ShowBalloonTip(10000);
    }

    /// <summary>Surfaces an available update: a highlighted menu item at the top + a balloon whose
    /// click also applies it. The balloon comes once per version — every check that found it used to
    /// raise it again, so «Actualizar ahora» opened with an offer for the update it was already doing.</summary>
    public void SetUpdateAvailable(string version)
    {
        string text = Loc.T("Actualizar a {0}", version);
        if (_updateItem is null)
        {
            _updateItem = new ToolStripMenuItem(text, null, (_, _) => UpdateRequested?.Invoke()) { Font = new Font(_icon.ContextMenuStrip!.Font, FontStyle.Bold) };
            _icon.ContextMenuStrip!.Items.Insert(0, _updateItem);
            _icon.ContextMenuStrip.Items.Insert(1, new ToolStripSeparator());
        }
        else _updateItem.Text = text;

        if (version == _announcedVersion) return;
        _announcedVersion = version;
        _balloonOffersUpdate = true;
        _icon.BalloonTipTitle = "SidebarMonitor";
        _icon.BalloonTipText = Loc.T("Hay una versión nueva disponible ({0}). Clic para actualizar.", version);
        _icon.BalloonTipIcon = ToolTipIcon.None;
        _icon.ShowBalloonTip(5000);
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
