using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

internal sealed class MainWindow : AppBarWindow
{
    private readonly UiConfig _cfg;
    private readonly List<(IntPtr Handle, MonitorInfo Info)> _monitors;

    private readonly DispatcherTimer _timer = new();
    // Coalesces the burst of WM_DISPLAYCHANGE messages a monitor off/on emits, and waits for the
    // work area to settle, before re-placing once.
    private readonly DispatcherTimer _displaySettle = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Dictionary<string, long> _sectionLastTick = [];   // per-section refresh gating
    private SeqLockReader<Snapshot>? _reader;
    private System.Diagnostics.Process? _ownedAgent;
    private TrayIcon? _tray;

    private readonly List<Section> _sections = [];
    private readonly StackPanel _stack = new();
    private readonly TextBlock _status;
    private readonly CsvLogger _csv = new();
    private readonly TextBlock _debug = new()
    {
        FontFamily = Theme.Mono,
        FontSize = 9.5,
        Foreground = Theme.InkMuted,
        Margin = new Thickness(8, 0, 8, 4),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly ScrollViewer _body;
    private readonly Border _tab;
    private readonly TextBlock _tabArrow;
    private readonly TextBlock _minButton;
    private readonly Border _titleBar;

    // CPU
    private readonly TextBlock _cpuName = Theme.Text("", 10, Theme.InkMuted);
    private readonly TextBlock _cpuFreq = Stat();
    private readonly TextBlock _cpuFreqCaption = Theme.Text("GHz", 9, Theme.InkMuted);
    private readonly TextBlock _cpuWatts = Stat();
    private readonly TextBlock _cpuTemp = Stat();
    private readonly TextBlock _cpuPct;
    private readonly Sparkline _cpuSpark = new(Theme.SeriesCpu) { FixedMax = 100 };
    private readonly CoreSparkline _cpuCoreSpark = new(height: 48);
    private readonly CoreGrid _cpuCoreGrid = new();
    private readonly Grid _cpuGraphHost = new();
    private readonly TextBlock _cpuExtra = Muted();
    private readonly TextBlock _cpuThrottle = Muted();
    private readonly TextBlock _cpuBoost = Muted();
    // Highest best-core clock seen this session — the chip's real peak boost (≈ rated Fmax when
    // cool), used as the "nominal" the current boost is measured against.
    private double _bestFreqPeakMhz;
    private readonly CoreRows _coreRows = new();

    // RAM
    private readonly BarMeter _ramMeter = new(Theme.SeriesCpu);
    private readonly TextBlock _ramText = Value();
    private readonly TextBlock _commitText = Muted();
    private readonly TextBlock _ramModules = Muted();

    // GPU — a self-contained widget: selector for discrete/integrated/both, per-engine mini-graphs.
    private readonly GpuSection _gpuSection = new();

    // NET
    private readonly Sparkline _netSpark = new(Theme.SeriesIn, Theme.SeriesOut);
    private readonly TextBlock _netDl = Value();
    private readonly TextBlock _netUl = Value();
    private readonly TextBlock _netPrimary = Muted();
    private readonly StackPanel _netProcRows = new();

    // DISK
    private readonly StackPanel _diskPanels = new();
    private readonly List<DiskBlock> _diskBlocks = [];

    private sealed record DiskBlock(string Key, TextBlock Head, TextBlock Temp, TextBlock Volumes, TextBlock Sub,
                                    BarMeter Active, TextBlock ActiveText, Sparkline Spark, TextBlock Rates);

    // TOP
    private readonly (TextBlock Name, TextBlock Cpu, TextBlock Mem)[] _topRows;
    private readonly TextBlock _totals = Muted();

    // DOCKER / WSL — read from inside the guest, since Windows only sees the vmmemWSL aggregate.
    private readonly DockerCollector _docker = new();
    private readonly WslCollector _wsl = new();
    private readonly StackPanel _dockerRows = new();
    private readonly StackPanel _wslRows = new();

    public MainWindow(UiConfig cfg, List<(IntPtr, MonitorInfo)> monitors)
    {
        _cfg = cfg;
        _monitors = monitors;
        // Migrate the old boolean "usage from C0" into the new 4-way bar mode.
        if (_cfg.CoreUsageC0 && _cfg.CoreBarMode == 0) { _cfg.CoreBarMode = 1; _cfg.CoreUsageC0 = false; }
        // Migrate the old boolean CPU-per-core-graph into the new 3-way graph mode.
        if (_cfg.CpuPerCoreGraph && _cfg.CpuGraphMode == 0) _cfg.CpuGraphMode = 1;
        Theme.TooltipBg.Opacity = Math.Clamp(_cfg.TooltipOpacity, 0.3, 1.0);   // shared by all tooltips

        Title = "SidebarMonitor";
        Background = Theme.Page;

        _cpuPct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
        _cpuPct.FontWeight = FontWeights.Bold;
        _status = Theme.Text(Loc.T("esperando al agente…"), 10, Theme.InkMuted);
        _minButton = Theme.Text("»", 11, Theme.InkMuted);
        _tabArrow = Theme.Text("‹", 12, Theme.InkMuted);
        _topRows = new (TextBlock, TextBlock, TextBlock)[8];

        _debug.Visibility = _cfg.LogVerbose ? Visibility.Visible : Visibility.Collapsed;
        _titleBar = BuildTitleBar();   // hosts _debug so it survives ApplySectionOrder's rebuild
        _stack.Children.Add(_titleBar);
        if (_cfg.LogCsv) _csv.Start(DateTime.Now);
        Register(new Section("cpu", Loc.T("CPU"), BuildCpu()));
        Register(new Section("ram", Loc.T("MEMORIA"), BuildRam()));
        Register(new Section("gpu", Loc.T("GPU"), BuildGpu()));
        Register(new Section("net", Loc.T("RED"), BuildNet()));
        Register(new Section("disk", Loc.T("DISCOS"), BuildDisks()));
        Register(new Section("top", Loc.T("PROCESOS"), BuildTop()));
        Register(new Section("docker", Loc.T("DOCKER"), BuildGuest(_dockerRows)));
        Register(new Section("wsl", Loc.T("WSL"), BuildGuest(_wslRows)));
        // Docker/WSL are opt-in: hidden by default so we never spawn docker/wsl unless asked.
        // A saved state (LoadSections) overrides this.
        Find("docker").Visibility = Visibility.Collapsed;
        Find("wsl").Visibility = Visibility.Collapsed;
        ApplySectionOrder();

        _body = new ScrollViewer
        {
            Content = _stack,
            Background = Theme.Page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };

        _tabArrow.HorizontalAlignment = HorizontalAlignment.Center;
        _tabArrow.VerticalAlignment = VerticalAlignment.Center;
        _tab = new Border { Background = Theme.Page, Child = _tabArrow, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        _tab.MouseLeftButtonUp += (_, _) => SetMinimized(false);
        ToolTipService.SetToolTip(_tab, Loc.T("Abrir SidebarMonitor"));

        var root = new Grid();
        root.Children.Add(_body);
        root.Children.Add(_tab);
        Content = root;

        ContextMenu = BuildMenu();
        LoadSections();

        ConfigureSparklineHovers();

        ApplyRefreshRates();
        Resized += OnPanelResized;
        _displaySettle.Tick += (_, _) => RecoverFromDisplayChange();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            ReapplyPlacement();
            SetClickThrough(_cfg.ClickThrough);
            SetMinimized(_cfg.Minimized);
            Tick();
            _timer.Start();

            // One delayed re-placement: if the shell wasn't ready at Loaded (explorer-spawned at
            // logon), the first AppBar registration can fall back to a plain edge placement. Retrying
            // once the message pump has settled registers the AppBar properly and reserves the strip.
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            settle.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); ReapplyPlacement(); };
            settle.Start();

            // Static RAM module info (model/type/speed) via WMI — slow first call, so off-thread.
            RamInfo.LoadAsync(() => Dispatcher.BeginInvoke(ApplyRamModulesMode));
        };
    }

    /// <summary>Show the WMI RAM module info per the configured mode: hidden, compact summary, or the
    /// full per-stick detail as text. The tooltip always carries the detail.</summary>
    private void ApplyRamModulesMode()
    {
        if (!RamInfo.HasData || _cfg.RamModulesMode == 0) { _ramModules.Visibility = Visibility.Collapsed; return; }
        // Module capacities are always binary GiB (their real size); the units toggle is for the
        // OS usage line only.
        string detail = RamInfo.DetailText();
        _ramModules.Text = _cfg.RamModulesMode == 2 && detail.Length > 0 ? detail : RamInfo.SummaryText();
        if (_ramModules.ToolTip is ToolTip tt && detail.Length > 0)
        {
            var tb = Theme.TipBlock();
            tb.Inlines.Add(Theme.TipHead(Loc.T("Módulos de memoria")));
            tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            tb.Inlines.Add(new System.Windows.Documents.Run(detail));
            tt.Content = tb;
        }
        _ramModules.Visibility = Visibility.Visible;
    }

    public void AttachTray(TrayIcon tray)
    {
        _tray = tray;
        tray.ToggleRequested += () => Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        tray.ConfigRequested += () => { if (ContextMenu is not null) ContextMenu.IsOpen = true; };
        tray.ExitRequested += Close;
    }

    private void Register(Section s)
    {
        s.StateChanged += SaveSections;
        _sections.Add(s);
    }

    /// <summary>The sections in display order: those named in the saved order first, then any
    /// unlisted ones in their default (registration) position.</summary>
    private IEnumerable<Section> OrderedSections()
    {
        var byKey = _sections.ToDictionary(s => s.Key);
        var seen = new HashSet<string>();
        foreach (var key in _cfg.SectionOrder)
            if (byKey.TryGetValue(key, out var s) && seen.Add(key))
                yield return s;
        foreach (var s in _sections)
            if (seen.Add(s.Key))
                yield return s;
    }

    /// <summary>Rehomes the section panels under the title bar in the configured order.</summary>
    private void ApplySectionOrder()
    {
        for (int i = _stack.Children.Count - 1; i >= 1; i--)   // keep the title bar at index 0
            _stack.Children.RemoveAt(i);
        foreach (var s in OrderedSections())
            _stack.Children.Add(s);
    }

    /// <summary>Moves a section one slot up (-1) or down (+1) and persists the new order.</summary>
    private void MoveSection(string key, int dir)
    {
        var order = OrderedSections().Select(s => s.Key).ToList();
        int i = order.IndexOf(key);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _cfg.SectionOrder = order;
        _cfg.Save();
        ApplySectionOrder();
        ContextMenu = BuildMenu();   // rebuild so the "Orden" submenu reflects the new positions
    }

    // ---------------------------------------------------------------- placement

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

    // ---------------------------------------------------------------- layout

    /// <summary>The build's version, "vMajor.Minor.Patch", read from the assembly (stamped by
    /// Directory.Build.props). Shown next to the title so a stale install is spotted immediately.</summary>
    private static string AppVersion
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private Border BuildTitleBar()
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = Theme.Text("SIDEBAR MONITOR", 10, Theme.InkMuted);
        title.FontWeight = FontWeights.SemiBold;

        // Build version next to the name so mismatched installs are obvious at a glance.
        var ver = Theme.Text(AppVersion, 9, Theme.InkMuted, mono: true);
        ver.Margin = new Thickness(5, 0, 0, 0);
        ver.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(ver, Loc.T("Versión de SidebarMonitor (UI). Debe coincidir con la del agente/helper instalados."));

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(title);
        titleRow.Children.Add(ver);

        _status.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_status, 1);

        _minButton.Cursor = Cursors.Hand;
        _minButton.Padding = new Thickness(4, 0, 2, 0);
        _minButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetMinimized(true); };
        ToolTipService.SetToolTip(_minButton, Loc.T("Minimizar a pestaña"));
        Grid.SetColumn(_minButton, 2);

        grid.Children.Add(titleRow);
        grid.Children.Add(_status);
        grid.Children.Add(_minButton);

        var bar = new Border { Child = grid, Background = Brushes.Transparent };
        bar.MouseLeftButtonDown += BeginDrag;
        bar.MouseMove += ContinueDrag;
        bar.MouseLeftButtonUp += EndDrag;

        var panel = new StackPanel();
        panel.Children.Add(bar);
        panel.Children.Add(_debug);   // verbose overlay, toggled by LogVerbose
        panel.Children.Add(new Border { Height = 1, Background = Theme.Grid });
        return new Border { Child = panel };
    }

    private UIElement BuildCpu()
    {
        var panel = new StackPanel();
        _cpuName.Margin = new Thickness(0, 0, 0, 3);
        _cpuName.TextWrapping = TextWrapping.NoWrap;
        _cpuName.Visibility = Visibility.Collapsed;   // shown only in "inside" name mode
        panel.Children.Add(_cpuName);
        panel.Children.Add(StatRow((_cpuFreqCaption, _cpuFreq), Cap("W", _cpuWatts), Cap("°C", _cpuTemp)));
        UpdateFreqCaption();

        // The three graph modes live stacked; only one is visible. The % label overlays the two
        // single-chart modes (in the grid mode each cell shows its own %).
        _cpuGraphHost.Children.Add(_cpuSpark);
        _cpuGraphHost.Children.Add(_cpuCoreSpark);
        _cpuCoreGrid.Columns = _cfg.CpuGraphColumns;
        _cpuGraphHost.Children.Add(_cpuCoreGrid);
        _cpuPct.HorizontalAlignment = HorizontalAlignment.Right;
        _cpuPct.VerticalAlignment = VerticalAlignment.Top;
        _cpuPct.Margin = new Thickness(0, 1, 6, 0);
        _cpuGraphHost.Children.Add(_cpuPct);
        Theme.SetCorePalette(_cfg.CorePalette);
        ApplyCpuGraphMode();
        panel.Children.Add(_cpuGraphHost);

        // The binding-limiter indicator: what (if anything) is holding the boost back right now.
        _cpuThrottle.Margin = new Thickness(0, 3, 0, 0);
        _cpuThrottle.Visibility = Visibility.Collapsed;
        panel.Children.Add(_cpuThrottle);

        // Achieved best-core boost vs the session peak — makes the continuous, temperature-driven
        // boost curve visible even when no hard cap is binding.
        _cpuBoost.Margin = new Thickness(0, 2, 0, 0);
        _cpuBoost.Visibility = Visibility.Collapsed;
        panel.Children.Add(_cpuBoost);

        _cpuExtra.Margin = new Thickness(0, 3, 0, 0);
        _cpuExtra.TextWrapping = TextWrapping.Wrap;   // the limits line can be long; let it flow
        _cpuExtra.Visibility = Visibility.Collapsed;
        panel.Children.Add(_cpuExtra);

        _coreRows.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(_coreRows);
        return panel;
    }

    private void ApplyCpuGraphMode()
    {
        int mode = _cfg.CpuGraphMode;   // 0 total, 1 overlay, 2 per-core grid
        _cpuSpark.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        _cpuCoreSpark.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
        _cpuCoreGrid.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        _cpuPct.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;   // each grid cell has its own %
        _coreRows.UseCoreColors = mode != 0;   // colour the bars by core when a per-core graph is shown
        _coreRows.ShowFreq = _cfg.ShowCoreFreq;
        _coreRows.ShowTemp = _cfg.ShowCoreTemp;
        _coreRows.MetricPos = _cfg.CoreMetricPos;
        _coreRows.MarkSleep = _cfg.MarkSleepCores;
        _coreRows.BarMode = _cfg.CoreBarMode;
    }

    /// <summary>Hover on any sparkline reads the value at that instant; each needs its units.</summary>
    private void ConfigureSparklineHovers()
    {
        static string Pct(float v) => v.ToString("F0", CultureInfo.InvariantCulture) + " %";
        double secs = _cfg.RefreshMs / 1000.0;

        _cpuSpark.Format = Pct; _cpuSpark.SecondsPerSample = secs;
        _cpuSpark.AutoScale = _cfg.CpuGraphAuto; _cpuSpark.MinRange = 10;
        _cpuCoreSpark.AutoScale = _cfg.CpuGraphAuto; _cpuCoreSpark.SecondsPerSample = secs;
        // The per-core grid has its own axis choice: fixed 0..100 (comparable) by default, or its own
        // per-cell autoscale — independent of the global CPU autoscale used by the total/overlay modes.
        _cpuCoreGrid.AutoScale = _cfg.CpuGridAutoScale; _cpuCoreGrid.SecondsPerSample = secs;
        _cpuCoreGrid.ResetColors();   // rebuild cells so the axis choice / spacing take effect

        _gpuSection.SecondsPerSample = EffectiveRefresh("gpu") / 1000.0;
        _gpuSection.AutoScaleLoad = _cfg.GpuGraphAuto;

        _netSpark.Format = v => Theme.Bytes(v, _cfg.NetUnitsBinary);
        _netSpark.LabelA = "DL"; _netSpark.LabelB = "UL"; _netSpark.SecondsPerSample = secs;
        _netSpark.AutoScale = _cfg.NetGraphAuto; _netSpark.MinRange = 4096;   // 4 KiB/s floor

        ApplyGraphHeights();
    }

    /// <summary>Scales every graph's height by the configured multiplier so the user can trade
    /// desktop space for readable detail.</summary>
    private void ApplyGraphHeights()
    {
        // Each graph follows its own override if set, else the global GraphScale.
        double Scale(string k) => _cfg.GraphScales.TryGetValue(k, out double v) ? v : _cfg.GraphScale;
        _cpuSpark.Height = 36 * Scale("cpu");
        _cpuCoreSpark.Height = 48 * Scale("cpu");
        _cpuCoreGrid.ApplyGraphScale(Scale("cpu"));
        _gpuSection.ApplyGraphScale(Scale("gpu"));
        _netSpark.Height = 36 * Scale("net");
        foreach (var b in _diskBlocks) b.Spark.Height = 22 * Scale("disk");
    }

    private void ApplyAutoScale()
    {
        _cpuSpark.AutoScale = _cpuCoreSpark.AutoScale = _cfg.CpuGraphAuto;
        _gpuSection.AutoScaleLoad = _cfg.GpuGraphAuto;
        _netSpark.AutoScale = _cfg.NetGraphAuto;
        foreach (var b in _diskBlocks) b.Spark.AutoScale = _cfg.DiskGraphAuto;
    }

    /// <summary>A section's refresh interval: its own override, or the global rate.</summary>
    private int EffectiveRefresh(string key) =>
        _cfg.SectionRefreshMs.TryGetValue(key, out int ms) ? ms : _cfg.RefreshMs;

    /// <summary>True (and arms the next window) when this section is due for a redraw. Sections
    /// can run at different rates, so the timer ticks at the fastest and each gates itself here.</summary>
    private bool SectionDue(string key)
    {
        if (_sectionLastTick.TryGetValue(key, out long last) &&
            System.Diagnostics.Stopwatch.GetElapsedTime(last).TotalMilliseconds < EffectiveRefresh(key) - 20)
            return false;
        _sectionLastTick[key] = System.Diagnostics.Stopwatch.GetTimestamp();
        return true;
    }

    /// <summary>Ticks the timer at the fastest section rate, and matches each graph's sample spacing
    /// to its section's refresh so the time axis stays honest.</summary>
    private void ApplyRefreshRates()
    {
        int fastest = _cfg.RefreshMs;
        foreach (var sec in _sections) fastest = Math.Min(fastest, EffectiveRefresh(sec.Key));
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, fastest));

        _cpuSpark.SecondsPerSample = _cpuCoreSpark.SecondsPerSample = EffectiveRefresh("cpu") / 1000.0;
        _gpuSection.SecondsPerSample = EffectiveRefresh("gpu") / 1000.0;
        _netSpark.SecondsPerSample = EffectiveRefresh("net") / 1000.0;
        foreach (var b in _diskBlocks) b.Spark.SecondsPerSample = EffectiveRefresh("disk") / 1000.0;
    }

    private UIElement BuildRam()
    {
        var panel = new StackPanel();
        panel.Children.Add(_ramText);
        _ramMeter.Margin = new Thickness(0, 3, 0, 3);
        panel.Children.Add(_ramMeter);
        panel.Children.Add(_commitText);

        // Static module info (model/type/speed) from WMI, filled in once it loads. Hidden until then;
        // a tooltip carries the per-stick breakdown.
        _ramModules.Margin = new Thickness(0, 2, 0, 0);
        _ramModules.TextWrapping = TextWrapping.Wrap;
        _ramModules.Visibility = Visibility.Collapsed;
        _ramModules.ToolTip = Theme.MakeToolTip();
        panel.Children.Add(_ramModules);
        return panel;
    }

    private UIElement BuildGpu()
    {
        _gpuSection.View = _cfg.GpuView;
        _gpuSection.Columns = _cfg.GpuEngineColumns;
        _gpuSection.ShowEngines = _cfg.ShowGpuEngines;
        _gpuSection.GraphScale = _cfg.GraphScale;
        _gpuSection.AutoScaleLoad = _cfg.GpuGraphAuto;
        _gpuSection.SecondsPerSample = EffectiveRefresh("gpu") / 1000.0;
        return _gpuSection;
    }

    private UIElement BuildNet()
    {
        var panel = new StackPanel();
        panel.Children.Add(_netSpark);
        panel.Children.Add(SpacedTop(LegendRow((Theme.SeriesIn, "DL", _netDl), (Theme.SeriesOut, "UL", _netUl)), 3));
        panel.Children.Add(_netPrimary);
        _netProcRows.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(_netProcRows);
        return panel;
    }

    private UIElement BuildDisks() => _diskPanels;

    /// <summary>Physical-disk indices that pass the visibility filters, in order.</summary>
    private List<int> VisibleDisks(ref Snapshot s)
    {
        var list = new List<int>(s.DiskCount);
        for (int i = 0; i < s.DiskCount; i++)
        {
            ref var d = ref s.Disks[i];
            if (_cfg.HideVirtualDisks && d.IsVirtual != 0) continue;
            if (_cfg.HideRemovableDisks && d.IsRemovable != 0) continue;
            if (_cfg.HideSystemDisk && d.IsSystem != 0) continue;
            list.Add(i);
        }
        return list;
    }

    /// <summary>Rebuilt only when the visible set of disks changes, so sparklines keep history.</summary>
    private void SyncDiskBlocks(ref Snapshot s, List<int> visible)
    {
        bool same = _diskBlocks.Count == visible.Count;
        for (int i = 0; same && i < visible.Count; i++)
            same = _diskBlocks[i].Key == NameField.Get(ref s.Disks[visible[i]].Name);
        if (same) return;

        _diskBlocks.Clear();
        _diskPanels.Children.Clear();

        foreach (int di in visible)
        {
            var head = Theme.Text("", 10.5, Theme.InkSecondary);
            var temp = Theme.Text("", 10.5, Theme.InkSecondary, mono: true);
            temp.HorizontalAlignment = HorizontalAlignment.Right;

            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(temp, 1);
            headGrid.Children.Add(head);
            headGrid.Children.Add(temp);

            var volumes = Theme.Text("", 9.5, Theme.InkSecondary);
            var sub = Theme.Text("", 9, Theme.InkMuted);

            var activeText = Theme.Text("", 9.5, Theme.InkSecondary, mono: true);
            var active = new BarMeter(Theme.SeriesCpu) { Margin = new Thickness(0, 2, 0, 2) };

            var spark = new Sparkline(Theme.SeriesIn, Theme.SeriesOut, height: 22 * _cfg.GraphScale)
            {
                Margin = new Thickness(0, 2, 0, 2),
                SecondsPerSample = EffectiveRefresh("disk") / 1000.0,
                Format = v => Theme.Bytes(v, _cfg.DiskUnitsBinary),
                LabelA = "R", LabelB = "W",
                AutoScale = _cfg.DiskGraphAuto,
                MinRange = 65536,   // 64 KiB/s floor
            };
            var rates = Theme.Text("", 9.5, Theme.InkMuted, mono: true);

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            block.Children.Add(headGrid);
            block.Children.Add(volumes);
            block.Children.Add(sub);
            block.Children.Add(activeText);
            block.Children.Add(active);
            block.Children.Add(spark);
            block.Children.Add(rates);
            _diskPanels.Children.Add(block);

            _diskBlocks.Add(new DiskBlock(NameField.Get(ref s.Disks[di].Name), head, temp, volumes, sub, active, activeText, spark, rates));
        }
    }

    private UIElement BuildTop()
    {
        var panel = new StackPanel();

        // Header, because "116 MB" on its own does not say it is the working set.
        var hdr = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        var hName = Theme.Text(Loc.T("proceso"), 9, Theme.InkMuted);
        var hCpu = Theme.Text("CPU", 9, Theme.InkMuted);
        var hMem = Theme.Text("RAM", 9, Theme.InkMuted);
        hCpu.HorizontalAlignment = HorizontalAlignment.Right;
        hMem.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(hCpu, 1);
        Grid.SetColumn(hMem, 2);
        hdr.Children.Add(hName);
        hdr.Children.Add(hCpu);
        hdr.Children.Add(hMem);
        panel.Children.Add(hdr);

        for (int i = 0; i < _topRows.Length; i++)
        {
            var name = Theme.Text("", 10.5, Theme.InkSecondary);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var cpu = Theme.Text("", 10.5, Theme.InkPrimary, mono: true);
            cpu.HorizontalAlignment = HorizontalAlignment.Right;
            var mem = Theme.Text("", 10.5, Theme.InkMuted, mono: true);
            mem.HorizontalAlignment = HorizontalAlignment.Right;
            _topRows[i] = (name, cpu, mem);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            Grid.SetColumn(cpu, 1);
            Grid.SetColumn(mem, 2);
            row.Children.Add(name);
            row.Children.Add(cpu);
            row.Children.Add(mem);
            panel.Children.Add(row);
        }
        panel.Children.Add(SpacedTop(_totals, 3));
        return panel;
    }

    private UIElement BuildGuest(StackPanel rows)
    {
        var panel = new StackPanel();
        var hdr = Theme.Text(Loc.T("nombre            CPU  RAM"), 9, Theme.InkMuted, mono: true);
        hdr.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(hdr);
        panel.Children.Add(rows);
        return panel;
    }

    /// <summary>Polls the guest collector (non-blocking) and renders its latest rows. Only called
    /// while the section is visible+expanded, so Docker/WSL are never spawned unless shown.</summary>
    private void UpdateGuest(GuestCollector col, StackPanel rows, string key, string unit)
    {
        col.Poll();
        var data = col.Latest;
        Find(key).SetSummary(data.Length > 0 ? $"{data.Length} {unit}" : "");

        if (data.Length == 0)
        {
            SyncRows(rows, 1);
            ((TextBlock)rows.Children[0]).Text = col.Available == false ? Loc.T("· no disponible / no responde") : Loc.T("· leyendo…");
            return;
        }

        SyncRows(rows, Math.Min(data.Length, 10));
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < rows.Children.Count; i++)
        {
            var r = data[i];
            string net = r.HasNet
                ? $" ↓{Theme.BytesShort(r.NetRxBps, _cfg.NetUnitsBinary)} ↑{Theme.BytesShort(r.NetTxBps, _cfg.NetUnitsBinary)}"
                : "";
            ((TextBlock)rows.Children[i]).Text = string.Create(ci, $"{Truncate(r.Name, 15),-15} {r.CpuPct,4:F0}% {GuestMem(r.MemBytes),6}{net}");
        }
    }

    private static string GuestMem(ulong b) =>
        b >= (1UL << 30) ? string.Create(CultureInfo.InvariantCulture, $"{b / (double)(1 << 30):F1}G")
                         : string.Create(CultureInfo.InvariantCulture, $"{b / (double)(1 << 20):F0}M");

    private UIElement StatRow(params (TextBlock Caption, TextBlock Value)[] stats)
    {
        // Value and its unit sit on ONE line (e.g. "4.79 GHz máx"), not stacked, with the caption
        // baseline-aligned to the big number. Three such pairs share the width evenly.
        var grid = new UniformGrid { Rows = 1, Columns = stats.Length, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (caption, value) in stats)
        {
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            value.HorizontalAlignment = HorizontalAlignment.Left;
            value.VerticalAlignment = VerticalAlignment.Bottom;
            caption.VerticalAlignment = VerticalAlignment.Bottom;
            caption.Margin = new Thickness(3, 0, 0, 1.5);   // small gap, sat on the number's baseline
            cell.Children.Add(value);
            cell.Children.Add(caption);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static (TextBlock, TextBlock) Cap(string label, TextBlock value) => (Theme.Text(label, 9, Theme.InkMuted), value);

    private void UpdateFreqCaption() =>
        _cpuFreqCaption.Text = _cfg.CpuFreqMode switch { 1 => Loc.T("GHz medio"), 2 => Loc.T("GHz mediana"), _ => Loc.T("GHz máx") };

    /// <summary>Trim the registry CPU string to what fits a title: "AMD Ryzen 7 7800X3D 8-Core
    /// Processor" → "Ryzen 7 7800X3D". Falls back to the raw string if the pattern isn't there.</summary>
    private static string ShortCpuName(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "";
        string s = full.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        foreach (var v in new[] { "AMD ", "Intel ", "Genuine " }) if (s.StartsWith(v, StringComparison.OrdinalIgnoreCase)) s = s[v.Length..];
        var rx = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        // Cut the whole tail at the core-count token ("8-Core Processor", "8 Core"), an APU's
        // "with Radeon Graphics", or a lone "Processor"/"CPU" — whichever comes first.
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+[\s-]?Core.*$", "", rx);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+(with|w/)\s+.*$", "", rx);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+(Processor|CPU)\s*$", "", rx);
        return s.Trim();
    }

    /// <summary>"NVIDIA GeForce RTX 4070 Ti SUPER" → "RTX 4070 Ti SUPER"; "AMD Radeon(TM) Graphics"
    /// → "Radeon Graphics". Keeps it short enough to sit in the title.</summary>
    private static string ShortGpuName(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "";
        string s = full.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        foreach (var v in new[] { "NVIDIA ", "GeForce ", "AMD ", "Intel(R) ", "Intel " }) if (s.StartsWith(v, StringComparison.OrdinalIgnoreCase)) s = s[v.Length..];
        return s.Trim();
    }

    private static UIElement LegendRow(params (Color Color, string Label, TextBlock Value)[] series)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (color, label, value) in series)
        {
            row.Children.Add(new Border
            {
                Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5),
                Background = Theme.SeriesBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            var caption = Theme.Text(label, 10, Theme.InkMuted);
            caption.Margin = new Thickness(0, 0, 4, 0);
            caption.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(caption);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(value);
        }
        return row;
    }

    private static UIElement SpacedTop(UIElement e, double top)
    {
        ((FrameworkElement)e).Margin = new Thickness(0, top, 0, 0);
        return e;
    }

    private static TextBlock Stat()
    {
        var t = Theme.Text("—", 14, Theme.InkPrimary, mono: true);
        t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    private static TextBlock Value() => Theme.Text("", 11, Theme.InkSecondary, mono: true);
    private static TextBlock Muted() => Theme.Text("", 10, Theme.InkMuted, mono: true);

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
        string csv = _csv.IsRunning ? string.Create(ci, $"CSV●{_csv.RowCount}") : "CSV○";
        _debug.Text = string.Create(ci,
            $"{vendor} · {CpuVendor.Brand}\n" +
            $"snap v{SnapshotLayout.Version} · etw v{EtwLayout.Version} · {sdk} · " +
            $"{(s.EtwAvailable ? "helper✓" : "helper✗")} · {_cfg.RefreshMs}ms · age {age.TotalMilliseconds:F0}ms · {csv}\n" +
            $"cfg: temp={_cfg.ShowCoreTemp} bar={_cfg.CoreBarMode} pos={_cfg.CoreMetricPos} w={_cfg.Width} " +
            $"net={(_cfg.NetUnitsBinary ? "bin" : "dec")} dock={_cfg.Docked} cpuGraph={_cfg.CpuGraphMode} pal={_cfg.CorePalette}");
    }

    // ---------------------------------------------------------------- data

    private void Tick()
    {
        if (_reader is null)
        {
            _reader = SnapshotChannel.TryOpenReader(out string? err);
            if (_reader is null)
            {
                TryLaunchAgent();
                _status.Text = err is not null && err.Contains("versi", StringComparison.OrdinalIgnoreCase)
                    ? Loc.T("agente desfasado")
                    : Loc.T("esperando al agente…");
                return;
            }
        }

        if (!_reader.TryRead(out var s)) return;

        // A dead agent leaves a stale map behind; detect it by the timestamp going flat.
        var age = DateTime.UtcNow - new DateTime(s.TimestampUtcTicks, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(10))
        {
            _status.Text = Loc.T("agente parado ({0:F0} s)", age.TotalSeconds);
            return;
        }
        // The elevated helper carries CPU temp/power (AMD SDK), SATA temps and per-process
        // attribution. When it is up we say nothing; when it is down, that is the only thing worth
        // flagging — everything else the unelevated agent covers on its own.
        _status.Text = s.CpuFromAmd ? ""
                     : !s.EtwAvailable ? Loc.T("sin helper (lanza SidebarMonitor.Etw)")
                     : "";

        // Diagnostics run regardless of minimised state: CSV keeps recording while tucked away, and
        // the verbose readout reflects live contract/SDK/helper state.
        if (_csv.IsRunning) _csv.Log(ref s, DateTime.UtcNow);
        if (_cfg.LogVerbose) UpdateDebugOverlay(ref s, age);

        // A minimized panel shows nothing; skip every update but keep the reader warm.
        if (_cfg.Minimized) return;

        var ci = CultureInfo.InvariantCulture;
        ref var c = ref s.Cpu;

        // CPU model name, where the user asked for it: after the title, inside the section, or off.
        string cpuName = NameField.Get(ref c.Name);
        Find("cpu").SetTitleSuffix(_cfg.CpuNameMode == 1 ? ShortCpuName(cpuName) : "");
        if (_cfg.CpuNameMode == 2 && cpuName.Length > 0)
        {
            _cpuName.Text = cpuName;
            _cpuName.Visibility = Visibility.Visible;
        }
        else _cpuName.Visibility = Visibility.Collapsed;

        float freq = _cfg.CpuFreqMode switch { 1 => c.FreqMeanMhz, 2 => c.FreqMedianMhz, _ => c.FreqBestMhz };
        string ghz = float.IsNaN(freq) ? "" : string.Create(ci, $" {freq / 1000:F2}GHz");
        Find("cpu").SetSummary(string.Create(ci, $"{c.TotalUsagePct,3:F0}%{ghz}{W(c.PackagePowerW)}"));
        // Stays live while folded too (like GPU): the graph keeps accumulating so it's already
        // populated when opened. Skipped only when hidden outright or not due this tick.
        if (SectionDue("cpu") && Find("cpu").Visibility == Visibility.Visible)
        {
            _cpuPct.Text = string.Create(ci, $"{c.TotalUsagePct:F0} %");
            _cpuFreq.Text = float.IsNaN(freq) ? "—" : string.Create(ci, $"{freq / 1000:F2}");
            _cpuWatts.Text = float.IsNaN(c.PackagePowerW) ? "—" : string.Create(ci, $"{c.PackagePowerW:F1}");
            _cpuTemp.Text = float.IsNaN(c.TempC) ? "—" : string.Create(ci, $"{c.TempC:F1}");
            // Colour the temperature by how close it is to the SDK's throttle limit (Tjmax/cHTC),
            // and pulse red once it's within a few degrees — you can't miss it about to throttle.
            int tlvl = TempLevel(c.TempC, c.TjMaxC);
            _cpuTemp.Foreground = tlvl == 2 ? Theme.StatusCritical : tlvl == 1 ? Theme.StatusSerious : Theme.InkPrimary;
            SetTempBlink(tlvl == 2);

            // The binding-limiter indicator: which cap (power / current / thermal) is pulling the
            // boost back right now — the "is something throttling?" answer at a glance.
            if (_cfg.ShowThrottle && c.TjMaxC > 0)
            {
                var (ttext, tlevel) = ThrottleState(ref c);
                _cpuThrottle.Text = ttext;
                _cpuThrottle.Foreground = tlevel == 2 ? Theme.StatusCritical : tlevel == 1 ? Theme.StatusSerious : Theme.InkMuted;
                _cpuThrottle.Visibility = Visibility.Visible;
            }
            else _cpuThrottle.Visibility = Visibility.Collapsed;

            // Achieved best-core boost vs its session peak: the temperature-driven boost curve made
            // visible. On a Zen 4 X3D the clock eases down continuously with heat, so the best core
            // sits below its cool-idle peak (e.g. 4.78 vs 5.05) long before any hard cap — this shows
            // exactly that, with no driver. The peak stands in for the rated Fmax the SDK won't hand
            // us dynamically; the true dynamic limit lives in the SMU PM_Table (ring0 only).
            if (_cfg.ShowBoost && !float.IsNaN(c.FreqBestMhz) && c.FreqBestMhz > 0)
            {
                _bestFreqPeakMhz = Math.Max(_bestFreqPeakMhz, c.FreqBestMhz);
                double cur = c.FreqBestMhz / 1000.0, peak = _bestFreqPeakMhz / 1000.0;
                // "mejor núcleo" is only meaningful with the AMD SDK's per-core boost clock. Without it
                // (Intel, or AMD with no helper) FreqBest is just the fastest core by PDH — say so honestly.
                string bestLabel = s.CpuFromAmd ? Loc.T(" (mejor núcleo)") : "";
                _cpuBoost.Text = string.Create(ci, $"boost {cur:F2} / {peak:F2} GHz{bestLabel}");
                _cpuBoost.Visibility = Visibility.Visible;
            }
            else _cpuBoost.Visibility = Visibility.Collapsed;

            // Optional AMD-SDK extras: VID, and the HWiNFO-style "Limits" line (frequency ceiling
            // + PPT/TDC/EDC/thermal usage), plus a thermal-throttle warning when it hits Tjmax.
            // Thermal uses die-avg ≥ Tjmax−3: validated under load, the average tracks the hotspot
            // (Tctl) within ~3°, so that's the real throttle point without needing the Tctl sensor.
            var extra = new List<string>(6);
            bool throttle = c.TjMaxC > 0 && !float.IsNaN(c.TempC) && c.TempC >= c.TjMaxC - 3f;
            if (_cfg.ShowCpuVid && c.VidV > 0) extra.Add(string.Create(ci, $"VID {c.VidV:F3}V"));
            if (_cfg.ShowCpuLimits)
            {
                if (throttle) extra.Add(Loc.T("⚠ throttle térmico"));
                if (c.PptPct > 0) extra.Add(string.Create(ci, $"PPT {c.PptPct:F0}%"));
                if (c.TdcPct > 0) extra.Add(string.Create(ci, $"TDC {c.TdcPct:F0}%"));
                if (c.EdcPct > 0) extra.Add(string.Create(ci, $"EDC {c.EdcPct:F0}%"));
                if (c.TjMaxC > 0 && !float.IsNaN(c.TempC)) extra.Add(Loc.T("térm {0:F0}%", c.TempC / c.TjMaxC * 100));
            }
            _cpuExtra.Text = string.Join("  ·  ", extra);
            _cpuExtra.Foreground = throttle && _cfg.ShowCpuLimits ? Theme.StatusCritical : Theme.InkMuted;
            _cpuExtra.Visibility = extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            switch (_cfg.CpuGraphMode)
            {
                case 1: _cpuCoreSpark.Push(ref s); break;
                case 2: _cpuCoreGrid.Push(ref s); break;
                default: _cpuSpark.Push(c.TotalUsagePct); break;
            }
            _coreRows.Update(ref s);
        }

        bool memBin = _cfg.MemUnitsBinary;
        string mu = Theme.MemUnit(memBin);
        double ramFrac = s.Mem.PhysTotal > 0 ? (double)s.Mem.PhysUsed / s.Mem.PhysTotal : 0;
        Find("ram").SetSummary(string.Create(ci, $"{ramFrac * 100,3:F0}%  {Theme.MemVal(s.Mem.PhysUsed, memBin)}{(memBin ? "G" : "g")}"));
        if (SectionDue("ram") && Find("ram").IsUpdateWorthy())
        {
            _ramText.Text = string.Create(ci, $"{Theme.MemVal(s.Mem.PhysUsed, memBin)} / {Theme.MemVal(s.Mem.PhysTotal, memBin)} {mu}");
            _ramMeter.Update(ramFrac);
            _commitText.Text = string.Create(ci, $"commit {Theme.MemVal(s.Mem.CommitUsed, memBin)} / {Theme.MemVal(s.Mem.CommitTotal, memBin)} {mu}");
        }

        // Primary GPU (Gpus[0]: discrete-first) model name after the GPU title, when asked. Inside
        // the section each GPU block already prints its own name, so mode 2 needs nothing here.
        Find("gpu").SetTitleSuffix(_cfg.GpuNameMode == 1 && s.GpuCount > 0
            ? ShortGpuName(NameField.Get(ref s.Gpus[0].Name)) : "");

        // The GPU section stays live even while folded (not just when expanded): its summary keeps
        // updating and its mini-graphs keep accumulating history, so opening it shows a populated
        // chart instead of an empty one that fills over the next few seconds. Only skip it when the
        // section is hidden outright, or when it isn't due this tick.
        if (s.GpuCount > 0 && SectionDue("gpu") && Find("gpu").Visibility == Visibility.Visible)
        {
            _gpuSection.Update(ref s);
            Find("gpu").SetSummary(_gpuSection.Summary);
        }

        // Only the primary interface — the one the default route uses. The rest (Tailscale,
        // the Hyper-V switches) are noise here. If none is flagged, fall back to the busiest.
        int prim = -1;
        for (int i = 0; i < s.NicCount; i++) if (s.Nics[i].IsPrimary) { prim = i; break; }
        if (prim < 0)
        {
            double best = -1;
            for (int i = 0; i < s.NicCount; i++)
            {
                double t = s.Nics[i].RxBytesPerSec + s.Nics[i].TxBytesPerSec;
                if (t > best) { best = t; prim = i; }
            }
        }

        double dl = prim >= 0 ? s.Nics[prim].RxBytesPerSec : 0;
        double ul = prim >= 0 ? s.Nics[prim].TxBytesPerSec : 0;
        bool netBin = _cfg.NetUnitsBinary;
        Find("net").SetSummary($"↓{Theme.BytesShort(dl, netBin)} ↑{Theme.BytesShort(ul, netBin)}");
        if (SectionDue("net") && Find("net").Visibility == Visibility.Visible)
        {
            _netSpark.Push((float)dl, (float)ul);
            _netDl.Text = Theme.Bytes(dl, netBin);
            _netUl.Text = Theme.Bytes(ul, netBin);

            if (prim >= 0)
            {
                ref var n = ref s.Nics[prim];
                string link = n.LinkBitsPerSec > 0 ? $" · {n.LinkBitsPerSec / 1_000_000:F0} Mbps" : "";
                _netPrimary.Text = $"{NameField.Get(ref n.Name)}{link}";
            }
            else _netPrimary.Text = Loc.T("sin interfaz activa");

            // Per-process breakdown from ETW; without the helper there is nothing to attribute.
            // The row count is fixed (padded with blank rows) so the sections below never shift as
            // the number of talking processes rises and falls — a steady glance, not a jitter.
            int netRows = _cfg.NetProcRows;
            SyncRows(_netProcRows, netRows);
            if (!s.EtwAvailable)
            {
                if (netRows > 0) ((TextBlock)_netProcRows.Children[0]).Text = Loc.T("· ETW para ver el tráfico por proceso");
                for (int i = 1; i < netRows; i++) ((TextBlock)_netProcRows.Children[i]).Text = " ";
            }
            else
            {
                for (int i = 0; i < netRows; i++)
                {
                    if (i < s.NetProcCount)
                    {
                        ref var np = ref s.NetProcs[i];
                        ((TextBlock)_netProcRows.Children[i]).Text =
                            $"{Truncate(NameField.Get(ref np.Name), 15),-15} ↓{Theme.BytesShort(np.RxBytesPerSec, netBin),-7} ↑{Theme.BytesShort(np.TxBytesPerSec, netBin)}";
                    }
                    else
                    {
                        ((TextBlock)_netProcRows.Children[i]).Text = " ";   // blank keeps the line height
                    }
                }
            }
        }

        var visibleDisks = VisibleDisks(ref s);
        double rd = 0, wr = 0, busiest = 0;
        foreach (int di in visibleDisks)
        {
            rd += s.Disks[di].ReadBytesPerSec;
            wr += s.Disks[di].WriteBytesPerSec;
            if (!float.IsNaN(s.Disks[di].ActivePct)) busiest = Math.Max(busiest, s.Disks[di].ActivePct);
        }
        bool diskBin = _cfg.DiskUnitsBinary;
        Find("disk").SetSummary(string.Create(ci, $"{busiest,3:F0}%  R{Theme.BytesShort(rd, diskBin)} W{Theme.BytesShort(wr, diskBin)}"));
        if (SectionDue("disk") && Find("disk").Visibility == Visibility.Visible)
        {
            SyncDiskBlocks(ref s, visibleDisks);
            for (int i = 0; i < visibleDisks.Count && i < _diskBlocks.Count; i++)
            {
                ref var d = ref s.Disks[visibleDisks[i]];
                var b = _diskBlocks[i];

                string label = NameField.Get(ref d.Label);
                string model = NameField.Get(ref d.Model);
                string vols = NameField.Get(ref d.Volumes);

                // A single labelled partition would show the label twice — as the title and again
                // in the volumes line. Merge: the head becomes the richer line (label + letter +
                // used/total), and the separate volumes row disappears. Several partitions (or an
                // unlabelled one) aren't redundant: head is the model, volumes lists the rest.
                if (d.VolumeCount == 1 && label.Length > 0)
                {
                    b.Head.Text = vols.Length > 0 ? vols : label;
                    b.Volumes.Text = "";
                    b.Volumes.Visibility = Visibility.Collapsed;
                }
                else
                {
                    b.Head.Text = model.Length > 0 ? model : label.Length > 0 ? label : NameField.Get(ref d.Name);
                    b.Volumes.Text = vols;
                    b.Volumes.Visibility = vols.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                b.Temp.Text = float.IsNaN(d.TempC) ? "" : string.Create(ci, $"{d.TempC:F0} °C");
                // A spinning disk sits happily at 45-50 °C; alarming there would cry wolf.
                b.Temp.Foreground = d.TempC >= 60 ? Theme.StatusCritical
                                  : d.TempC >= 52 ? Theme.StatusSerious
                                  : Theme.InkSecondary;

                string media = d.Media switch { DiskMedia.Ssd => "SSD", DiskMedia.Hdd => "HDD", _ => "" };
                string size = d.SizeBytes > 0 ? string.Create(ci, $"{d.SizeBytes / 1e12:F1} TB") : "";
                b.Sub.Text = string.Join("  ·  ", new[] { media, NameField.Get(ref d.Bus), size }.Where(x => x.Length > 0));

                float active = float.IsNaN(d.ActivePct) ? 0 : d.ActivePct;
                b.ActiveText.Text = Loc.T("actividad {0:F0} %", active);
                b.Active.Update(active / 100.0);

                b.Spark.Push((float)d.ReadBytesPerSec, (float)d.WriteBytesPerSec);
                b.Rates.Text = Loc.T("R {0,-6} W {1,-6} cola {2:F2}",
                    Theme.BytesShort(d.ReadBytesPerSec, diskBin), Theme.BytesShort(d.WriteBytesPerSec, diskBin), d.QueueLength);
            }
        }

        Find("top").SetSummary(TopSummary(ref s, ci));
        if (SectionDue("top") && Find("top").IsUpdateWorthy())
        {
            int rows = Math.Min(_topRows.Length, s.ProcCount);
            for (int i = 0; i < _topRows.Length; i++)
            {
                if (i >= rows) { _topRows[i].Name.Text = _topRows[i].Cpu.Text = _topRows[i].Mem.Text = ""; continue; }
                ref var p = ref s.Procs[i];
                string name = NameField.Get(ref p.Name);
                // "chrome ×42" says at a glance that this row is 42 processes summed.
                _topRows[i].Name.Text = p.Instances > 1 ? $"{name} ×{p.Instances}" : name;
                _topRows[i].Cpu.Text = string.Create(ci, $"{p.CpuPct:F1}%");
                _topRows[i].Mem.Text = string.Create(ci, $"{p.WorkingSet / (1024.0 * 1024):F0} MB");
            }
            _totals.Text = Loc.T("{0} procesos · {1} threads", s.TotalProcesses, s.TotalThreads);
        }

        if (SectionDue("docker") && Find("docker").IsUpdateWorthy()) UpdateGuest(_docker, _dockerRows, "docker", Loc.T("cont."));
        if (SectionDue("wsl") && Find("wsl").IsUpdateWorthy()) UpdateGuest(_wsl, _wslRows, "wsl", Loc.T("proc."));
    }

    /// <summary>The heaviest process, for the folded header.</summary>
    private static string TopSummary(ref Snapshot s, CultureInfo ci)
    {
        if (s.ProcCount == 0) return "";
        ref var first = ref s.Procs[0];
        string name = NameField.Get(ref first.Name);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.Create(ci, $"{Truncate(name, 12)} {first.CpuPct:F1}%");
    }

    private static string W(float watts) => float.IsNaN(watts) ? "" : string.Create(CultureInfo.InvariantCulture, $" {watts:F0}W");

    /// <summary>CPU temperature severity by nearness to the throttle limit. With the SDK's Tjmax
    /// (cHTC) known, 1 = amber within 12 °C, 2 = red within 4 °C of it; otherwise generic 80/90 °C.</summary>
    private static int TempLevel(float temp, float tjmax)
    {
        if (float.IsNaN(temp)) return 0;
        float hot = tjmax > 0 ? tjmax - 4 : 90;
        float warm = tjmax > 0 ? tjmax - 12 : 80;
        return temp >= hot ? 2 : temp >= warm ? 1 : 0;
    }

    /// <summary>
    /// Which HARD cap is throttling the boost right now, from the AMD SDK: power (PPT), current
    /// (TDC/EDC) or thermal. Level 2 = actively throttled at a cap, 1 = approaching one, 0 = no hard
    /// cap. Note "sin throttle" is NOT "max boost": on a Zen 4 X3D the achievable clock is a
    /// continuous function of temperature that eases down well before any hard cap, so this only
    /// answers "is a hard limit binding", never "is the chip boosting freely". The thermal test uses
    /// die-avg ≥ Tjmax−3: empirically (idle→scalar→AVX2→AVX-512 against HWiNFO's Tctl) the die
    /// average tracks the Tctl hotspot within ~3° under load — exactly when temps near Tjmax — so
    /// that threshold marks the real throttle point without the Tctl sensor we can't read.
    /// </summary>
    private static (string Text, int Level) ThrottleState(ref SidebarMonitor.Shared.CpuInfo c)
    {
        var hard = new List<string>(3);
        var warn = new List<string>(3);
        bool haveTemp = c.TjMaxC > 0 && !float.IsNaN(c.TempC);

        if (c.PptPct >= 99) hard.Add(Loc.T("POT")); else if (c.PptPct >= 95) warn.Add(Loc.T("POT"));
        if (c.TdcPct >= 99 || c.EdcPct >= 99) hard.Add(Loc.T("CORR")); else if (c.TdcPct >= 95 || c.EdcPct >= 95) warn.Add(Loc.T("CORR"));
        if (haveTemp && c.TempC >= c.TjMaxC - 3) hard.Add(Loc.T("TÉRM")); else if (haveTemp && c.TempC >= c.TjMaxC - 7) warn.Add(Loc.T("TÉRM"));

        if (hard.Count > 0) return (Loc.T("throttle: {0}", string.Join("+", hard)), 2);
        if (warn.Count > 0) return (Loc.T("cerca de throttle: {0}", string.Join("+", warn)), 1);
        return (Loc.T("sin throttle"), 0);
    }

    private bool _tempBlink;

    /// <summary>Pulses the CPU temperature's opacity while it's critical (near Tjmax), and stops
    /// cleanly when it drops back.</summary>
    private void SetTempBlink(bool on)
    {
        if (on == _tempBlink) return;
        _tempBlink = on;
        if (on)
        {
            var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(450))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            _cpuTemp.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            _cpuTemp.BeginAnimation(OpacityProperty, null);
            _cpuTemp.Opacity = 1.0;
        }
    }

    private Section Find(string key) => _sections.First(s => s.Key == key);

    private static void SyncRows(StackPanel host, int count)
    {
        while (host.Children.Count < count) host.Children.Add(Muted());
        while (host.Children.Count > count) host.Children.RemoveAt(host.Children.Count - 1);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void TryLaunchAgent()
    {
        if (_ownedAgent is { HasExited: false }) return;
        string path = Path.Combine(AppContext.BaseDirectory, "SidebarMonitor.Agent.exe");
        if (!File.Exists(path)) return;
        try
        {
            _ownedAgent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                Arguments = $"--interval={_cfg.RefreshMs}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* the status line keeps saying "esperando" */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        SaveSections();
        _cfg.Save();
        _csv.Dispose();   // flush and close any open CSV
        if (_ownedAgent is { HasExited: false }) { try { _ownedAgent.Kill(); } catch { } }
        _reader?.Dispose();
        _tray?.Dispose();
        base.OnClosing(e);
        Application.Current?.Shutdown();
    }

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

internal static class SectionExtensions
{
    /// <summary>Folded or hidden sections skip their body updates; the header summary is enough.</summary>
    public static bool IsUpdateWorthy(this Section s) => s.Visibility == Visibility.Visible && s.Expanded;
}
