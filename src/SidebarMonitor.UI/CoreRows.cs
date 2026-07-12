using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        public float FreqMhz = float.NaN;
        public float TempC = float.NaN;
        public float C0 = -1;        // C0 (active) residency %; ~0 = parked/asleep. -1 = unknown.
        public Segment[] Segments = [];
        public bool Attributed;
        public string Detail = "";   // parent + command line of the dominant process (from the helper)
    }

    private Row[] _rows = [];
    private readonly Brush _plainFill = Theme.SeriesBrush(Theme.SeriesCpu);
    private int _hoverRow = -1;

    // Best / second-best physical core (from the AMD SDK) and how many logical rows map to one
    // physical core (SMT), so the star lands on the right row(s).
    private int _bestCore = -1, _secondCore = -1, _threadsPerCore = 1;

    private static readonly Brush StarBest = Frozen(Color.FromRgb(0xF5, 0xC5, 0x18));    // gold
    private static readonly Brush StarSecond = Frozen(Color.FromRgb(0xB9, 0xC2, 0xCE));  // silver
    private static readonly Brush FreqInk = Frozen(Color.FromArgb(0xD8, 0x08, 0x08, 0x08));    // dark, reads on the bright fill
    private static readonly Brush MetricInk = Frozen(Color.FromArgb(0xEC, 0xEA, 0xEA, 0xE2));  // light, for overlay / outside
    private static readonly Brush MetricBack = Frozen(Color.FromArgb(0xA8, 0x12, 0x12, 0x10)); // faint dark backing behind the overlay
    private static readonly Brush SleepInk = Frozen(Color.FromArgb(0x70, 0x89, 0x87, 0x81));   // very faint: a parked core reads as "off"
    private static readonly Brush AwakeTick = Frozen(Color.FromArgb(0xCC, 0xC3, 0xC2, 0xB7));  // the "awake" marker in tick mode
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>C0 residency below this (%) counts as parked/asleep — Ryzen Master's "Sleep".</summary>
    private const float SleepBelow = 3f;

    /// <summary>The freq/temp label for a row, per the toggles: "4.75", "62°", or "4.75 62°".
    /// Reports where the temperature substring sits, so only it can be tinted by heat.</summary>
    private string MetricText(Row r, out int tempStart, out int tempLen)
    {
        tempStart = -1; tempLen = 0;
        string s = "";
        if (ShowFreq && !float.IsNaN(r.FreqMhz) && r.FreqMhz > 0)
            s = (r.FreqMhz / 1000).ToString("F2", CultureInfo.InvariantCulture);
        if (ShowTemp && !float.IsNaN(r.TempC) && r.TempC > 0)
        {
            string t = r.TempC.ToString("F0", CultureInfo.InvariantCulture) + "°";
            if (s.Length > 0) { s += " "; tempStart = s.Length; s += t; }
            else { tempStart = 0; s = t; }
            tempLen = t.Length;
        }
        return s;
    }

    /// <summary>0 normal, 1 warm (within 12° of Tjmax), 2 hot (within 4°). Mirrors the CPU number.</summary>
    private int TempLevel(float temp)
    {
        if (float.IsNaN(temp) || TjMaxC <= 0) return 0;
        return temp >= TjMaxC - 4 ? 2 : temp >= TjMaxC - 12 ? 1 : 0;
    }

    /// <summary>
    /// Two views, one colour language each. Core view (paired with the composite graph): the bar,
    /// the index and the graph line all wear the core's colour; the process is on hover. Process
    /// view: the bar is segmented by process, and the index and name wear the dominant process's
    /// colour. Never both colour systems at once — that was the ambiguity.
    /// </summary>
    public bool UseCoreColors { get; set; }

    /// <summary>Per-core clock / temperature shown on each row.</summary>
    public bool ShowFreq { get; set; } = true;
    public bool ShowTemp { get; set; }
    /// <summary>Thermal limit (Tjmax/cHTC) for tinting per-core temps toward red as they near it.</summary>
    public float TjMaxC { get; set; }
    /// <summary>Where the freq/temp goes: 0 inside the filled part, 1 overlaid at the bar's end
    /// (with a faint backing), 2 outside the bar, before the % (the bar shrinks to make room).</summary>
    public int MetricPos { get; set; } = 1;

    /// <summary>Dim parked cores (C0 ≈ 0) and label them "sleep", like Ryzen Master.</summary>
    public bool MarkSleep { get; set; } = true;
    /// <summary>What the per-core bar shows: 0 = %Util (work, Task-Manager-style), 1 = C0 residency
    /// (awake time, reaches 0 when parked), 2 = combined (C0 faint under the work bar), 3 = work bar
    /// with an "awake" tick. Falls back to %Util when there's no SDK C0 data.</summary>
    public int BarMode { get; set; }

    public CoreRows()
    {
        SnapsToDevicePixels = true;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTip = Theme.MakeToolTip();
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

        _bestCore = s.Cpu.BestCore;
        _secondCore = s.Cpu.SecondCore;
        _threadsPerCore = s.Cpu.PhysicalCores > 0 ? Math.Max(1, n / s.Cpu.PhysicalCores) : 1;
        TjMaxC = s.Cpu.TjMaxC;

        for (int i = 0; i < n; i++)
        {
            var row = _rows[i];
            row.Usage = s.Cpu.CoreUsagePct[i];
            row.FreqMhz = s.Cpu.CoreFreqMhz[i];
            row.TempC = s.Cpu.CoreTempC[i];
            row.C0 = s.Cpu.CoreC0Pct[i];
            row.Attributed = s.EtwAvailable && s.CoreOwnerSamples[i] > 0;
            row.Detail = s.EtwAvailable ? NameField.Get(ref s.CoreDetail[i]) : "";

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
        var ci = CultureInfo.InvariantCulture;

        var tb = Theme.TipBlock();
        tb.Inlines.Add(Theme.TipHead($"Core {row}"));
        tb.Inlines.Add(new Run(string.Create(ci, $"  {r.Usage:F1}% uso")));

        int phys = _threadsPerCore > 0 ? row / _threadsPerCore : row;
        if (_bestCore >= 0 && phys == _bestCore) tb.Inlines.Add(Theme.TipColor("   ★ mejor núcleo", StarBest));
        else if (_secondCore >= 0 && phys == _secondCore) tb.Inlines.Add(Theme.TipColor("   ◆ 2º mejor", StarSecond));

        // Awake-time (C0, shared by the SMT pair) plus this core's clock and temperature.
        var bits = new List<string>(3);
        if (r.C0 >= 0) bits.Add(string.Create(ci, $"C0 (despierto) {r.C0:F0}%"));
        if (!float.IsNaN(r.FreqMhz) && r.FreqMhz > 0) bits.Add(string.Create(ci, $"{r.FreqMhz / 1000:F2} GHz"));
        if (!float.IsNaN(r.TempC) && r.TempC > 0) bits.Add(string.Create(ci, $"{r.TempC:F0}°"));
        if (bits.Count > 0)
        {
            tb.Inlines.Add(new LineBreak());
            tb.Inlines.Add(Theme.TipDim(string.Join("  ·  ", bits)));
        }

        tb.Inlines.Add(new LineBreak());
        if (r.Segments.Length == 0)
        {
            tb.Inlines.Add(Theme.TipDim(r.Attributed ? "sin atribución" : "lanza el helper ETW para ver qué proceso lo ocupa"));
        }
        else
        {
            for (int k = 0; k < r.Segments.Length; k++)
            {
                if (k > 0) tb.Inlines.Add(new LineBreak());
                var sg = r.Segments[k];
                tb.Inlines.Add(new Run($"{sg.Name} "));
                tb.Inlines.Add(Theme.TipDim(string.Create(ci, $"{sg.Pct:F0}%")));
            }
        }

        // Parent + command line of the process that owns most of this core (from the elevated helper).
        if (r.Detail.Length > 0)
            foreach (var line in r.Detail.Split('\n'))
            {
                tb.Inlines.Add(new LineBreak());
                tb.Inlines.Add(Theme.TipDim(line));
            }

        ((ToolTip)ToolTip).Content = tb;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 0 || _rows.Length == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var monoFace = new Typeface(Theme.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var uiFace = new Typeface(Theme.Ui, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // marker (best-core star) | index | bar | [metric] | pct | dominant process
        const double markerW = 11, indexW = 15, pctW = 34;
        double idxRight = markerW + indexW;
        double nameW = Math.Max(48, w * 0.29);
        double barX = idxRight + 3;
        // In "outside" mode the freq/temp gets its own column before the %, so the bar gives it room.
        bool anyMetric = ShowFreq || ShowTemp;
        double metricW = MetricPos == 2 && anyMetric ? (ShowFreq && ShowTemp ? 52 : 30) : 0;
        double barW = w - barX - metricW - pctW - nameW - 6;
        if (barW < 20) { barW = Math.Max(10, w * 0.4); nameW = Math.Max(0, w - barX - barW - metricW - pctW - 6); }

        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            double y = i * RowHeight;
            double barY = y + BarGap;
            double barH = RowHeight - BarGap * 2;

            // Bar mode: 0 = %Util (work), 1 = C0 (awake), 2 = combined (C0 faint under + work solid),
            // 3 = work solid + an "awake" tick. No C0 data → fall back to %Util.
            int mode = row.C0 >= 0 ? BarMode : 0;
            double work = row.Usage;
            double awake = row.C0 >= 0 ? row.C0 : 0;
            bool sleeping = MarkSleep && row.C0 >= 0 && row.C0 < SleepBelow;

            double mainPct = mode == 1 ? awake : work;   // the length (and %) the solid bar measures

            Brush dominantBrush = row.Segments.Length == 0
                ? Theme.InkMuted
                : row.Segments[0].Kernel ? Theme.KernelFill : Theme.ProcessBrush(row.Segments[0].Name);

            // Best / second-best physical core gets a star / diamond before its index. With SMT a
            // physical core spans two logical rows, so both siblings are marked.
            int phys = _threadsPerCore > 0 ? i / _threadsPerCore : i;
            if (_bestCore >= 0 && phys == _bestCore)
                dc.DrawText(Text("★", 8.5, StarBest, uiFace, dpi), new Point(1, y + BarGap - 0.5));
            else if (_secondCore >= 0 && phys == _secondCore)
                dc.DrawText(Text("◆", 7.5, StarSecond, uiFace, dpi), new Point(1.5, y + BarGap));

            Brush indexBrush = sleeping ? SleepInk : UseCoreColors ? Theme.CoreBrush(i) : dominantBrush;
            Draw(dc, Text(i.ToString(CultureInfo.InvariantCulture), 9, indexBrush, monoFace, dpi), idxRight, y, barH, rightAlign: true);

            double filled = Math.Clamp(mainPct / 100.0, 0, 1) * barW;
            double filledAwake = Math.Clamp(awake / 100.0, 0, 1) * barW;
            Brush baseBrush = UseCoreColors ? Theme.CoreBrush(i) : row.Segments.Length == 0 ? _plainFill : dominantBrush;

            // C0 is shared by an SMT pair (both siblings report the same value), so in the C0 and
            // combined modes draw ONE block behind the whole physical core — both sibling rows plus
            // the gap between them. It fills that black gap and shows at a glance the two rows are one
            // core. Combined = faint underlay under each sibling's work bar; C0-only = the solid bar
            // itself. Drawn once at the first sibling, over the group's tracks. tpc==1 (no SMT) →
            // per-row rendering.
            int tpc = Math.Max(1, _threadsPerCore);
            bool grouped = (mode == 1 || mode == 2) && tpc > 1;

            if (!grouped)
                dc.DrawRoundedRectangle(Theme.Grid, null, new WRect(barX, barY, barW, barH), 2, 2);
            else if (i % tpc == 0)
            {
                int last = Math.Min(i + tpc - 1, _rows.Length - 1);
                for (int k = i; k <= last; k++)
                    dc.DrawRoundedRectangle(Theme.Grid, null, new WRect(barX, k * RowHeight + BarGap, barW, barH), 2, 2);
                if (filledAwake >= 1)
                {
                    double gTop = i * RowHeight + BarGap, gBottom = last * RowHeight + BarGap + barH;
                    if (mode == 2) dc.PushOpacity(0.30);   // combined: faint; C0-only: solid
                    dc.DrawRoundedRectangle(baseBrush, null, new WRect(barX, gTop, filledAwake, gBottom - gTop), 2, 2);
                    if (mode == 2) dc.Pop();
                }
            }

            // A parked core's whole bar fades with its text, so the row reads as "off".
            if (sleeping) dc.PushOpacity(0.28);

            // Non-SMT combined: per-row faint underlay (grouped mode already drew the pair block).
            if (mode == 2 && !grouped && filledAwake >= 1)
            {
                dc.PushOpacity(0.30);
                dc.DrawRoundedRectangle(baseBrush, null, new WRect(barX, barY, filledAwake, barH), 2, 2);
                dc.Pop();
            }

            // C0-only grouped: the pair block above already is the bar, so skip the per-row fill.
            if (filled >= 1 && !(mode == 1 && grouped))
            {
                dc.PushClip(new RectangleGeometry(new WRect(barX, barY, filled, barH), 2, 2));

                if (UseCoreColors)
                {
                    // Core view: a single fill in the core's colour, matching its graph line. Load
                    // is the length; who runs on it is on hover, not in colour.
                    dc.DrawRectangle(Theme.CoreBrush(i), null, new WRect(barX, barY, filled, barH));
                }
                else if (row.Segments.Length == 0)
                {
                    dc.DrawRectangle(_plainFill, null, new WRect(barX, barY, filled, barH));
                }
                else
                {
                    // Process view: shares subdivide `filled`. A 1px gap keeps hues from merging.
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

                dc.Pop();   // clip
            }

            // Tick mode: a marker for how far the core was awake, independent of the work bar.
            if (mode == 3)
                dc.DrawRectangle(AwakeTick, null, new WRect(barX + filledAwake - 1, barY - 2, 2, barH + 4));

            if (sleeping) dc.Pop();   // sleep opacity

            // The core's freq/temp. Three placements:
            //   0 inside the filled part (dark on the bright fill; skipped if the fill is too short),
            //   1 overlaid at the bar's end on a faint backing (always visible — the default),
            //   2 in its own column just before the %.
            int tempStart = -1, tempLen = 0;
            string metric = anyMetric ? MetricText(row, out tempStart, out tempLen) : "";
            if (metric.Length > 0)
            {
                // Tint just the temperature digits toward red as the core nears Tjmax — but a parked
                // core reads faint (it's "off"), heat colour and all.
                int tlvl = tempLen > 0 && !sleeping ? TempLevel(row.TempC) : 0;
                Brush? heat = tlvl == 2 ? Theme.StatusCritical : tlvl == 1 ? Theme.StatusSerious : null;
                void Tint(FormattedText ft) { if (heat is not null) ft.SetForegroundBrush(heat, tempStart, tempLen); }
                Brush freqInk = sleeping ? SleepInk : FreqInk;
                Brush metricInk = sleeping ? SleepInk : MetricInk;

                if (MetricPos == 0)
                {
                    var ft = Text(metric, 8, freqInk, monoFace, dpi);
                    Tint(ft);
                    if (filled >= ft.Width + 6)
                        dc.DrawText(ft, new Point(barX + filled - ft.Width - 3, y + (RowHeight - ft.Height) / 2));
                }
                else if (MetricPos == 1)
                {
                    var ft = Text(metric, 8, metricInk, monoFace, dpi);
                    Tint(ft);
                    double tx = barX + barW - ft.Width - 3;
                    dc.DrawRoundedRectangle(MetricBack, null, new WRect(tx - 2, barY, ft.Width + 5, barH), 2, 2);
                    dc.DrawText(ft, new Point(tx, y + (RowHeight - ft.Height) / 2));
                }
                else
                {
                    var ft = Text(metric, 8, metricInk, monoFace, dpi);
                    Tint(ft);
                    dc.DrawText(ft, new Point(barX + barW + metricW - ft.Width - 2, y + (RowHeight - ft.Height) / 2));
                }
            }

            // A parked core reads "sleep" (faint) instead of a usage number; otherwise the activity
            // percentage — which is C0 residency or % Utility, whichever drives the bar.
            double afterBar = barX + barW + metricW;
            FormattedText pctText = sleeping
                ? Text("sleep", 8.5, SleepInk, uiFace, dpi)
                : Text(string.Create(CultureInfo.InvariantCulture, $"{mainPct:F0}%"), 9.5,
                       mainPct >= 90 ? Theme.InkPrimary : Theme.InkSecondary, monoFace, dpi);
            Draw(dc, pctText, afterBar + pctW + 2, y, barH, rightAlign: true);

            // The dominant process's name. In process view it wears the process colour (matches
            // its bar segment); in core view it stays muted so nothing competes with the core hue.
            string dominant = row.Segments.Length > 0 ? row.Segments[0].Name : "";
            if (dominant.Length > 0)
            {
                // A parked core's "dominant process" is just the sliver of work that briefly woke it,
                // so it fades with the rest of the row — no bright process name on an asleep core.
                Brush nameBrush = sleeping ? SleepInk : UseCoreColors ? Theme.InkMuted : dominantBrush;
                var ft = Text(dominant, 9, nameBrush, uiFace, dpi);
                ft.MaxTextWidth = Math.Max(10, nameW);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(ft, new Point(afterBar + pctW + 6, y + (RowHeight - ft.Height) / 2));
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
