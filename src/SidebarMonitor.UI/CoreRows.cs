using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SidebarMonitor.Shared;

// Interop.cs defines its own RECT-shaped `Rect` in this namespace, which shadows WPF's.
using WRect = System.Windows.Rect;

namespace SidebarMonitor.UI;

/// <summary>
/// One row per core: index, a horizontal bar segmented by the processes that own it, the usage
/// percentage, and the dominant process named in text.
///
/// The bar's LENGTH always comes from PDH. The segment WIDTHS come from ETW sample shares, and
/// only subdivide that length. The sampled profiler emits nothing while a core sleeps in a deep
/// C-state, so its counts say who owns the core but never how busy it is — deriving the length
/// from samples would show every idle core at 95%.
///
/// Without the elevated helper there is a single segment in the CPU series colour, which is
/// exactly the old rendering.
/// </summary>
internal sealed class CoreRows : FrameworkElement
{
    private const double RowHeight = 13;
    private const double BarGap = 2;

    private readonly struct Segment(string name, float pct, bool kernel)
    {
        public readonly string Name = name;
        public readonly float Pct = pct;          // share of the core's non-idle samples
        public readonly bool Kernel = kernel;
    }

    private sealed class Row
    {
        public float Usage;
        public Segment[] Segments = [];
        public bool Attributed;
    }

    private Row[] _rows = [];
    private readonly Brush _plainFill = Theme.SeriesBrush(Theme.SeriesCpu);
    private int _hoverRow = -1;

    public CoreRows()
    {
        SnapsToDevicePixels = true;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTip = new ToolTip { Content = "" };
    }

    public void Update(ref Snapshot s)
    {
        int n = s.Cpu.CoreCount;
        if (_rows.Length != n)
        {
            _rows = new Row[n];
            for (int i = 0; i < n; i++) _rows[i] = new Row();
            Height = n * RowHeight;
        }

        for (int i = 0; i < n; i++)
        {
            var row = _rows[i];
            row.Usage = s.Cpu.CoreUsagePct[i];
            row.Attributed = s.EtwAvailable && s.CoreOwnerSamples[i] > 0;

            if (!row.Attributed) { row.Segments = []; continue; }

            ref var owners = ref s.CoreOwners[i];
            var list = new List<Segment>(EtwLayout.TopPerCore);
            for (int k = 0; k < EtwLayout.TopPerCore; k++)
            {
                ref var o = ref owners[k];
                if (o.Pct <= 0.5f) continue;   // below half a percent it is not a visible slice
                list.Add(new Segment(NameField.Get(ref o.Name), o.Pct, o.IsKernel != 0));
            }
            row.Segments = [.. list];
        }

        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = (int)(e.GetPosition(this).Y / RowHeight);
        if (row == _hoverRow || (uint)row >= (uint)_rows.Length) return;

        _hoverRow = row;
        var r = _rows[row];
        string detail = r.Segments.Length == 0
            ? (r.Attributed ? "sin atribucion" : "lanza el helper ETW para ver que proceso lo ocupa")
            : string.Join("\n", r.Segments.Select(sg => $"  {sg.Name}  {sg.Pct:F0} % del core"));

        ((ToolTip)ToolTip).Content = string.Create(CultureInfo.InvariantCulture,
            $"Core {row} — {r.Usage:F1} % de uso\n{detail}");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 0 || _rows.Length == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var monoFace = new Typeface(Theme.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var uiFace = new Typeface(Theme.Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // index | bar | pct | dominant process
        const double indexW = 15, pctW = 34;
        double nameW = Math.Max(48, w * 0.29);
        double barX = indexW + 3;
        double barW = w - indexW - 3 - pctW - nameW - 6;
        if (barW < 20) { barW = Math.Max(10, w * 0.4); nameW = Math.Max(0, w - barX - barW - pctW - 6); }

        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            double y = i * RowHeight;
            double barY = y + BarGap;
            double barH = RowHeight - BarGap * 2;

            // Index and dominant name both wear the dominant process's colour, so the eye links
            // the number, the bar and the label without reading text.
            Brush dominantBrush = row.Segments.Length == 0
                ? Theme.InkMuted
                : row.Segments[0].Kernel ? Theme.KernelFill : Theme.ProcessBrush(row.Segments[0].Name);

            Draw(dc, Text(i.ToString(CultureInfo.InvariantCulture), 9, dominantBrush, monoFace, dpi), indexW, y, barH, rightAlign: true);

            dc.DrawRoundedRectangle(Theme.Grid, null, new WRect(barX, barY, barW, barH), 2, 2);

            double filled = Math.Clamp(row.Usage / 100.0, 0, 1) * barW;
            if (filled >= 1)
            {
                dc.PushClip(new RectangleGeometry(new WRect(barX, barY, filled, barH), 2, 2));

                if (row.Segments.Length == 0)
                {
                    dc.DrawRectangle(_plainFill, null, new WRect(barX, barY, filled, barH));
                }
                else
                {
                    // Shares are of non-idle samples, so they subdivide `filled`, not the track.
                    // A 1px gap between adjacent fills keeps two similar hues from merging.
                    const double gap = 1;
                    double x = barX, covered = 0;
                    foreach (var seg in row.Segments)
                    {
                        double segW = filled * (seg.Pct / 100.0);
                        Brush fill = seg.Kernel ? Theme.KernelFill : Theme.ProcessBrush(seg.Name);
                        dc.DrawRectangle(fill, null, new WRect(x, barY, Math.Max(0.5, segW - gap), barH));
                        x += segW;
                        covered += seg.Pct;
                    }
                    if (covered < 99.5 && x < barX + filled)
                        dc.DrawRectangle(Theme.OtherFill, null, new WRect(x, barY, barX + filled - x, barH));
                }

                dc.Pop();
            }

            Draw(dc, Text(string.Create(CultureInfo.InvariantCulture, $"{row.Usage:F0}%"), 9.5,
                 row.Usage >= 90 ? Theme.InkPrimary : Theme.InkSecondary, monoFace, dpi),
                 barX + barW + pctW + 2, y, barH, rightAlign: true);

            // Direct label in the dominant process's colour: extra visual cue, and identity
            // still never rides on colour alone because the name is right there in text.
            string dominant = row.Segments.Length > 0 ? row.Segments[0].Name : "";
            if (dominant.Length > 0)
            {
                var ft = Text(dominant, 9, dominantBrush, uiFace, dpi);
                ft.MaxTextWidth = Math.Max(10, nameW);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(ft, new Point(barX + barW + pctW + 6, y + (RowHeight - ft.Height) / 2));
            }
        }
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface face, DpiScale dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpi.PixelsPerDip);

    private static void Draw(DrawingContext dc, FormattedText ft, double x, double y, double h, bool rightAlign)
    {
        double tx = rightAlign ? x - ft.Width : x;
        dc.DrawText(ft, new Point(tx, y + (RowHeight - ft.Height) / 2));
    }
}
