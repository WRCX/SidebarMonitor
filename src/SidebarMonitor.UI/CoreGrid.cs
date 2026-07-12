using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The CPU usage graph's third mode: one small sparkline per logical core, laid out in a configurable
/// number of columns (like the GPU engine grid). Each cell is coloured by its core colour (so it
/// matches the index number in the rows below) and labelled with the core index and its live %. A
/// fixed 0..100 axis keeps the cores visually comparable.
/// </summary>
internal sealed class CoreGrid : Grid
{
    private sealed class Cell
    {
        public required TextBlock Label;
        public required Sparkline Spark;
        public required UIElement Root;
    }

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private Cell[] _cells = [];
    private int _coreCount = -1;
    private int _columns = 4;

    public int Columns { get => _columns; set { if (_columns != value) { _columns = value; Relayout(); } } }
    public double GraphScale { get; set; } = 1.0;
    public double SecondsPerSample { get; set; } = 1;
    public bool AutoScale { get; set; }

    /// <summary>Force a rebuild so the cells pick up a new colour palette (their series colour is
    /// baked into each Sparkline at construction).</summary>
    public void ResetColors() { _coreCount = -1; }

    public void Push(ref Snapshot s)
    {
        int n = s.Cpu.CoreCount;
        if (n != _coreCount) Build(n);
        for (int i = 0; i < n && i < _cells.Length; i++)
        {
            float v = s.Cpu.CoreUsagePct[i];
            _cells[i].Spark.Push(v);
            _cells[i].Label.Text = string.Create(Ci, $"{i}·{v,3:F0}%");
        }
    }

    public void ApplyGraphScale(double g)
    {
        GraphScale = g;
        foreach (var c in _cells) c.Spark.Height = 22 * g;
    }

    private void Build(int n)
    {
        _coreCount = n;
        _cells = new Cell[n];
        for (int i = 0; i < n; i++)
        {
            var color = Theme.CoreColor(i);
            var spark = new Sparkline(color, height: 22 * GraphScale)
            {
                FixedMax = AutoScale ? 0 : 100,
                AutoScale = AutoScale,
                ShowAxis = true,   // show the min/max range in the corners, like the other graphs
                MinRange = 10,
                SecondsPerSample = SecondsPerSample,
                Format = v => v.ToString("F0", Ci) + " %",
            };
            var label = Theme.Text("", 8.5, Theme.CoreBrush(i), mono: true);
            label.Text = i.ToString(Ci);
            var box = new StackPanel { Margin = new Thickness(0, 0, 4, 4) };
            box.Children.Add(label);
            box.Children.Add(spark);
            _cells[i] = new Cell { Label = label, Spark = spark, Root = box };
        }
        Relayout();
    }

    private void Relayout()
    {
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        if (_cells.Length == 0) return;

        int cols = Math.Max(1, _columns);
        int rows = (int)Math.Ceiling(_cells.Length / (double)cols);
        for (int c = 0; c < cols; c++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < _cells.Length; i++)
        {
            var e = _cells[i].Root;
            SetColumn(e, i % cols);
            SetRow(e, i / cols);
            Children.Add(e);
        }
    }
}
