using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The BATTERY section (laptops; absent on a desktop): charge, what it is doing, and how long that
/// lasts — plus a graph of the power flowing in or out. On battery that power is the whole machine's
/// draw, which no other section can show, and the one number that explains a short runtime.
/// </summary>
internal sealed class BatterySection : StackPanel
{
    public double SecondsPerSample { get => _spark.SecondsPerSample; set => _spark.SecondsPerSample = value; }
    public string Summary { get; private set; } = "";

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private readonly TextBlock _pct;     // "87"
    private readonly TextBlock _state;   // "descargando" / "cargando" / "conectada"
    private readonly BarMeter _meter = new(Theme.SeriesIn) { LowIsBad = true, Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock _flow;    // "−12.4 W · quedan 3 h 20 min"
    private readonly TextBlock _cap;     // "41.2 / 47.3 Wh"
    private readonly Sparkline _spark;   // |W| over time

    public BatterySection()
    {
        _pct = Theme.Text("", 22, Theme.InkPrimary);
        _pct.FontWeight = FontWeights.Bold;
        var unit = Theme.Text(" %", 11, Theme.InkMuted);
        unit.Margin = new Thickness(2, 8, 0, 0);
        _state = Theme.Text("", 11, Theme.InkSecondary);
        _state.Margin = new Thickness(10, 8, 0, 0);

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_pct);
        head.Children.Add(unit);
        head.Children.Add(_state);

        _flow = Theme.Text("", 11, Theme.InkSecondary, mono: true);
        _cap = Theme.Text("", 10, Theme.InkMuted, mono: true);
        _spark = new Sparkline(Theme.SeriesOut, height: 30)
        {
            Margin = new Thickness(0, 4, 0, 0),
            AutoScale = true, MinRange = 2,
            Format = v => v.ToString("F1", Ci) + " W",
        };

        Children.Add(head);
        Children.Add(_meter);
        Children.Add(_flow);
        Children.Add(_cap);
        Children.Add(_spark);
    }

    /// <summary>Applies the graph-height multiplier, like every other chart.</summary>
    public void ApplyGraphScale(double scale) => _spark.Height = 30 * scale;

    public void Update(ref BatteryInfo b)
    {
        bool pctKnown = !float.IsNaN(b.Pct);
        _pct.Text = pctKnown ? b.Pct.ToString("F0", Ci) : "—";
        _meter.Update(pctKnown ? b.Pct / 100.0 : 0);

        string state = StateText(ref b);
        _state.Text = state;
        _state.Foreground = b.Discharging != 0 && pctKnown && b.Pct <= 20 ? Theme.StatusSerious : Theme.InkSecondary;

        var flow = new List<string>(2);
        if (!float.IsNaN(b.RateW)) flow.Add(string.Create(Ci, $"{(b.RateW > 0 ? "+" : b.RateW < 0 ? "−" : "")}{Math.Abs(b.RateW):F1} W"));
        if (b.SecondsLeft >= 0)
            flow.Add(b.Discharging != 0 ? Loc.T("quedan {0}", Duration(b.SecondsLeft))
                                        : Loc.T("llena en ~{0}", Duration(b.SecondsLeft)));
        _flow.Text = string.Join("  ·  ", flow);
        _flow.Visibility = flow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _cap.Text = !float.IsNaN(b.RemainingWh) && !float.IsNaN(b.FullWh)
            ? string.Create(Ci, $"{b.RemainingWh:F1} / {b.FullWh:F1} Wh") : "";
        _cap.Visibility = _cap.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The graph plots the magnitude: in or out is already said in words, and a line that jumps
        // across zero when the charger is plugged in reads as noise.
        _spark.Push(float.IsNaN(b.RateW) ? float.NaN : Math.Abs(b.RateW));

        string head = pctKnown ? b.Pct.ToString("F0", Ci) + "%" : "—";
        Summary = b.Charging != 0 ? head + " ⚡"
                : b.Discharging != 0 && !float.IsNaN(b.RateW) ? string.Create(Ci, $"{head} −{Math.Abs(b.RateW):F0}W")
                : b.AcOnline != 0 ? head + " AC"
                : head;
    }

    private static string StateText(ref BatteryInfo b) =>
        b.Charging != 0 ? Loc.T("cargando")
        : b.Discharging != 0 ? Loc.T("con batería")
        : b.AcOnline != 0 ? (b.Pct >= 99 ? Loc.T("conectada · llena") : Loc.T("conectada"))
        : "";

    /// <summary>"3 h 20 min", "45 min", "&lt;1 min". Minutes are enough: the estimate itself moves by more
    /// than that from one second to the next.</summary>
    internal static string Duration(int seconds)
    {
        if (seconds < 60) return "<1 min";
        int min = seconds / 60;
        return min < 60 ? string.Create(Ci, $"{min} min") : string.Create(Ci, $"{min / 60} h {min % 60:D2} min");
    }
}
