using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

// Non-hot-path glue split out of MainWindow.cs: section show/hide persistence, the Settings-window
// facade (ApplyLive + accessors that SettingsWindow.cs calls), and the test/preview hooks. Partial.
internal sealed partial class MainWindow
{
    // ---------------------------------------------------------------- state

    private void LoadSections()
    {
        foreach (var s in _sections)
        {
            if (!_cfg.Sections.TryGetValue(s.Key, out int v)) continue;
            s.Visibility = v == 0 ? Visibility.Collapsed : Visibility.Visible;
            s.Expanded = v == 2;
        }
    }

    private void SaveSections()
    {
        _cfg.Sections = _sections.ToDictionary(
            s => s.Key,
            s => s.Visibility != Visibility.Visible ? 0 : s.Expanded ? 2 : 1);
        _cfg.Save();
    }

    // ---------------------------------------------------------------- settings window facade
    // The proper Settings window (SettingsWindow.cs) drives the same config + live-apply the context
    // menu does, without duplicating logic. It reads/writes _cfg then calls ApplyLive(key) to push the
    // change into the running UI. One dispatcher + a few accessors keeps the coupling small.

    private SettingsWindow? _settings;
    private Snapshot _lastSnap;
    private bool _haveSnap;

    /// <summary>Plain-text sensors report for the "support my CPU" GitHub issue flow. Invariant
    /// culture and stable keys on purpose — it's for a bug tracker, not for reading in-app.</summary>
    internal string BuildSensorDiagnostics()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(512);
        ref var s = ref _lastSnap;
        sb.AppendLine(ci, $"app_version={CurrentVersionText}");
        sb.AppendLine(ci, $"cpu={CpuVendor.Brand}");
        sb.AppendLine(ci, $"vendor={CpuVendor.Maker}");
        sb.AppendLine(ci, $"contracts=snap v{SnapshotLayout.Version} / etw v{EtwLayout.Version}");
        sb.AppendLine(ci, $"helper={(_haveSnap && s.EtwAvailable ? "ok" : "no")}");
        sb.AppendLine(ci, $"amd_sdk={(_haveSnap && s.CpuFromAmd ? "ok" : "no")}");
        sb.AppendLine(ci, $"pawnio_toggle={_cfg.AmdAdvanced}");
        sb.AppendLine(ci, $"pawnio_temp={(_haveSnap && s.CpuFromPawnIo ? "ok" : "no")}");
        sb.AppendLine(ci, $"pm_table_version=0x{(_haveSnap ? s.CpuPmTableVersion : 0):X}");
        sb.AppendLine(ci, $"intel_toggle={_cfg.IntelSensors}");
        sb.AppendLine(ci, $"intel_msr={(_haveSnap && s.CpuFromIntel ? "ok" : "no")}");
        sb.AppendLine(ci, $"fan_toggle={_cfg.FanPawnIo}");
        sb.AppendLine(ci, $"fan_pct={(_haveSnap && !float.IsNaN(s.Cpu.FanPct) ? s.Cpu.FanPct.ToString("F0", ci) : "no")}");
        if (_haveSnap)
            sb.AppendLine(ci, $"temp_c={s.Cpu.TempC:F1} package_w={s.Cpu.PackagePowerW:F1} tjmax_c={s.Cpu.TjMaxC:F0}");
        return sb.ToString();
    }

    internal UiConfig Config => _cfg;
    internal GpuSection GpuSectionRef => _gpuSection;
    internal CsvLogger CsvLoggerRef => _csv;
    internal IReadOnlyList<Section> SectionsRO => _sections;
    internal IReadOnlyList<(IntPtr Handle, MonitorInfo Info)> MonitorsRO => _monitors;

    /// <summary>Push a just-changed config value into the live UI, then persist. `what` names the
    /// affected subsystem; mirrors exactly what the context-menu handlers do.</summary>
    internal void ApplyLive(string what)
    {
        switch (what)
        {
            case "cpugraph": ApplyCpuGraphMode(); break;
            case "cpucols": _cpuCoreGrid.Columns = _cfg.CpuGraphColumns; break;
            case "cpugridaxis": _cpuCoreGrid.AutoScale = _cfg.CpuGridAutoScale; _cpuCoreGrid.ResetColors(); break;
            case "ram": ApplyRamModulesMode(); break;
            case "palette":
                Theme.SetCorePalette(_cfg.CorePalette);
                _cpuCoreSpark.ResetColors();
                _cpuCoreGrid.ResetColors();
                _coreRows.InvalidateVisual();
                break;
            case "freqcaption": UpdateFreqCaption(); break;
            case "graphheights": ApplyGraphHeights(); break;
            case "autoscale": ApplyAutoScale(); break;
            case "refresh": ApplyRefreshRates(); break;
            case "placement": ReapplyPlacement(); break;
            case "clickthrough": SetClickThrough(_cfg.ClickThrough); break;
            case "tooltip": Theme.TooltipBg.Opacity = _cfg.TooltipOpacity; break;
            case "gpuview": _gpuSection.View = _cfg.GpuView; break;
            case "gpuengines": _gpuSection.ShowEngines = _cfg.ShowGpuEngines; break;
            case "gpucols": _gpuSection.ApplyColumns(_cfg.GpuEngineColumns); break;
            case "csv": if (_cfg.LogCsv) _csv.Start(DateTime.Now); else _csv.Stop(); break;
            case "verbose": _debug.Visibility = _cfg.LogVerbose ? Visibility.Visible : Visibility.Collapsed; break;
            case "netrows": _netProcRows.Children.Clear(); break;
            case "disks": _diskBlocks.Clear(); _diskPanels.Children.Clear(); break;
            case "menu": ContextMenu = BuildMenu(); break;
        }
        _cfg.Save();
    }

    internal void SetSectionShown(Section s, bool shown)
    {
        s.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SaveSections();
    }

    /// <summary>Open the Settings window (single instance; re-focus if already open).</summary>
    internal void OpenSettings()
    {
        if (_settings is { IsLoaded: true }) { _settings.Activate(); return; }
        _settings = new SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    internal IReadOnlyList<Section> OrderedSectionsRO() => OrderedSections().ToList();

    /// <summary>Reorder a section (settings window's up/down); persists and re-lays the panels.</summary>
    internal void MoveSectionLive(string key, int dir) => MoveSection(key, dir);

    /// <summary>Global refresh interval; restarts the owned agent so it samples at the new rate.</summary>
    internal void SetGlobalRefresh(int ms)
    {
        _cfg.RefreshMs = ms;
        ApplyRefreshRates();
        if (_ownedAgent is { HasExited: false })
        {
            try { _ownedAgent.Kill(); _ownedAgent.WaitForExit(2000); } catch { }
            _ownedAgent = null; _reader?.Dispose(); _reader = null;
            TryLaunchAgent();
        }
        _cfg.Save();
    }

    /// <summary>How often the agent reads the GPU vendor sensors (temp/power/clocks). Restarts the
    /// owned agent so it picks up the new --gpu-every. Only affects the sensor numbers; the GPU load
    /// graph keeps sampling at the global rate.</summary>
    internal void SetGpuRefresh(int ms)
    {
        _cfg.GpuRefreshMs = ms;
        if (_ownedAgent is { HasExited: false })
        {
            try { _ownedAgent.Kill(); _ownedAgent.WaitForExit(2000); } catch { }
            _ownedAgent = null; _reader?.Dispose(); _reader = null;
            TryLaunchAgent();
        }
        _cfg.Save();
    }

    /// <summary>Per-section refresh override; ms &lt; 0 clears it (follow the global rate).</summary>
    internal void SetSectionRefresh(string key, int ms)
    {
        if (ms < 0) _cfg.SectionRefreshMs.Remove(key);
        else _cfg.SectionRefreshMs[key] = ms;
        ApplyRefreshRates();
        _cfg.Save();
    }

    // ---------------------------------------------------------------- proof

    /// <summary>Test hook: fold, unfold or hide sections by key, as clicking would.</summary>
    public void Apply(string[] collapse, string[] expand, string[] hide)
    {
        foreach (var s in _sections)
        {
            if (collapse.Contains(s.Key)) s.Expanded = false;
            if (expand.Contains(s.Key)) { s.Expanded = true; s.Visibility = Visibility.Visible; }
            if (hide.Contains(s.Key)) s.Visibility = Visibility.Collapsed;
        }
    }

    public void SetMinimizedForTest(bool minimized) => SetMinimized(minimized);

    public void ForceHoverForTest() { _cpuSpark.ForceHover(0.5); _cpuCoreSpark.ForceHover(0.5); _netSpark.ForceHover(0.5); }

    /// <summary>Walks the visual tree and reports every TextBlock's text, size and brush.</summary>
    public void DumpText()
    {
        Console.WriteLine($"{"texto",-30} {"w",6} {"h",6}  {"fg",-10} {"parent"}");
        Walk(this);

        static void Walk(DependencyObject o)
        {
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(o, i);
                if (child is TextBlock tb)
                {
                    string fg = tb.Foreground is SolidColorBrush sb ? sb.Color.ToString() : "?";
                    string txt = tb.Text.Length > 28 ? tb.Text[..28] : tb.Text;
                    Console.WriteLine($"{'"' + txt + '"',-30} {tb.ActualWidth,6:F0} {tb.ActualHeight,6:F0}  {fg,-10} {VisualTreeHelper.GetParent(tb)?.GetType().Name}");
                }
                Walk(child);
            }
        }
    }

    /// <summary>
    /// Renders the root visual straight into the bitmap. Going through a VisualBrush instead
    /// silently drops text runs — the elements measure fine, they just never rasterize.
    /// </summary>
    public void SaveScreenshot(string path)
    {
        var root = (FrameworkElement)Content;
        var bmp = new RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
