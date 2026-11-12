using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The GPU section, rebuilt to hold more than one GPU. A selector chooses the discrete card, the
/// integrated GPU, or both; each shown GPU gets its name, load graph, (for the NVIDIA card) the
/// temp/power/fan/VRAM/clock readouts, and a grid of per-engine mini-graphs — 3D, compute, video
/// decode/encode, copy — in a configurable number of columns, System-Informer-style. An adapter we
/// only see through the vendor-neutral counter (the iGPU) has no rich telemetry, so its block is
/// just name + load + engines.
/// </summary>
internal sealed class GpuSection : StackPanel
{
    public int View { get; set; } = 2;            // 0 = discrete, 1 = integrated, 2 = both
    public int Columns { get; set; } = 4;
    public bool ShowEngines { get; set; } = true;
    public double GraphScale { get; set; } = 1.0;
    public double SecondsPerSample { get; set; } = 1.0;
    public bool AutoScaleLoad { get; set; } = true;

    /// <summary>The header summary for the collapsed section, e.g. "38%  2610MHz 210W".</summary>
    public string Summary { get; private set; } = "";

    // Engines always shown, so the grid has a stable default even at idle. The rest appear once
    // they've done real work and then stay, so the layout grows but never dances.
    private static readonly int[] BaseEngines = [GpuEngines.Idx3D, GpuEngines.IdxCompute, GpuEngines.IdxDecode, GpuEngines.IdxEncode];

    private sealed class EngineCell
    {
        public readonly int Slot;
        public readonly Border Root;
        public readonly TextBlock Label;
        public readonly Sparkline Spark;

        public EngineCell(int slot, double height, double secs)
        {
            Slot = slot;
            Label = Theme.Text(GpuEngines.Names[slot], 8.5, Theme.InkMuted, mono: true);
            Label.TextTrimming = TextTrimming.CharacterEllipsis;   // never spill the star cell
            Spark = new Sparkline(Theme.SeriesGpu, height: height) { FixedMax = 100, AutoScale = false, SecondsPerSample = secs };
            var p = new StackPanel { Margin = new Thickness(0, 0, 4, 3) };
            p.Children.Add(Label);
            p.Children.Add(Spark);
            Root = new Border { Child = p };
        }

        public void Push(float v)
        {
            Label.Text = string.Create(CultureInfo.InvariantCulture, $"{GpuEngines.Names[Slot]} {v:F0}%");
            Spark.Push(v);
        }
    }

    private sealed class Block
    {
        public int GpuIndex;
        public bool HasDetail;
        public readonly Border Root = new();
        public readonly TextBlock Header;
        public readonly TextBlock Pct;
        public readonly ProcSparkline Load;
        public readonly TextBlock? Watts, Temp, Fan, Vram, Clocks;
        /// <summary>Who's using this GPU: a coloured dot per process, matching its line and segment.</summary>
        public readonly TextBlock Legend;
        public readonly BarMeter? VramMeter;
        /// <summary>This tick's processes and their colours, for the VRAM bar's tooltip, built only when
        /// it opens.</summary>
        public GpuProcRanking.Row[] Procs = [];
        public ProcessColorMap Colors = ProcessColorMap.Empty;
        // A Grid with star columns, not a UniformGrid: star columns divide the panel width exactly,
        // so the cells never grow to their content and spill the mini-graphs off the sidebar edge.
        public readonly Grid EngineGrid = new() { Margin = new Thickness(0, 3, 0, 0) };
        public readonly Dictionary<int, EngineCell> Cells = [];

