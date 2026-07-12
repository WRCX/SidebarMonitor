using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The GAME section: frame-timing for the foreground game, the numbers CapFrameX/PresentMon made
/// standard — current FPS, an FPS-over-time graph, the 1% and 0.1% lows, GPU-busy, latency and
/// animation error (stutter). When frame generation is active (DLSS-FG/FSR-FG) the on-screen
/// (displayed) rate is the headline and the real engine rate is shown honestly beside it. It reads
/// aggregated stats once a second, so it is a near-real-time readout, not a per-frame-synced overlay —
/// which is exactly the right thing on a second monitor (no OLED burn-in, no injection into the game).
/// </summary>
internal sealed class GameSection : StackPanel
{
    public double SecondsPerSample { get => _spark.SecondsPerSample; set => _spark.SecondsPerSample = value; }
    public string Summary { get; private set; } = "";

    private static readonly Color FpsColor = Color.FromRgb(0x37, 0xD6, 0xC0);   // aqua
    private static readonly Brush Dim = Theme.InkMuted;

    private readonly TextBlock _fps;      // headline FPS (big)
    private readonly TextBlock _gen;      // frame-generation reveal (only when active)
    private readonly Sparkline _spark;    // FPS over time
    private readonly TextBlock _lows;     // avg · 1% · 0.1% · frametime
    private readonly TextBlock _extra;    // GPU busy · latency · stutter

    public GameSection()
    {
        Margin = new Thickness(0, 2, 0, 0);

        _fps = Theme.Text("", 22, Theme.InkPrimary);
        _fps.FontWeight = FontWeights.Bold;
        _gen = Theme.Text("", 10.5, Dim, mono: true) ;
        _gen.Margin = new Thickness(0, 0, 0, 2);

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_fps);
        var unit = Theme.Text(" FPS", 11, Dim);
        unit.Margin = new Thickness(2, 8, 0, 0);
        head.Children.Add(unit);

        _spark = new Sparkline(FpsColor, height: 40) { AutoScale = true, ShowAxis = true };
        _lows = Theme.Text("", 11, Theme.InkSecondary, mono: true);
        _lows.Margin = new Thickness(0, 3, 0, 0);
        _extra = Theme.Text("", 11, Dim, mono: true);
        _extra.Margin = new Thickness(0, 1, 0, 0);

        Children.Add(head);
        Children.Add(_gen);
        Children.Add(_spark);
        Children.Add(_lows);
        Children.Add(_extra);
    }

    /// <summary>True while a game is presenting; the caller shows/hides the whole section by this.</summary>
    public bool Update(ref Snapshot s)
    {
        string app = NameField.Get(ref s.Frame.App);
        if (app.Length == 0) return false;

        var f = s.Frame;
        var ci = CultureInfo.InvariantCulture;

        // Frame generation: displayed noticeably above the engine's presented rate.
        bool fg = f.FpsDisplayed > f.FpsPresented * 1.15f && f.FpsDisplayed > 0;
        float headline = fg ? f.FpsDisplayed : f.FpsPresented;
        _fps.Text = headline.ToString("F0", ci);
        _spark.Push(headline);

        if (fg)
        {
            _gen.Visibility = Visibility.Visible;
            _gen.Text = Loc.T("{0} reales · frame-gen", f.FpsPresented.ToString("F0", ci));
        }
        else _gen.Visibility = Visibility.Collapsed;

        _lows.Text = string.Create(ci,
            $"1% {f.Low1PctFps:F0}  ·  0.1% {f.Low01PctFps:F0}  ·  {f.FrametimeMs:F1} ms");

        var extra = new List<string>(3);
        if (!float.IsNaN(f.GpuBusyPct)) extra.Add(string.Create(ci, $"GPU {f.GpuBusyPct:F0}%"));
        if (!float.IsNaN(f.LatencyMs) && f.LatencyMs > 0) extra.Add(string.Create(ci, $"lat {f.LatencyMs:F0} ms"));
        if (!float.IsNaN(f.AnimationErrorMs)) extra.Add(string.Create(ci, $"stutter {f.AnimationErrorMs:F1} ms"));
        _extra.Text = string.Join("  ·  ", extra);
        _extra.Visibility = extra.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Summary = string.Create(ci, $"{headline:F0} fps · 1% {f.Low1PctFps:F0}");
        return true;
    }
}
