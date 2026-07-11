using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SidebarMonitor.Shared;

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
    private readonly Color _colorA;
    private readonly Color? _colorB;

    private double _hoverX = -1;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative };

    /// <summary>0 = autoscale. A fixed value pins the top of the axis to it (used for %, 0..100).</summary>
    public double FixedMax { get; init; }

    /// <summary>
    /// Auto fits the Y axis to the min..max of the visible window (plus a margin), lifting the
    /// baseline off zero so low, flat traffic still fills the chart — the whole point of this
    /// being readable at 36 px. Fixed pins 0..<see cref="FixedMax"/>. Toggled per graph.
    /// </summary>
    public bool AutoScale { get; set; } = true;

    /// <summary>Smallest Y span the auto scale will zoom to, so a flat line doesn't magnify noise.
    /// In the series' own unit (%, or bytes/s).</summary>
    public double MinRange { get; set; } = 1;

    // The bounds actually drawn this frame, eased toward the target so the axis doesn't snap.
    private double _lo, _hi;
    private bool _boundsInit;

    /// <summary>How far apart samples are, for the hover tooltip's "hace N s".</summary>
    public double SecondsPerSample { get; set; } = 1;

    /// <summary>Formats a sample for the tooltip; defaults to a plain number.</summary>
    public Func<float, string> Format { get; set; } = v => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

    public string LabelA { get; set; } = "";
    public string LabelB { get; set; } = "";

    public Sparkline(Color seriesA, Color? seriesB = null, double height = 36)
    {
        Height = height;
        SnapsToDevicePixels = true;

        _colorA = seriesA;
        _penA = MakePen(seriesA);
        _fillA = MakeFill(seriesA);
        if (seriesB is { } b)
        {
            _b = new float[Capacity];
            _colorB = b;
            _penB = MakePen(b);
            _fillB = MakeFill(b);
        }

        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 100);
        ToolTipService.SetBetweenShowDelay(this, 0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        UpdateTip();
        InvalidateVisual();
    }

    /// <summary>Test hook: place the crosshair without a real mouse, for screenshot verification.</summary>
    public void ForceHover(double fraction)
    {
        _hoverX = ActualWidth * fraction;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    private int HoverIndex(double w)
    {
        if (_hoverX < 0 || _count < 2) return -1;
        double dx = w / (Capacity - 1);
        double x0 = w - (_count - 1) * dx;
        int i = (int)Math.Round((_hoverX - x0) / dx);
        return Math.Clamp(i, 0, _count - 1);
    }

    private void UpdateTip()
    {
        int i = HoverIndex(ActualWidth);
        if (i < 0) { _tip.IsOpen = false; return; }

        double ago = (_count - 1 - i) * SecondsPerSample;
        string when = ago < 0.5 ? "ahora" : $"hace {ago:F0} s";
        string body = _b is null
            ? $"{Format(_a[i])}"
            : $"{(LabelA.Length > 0 ? LabelA + " " : "")}{Format(_a[i])}\n{(LabelB.Length > 0 ? LabelB + " " : "")}{Format(_b[i])}";

        _tip.Content = $"{when}\n{body}";
        _tip.HorizontalOffset = _hoverX + 12;
        _tip.VerticalOffset = 4;
        _tip.IsOpen = true;
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

        ComputeBounds(out double lo, out double hi2);

        Draw(dc, _a, lo, hi2, w, h, _fillA, _penA);
        if (_b is not null) Draw(dc, _b, lo, hi2, w, h, _fillB!, _penB!);

        int hoverIdx = HoverIndex(w);
        if (hoverIdx >= 0)
        {
            double dx = w / (Capacity - 1);
            double x = w - (_count - 1) * dx + hoverIdx * dx;
            dc.DrawLine(new Pen(Theme.Grid, 1), new System.Windows.Point(x, 0), new System.Windows.Point(x, h));
            Dot(dc, x, YOf(_a[hoverIdx], lo, hi2, h), _colorA);
            if (_b is not null && _colorB is { } cb) Dot(dc, x, YOf(_b[hoverIdx], lo, hi2, h), cb);
        }
    }

    /// <summary>Target Y bounds, eased toward so the axis glides instead of snapping each tick.</summary>
    private void ComputeBounds(out double lo, out double hi)
    {
        double dLo = double.MaxValue, dHi = double.MinValue;
        for (int i = 0; i < _count; i++)
        {
            dLo = Math.Min(dLo, _a[i]); dHi = Math.Max(dHi, _a[i]);
            if (_b is not null) { dLo = Math.Min(dLo, _b[i]); dHi = Math.Max(dHi, _b[i]); }
        }
        if (dHi < dLo) { dLo = 0; dHi = MinRange; }

        double tLo, tHi;
        if (AutoScale)
        {
            // Fit min..max of the window, lifting the baseline off zero to zoom into the detail.
            double range = Math.Max(dHi - dLo, MinRange);
            double margin = range * 0.18;
            tHi = dHi + margin;
            tLo = Math.Max(0, dLo - margin);
            if (tHi - tLo < MinRange) tHi = tLo + MinRange;
        }
        else if (FixedMax > 0)
        {
            tLo = 0; tHi = FixedMax;                 // %: a fixed 0..100 axis
        }
        else
        {
            tLo = 0; tHi = Math.Max(dHi * 1.15, MinRange);   // zero-anchored to the window peak
        }

        if (!_boundsInit) { _lo = tLo; _hi = tHi; _boundsInit = true; }
        else { _lo += (tLo - _lo) * 0.25; _hi += (tHi - _hi) * 0.25; }   // ~4-tick ease
        lo = _lo; hi = _hi;

        // Keep easing until settled; otherwise a one-shot Push wouldn't finish the glide.
        if (Math.Abs(_lo - tLo) > 1e-4 || Math.Abs(_hi - tHi) > 1e-4)
            Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static double YOf(float v, double lo, double hi, double h)
    {
        double span = hi - lo;
        double t = span > 1e-9 ? (v - lo) / span : 0;
        return h - 1 - Math.Clamp(t, 0, 1) * (h - 5);
    }

    private static void Dot(DrawingContext dc, double x, double y, Color c)
    {
        dc.DrawEllipse(Theme.Surface, new Pen(Theme.SeriesBrush(c), 1.5), new System.Windows.Point(x, y), 2.5, 2.5);
    }

    private void Draw(DrawingContext dc, float[] values, double lo, double hi, double w, double h, Brush fill, Pen pen)
    {
        double dx = w / (Capacity - 1);
        double x0 = w - (_count - 1) * dx;   // history grows from the right edge leftwards
        double Y(int i) => YOf(values[i], lo, hi, h);

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

/// <summary>
/// One line per core on a shared 0..100 scale, each in its own colour (see Theme.CoreColor) so
/// the cores can be told apart and matched to the coloured index numbers in the rows below. No
/// fills — 16 stacked translucent areas would be mud. The total sits on top as a thick white
/// line: the aggregate you actually read, unambiguous against the colour wheel underneath.
/// </summary>
internal sealed class CoreSparkline : FrameworkElement
{
    private const int Capacity = 120;
    private float[][] _cores = [];
    private readonly float[] _total = new float[Capacity];
    private int _count;
    private int _coreCount;

    private readonly List<Pen> _corePens = [];
    private readonly Pen _totalPen;

    private double _lo, _hi;
    private bool _boundsInit;

    /// <summary>Fit the shared axis to the busiest and quietest core in the window, so 16 low
    /// cores spread across the height instead of hugging the baseline. Off = fixed 0..100.</summary>
    public bool AutoScale { get; set; } = true;

    public CoreSparkline(double height = 48)
    {
        Height = height;
        SnapsToDevicePixels = true;

        _totalPen = new Pen(Theme.InkPrimary, 2)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _totalPen.Freeze();
    }

    private Pen CorePen(int index)
    {
        while (_corePens.Count <= index)
        {
            var p = new Pen(Theme.CoreBrush(_corePens.Count), 1) { LineJoin = PenLineJoin.Round };
            p.Freeze();
            _corePens.Add(p);
        }
        return _corePens[index];
    }

    public void Push(ref Snapshot s)
    {
        int n = s.Cpu.CoreCount;
        if (_coreCount != n)
        {
            _coreCount = n;
            _cores = new float[n][];
            for (int i = 0; i < n; i++) _cores[i] = new float[Capacity];
            _count = 0;
        }

        if (_count == Capacity)
        {
            for (int c = 0; c < n; c++) Array.Copy(_cores[c], 1, _cores[c], 0, Capacity - 1);
            Array.Copy(_total, 1, _total, 0, Capacity - 1);
            _count--;
        }

        for (int c = 0; c < n; c++) _cores[c][_count] = s.Cpu.CoreUsagePct[c];
        _total[_count] = s.Cpu.TotalUsagePct;
        _count++;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRoundedRectangle(Theme.Surface, null, new System.Windows.Rect(0, 0, w, h), 3, 3);
        dc.DrawLine(new Pen(Theme.Baseline, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        if (_count < 2) return;

        ComputeBounds(out double lo, out double hi);
        for (int c = 0; c < _coreCount; c++) DrawLine(dc, _cores[c], lo, hi, w, h, CorePen(c));
        DrawLine(dc, _total, lo, hi, w, h, _totalPen);   // white, on top
    }

    private void ComputeBounds(out double lo, out double hi)
    {
        double tLo = 0, tHi = 100;
        if (AutoScale)
        {
            double dLo = double.MaxValue, dHi = double.MinValue;
            for (int c = 0; c < _coreCount; c++)
                for (int i = 0; i < _count; i++) { dLo = Math.Min(dLo, _cores[c][i]); dHi = Math.Max(dHi, _cores[c][i]); }
            for (int i = 0; i < _count; i++) { dLo = Math.Min(dLo, _total[i]); dHi = Math.Max(dHi, _total[i]); }
            if (dHi < dLo) { dLo = 0; dHi = 10; }
            double range = Math.Max(dHi - dLo, 8);   // never zoom past an 8-point span
            double margin = range * 0.15;
            tHi = Math.Min(100, dHi + margin);
            tLo = Math.Max(0, dLo - margin);
        }

        if (!_boundsInit) { _lo = tLo; _hi = tHi; _boundsInit = true; }
        else { _lo += (tLo - _lo) * 0.25; _hi += (tHi - _hi) * 0.25; }
        lo = _lo; hi = _hi;

        if (Math.Abs(_lo - tLo) > 1e-4 || Math.Abs(_hi - tHi) > 1e-4)
            Dispatcher.BeginInvoke(InvalidateVisual, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void DrawLine(DrawingContext dc, float[] v, double lo, double hi, double w, double h, Pen pen)
    {
        double dx = w / (Capacity - 1);
        double x0 = w - (_count - 1) * dx;
        double span = hi - lo;
        double Y(int i) => h - 1 - Math.Clamp(span > 1e-9 ? (v[i] - lo) / span : 0, 0, 1) * (h - 5);

        var line = new StreamGeometry();
        using (var lc = line.Open())
        {
            lc.BeginFigure(new Point(x0, Y(0)), false, false);
            for (int i = 1; i < _count; i++) lc.LineTo(new Point(x0 + i * dx, Y(i)), true, false);
        }
        line.Freeze();
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