        public Block(int gpuIndex, bool hasDetail, double graphScale, double secs, bool autoLoad, bool showEngines, int columns)
        {
            GpuIndex = gpuIndex;
            HasDetail = hasDetail;

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

            Header = Theme.Text("", 10.5, Theme.InkSecondary);
            Header.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(Header);

            Pct = Theme.Text("", 16, Theme.InkPrimary, mono: true);
            Pct.FontWeight = FontWeights.Bold;
            Pct.HorizontalAlignment = HorizontalAlignment.Right;
            Pct.VerticalAlignment = VerticalAlignment.Top;
            Pct.Margin = new Thickness(0, 1, 6, 0);
            // The driver's own load in the tooltip, so a different figure in Afterburner has an answer.
            Load = new ProcSparkline(Theme.SeriesGpu, height: 36 * graphScale) { AutoScale = autoLoad, MinRange = 10, SecondsPerSample = secs, AuxLabel = "driver" };
            var loadHost = new Grid();
            loadHost.Children.Add(Load);
            loadHost.Children.Add(Pct);
            panel.Children.Add(loadHost);

            if (hasDetail)
            {
                Watts = Stat(); Temp = Stat(); Fan = Stat();
                panel.Children.Add(StatRow(("W", Watts), ("°C", Temp), ("fan %", Fan)));

                Vram = Theme.Text("", 11, Theme.InkSecondary, mono: true);
                Vram.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(Vram);
                // The panel's own tooltip, with only its content swapped on open: assigning the content
                // straight to ToolTip got WPF's default one — a white box with the dark-theme text.
                var vramTip = Theme.MakeToolTip();
                VramMeter = new BarMeter(Theme.SeriesGpu) { Margin = new Thickness(0, 3, 0, 3), ToolTip = vramTip };
                ToolTipService.SetInitialShowDelay(VramMeter, 100);
                VramMeter.ToolTipOpening += (_, _) => vramTip.Content = VramTip(Procs, Colors);
                panel.Children.Add(VramMeter);
                Clocks = Theme.Text("", 10, Theme.InkMuted, mono: true);
                panel.Children.Add(Clocks);
            }

            // Wraps rather than trims: a name cut to "Sandf…" no longer says whose colour it is.
            Legend = Theme.Text("", 10, Theme.InkMuted, mono: true);
            Legend.Margin = new Thickness(0, 3, 0, 0);
            Legend.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(Legend);

            if (showEngines)
            {
                panel.Children.Add(EngineGrid);
                foreach (int slot in BaseEngines) EnsureCell(slot, graphScale, secs);
                Relayout(columns);
            }

            Root.Child = panel;
        }

        public void EnsureCell(int slot, double graphScale, double secs)
        {
            if (Cells.ContainsKey(slot)) return;
            Cells[slot] = new EngineCell(slot, 20 * graphScale, secs);
        }

