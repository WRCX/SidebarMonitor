using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SidebarMonitor.UI;

/// <summary>
/// Rolling one- or two-series micro-chart. 2px line, translucent fill to the baseline,
/// recessive hairline baseline, chart surface as its own background. No axes: the current
/// value is direct-labeled in the adjacent text row.
/// </summary>
internal sealed class Sparkline : FrameworkElement
{
    private const int Capacity = 120;
    private readonly float[] _a = new float[Capacity];
    private readonly float[]? _b;
    private int _count;

    private readonly Pen _penA;
    private readonly Pen? _penB;
    private readonly Brush _fillA;
    private readonly Brush? _fillB;

    /// <summary>0 = autoscale to the window's max (with headroom).</summary>
    public double FixedMax { get; init; }

    public Sparkline(Color seriesA, Color? seriesB = null, double height = 36)
    {
        Height = height;
        SnapsToDevicePixels = true;

        _penA = MakePen(seriesA);
        _fillA = MakeFill(seriesA);
        if (seriesB is { } b)
        {
            _b = new float[Capacity];
            _penB = MakePen(b);
            _fillB = MakeFill(b);
        }
    }

    private static Pen MakePen(Color c)
    {
        var pen = new Pen(Theme.SeriesBrush(c), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush MakeFill(Color c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    public void Push(float a, float b = float.NaN)
    {
        if (_count == Capacity)
        {
            Array.Copy(_a, 1, _a, 0, Capacity - 1);
            if (_b is not null) Array.Copy(_b, 1, _b, 0, Capacity - 1);
            _count--;
        }
        _a[_count] = float.IsNaN(a) ? 0 : a;
        if (_b is not null) _b[_count] = float.IsNaN(b) ? 0 : b;
        _count++;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRoundedRectangle(Theme.Surface, null, new System.Windows.Rect(0, 0, w, h), 3, 3);
        dc.DrawLine(new Pen(Theme.Baseline, 1), new System.Windows.Point(0, h - 0.5), new System.Windows.Point(w, h - 0.5));
        if (_count < 2) return;

        double max = FixedMax;
        if (max <= 0)
        {
            for (int i = 0; i < _count; i++)
            {
                max = Math.Max(max, _a[i]);
                if (_b is not null) max = Math.Max(max, _b[i]);
            }
            max = Math.Max(max * 1.15, 1e-6);
        }

        Draw(dc, _a, max, w, h, _fillA, _penA);
        if (_b is not null) Draw(dc, _b, max, w, h, _fillB!, _penB!);
    }

    private void Draw(DrawingContext dc, float[] values, double max, double w, double h, Brush fill, Pen pen)
    {
        double dx = w / (Capacity - 1);
        double x0 = w - (_count - 1) * dx;   // history grows from the right edge leftwards
        double Y(int i) => h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 5);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            lc.BeginFigure(new System.Windows.Point(x0, Y(0)), false, false);
            ac.BeginFigure(new System.Windows.Point(x0, h - 1), true, true);
            ac.LineTo(new System.Windows.Point(x0, Y(0)), false, false);
            for (int i = 1; i < _count; i++)
            {
                var p = new System.Windows.Point(x0 + i * dx, Y(i));
                lc.LineTo(p, true, false);
                ac.LineTo(p, true, false);
            }
            ac.LineTo(new System.Windows.Point(x0 + (_count - 1) * dx, h - 1), false, false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Horizontal capacity meter. Turns status-critical near exhaustion, label alongside.</summary>
internal sealed class BarMeter : FrameworkElement
{
    private double _fraction;
    private readonly Brush _fill;

    public BarMeter(Color series) { Height = 6; _fill = Theme.SeriesBrush(series); SnapsToDevicePixels = true; }

    public void Update(double fraction)
    {
        _fraction = Math.Clamp(fraction, 0, 1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        dc.DrawRoundedRectangle(Theme.Grid, null, new System.Windows.Rect(0, 0, w, h), 3, 3);
        Brush fill = _fraction >= 0.90 ? Theme.StatusCritical : _fraction >= 0.80 ? Theme.StatusSerious : _fill;
        if (_fraction > 0.005)
            dc.DrawRoundedRectangle(fill, null, new System.Windows.Rect(0, 0, w * _fraction, h), 3, 3);
    }
}

/// <summary>
/// Collapsible block: clicking the header folds the body, and the header keeps showing a live
/// summary so a folded section still informs. Sections can also be hidden entirely from the
/// window's context menu.
/// </summary>
internal sealed class Section : StackPanel
{
    public string Key { get; }
    public string Title { get; }
    public UIElement Body { get; }
    private readonly TextBlock _arrow;
    private readonly TextBlock _summary;
    public event Action? StateChanged;

    public Section(string key, string title, UIElement body)
    {
        Key = key;
        Title = title;
        Body = body;

        _arrow = Theme.Text("▾", 9, Theme.InkMuted);
        _arrow.Margin = new Thickness(0, 2, 6, 0);
        _summary = Theme.Text("", 11, Theme.InkSecondary, mono: true);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;

        var titleBlock = Theme.Text(title, 11, Theme.InkPrimary);
        titleBlock.FontWeight = FontWeights.SemiBold;

        var headerGrid = new Grid { Margin = new Thickness(8, 5, 8, 3) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_arrow, 0);
        Grid.SetColumn(titleBlock, 1);
        Grid.SetColumn(_summary, 2);
        headerGrid.Children.Add(_arrow);
        headerGrid.Children.Add(titleBlock);
        headerGrid.Children.Add(_summary);

        var header = new Border { Child = headerGrid, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        header.MouseLeftButtonUp += (_, _) => { Expanded = !Expanded; StateChanged?.Invoke(); };

        var bodyHost = new Border { Child = body, Padding = new Thickness(8, 0, 8, 6) };

        Children.Add(header);
        Children.Add(bodyHost);
        Children.Add(new Border { Height = 1, Background = Theme.Grid });
    }

    /// <summary>Reads the body host, which is what the setter collapses — not Body itself.</summary>
    public bool Expanded
    {
        get => Children[1].Visibility == Visibility.Visible;
        set
        {
            Children[1].Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _arrow.Text = value ? "▾" : "▸";
        }
    }

    public void SetSummary(string text) => _summary.Text = text;
}
