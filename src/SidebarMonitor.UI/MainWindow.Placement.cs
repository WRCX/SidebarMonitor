using System.Windows;
using System.Windows.Input;

namespace SidebarMonitor.UI;

// Window placement (docked/floating/monitor recovery) and edge-drag handling, split out of
// MainWindow.cs for readability. Same class (partial) — shares _cfg, _monitors, the tab/body/button
// controls and the AppBarWindow base members (ApplyPlacement, WindowRect, MoveFloatingTo).
internal sealed partial class MainWindow
{
    private void ReapplyPlacement()
    {
        if (_monitors.Count == 0) return;
        int index = Math.Clamp(_cfg.Monitor, 0, _monitors.Count - 1);
        ApplyPlacement(_monitors[index].Handle, _cfg.Docked, _cfg.ReserveSpace, _cfg.EdgeLeft, _cfg.Width,
                       _cfg.Minimized, _cfg.Topmost, _cfg.FloatX, _cfg.FloatY, _cfg.FloatHeight);
    }

    /// <summary>
    /// A monitor was powered off/on (or the topology otherwise changed). The HMONITORs captured at
    /// startup are now stale — using them strands the panel on the primary at zero size. Debounce
    /// the message burst, re-enumerate for fresh handles, then re-place onto the chosen display.
    /// </summary>
    protected override void OnDisplayChanged()
    {
        _displaySettle.Stop();
        _displaySettle.Start();
    }

    private void RecoverFromDisplayChange()
    {
        _displaySettle.Stop();

        var fresh = Native.EnumerateMonitors();
        if (fresh.Count == 0) return;   // mid-transition; a later WM_DISPLAYCHANGE will retry

        _monitors.Clear();
        _monitors.AddRange(fresh);
        // Don't overwrite _cfg.Monitor here: the chosen display may just be off. ReapplyPlacement
        // clamps locally, so the panel rides the surviving monitor now and snaps back to the chosen
        // one the moment it returns.

        ReapplyPlacement();
        ContextMenu = BuildMenu();   // the monitor list (and its checkmark) may have changed
    }

    /// <summary>Persists the new size after an edge drag. Width applies docked or floating (min
    /// 240); height only floating. Docked re-snaps the AppBar to the new width.</summary>
    private void OnPanelResized()
    {
        // Never persist a width measured while collapsed. The strip is MinimizedWidth wide, so this
        // would store max(MinPanelWidth, 18) = 240 and silently destroy the user's real width. The
        // grip is disabled when minimized now, so no resize should even reach here — but the config
        // is the user's, and no phantom drag gets to overwrite it.
        if (Minimized) return;

        var r = WindowRect;
        _cfg.Width = Math.Max(MinPanelWidth, r.Width);
        if (_cfg.Docked)
        {
            ReapplyPlacement();
        }
        else
        {
            _cfg.FloatX = r.Left;
            _cfg.FloatY = r.Top;
            _cfg.FloatHeight = r.Height;
        }
        _cfg.Save();
    }

    private void SetMinimized(bool minimized)
    {
        _cfg.Minimized = minimized;
        _body.Visibility = minimized ? Visibility.Collapsed : Visibility.Visible;
        _tab.Visibility = minimized ? Visibility.Visible : Visibility.Collapsed;
        _tabArrow.Text = _cfg.EdgeLeft ? "›" : "‹";
        _minButton.Text = _cfg.EdgeLeft ? "«" : "»";
        ReapplyPlacement();
        _cfg.Save();
    }

    // Dragging: the window never activates, so WPF's DragMove is out. Track raw cursor deltas.
    private bool _dragging;
    private Native.PointI _dragOrigin;
    private (int X, int Y) _windowOrigin;

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (_cfg.Docked || e.ChangedButton != MouseButton.Left) return;
        // Don't hijack a click on the minimize button (it lives inside the drag bar).
        if (ReferenceEquals(e.OriginalSource, _minButton)) return;
        Native.GetCursorPos(out _dragOrigin);
        var r = WindowRect;
        _windowOrigin = (r.Left, r.Top);
        _dragging = true;
        // Capture on the very element that carries the MouseMove/Up handlers. Capturing an ancestor
        // instead routes the events to the ancestor, so the child's ContinueDrag/EndDrag never fire
        // and the drag (and the capture) get stuck — which is exactly what broke floating drag.
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Native.GetCursorPos(out var now);
        MoveFloatingTo(_windowOrigin.X + (now.X - _dragOrigin.X), _windowOrigin.Y + (now.Y - _dragOrigin.Y));
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
        var r = WindowRect;
        _cfg.FloatX = r.Left;
        _cfg.FloatY = r.Top;
        _cfg.FloatHeight = r.Height;
        _cfg.Save();
    }
}