        /// <summary>Re-adds the cells to the grid in engine order, at the requested column count.
        /// Star columns keep every cell at exactly panelWidth/columns, so nothing overflows.</summary>
        public void Relayout(int columns)
        {
            int cols = Math.Max(1, columns);
            var cells = Cells.Values.OrderBy(c => c.Slot).ToList();
            int rows = (cells.Count + cols - 1) / cols;

            EngineGrid.Children.Clear();
            EngineGrid.ColumnDefinitions.Clear();
            EngineGrid.RowDefinitions.Clear();
            for (int c = 0; c < cols; c++)
                EngineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int r = 0; r < rows; r++)
                EngineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int k = 0; k < cells.Count; k++)
            {
                Grid.SetColumn(cells[k].Root, k % cols);
                Grid.SetRow(cells[k].Root, k / cols);
                EngineGrid.Children.Add(cells[k].Root);
            }
        }

        /// <summary>Every process on the VRAM bar, biggest first, with the GPU-visible system RAM it
        /// also holds (which on a card is memory that did not fit in VRAM).</summary>
        private static object VramTip(GpuProcRanking.Row[] procs, ProcessColorMap colors)
        {
            var tb = Theme.TipBlock();
            tb.Inlines.Add(Theme.TipHead(Loc.T("VRAM por proceso")));
            foreach (var p in procs.Where(p => p.Local >= GpuProcRanking.MinBytes).OrderByDescending(p => p.Local))
            {
                string name = Theme.NormalizeProcess(p.Name);
                tb.Inlines.Add(new System.Windows.Documents.LineBreak());
                tb.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = colors.For(name) });
                string ram = p.NonLocal >= GpuProcRanking.MinBytes ? $"  +{Size(p.NonLocal)} RAM" : "";
                tb.Inlines.Add(new System.Windows.Documents.Run($"{name}  {Size(p.Local)}{ram}"));
            }
            return tb;
        }

        private static TextBlock Stat()
        {
            var t = Theme.Text("—", 13, Theme.InkPrimary, mono: true);
            t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        private static UIElement StatRow(params (string Caption, TextBlock Value)[] stats)
        {
            // Value and its unit on one line ("53.6 W"), baseline-aligned — matching the CPU section.
            var grid = new UniformGrid { Rows = 1, Columns = stats.Length, Margin = new Thickness(0, 4, 0, 0) };
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
                var cap = Theme.Text(caption, 9, Theme.InkMuted);
                cap.VerticalAlignment = VerticalAlignment.Bottom;
                cap.Margin = new Thickness(3, 0, 0, 1.5);
                cell.Children.Add(value);
                cell.Children.Add(cap);
                grid.Children.Add(cell);
            }
            return grid;
        }
    }

    private readonly List<Block> _blocks = [];
    private string _layoutKey = "";

    /// <summary>The GPU indices to show for the current view, discrete before integrated.</summary>
    private List<int> ShownIndices(ref Snapshot s)
    {
        var discrete = new List<int>();
        var integrated = new List<int>();
        for (int i = 0; i < s.GpuCount; i++)
            (s.Gpus[i].IsIntegrated != 0 ? integrated : discrete).Add(i);

        return View switch
        {
            0 => discrete.Count > 0 ? [discrete[0]] : integrated,          // fall back if there's no discrete GPU
            1 => integrated.Count > 0 ? [integrated[0]] : discrete,
            _ => [.. discrete, .. integrated],
        };
    }

    public void Update(ref Snapshot s)
    {
        var shown = ShownIndices(ref s);

        // Rebuild the blocks only when the shown set (or a block's detail availability) changes.
        var keyBuilder = new System.Text.StringBuilder($"{View}|{ShowEngines}|");
        foreach (int i in shown) keyBuilder.Append(i).Append(':').Append(s.Gpus[i].HasDetail).Append(',');
        string key = keyBuilder.ToString();
        if (key != _layoutKey)
        {
            _layoutKey = key;
            Children.Clear();
            _blocks.Clear();
            for (int b = 0; b < shown.Count; b++)
            {
                int gi = shown[b];
                var block = new Block(gi, s.Gpus[gi].HasDetail != 0, GraphScale, SecondsPerSample, AutoScaleLoad, ShowEngines, Columns);
                _blocks.Add(block);
                if (b > 0) Children.Add(new Border { Height = 1, Background = Theme.Grid, Margin = new Thickness(0, 4, 0, 6) });
                Children.Add(block.Root);
            }
        }

        var ci = CultureInfo.InvariantCulture;
        Summary = "";
        for (int b = 0; b < _blocks.Count; b++)
        {
            var block = _blocks[b];
            ref var g = ref s.Gpus[block.GpuIndex];

            string name = NameField.Get(ref g.Name);
            block.Header.Text = g.IsIntegrated != 0 ? $"{name}  · iGPU" : name;
            block.Pct.Text = float.IsNaN(g.LoadPct) ? "—" : string.Create(ci, $"{g.LoadPct:F0} %");

            var procs = new GpuProcRanking.Row[Math.Clamp(g.ProcCount, 0, SnapshotLayout.MaxGpuProcs)];
            for (int k = 0; k < procs.Length; k++)
            {
                ref var p = ref g.Procs[k];
                procs[k] = new(NameField.Get(ref p.Name), p.Pct, p.VramLocal, p.VramNonLocal);
            }
            block.Procs = procs;
            // One assignment for the line, the bar segment, the legend dot and the tooltip, all
            // different from each other: busiest first keeps its usual hue.
            var colors = Theme.DistinctProcessColors(procs.Select(p => p.Name));
            block.Colors = colors;

            block.Load.Push(float.IsNaN(g.LoadPct) ? 0 : g.LoadPct, procs.Select(p => (p.Name, p.Pct)).ToArray(), g.DriverLoadPct, colors);
            block.Load.AutoScale = AutoScaleLoad;

            if (block.HasDetail && block.Watts is not null)
            {
                block.Watts.Text = float.IsNaN(g.PowerW) ? "—" : string.Create(ci, $"{g.PowerW:F1}");
                block.Temp!.Text = float.IsNaN(g.TempC) ? "—" : string.Create(ci, $"{g.TempC:F0}");
                block.Fan!.Text = g.FanPct < 0 ? "—" : string.Create(ci, $"{g.FanPct}");
                block.Vram!.Text = string.Create(ci, $"VRAM {Theme.Gib(g.VramUsed)} / {Theme.Gib(g.VramTotal)} GiB");
                // Split by process, biggest holder first; the unowned rest (driver, desktop, the
                // processes below the cut) is the grey tail.
                if (g.VramTotal > 0)
                {
                    double total = g.VramTotal;
                    var segs = procs.Where(p => p.Local >= GpuProcRanking.MinBytes)
                                    .OrderByDescending(p => p.Local)
                                    .Select(p => (colors.For(p.Name), p.Local / total))
                                    .ToArray();
                    block.VramMeter!.Update(g.VramUsed / total, segs);
                }
                else block.VramMeter!.Update(0);
                // An iGPU has no PCIe link of its own and reports width 0 — "PCIe x0" said nothing.
                string pcie = g.PcieWidth > 0 ? string.Create(ci, $"   PCIe x{g.PcieWidth}") : "";
                block.Clocks!.Text = string.Create(ci, $"{g.CoreClockMhz} / {g.MemClockMhz} MHz{pcie}");
            }

            UpdateLegend(block.Legend, procs, block.HasDetail, colors);

            if (ShowEngines)
            {
                bool grew = false;
                for (int e = 0; e < GpuEngines.Count; e++)
                {
                    // A non-base engine earns a cell the first time it does real work, then keeps it.
                    if (!block.Cells.ContainsKey(e) && g.Engines[e] > 0.5f)
                    {
                        block.EnsureCell(e, GraphScale, SecondsPerSample);
                        grew = true;
                    }
                }
                if (grew) block.Relayout(Columns);

                foreach (var cell in block.Cells.Values) cell.Push(g.Engines[cell.Slot]);
            }

            if (b == 0)
                Summary = float.IsNaN(g.LoadPct) ? "—" : string.Create(ci, $"{g.LoadPct,3:F0}%{(block.HasDetail ? string.Create(ci, $"  {g.CoreClockMhz}MHz{(float.IsNaN(g.PowerW) ? "" : string.Create(ci, $" {g.PowerW:F0}W"))}") : "")}");
        }
    }

    /// <summary>Most entries the legend lists; the rest are in the chart's and the bar's tooltips.</summary>
    private const int LegendMax = 4;

    /// <summary>
    /// "● Sandfall 97% 7.8G  ● chrome 2% 412M": who is using the GPU, each dot in the colour of that
    /// process's line and VRAM segment. Busy processes first; the VRAM is listed only where there is a
    /// VRAM bar for it to explain (the card, not the iGPU).
    /// </summary>
    private static void UpdateLegend(TextBlock legend, GpuProcRanking.Row[] procs, bool withVram, ProcessColorMap colors)
    {
        legend.Inlines.Clear();
        int n = 0;
        foreach (var p in procs)
        {
            bool busy = p.Pct >= GpuProcRanking.MinPct;
            bool holds = withVram && p.Local >= GpuProcRanking.MinBytes;
            if (!busy && !holds) continue;
            if (n++ == LegendMax) break;

            string name = Theme.NormalizeProcess(p.Name);   // no ".exe", like every other list
            if (n > 1) legend.Inlines.Add(new System.Windows.Documents.Run("  "));
            legend.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = colors.For(name) });
            string pct = busy ? string.Create(CultureInfo.InvariantCulture, $" {p.Pct:F0}%") : "";
            string vram = holds ? " " + Size(p.Local) : "";
            legend.Inlines.Add(new System.Windows.Documents.Run(name + pct + vram));
        }
    }

    /// <summary>"7.8G" from a gigabyte up, "412M" below it — compact enough for the legend row.</summary>
    private static string Size(ulong bytes) => bytes >= 1UL << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):F1}G")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes >> 20}M");

    /// <summary>Applies a column-count change without recreating blocks (keeps graph history).</summary>
    public void ApplyColumns(int columns)
    {
        Columns = columns;
        foreach (var block in _blocks) block.Relayout(columns);
    }

    /// <summary>Applies graph-height scaling to every sparkline in place.</summary>
    public void ApplyGraphScale(double scale)
    {
        GraphScale = scale;
        foreach (var block in _blocks)
        {
            block.Load.Height = 36 * scale;
            foreach (var cell in block.Cells.Values) cell.Spark.Height = 20 * scale;
        }
    }
}
