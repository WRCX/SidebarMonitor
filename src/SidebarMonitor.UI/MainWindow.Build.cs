using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

// Section-building (BuildCpu/Ram/Gpu/Net/Disks/Top/Guest) and the layout helpers, split out of
// MainWindow.cs for readability. Same class (partial).
internal sealed partial class MainWindow
{
    // ---------------------------------------------------------------- layout

    /// <summary>The build's version, "vMajor.Minor.Patch". Read from the <em>file</em> version, not the
    /// assembly version: AssemblyVersion is pinned stable (see Directory.Build.props) so a UI-only
    /// redeploy doesn't break its bind to Shared.dll, while FileVersion still tracks each release.
    /// Shown next to the title so a stale install is spotted immediately.</summary>
    private static string AppVersion
    {
        get
        {
            var attr = typeof(MainWindow).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>();
            if (attr is null) return "";
            var p = attr.Version.Split('.');
            return p.Length >= 3 ? $"v{p[0]}.{p[1]}.{p[2]}" : $"v{attr.Version}";
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
        // Fan (%) sits fixed next to °C for everyone — "—" when the model has no EC map or the opt-in
        // is off. Read from the embedded controller via PawnIO (see EcFan in the helper).
        panel.Children.Add(StatRow((_cpuFreqCaption, _cpuFreq), Cap("W", _cpuWatts), Cap("°C", _cpuTemp), Cap("%vent", _cpuFan)));
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

}
