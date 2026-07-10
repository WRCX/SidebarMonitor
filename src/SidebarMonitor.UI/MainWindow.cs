using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

internal sealed class MainWindow : AppBarWindow
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SidebarMonitor", "ui.json");

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SnapshotReader? _reader;
    private System.Diagnostics.Process? _ownedAgent;

    private readonly List<Section> _sections = [];
    private readonly TextBlock _status;

    // CPU
    private readonly TextBlock _cpuFreq = Stat();
    private readonly TextBlock _cpuWatts = Stat();
    private readonly TextBlock _cpuTemp = Stat();
    private readonly TextBlock _cpuPct;
    private readonly Sparkline _cpuSpark = new(Theme.SeriesCpu) { FixedMax = 100 };
    private readonly CoreBars _coreBars = new();

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

    // NET / DISK
    private readonly Sparkline _netSpark = new(Theme.SeriesIn, Theme.SeriesOut);
    private readonly TextBlock _netDl = Value();
    private readonly TextBlock _netUl = Value();
    private readonly StackPanel _nicRows = new();
    private readonly Sparkline _diskSpark = new(Theme.SeriesIn, Theme.SeriesOut, height: 28);
    private readonly StackPanel _diskRows = new();

    // TOP
    private readonly (TextBlock Name, TextBlock Cpu, TextBlock Mem)[] _topRows;
    private readonly TextBlock _totals = Muted();

    public MainWindow(IntPtr monitor, uint edge, int width) : base(monitor, edge, width)
    {
        Title = "SidebarMonitor";
        Background = Theme.Page;

        _cpuPct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
        _cpuPct.FontWeight = FontWeights.Bold;
        _gpuPct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
        _gpuPct.FontWeight = FontWeights.Bold;
        _status = Theme.Text("esperando al agente…", 10, Theme.InkMuted);
        _topRows = new (TextBlock, TextBlock, TextBlock)[8];

        var stack = new StackPanel();
        stack.Children.Add(BuildTitleBar());
        Add(stack, new Section("cpu", "CPU", BuildCpu()));
        Add(stack, new Section("ram", "MEMORIA", BuildRam()));
        Add(stack, new Section("gpu", "GPU", BuildGpu()));
        Add(stack, new Section("net", "RED", BuildNet()));
        Add(stack, new Section("disk", "DISCOS", BuildDisks()));
        Add(stack, new Section("top", "PROCESOS", BuildTop()));

        Content = new ScrollViewer
        {
            Content = stack,
            Background = Theme.Page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };

        ContextMenu = BuildMenu();
        LoadState();

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { Tick(); _timer.Start(); };
    }

    private void Add(StackPanel stack, Section s)
    {
        s.StateChanged += SaveState;
        _sections.Add(s);
        stack.Children.Add(s);
    }

    // ---------------------------------------------------------------- layout

    private UIElement BuildTitleBar()
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 8, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Theme.Text("SIDEBAR MONITOR", 10, Theme.InkMuted);
        title.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(_status, 1);
        grid.Children.Add(title);
        grid.Children.Add(_status);

        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(new Border { Height = 1, Background = Theme.Grid });
        return panel;
    }

    private UIElement BuildCpu()
    {
        var panel = new StackPanel();
        panel.Children.Add(StatRow(("GHz", _cpuFreq), ("W", _cpuWatts), ("°C", _cpuTemp)));
        panel.Children.Add(Overlay(_cpuSpark, _cpuPct));
        _coreBars.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(_coreBars);
        return panel;
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
        panel.Children.Add(StatRow(("W", _gpuWatts), ("°C", _gpuTemp), ("fan %", _gpuFan)));
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
        _nicRows.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(_nicRows);
        return panel;
    }

    private UIElement BuildDisks()
    {
        var panel = new StackPanel();
        panel.Children.Add(_diskSpark);
        _diskRows.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(_diskRows);
        return panel;
    }

    private UIElement BuildTop()
    {
        var panel = new StackPanel();
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

    private static UIElement StatRow(params (string Label, TextBlock Value)[] stats)
    {
        var grid = new UniformGrid { Rows = 1, Columns = stats.Length, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (label, value) in stats)
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var caption = Theme.Text(label, 9, Theme.InkMuted);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(value);
            cell.Children.Add(caption);
            grid.Children.Add(cell);
        }
        return grid;
    }

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

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        foreach (var s in _sections)
        {
            var item = new MenuItem { Header = ((TextBlock)((Grid)((Border)s.Children[0]).Child).Children[1]).Text, IsCheckable = true, IsChecked = true };
            item.Click += (_, _) =>
            {
                s.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveState();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Salir" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);
        return menu;
    }

    // ---------------------------------------------------------------- data

    private void Tick()
    {
        if (_reader is null)
        {
            _reader = SnapshotReader.TryOpen(out _);
            if (_reader is null)
            {
                TryLaunchAgent();
                _status.Text = "esperando al agente…";
                return;
            }
        }

        if (!_reader.TryRead(out var s)) return;

        // A dead agent leaves a stale map behind; detect it by the timestamp going flat.
        var age = DateTime.UtcNow - new DateTime(s.TimestampUtcTicks, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(10))
        {
            _status.Text = $"agente sin publicar ({age.TotalSeconds:F0} s)";
            return;
        }
        _status.Text = s.HwiNfoAvailable ? "" : "sin HWiNFO";

        var ci = CultureInfo.InvariantCulture;
        ref var c = ref s.Cpu;

        Find("cpu").SetSummary(string.Create(ci, $"{c.TotalUsagePct,3:F0}%  {W(c.PackagePowerW)}"));
        if (Find("cpu").IsUpdateWorthy())
        {
            _cpuPct.Text = string.Create(ci, $"{c.TotalUsagePct:F0} %");
            _cpuFreq.Text = float.IsNaN(c.FrequencyMhz) ? "—" : string.Create(ci, $"{c.FrequencyMhz / 1000:F2}");
            _cpuWatts.Text = float.IsNaN(c.PackagePowerW) ? "—" : string.Create(ci, $"{c.PackagePowerW:F1}");
            _cpuTemp.Text = float.IsNaN(c.TempC) ? "—" : string.Create(ci, $"{c.TempC:F1}");
            _cpuSpark.Push(c.TotalUsagePct);
            Span<float> cores = stackalloc float[c.CoreCount];
            for (int i = 0; i < c.CoreCount; i++) cores[i] = c.CoreUsagePct[i];
            _coreBars.Update(cores);
        }

        double ramFrac = s.Mem.PhysTotal > 0 ? (double)s.Mem.PhysUsed / s.Mem.PhysTotal : 0;
        Find("ram").SetSummary(string.Create(ci, $"{ramFrac * 100,3:F0}%"));
        if (Find("ram").IsUpdateWorthy())
        {
            _ramText.Text = string.Create(ci, $"{Theme.Gib(s.Mem.PhysUsed)} / {Theme.Gib(s.Mem.PhysTotal)} GiB");
            _ramMeter.Update(ramFrac);
            _commitText.Text = string.Create(ci, $"commit {Theme.Gib(s.Mem.CommitUsed)} / {Theme.Gib(s.Mem.CommitTotal)} GiB");
        }

        if (s.GpuCount > 0)
        {
            ref var g = ref s.Gpus[0];
            Find("gpu").SetSummary(string.Create(ci, $"{g.LoadPct,3:F0}%  {W(g.PowerW)}"));
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

        double dl = 0, ul = 0;
        for (int i = 0; i < s.NicCount; i++) { dl += s.Nics[i].RxBytesPerSec; ul += s.Nics[i].TxBytesPerSec; }
        Find("net").SetSummary($"↓{Theme.BytesShort(dl)} ↑{Theme.BytesShort(ul)}");
        if (Find("net").IsUpdateWorthy())
        {
            _netSpark.Push((float)dl, (float)ul);
            _netDl.Text = Theme.Bytes(dl);
            _netUl.Text = Theme.Bytes(ul);
            SyncRows(_nicRows, s.NicCount);
            for (int i = 0; i < s.NicCount; i++)
            {
                ref var n = ref s.Nics[i];
                ((TextBlock)_nicRows.Children[i]).Text =
                    $"{Truncate(NameField.Get(ref n.Name), 16),-16} ↓{Theme.BytesShort(n.RxBytesPerSec),-7} ↑{Theme.BytesShort(n.TxBytesPerSec)}";
            }
        }

        double rd = 0, wr = 0;
        for (int i = 0; i < s.DiskCount; i++) { rd += s.Disks[i].ReadBytesPerSec; wr += s.Disks[i].WriteBytesPerSec; }
        Find("disk").SetSummary($"R {Theme.BytesShort(rd)}  W {Theme.BytesShort(wr)}");
        if (Find("disk").IsUpdateWorthy())
        {
            _diskSpark.Push((float)rd, (float)wr);
            SyncRows(_diskRows, s.DiskCount);
            for (int i = 0; i < s.DiskCount; i++)
            {
                ref var d = ref s.Disks[i];
                ((TextBlock)_diskRows.Children[i]).Text =
                    $"{Truncate(NameField.Get(ref d.Name), 14),-14} R {Theme.BytesShort(d.ReadBytesPerSec),-7} W {Theme.BytesShort(d.WriteBytesPerSec)}";
            }
        }

        Find("top").SetSummary($"{s.TotalProcesses}p");
        if (Find("top").IsUpdateWorthy())
        {
            int rows = Math.Min(_topRows.Length, s.ProcCount);
            for (int i = 0; i < _topRows.Length; i++)
            {
                if (i >= rows) { _topRows[i].Name.Text = _topRows[i].Cpu.Text = _topRows[i].Mem.Text = ""; continue; }
                ref var p = ref s.Procs[i];
                _topRows[i].Name.Text = NameField.Get(ref p.Name);
                _topRows[i].Cpu.Text = string.Create(ci, $"{p.CpuPct:F1}%");
                _topRows[i].Mem.Text = string.Create(ci, $"{p.WorkingSet / (1024.0 * 1024):F0} MB");
            }
            _totals.Text = $"{s.TotalProcesses} procesos · {s.TotalThreads} threads";
        }
    }

    private static string W(float watts) => float.IsNaN(watts) ? "" : string.Create(CultureInfo.InvariantCulture, $"{watts,5:F1}W");

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
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* the status line keeps saying "esperando" */ }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        SaveState();
        if (_ownedAgent is { HasExited: false }) { try { _ownedAgent.Kill(); } catch { } }
        _reader?.Dispose();
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- state

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var state = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(StatePath));
            if (state is null) return;
            foreach (var s in _sections)
            {
                if (!state.TryGetValue(s.Key, out int v)) continue;
                s.Visibility = v == 0 ? Visibility.Collapsed : Visibility.Visible;
                s.Expanded = v == 2;
                if (ContextMenu is not null)
                {
                    int idx = _sections.IndexOf(s);
                    if (ContextMenu.Items[idx] is MenuItem mi) mi.IsChecked = v != 0;
                }
            }
        }
        catch { /* first run or corrupt state: defaults */ }
    }

    private void SaveState()
    {
        try
        {
            var state = _sections.ToDictionary(
                s => s.Key,
                s => s.Visibility != Visibility.Visible ? 0 : s.Expanded ? 2 : 1);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch { /* non-fatal */ }
    }

    // ---------------------------------------------------------------- proof

    /// <summary>Test hook: fold or hide sections by key, exercising the same paths as clicking.</summary>
    public void Apply(string[] collapse, string[] hide)
    {
        foreach (var s in _sections)
        {
            if (collapse.Contains(s.Key)) s.Expanded = false;
            if (hide.Contains(s.Key)) s.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Walks the visual tree and reports every TextBlock's text, size and brush.</summary>
    public void DumpText()
    {
        Console.WriteLine($"{"texto",-30} {"w",6} {"h",6}  {"fg",-10} {"parent"}");
        Walk(this, 0);

        static void Walk(DependencyObject o, int depth)
        {
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(o, i);
                if (child is TextBlock tb)
                {
                    string fg = tb.Foreground is SolidColorBrush sb ? sb.Color.ToString() : tb.Foreground?.ToString() ?? "null";
                    string txt = tb.Text.Length > 28 ? tb.Text[..28] : tb.Text;
                    Console.WriteLine($"{'"' + txt + '"',-30} {tb.ActualWidth,6:F0} {tb.ActualHeight,6:F0}  {fg,-10} {VisualTreeHelper.GetParent(tb)?.GetType().Name}");
                }
                Walk(child, depth + 1);
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
