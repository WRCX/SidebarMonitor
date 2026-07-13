using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

// The (pruned) right-click context menu builder, split out of MainWindow.cs. Everything configurable
// lives in the Settings window now; this is just quick actions. Partial class.
internal sealed partial class MainWindow
{
    // ---------------------------------------------------------------- menu

    /// <summary>The pruned context menu: everything configurable now lives in the Settings window
    /// (opened from here), so the right-click is just quick actions.</summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var settings = new MenuItem { Header = Loc.T("Ajustes…"), FontWeight = FontWeights.SemiBold };
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new Separator());

        // Quick show/hide per section (also in Ajustes → Secciones, but handy from the right-click).
        var quickSections = new MenuItem { Header = Loc.T("Secciones") };
        foreach (var s in _sections)
        {
            var sec = s;
            var it = new MenuItem { Header = sec.Title, IsCheckable = true, IsChecked = sec.Visibility == Visibility.Visible };
            it.Click += (_, _) => SetSectionShown(sec, it.IsChecked);
            quickSections.Items.Add(it);
        }
        menu.Items.Add(quickSections);

        menu.Items.Add(new Separator());

        var minimizeQuick = new MenuItem { Header = Loc.T("Minimizar a pestaña") };
        minimizeQuick.Click += (_, _) => SetMinimized(true);
        menu.Items.Add(minimizeQuick);

        var hideQuick = new MenuItem { Header = Loc.T("Ocultar (queda en la bandeja)") };
        hideQuick.Click += (_, _) => Visibility = Visibility.Hidden;
        menu.Items.Add(hideQuick);

        var exitQuick = new MenuItem { Header = Loc.T("Salir") };
        exitQuick.Click += (_, _) => Close();
        menu.Items.Add(exitQuick);

        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src)
                Native.SetForegroundWindow(src.Handle);
        };
        return menu;
    }


    /// <summary>The verbose readout: CPU vendor/brand, shared-memory contract versions, SDK/helper
    /// state, refresh cadence, snapshot age, and CSV recording status. For diagnosing "why is temp —?"
    /// or "am I on the latest contract?" without a debugger.</summary>
    private void UpdateDebugOverlay(ref Snapshot s, TimeSpan age)
    {
        var ci = CultureInfo.InvariantCulture;
        string vendor = CpuVendor.Maker switch { CpuMaker.Amd => "AMD", CpuMaker.Intel => "Intel", _ => "?" };
        string sdk = s.CpuFromAmd ? "SDK✓" : CpuVendor.IsAmd ? "SDK✗ (EULA/helper)" : "SDK n/a";
        // On Intel the "PawnIO" token reports the IntelMSR path (temp+RAPL); on AMD, the RyzenSMU path.
        string pawnIo = CpuVendor.IsIntel
            ? (s.CpuFromIntel ? "Intel✓" : _cfg.IntelSensors ? "Intel✗" : "Intel○")
            : (s.CpuFromPawnIo ? "PawnIO✓" : _cfg.AmdAdvanced ? "PawnIO✗" : "PawnIO○");
        if (s.CpuPmTableVersion != 0) pawnIo += string.Create(CultureInfo.InvariantCulture, $" PM:0x{s.CpuPmTableVersion:X}");
        pawnIo += !float.IsNaN(s.Cpu.FanPct) ? " Fan✓" : _cfg.FanPawnIo ? " Fan✗" : " Fan○";
        string csv = _csv.IsRunning ? string.Create(ci, $"CSV●{_csv.RowCount}") : "CSV○";
        _debug.Text = string.Create(ci,
            $"{vendor} · {CpuVendor.Brand}\n" +
            $"snap v{SnapshotLayout.Version} · etw v{EtwLayout.Version} · {sdk} · {pawnIo} · " +
            $"{(s.EtwAvailable ? "helper✓" : "helper✗")} · {_cfg.RefreshMs}ms · age {age.TotalMilliseconds:F0}ms · {csv}\n" +
            $"cfg: temp={_cfg.ShowCoreTemp} bar={_cfg.CoreBarMode} pos={_cfg.CoreMetricPos} w={_cfg.Width} " +
            $"net={(_cfg.NetUnitsBinary ? "bin" : "dec")} dock={_cfg.Docked} cpuGraph={_cfg.CpuGraphMode} pal={_cfg.CorePalette}");
    }

}
