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
    private SeqLockReader<Snapshot>? _reader;
    private System.Diagnostics.Process? _ownedAgent;
    private TrayIcon? _tray;

    private readonly List<Section> _sections = [];
    private readonly TextBlock _status;
    private readonly ScrollViewer _body;
    private readonly Border _tab;
    private readonly TextBlock _tabArrow;
    private readonly TextBlock _minButton;
    private readonly Border _titleBar;

    // CPU
    private readonly TextBlock _cpuFreq = Stat();
    private readonly TextBlock _cpuFreqCaption = Theme.Text("GHz", 9, Theme.InkMuted);
    private readonly TextBlock _cpuWatts = Stat();
    private readonly TextBlock _cpuTemp = Stat();
    private readonly TextBlock _cpuPct;
    private readonly Sparkline _cpuSpark = new(Theme.SeriesCpu) { FixedMax = 100 };
    private readonly CoreSparkline _cpuCoreSpark = new(height: 48);
    private readonly Grid _cpuGraphHost = new();
    private readonly CoreRows _coreRows = new();

    // RAM
    private readonly BarMeter _ramMeter = new(Theme.SeriesCpu);
    private readonly TextBlock _ramText = Value();
    private readonly TextBlock _commitText = Muted();

    // GPU
    private readonly TextBlock _gpuWatts = Stat();
    private readonly TextBlock _gpuTemp = Stat();
    private readonly TextBlock _gpuFan = Stat();
    private readonly TextBlock _gpuPct;
    private readonly Sparkline _gpuSpark = new(Theme.SeriesGpu) { FixedMax = 100 };
    private readonly BarMeter _vramMeter = new(Theme.SeriesGpu);
    private readonly TextBlock _vramText = Value();
    private readonly TextBlock _gpuClocks = Muted();

    // NET
    private readonly Sparkline _netSpark = new(Theme.SeriesIn, Theme.SeriesOut);
    private readonly TextBlock _netDl = Value();
    private readonly TextBlock _netUl = Value();
    private readonly TextBlock _netPrimary = Muted();
    private readonly StackPanel _netProcRows = new();

    // DISK
    private readonly StackPanel _diskPanels = new();
    private readonly List<DiskBlock> _diskBlocks = [];

    private sealed record DiskBlock(string Key, TextBlock Head, TextBlock Temp, TextBlock Sub,
                                    BarMeter Active, TextBlock ActiveText, Sparkline Spark, TextBlock Rates);

    // TOP
    private readonly (TextBlock Name, TextBlock Cpu, TextBlock Mem)[] _topRows;
    private readonly TextBlock _totals = Muted();

    public MainWindow(UiConfig cfg, List<(IntPtr, MonitorInfo)> monitors)
    {
        _cfg = cfg;
        _monitors = monitors;

        Title = "SidebarMonitor";
        Background = Theme.Page;

        _cpuPct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
        _cpuPct.FontWeight = FontWeights.Bold;
        _gpuPct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
        _gpuPct.FontWeight = FontWeights.Bold;
        _status = Theme.Text("esperando al agente…", 10, Theme.InkMuted);
        _minButton = Theme.Text("»", 11, Theme.InkMuted);
        _tabArrow = Theme.Text("‹", 12, Theme.InkMuted);
        _topRows = new (TextBlock, TextBlock, TextBlock)[8];

        var stack = new StackPanel();
        _titleBar = BuildTitleBar();
        stack.Children.Add(_titleBar);
        Add(stack, new Section("cpu", "CPU", BuildCpu()));
        Add(stack, new Section("ram", "MEMORIA", BuildRam()));
        Add(stack, new Section("gpu", "GPU", BuildGpu()));
        Add(stack, new Section("net", "RED", BuildNet()));
        Add(stack, new Section("disk", "DISCOS", BuildDisks()));
        Add(stack, new Section("top", "PROCESOS", BuildTop()));

        _body = new ScrollViewer
        {
            Content = stack,
            Background = Theme.Page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };

        _tabArrow.HorizontalAlignment = HorizontalAlignment.Center;
        _tabArrow.VerticalAlignment = VerticalAlignment.Center;
        _tab = new Border { Background = Theme.Page, Child = _tabArrow, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };
        _tab.MouseLeftButtonUp += (_, _) => SetMinimized(false);
        ToolTipService.SetToolTip(_tab, "Abrir SidebarMonitor");

        var root = new Grid();
        root.Children.Add(_body);
        root.Children.Add(_tab);
        Content = root;

        ContextMenu = BuildMenu();
        LoadSections();

        ConfigureSparklineHovers();

        _timer.Interval = TimeSpan.FromMilliseconds(_cfg.RefreshMs);
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            ReapplyPlacement();
            SetClickThrough(_cfg.ClickThrough);
            SetMinimized(_cfg.Minimized);
            Tick();
            _timer.Start();
        };
    }

    public void AttachTray(TrayIcon tray)
    {
        _tray = tray;
        tray.ToggleRequested += () => Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        tray.ConfigRequested += () => { if (ContextMenu is not null) ContextMenu.IsOpen = true; };
        tray.ExitRequested += Close;
    }

    private void Add(StackPanel stack, Section s)
    {
        s.StateChanged += SaveSections;
        _sections.Add(s);
        stack.Children.Add(s);
    }

    // ---------------------------------------------------------------- placement

    private void ReapplyPlacement()
    {
        int index = Math.Clamp(_cfg.Monitor, 0, _monitors.Count - 1);
        ApplyPlacement(_monitors[index].Handle, _cfg.Docked, _cfg.EdgeLeft, _cfg.Width,
                       _cfg.Minimized, _cfg.Topmost, _cfg.FloatX, _cfg.FloatY, _cfg.FloatHeight);
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
        Native.GetCursorPos(out _dragOrigin);
        var r = WindowRect;
        _windowOrigin = (r.Left, r.Top);
        _dragging = true;
        _titleBar.CaptureMouse();
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
        _titleBar.ReleaseMouseCapture();
        var r = WindowRect;
        _cfg.FloatX = r.Left;
        _cfg.FloatY = r.Top;
        _cfg.FloatHeight = r.Height;
        _cfg.Save();
    }

    // ---------------------------------------------------------------- layout

    private Border BuildTitleBar()
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = Theme.Text("SIDEBAR MONITOR", 10, Theme.InkMuted);
        title.FontWeight = FontWeights.SemiBold;

        _status.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_status, 1);

        _minButton.Cursor = Cursors.Hand;
        _minButton.Padding = new Thickness(4, 0, 2, 0);
        _minButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetMinimized(true); };
        ToolTipService.SetToolTip(_minButton, "Minimizar a pestaña");
        Grid.SetColumn(_minButton, 2);

        grid.Children.Add(title);
        grid.Children.Add(_status);
        grid.Children.Add(_minButton);

        var bar = new Border { Child = grid, Background = Brushes.Transparent };
        bar.MouseLeftButtonDown += BeginDrag;
        bar.MouseMove += ContinueDrag;
        bar.MouseLeftButtonUp += EndDrag;

        var panel = new StackPanel();
        panel.Children.Add(bar);
        panel.Children.Add(new Border { Height = 1, Background = Theme.Grid });
        return new Border { Child = panel };
    }

    private UIElement BuildCpu()
    {
        var panel = new StackPanel();
        panel.Children.Add(StatRow((_cpuFreqCaption, _cpuFreq), Cap("W", _cpuWatts), Cap("°C", _cpuTemp)));
        UpdateFreqCaption();

        // Both graphs live stacked; only one is visible. The % label overlays whichever shows.
        _cpuGraphHost.Children.Add(_cpuSpark);
        _cpuGraphHost.Children.Add(_cpuCoreSpark);
        _cpuPct.HorizontalAlignment = HorizontalAlignment.Right;
        _cpuPct.VerticalAlignment = VerticalAlignment.Top;
        _cpuPct.Margin = new Thickness(0, 1, 6, 0);
        _cpuGraphHost.Children.Add(_cpuPct);
        ApplyCpuGraphMode();
        panel.Children.Add(_cpuGraphHost);

        _coreRows.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(_coreRows);
        return panel;
    }

    private void ApplyCpuGraphMode()
    {
        bool perCore = _cfg.CpuPerCoreGraph;
        _cpuSpark.Visibility = perCore ? Visibility.Collapsed : Visibility.Visible;
        _cpuCoreSpark.Visibility = perCore ? Visibility.Visible : Visibility.Collapsed;
        _coreRows.UseCoreColors = perCore;
    }

    /// <summary>Hover on any sparkline reads the value at that instant; each needs its units.</summary>
    private void ConfigureSparklineHovers()
    {
        static string Pct(float v) => v.ToString("F0", CultureInfo.InvariantCulture) + " %";
        double secs = _cfg.RefreshMs / 1000.0;

        _cpuSpark.Format = Pct; _cpuSpark.SecondsPerSample = secs;
        _gpuSpark.Format = Pct; _gpuSpark.SecondsPerSample = secs;

        _netSpark.Format = v => Theme.Bytes(v);
        _netSpark.LabelA = "DL"; _netSpark.LabelB = "UL"; _netSpark.SecondsPerSample = secs;
    }

    private UIElement BuildRam()
    {
        var panel = new StackPanel();
        panel.Children.Add(_ramText);
        _ramMeter.Margin = new Thickness(0, 3, 0, 3);
        panel.Children.Add(_ramMeter);
        panel.Children.Add(_commitText);
        return panel;
    }

    private UIElement BuildGpu()
    {
        var panel = new StackPanel();
        panel.Children.Add(StatRow(Cap("W", _gpuWatts), Cap("°C", _gpuTemp), Cap("fan %", _gpuFan)));
        panel.Children.Add(Overlay(_gpuSpark, _gpuPct));
        panel.Children.Add(SpacedTop(_vramText, 4));
        _vramMeter.Margin = new Thickness(0, 3, 0, 3);
        panel.Children.Add(_vramMeter);
        panel.Children.Add(_gpuClocks);
        return panel;
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

            var sub = Theme.Text("", 9, Theme.InkMuted);

            var activeText = Theme.Text("", 9.5, Theme.InkSecondary, mono: true);
            var active = new BarMeter(Theme.SeriesCpu) { Margin = new Thickness(0, 2, 0, 2) };

            var spark = new Sparkline(Theme.SeriesIn, Theme.SeriesOut, height: 22)
            {
                Margin = new Thickness(0, 2, 0, 2),
                SecondsPerSample = _cfg.RefreshMs / 1000.0,
                Format = v => Theme.Bytes(v),
                LabelA = "R", LabelB = "W",
            };
            var rates = Theme.Text("", 9.5, Theme.InkMuted, mono: true);

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            block.Children.Add(headGrid);
            block.Children.Add(sub);
            block.Children.Add(activeText);
            block.Children.Add(active);
            block.Children.Add(spark);
            block.Children.Add(rates);
            _diskPanels.Children.Add(block);

            _diskBlocks.Add(new DiskBlock(NameField.Get(ref s.Disks[di].Name), head, temp, sub, active, activeText, spark, rates));
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
        var hName = Theme.Text("proceso", 9, Theme.InkMuted);
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

    private UIElement StatRow(params (TextBlock Caption, TextBlock Value)[] stats)
    {
        var grid = new UniformGrid { Rows = 1, Columns = stats.Length, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (caption, value) in stats)
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            value.HorizontalAlignment = HorizontalAlignment.Center;
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(value);
            cell.Children.Add(caption);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static (TextBlock, TextBlock) Cap(string label, TextBlock value) => (Theme.Text(label, 9, Theme.InkMuted), value);

    private void UpdateFreqCaption() =>
        _cpuFreqCaption.Text = _cfg.CpuFreqMode switch { 1 => "GHz medio", 2 => "GHz mediana", _ => "GHz máx" };

    private static UIElement Overlay(Sparkline spark, TextBlock label)
    {
        var grid = new Grid();
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(0, 1, 6, 0);
        grid.Children.Add(spark);
        grid.Children.Add(label);
        return grid;
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

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var sections = new MenuItem { Header = "Secciones" };
        foreach (var s in _sections)
        {
            var item = new MenuItem { Header = s.Title, IsCheckable = true, IsChecked = s.Visibility == Visibility.Visible };
            item.Click += (_, _) =>
            {
                s.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveSections();
            };
            sections.Items.Add(item);
        }
        menu.Items.Add(sections);

        var cpuGraph = new MenuItem { Header = "CPU: vista por core", IsCheckable = true, IsChecked = _cfg.CpuPerCoreGraph };
        cpuGraph.Click += (_, _) =>
        {
            _cfg.CpuPerCoreGraph = cpuGraph.IsChecked;
            ApplyCpuGraphMode();
            _cfg.Save();
        };
        ToolTipService.SetToolTip(cpuGraph, "Vista por core: gráfica de 16 curvas y barras coloreadas por core, proceso al pasar el ratón. Desmarcado: vista por proceso, con las barras segmentadas y el uso total.");
        menu.Items.Add(cpuGraph);

        var freqMode = new MenuItem { Header = "Frecuencia CPU" };
        (int Mode, string Label)[] modes = [(0, "Mejor núcleo"), (1, "Media"), (2, "Mediana")];
        foreach (var (mode, label) in modes)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _cfg.CpuFreqMode == mode };
            item.Click += (_, _) =>
            {
                _cfg.CpuFreqMode = mode;
                foreach (var o in freqMode.Items.OfType<MenuItem>()) o.IsChecked = false;
                item.IsChecked = true;
                UpdateFreqCaption();
                _cfg.Save();
            };
            freqMode.Items.Add(item);
        }
        ToolTipService.SetToolTip(freqMode, "Mejor núcleo muestra el boost que alcanza el core más rápido (p. ej. 5.05 GHz en juegos).");
        menu.Items.Add(freqMode);

        var disks = new MenuItem { Header = "Discos" };
        void DiskFilter(string label, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = get() };
            item.Click += (_, _) =>
            {
                set(item.IsChecked);
                _diskBlocks.Clear();
                _diskPanels.Children.Clear();   // force a rebuild against the new filter
                _cfg.Save();
            };
            disks.Items.Add(item);
        }
        DiskFilter("Ocultar discos virtuales", () => _cfg.HideVirtualDisks, v => _cfg.HideVirtualDisks = v);
        DiskFilter("Ocultar discos extraíbles", () => _cfg.HideRemovableDisks, v => _cfg.HideRemovableDisks = v);
        DiskFilter("Ocultar disco del sistema", () => _cfg.HideSystemDisk, v => _cfg.HideSystemDisk = v);
        menu.Items.Add(disks);

        var refresh = new MenuItem { Header = "Refresco" };
        foreach (int ms in (int[])[500, 1000, 2000, 5000])
        {
            var item = new MenuItem { Header = ms >= 1000 ? $"{ms / 1000} s" : $"{ms} ms", IsCheckable = true, IsChecked = _cfg.RefreshMs == ms };
            item.Click += (_, _) => SetRefresh(ms, refresh);
            refresh.Items.Add(item);
        }
        menu.Items.Add(refresh);

        var place = new MenuItem { Header = "Colocación" };

        var topmost = new MenuItem { Header = "Siempre encima", IsCheckable = true, IsChecked = _cfg.Topmost };
        topmost.Click += (_, _) => { _cfg.Topmost = topmost.IsChecked; ReapplyPlacement(); _cfg.Save(); };
        place.Items.Add(topmost);

        var docked = new MenuItem { Header = "Anclado al borde", IsCheckable = true, IsChecked = _cfg.Docked };
        docked.Click += (_, _) => { _cfg.Docked = docked.IsChecked; ReapplyPlacement(); _cfg.Save(); };
        ToolTipService.SetToolTip(docked, "Anclado reserva espacio: nada lo tapa. Flotante se arrastra por su cabecera.");
        place.Items.Add(docked);

        var left = new MenuItem { Header = "Borde izquierdo", IsCheckable = true, IsChecked = _cfg.EdgeLeft };
        left.Click += (_, _) => { _cfg.EdgeLeft = left.IsChecked; SetMinimized(_cfg.Minimized); };
        place.Items.Add(left);

        var clickThrough = new MenuItem { Header = "Ignorar clics (pasan a través)", IsCheckable = true, IsChecked = _cfg.ClickThrough };
        clickThrough.Click += (_, _) => { _cfg.ClickThrough = clickThrough.IsChecked; SetClickThrough(_cfg.ClickThrough); _cfg.Save(); };
        ToolTipService.SetToolTip(clickThrough, "Los clics atraviesan el panel hacia la ventana de detrás. Útil en flotante. Vuelve a activarlo desde la bandeja.");
        place.Items.Add(clickThrough);

        place.Items.Add(new Separator());
        for (int i = 0; i < _monitors.Count; i++)
        {
            int index = i;
            var mi = _monitors[i].Info;
            var item = new MenuItem
            {
                Header = $"Monitor {i + 1} — {mi.rcMonitor.Width}×{mi.rcMonitor.Height}{(mi.dwFlags == 1 ? " (principal)" : "")}",
                IsCheckable = true,
                IsChecked = _cfg.Monitor == i,
            };
            item.Click += (_, _) =>
            {
                _cfg.Monitor = index;
                foreach (var o in place.Items.OfType<MenuItem>())
                    if (o.Header is string h && h.StartsWith("Monitor")) o.IsChecked = false;
                item.IsChecked = true;
                ReapplyPlacement();
                _cfg.Save();
            };
            place.Items.Add(item);
        }

        place.Items.Add(new Separator());
        foreach (int w in (int[])[240, 280, 320, 360])
        {
            var item = new MenuItem { Header = $"Ancho {w} px", IsCheckable = true, IsChecked = _cfg.Width == w };
            item.Click += (_, _) =>
            {
                _cfg.Width = w;
                foreach (var o in place.Items.OfType<MenuItem>())
                    if (o.Header is string h && h.StartsWith("Ancho")) o.IsChecked = false;
                item.IsChecked = true;
                ReapplyPlacement();
                _cfg.Save();
            };
            place.Items.Add(item);
        }
        menu.Items.Add(place);

        menu.Items.Add(new Separator());
        var minimize = new MenuItem { Header = "Minimizar a pestaña" };
        minimize.Click += (_, _) => SetMinimized(true);
        menu.Items.Add(minimize);

        var hide = new MenuItem { Header = "Ocultar (queda en la bandeja)" };
        hide.Click += (_, _) => Visibility = Visibility.Hidden;
        menu.Items.Add(hide);

        var exit = new MenuItem { Header = "Salir" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>
    /// The refresh rate is the UI's. The agent samples at its own cadence, so when we own the
    /// agent we restart it to match; otherwise we only redraw more or less often and say so.
    /// </summary>
    private void SetRefresh(int ms, MenuItem group)
    {
        _cfg.RefreshMs = ms;
        _timer.Interval = TimeSpan.FromMilliseconds(ms);
        double secs = ms / 1000.0;
        _cpuSpark.SecondsPerSample = _gpuSpark.SecondsPerSample = _netSpark.SecondsPerSample = secs;
        foreach (var b in _diskBlocks) b.Spark.SecondsPerSample = secs;
        foreach (var o in group.Items.OfType<MenuItem>()) o.IsChecked = false;
        foreach (var o in group.Items.OfType<MenuItem>())
            if (o.Header is string h && h == (ms >= 1000 ? $"{ms / 1000} s" : $"{ms} ms")) o.IsChecked = true;

        if (_ownedAgent is { HasExited: false })
        {
            try { _ownedAgent.Kill(); _ownedAgent.WaitForExit(2000); } catch { }
            _ownedAgent = null;
            _reader?.Dispose();
            _reader = null;
            TryLaunchAgent();
        }
        _cfg.Save();
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
                    ? "agente desfasado"
                    : "esperando al agente…";
                return;
            }
        }

        if (!_reader.TryRead(out var s)) return;

        // A dead agent leaves a stale map behind; detect it by the timestamp going flat.
        var age = DateTime.UtcNow - new DateTime(s.TimestampUtcTicks, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(10))
        {
            _status.Text = $"agente parado ({age.TotalSeconds:F0} s)";
            return;
        }
        _status.Text = !s.HwiNfoAvailable ? "sin HWiNFO" : !s.EtwAvailable ? "sin ETW" : "";

        // A minimized panel shows nothing; skip every update but keep the reader warm.
        if (_cfg.Minimized) return;

        var ci = CultureInfo.InvariantCulture;
        ref var c = ref s.Cpu;

        float freq = _cfg.CpuFreqMode switch { 1 => c.FreqMeanMhz, 2 => c.FreqMedianMhz, _ => c.FreqBestMhz };
        string ghz = float.IsNaN(freq) ? "" : string.Create(ci, $" {freq / 1000:F2}GHz");
        Find("cpu").SetSummary(string.Create(ci, $"{c.TotalUsagePct,3:F0}%{ghz}{W(c.PackagePowerW)}"));
        if (Find("cpu").IsUpdateWorthy())
        {
            _cpuPct.Text = string.Create(ci, $"{c.TotalUsagePct:F0} %");
            _cpuFreq.Text = float.IsNaN(freq) ? "—" : string.Create(ci, $"{freq / 1000:F2}");
            _cpuWatts.Text = float.IsNaN(c.PackagePowerW) ? "—" : string.Create(ci, $"{c.PackagePowerW:F1}");
            _cpuTemp.Text = float.IsNaN(c.TempC) ? "—" : string.Create(ci, $"{c.TempC:F1}");
            if (_cfg.CpuPerCoreGraph) _cpuCoreSpark.Push(ref s);
            else _cpuSpark.Push(c.TotalUsagePct);
            _coreRows.Update(ref s);
        }

        double ramFrac = s.Mem.PhysTotal > 0 ? (double)s.Mem.PhysUsed / s.Mem.PhysTotal : 0;
        Find("ram").SetSummary(string.Create(ci, $"{ramFrac * 100,3:F0}%  {Theme.Gib(s.Mem.PhysUsed)}G"));
        if (Find("ram").IsUpdateWorthy())
        {
            _ramText.Text = string.Create(ci, $"{Theme.Gib(s.Mem.PhysUsed)} / {Theme.Gib(s.Mem.PhysTotal)} GiB");
            _ramMeter.Update(ramFrac);
            _commitText.Text = string.Create(ci, $"commit {Theme.Gib(s.Mem.CommitUsed)} / {Theme.Gib(s.Mem.CommitTotal)} GiB");
        }

        if (s.GpuCount > 0)
        {
            ref var g = ref s.Gpus[0];
            Find("gpu").SetSummary(string.Create(ci, $"{g.LoadPct,3:F0}%  {g.CoreClockMhz}MHz{W(g.PowerW)}"));
            if (Find("gpu").IsUpdateWorthy())
            {
                _gpuPct.Text = string.Create(ci, $"{g.LoadPct:F0} %");
                _gpuWatts.Text = string.Create(ci, $"{g.PowerW:F1}");
                _gpuTemp.Text = string.Create(ci, $"{g.TempC:F0}");
                _gpuFan.Text = string.Create(ci, $"{g.FanPct}");
                _gpuSpark.Push(g.LoadPct);
                _vramText.Text = string.Create(ci, $"VRAM {Theme.Gib(g.VramUsed)} / {Theme.Gib(g.VramTotal)} GiB");
                _vramMeter.Update(g.VramTotal > 0 ? (double)g.VramUsed / g.VramTotal : 0);
                _gpuClocks.Text = string.Create(ci, $"{g.CoreClockMhz} / {g.MemClockMhz} MHz   PCIe x{g.PcieWidth}");
            }
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
        Find("net").SetSummary($"↓{Theme.BytesShort(dl)} ↑{Theme.BytesShort(ul)}");
        if (Find("net").IsUpdateWorthy())
        {
            _netSpark.Push((float)dl, (float)ul);
            _netDl.Text = Theme.Bytes(dl);
            _netUl.Text = Theme.Bytes(ul);

            if (prim >= 0)
            {
                ref var n = ref s.Nics[prim];
                string link = n.LinkBitsPerSec > 0 ? $" · {n.LinkBitsPerSec / 1_000_000:F0} Mbps" : "";
                _netPrimary.Text = $"{NameField.Get(ref n.Name)}{link}";
            }
            else _netPrimary.Text = "sin interfaz activa";

            // Per-process breakdown from ETW; without the helper there is nothing to attribute.
            if (s.EtwAvailable)
            {
                SyncRows(_netProcRows, s.NetProcCount);
                for (int i = 0; i < s.NetProcCount; i++)
                {
                    ref var np = ref s.NetProcs[i];
                    ((TextBlock)_netProcRows.Children[i]).Text =
                        $"{Truncate(NameField.Get(ref np.Name), 15),-15} ↓{Theme.BytesShort(np.RxBytesPerSec),-7} ↑{Theme.BytesShort(np.TxBytesPerSec)}";
                }
            }
            else
            {
                SyncRows(_netProcRows, 1);
                ((TextBlock)_netProcRows.Children[0]).Text = "· ETW para ver el tráfico por proceso";
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
        Find("disk").SetSummary(string.Create(ci, $"{busiest,3:F0}%  R{Theme.BytesShort(rd)} W{Theme.BytesShort(wr)}"));
        if (Find("disk").IsUpdateWorthy())
        {
            SyncDiskBlocks(ref s, visibleDisks);
            for (int i = 0; i < visibleDisks.Count && i < _diskBlocks.Count; i++)
            {
                ref var d = ref s.Disks[visibleDisks[i]];
                var b = _diskBlocks[i];

                // A disk with no mounted volume (the WSL vHD) has no label; fall back to its model.
                string label = NameField.Get(ref d.Label);
                if (label.Length == 0) label = NameField.Get(ref d.Model);
                if (label.Length == 0) label = NameField.Get(ref d.Name);
                b.Head.Text = label;

                b.Temp.Text = float.IsNaN(d.TempC) ? "" : string.Create(ci, $"{d.TempC:F0} °C");
                // A spinning disk sits happily at 45-50 °C; alarming there would cry wolf.
                b.Temp.Foreground = d.TempC >= 60 ? Theme.StatusCritical
                                  : d.TempC >= 52 ? Theme.StatusSerious
                                  : Theme.InkSecondary;

                string media = d.Media switch { DiskMedia.Ssd => "SSD", DiskMedia.Hdd => "HDD", _ => "" };
                string size = d.SizeBytes > 0 ? string.Create(ci, $"{d.SizeBytes / 1e12:F1} TB") : "";
                b.Sub.Text = string.Join("  ", new[] { NameField.Get(ref d.Model), media, NameField.Get(ref d.Bus), size }
                    .Where(x => x.Length > 0));

                float active = float.IsNaN(d.ActivePct) ? 0 : d.ActivePct;
                b.ActiveText.Text = string.Create(ci, $"actividad {active:F0} %");
                b.Active.Update(active / 100.0);

                b.Spark.Push((float)d.ReadBytesPerSec, (float)d.WriteBytesPerSec);
                b.Rates.Text = string.Create(ci,
                    $"R {Theme.BytesShort(d.ReadBytesPerSec),-6} W {Theme.BytesShort(d.WriteBytesPerSec),-6} cola {d.QueueLength:F2}");
            }
        }

        Find("top").SetSummary(TopSummary(ref s, ci));
        if (Find("top").IsUpdateWorthy())
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
            _totals.Text = $"{s.TotalProcesses} procesos · {s.TotalThreads} threads";
        }
    }

    /// <summary>The two heaviest processes, if they fit; otherwise just the first.</summary>
    private static string TopSummary(ref Snapshot s, CultureInfo ci)
    {
        if (s.ProcCount == 0) return "";

        static string Short(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

        ref var first = ref s.Procs[0];
        string one = string.Create(ci, $"{Truncate(Short(NameField.Get(ref first.Name)), 9)} {first.CpuPct:F1}%");
        if (s.ProcCount < 2) return one;

        ref var second = ref s.Procs[1];
        string two = string.Create(ci, $"{Truncate(Short(NameField.Get(ref second.Name)), 8)} {second.CpuPct:F1}%");
        string both = $"{one} · {two}";
        return both.Length <= 26 ? both : one;
    }

    private static string W(float watts) => float.IsNaN(watts) ? "" : string.Create(CultureInfo.InvariantCulture, $" {watts:F0}W");

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

    public void ForceHoverForTest() { _cpuSpark.ForceHover(0.5); _netSpark.ForceHover(0.5); }

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
