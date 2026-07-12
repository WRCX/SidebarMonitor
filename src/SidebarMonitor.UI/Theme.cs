using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace SidebarMonitor.UI;

/// <summary>
/// Dark-mode slots of the dataviz reference palette, used unmodified on its reference dark
/// surface. Series colors follow the entity (CPU=blue, GPU=violet, in=aqua, out=orange) and
/// every series is direct-labeled in text, so identity never rides on color alone.
/// </summary>
internal static class Theme
{
    public static readonly Brush Page = Freeze("#0D0D0D");        // window plane
    public static readonly Brush Surface = Freeze("#1A1A19");     // chart surface
    public static readonly Brush InkPrimary = Freeze("#FFFFFF");
    public static readonly Brush InkSecondary = Freeze("#C3C2B7");
    public static readonly Brush InkMuted = Freeze("#898781");
    public static readonly Brush Grid = Freeze("#2C2C2A");        // hairlines, meter tracks
    public static readonly Brush Baseline = Freeze("#383835");

    public static readonly Color SeriesCpu = (Color)ColorConverter.ConvertFromString("#3987E5");   // blue
    public static readonly Color SeriesGpu = (Color)ColorConverter.ConvertFromString("#9085E9");   // violet
    public static readonly Color SeriesIn = (Color)ColorConverter.ConvertFromString("#199E70");    // aqua: DL, disk read
    public static readonly Color SeriesOut = (Color)ColorConverter.ConvertFromString("#D95926");   // orange: UL, disk write

    // Status is state, never a series. Meters switch to these near exhaustion, always
    // alongside the numeric label.
    public static readonly Brush StatusSerious = Freeze("#EC835A");
    public static readonly Brush StatusCritical = Freeze("#D03B3B");

    /// <summary>
    /// Categorical slots of the dataviz reference palette, dark steps. Used for per-core
    /// process segments. Never cycled by rank: a process keeps its colour whatever position it
    /// holds this second, otherwise the bars would flicker as the ranking churns.
    /// </summary>
    /// <remarks>
    /// Blue is deliberately absent: it is the CPU series colour used by the sparkline right
    /// above these bars, and a process landing on it would read as "the CPU series".
    /// </remarks>
    private static readonly Color[] ProcessPalette =
    [
        (Color)ColorConverter.ConvertFromString("#199E70"),   // aqua
        (Color)ColorConverter.ConvertFromString("#C98500"),   // yellow
        (Color)ColorConverter.ConvertFromString("#9085E9"),   // violet
        (Color)ColorConverter.ConvertFromString("#E66767"),   // red
        (Color)ColorConverter.ConvertFromString("#D55181"),   // magenta
        (Color)ColorConverter.ConvertFromString("#D95926"),   // orange
        (Color)ColorConverter.ConvertFromString("#008300"),   // green
    ];

    /// <summary>The kernel is not a series. It gets a reserved neutral, never a palette slot.</summary>
    public static readonly Brush KernelFill = Freeze("#898781");

    private static readonly Dictionary<int, Color> CoreColorCache = [];

    /// <summary>Active per-core colour preset: 0 rainbow, 1 high-contrast, 2 cool, 3 warm, 4 pastel.</summary>
    public static int CorePalette { get; private set; }

    /// <summary>Switch the per-core palette and drop the caches so every core widget recolours. The
    /// widgets that cache pens/brushes (CoreSparkline, CoreGrid) reset separately; CoreRows reads
    /// CoreBrush live and only needs an InvalidateVisual.</summary>
    public static void SetCorePalette(int id)
    {
        if (id == CorePalette) return;
        CorePalette = id;
        CoreColorCache.Clear();   // CoreBrush derives from CoreColor, so this recolours everything
    }

    /// <summary>
    /// A distinct colour per core index. 16 cores are 16 forced categories that cannot fold into
    /// "other", so this is a legitimate generated ramp. Five presets trade off identity vs harmony:
    /// an even HSL wheel (rainbow), a golden-angle spread (max contrast between neighbours), and
    /// hue-limited cool/warm/pastel schemes. The offset keeps core 0 off pure red (a status colour).
    /// </summary>
    public static Color CoreColor(int index)
    {
        if (CoreColorCache.TryGetValue(index, out var cached)) return cached;
        double alt = index % 2 == 0 ? 0 : 1;   // small lightness alternation to separate neighbours
        Color c = CorePalette switch
        {
            1 => FromHsl((index * 137.5 + 25) % 360, 0.72, 0.55 + alt * 0.12),          // high-contrast (golden angle)
            2 => FromHsl(180 + index * 120.0 / 15.0, 0.58, 0.60 + alt * 0.06),          // cool: cyan→blue→violet
            3 => FromHsl((300 + index * 150.0 / 15.0) % 360, 0.66, 0.58 + alt * 0.06),  // warm: magenta→red→orange→yellow
            4 => FromHsl((index * 360.0 / 16.0 + 25) % 360, 0.42, 0.72),                // pastel
            _ => FromHsl((index * 360.0 / 16.0 + 25) % 360, 0.68, 0.62),                // rainbow (default)
        };
        CoreColorCache[index] = c;
        return c;
    }

    /// <summary>Friendly names for the palette picker.</summary>
    public static readonly string[] CorePaletteNames = [Loc.T("Arcoíris"), Loc.T("Contraste"), Loc.T("Fríos"), Loc.T("Cálidos"), Loc.T("Pastel")];

    public static Brush CoreBrush(int index) => SeriesBrush(CoreColor(index));

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>
    /// Whatever falls outside the top-3 segments. Recessive, but it must never be mistaken for
    /// the empty track (#2C2C2A) behind it, nor for the kernel's lighter neutral.
    /// </summary>
    public static readonly Brush OtherFill = Freeze("#575651");

    private static readonly Dictionary<string, SolidColorBrush> ProcessBrushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stable name -> colour. FNV-1a so the same process lands on the same hue across ticks,
    /// across cores, and across restarts.
    /// </summary>
    public static Brush ProcessBrush(string name)
    {
        if (ProcessBrushes.TryGetValue(name, out var cached)) return cached;

        uint hash = 2166136261;
        foreach (char ch in name)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= 16777619;
        }

        var brush = new SolidColorBrush(ProcessPalette[hash % (uint)ProcessPalette.Length]);
        brush.Freeze();
        return ProcessBrushes[name] = brush;
    }

    public static readonly FontFamily Ui = new("Segoe UI");
    public static readonly FontFamily Mono = new("Consolas");

    /// <summary>
    /// A slim dark scrollbar (translucent rounded thumb, no arrow buttons, transparent track) to
    /// replace the classic grey Windows one, which clashes with the dark surface. Parsed from XAML
    /// once — a full ScrollBar template is impractical to hand-build with FrameworkElementFactory.
    /// Add it to a ScrollViewer/window's Resources keyed by typeof(ScrollBar) so it applies implicitly.
    /// </summary>
    public static Style DarkScrollBar()
    {
        const string xaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            "       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>" +
            "  <Setter Property='Background' Value='Transparent'/>" +
            "  <Setter Property='Width' Value='11'/>" +
            "  <Setter Property='Template'>" +
            "    <Setter.Value>" +
            "      <ControlTemplate TargetType='ScrollBar'>" +
            "        <Grid Background='Transparent'>" +
            "          <Track Name='PART_Track' IsDirectionReversed='True'>" +
            "            <Track.DecreaseRepeatButton>" +
            "              <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/>" +
            "            </Track.DecreaseRepeatButton>" +
            "            <Track.Thumb>" +
            "              <Thumb>" +
            "                <Thumb.Template>" +
            "                  <ControlTemplate TargetType='Thumb'>" +
            "                    <Border CornerRadius='4' Background='#40FFFFFF' Margin='3,2,3,2'/>" +
            "                  </ControlTemplate>" +
            "                </Thumb.Template>" +
            "              </Thumb>" +
            "            </Track.Thumb>" +
            "            <Track.IncreaseRepeatButton>" +
            "              <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/>" +
            "            </Track.IncreaseRepeatButton>" +
            "          </Track>" +
            "        </Grid>" +
            "      </ControlTemplate>" +
            "    </Setter.Value>" +
            "  </Setter>" +
            "</Style>";
        return (Style)XamlReader.Parse(xaml);
    }

    public static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static SolidColorBrush SeriesBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// A tooltip styled to match the panel: dark rounded surface, subtle border, soft shadow, mono
    /// face so columns line up. The wrong-monitor placement bug was fixed at the source (pinning the
    /// popup's PlacementTarget), so the transparent look is safe again.
    /// </summary>
    public static ToolTip MakeToolTip()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        // Bind the fill to the ToolTip's own Background (set to the shared TooltipBg below). Putting
        // the brush directly in the template would seal-freeze it, and then its Opacity couldn't be
        // retuned at runtime. Via the tooltip's Background property it stays mutable and shared.
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(Control.BackgroundProperty),
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
        });
        border.SetValue(Border.BorderBrushProperty, Freeze("#66FFFFFF"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 10, 8));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(6));   // room for the shadow
        border.SetValue(UIElement.EffectProperty, new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.5,
            Color = Colors.Black,
        });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(ToolTip)) { VisualTree = border };

        return new ToolTip
        {
            OverridesDefaultStyle = true,
            HasDropShadow = true,   // transparent popup for the rounded corners + shadow
            Template = template,
            Background = TooltipBg,   // shared, mutable → opacity is configurable at runtime
            Foreground = InkSecondary,
            FontFamily = Mono,
            FontSize = 11.5,
            Padding = new Thickness(0),
        };
    }

    /// <summary>Shared tooltip background. Deliberately NOT frozen: changing its Opacity retunes
    /// the translucency of every tooltip at once. Default 0.85; the menu adjusts it.</summary>
    public static readonly SolidColorBrush TooltipBg =
        new((Color)ColorConverter.ConvertFromString("#201F1D")) { Opacity = 0.85 };

    // ---- Structured tooltip content: a bold header over a mono body. ----

    /// <summary>A blank TextBlock configured for tooltip content (mono, so columns align).</summary>
    public static TextBlock TipBlock() =>
        new() { FontFamily = Mono, FontSize = 11.5, Foreground = InkSecondary };

    /// <summary>Bold, bright header run (the headline number/label).</summary>
    public static System.Windows.Documents.Run TipHead(string s) =>
        new(s) { FontWeight = FontWeights.Bold, Foreground = InkPrimary };

    /// <summary>Recessive run for secondary text (the "hace N s", units, etc.).</summary>
    public static System.Windows.Documents.Run TipDim(string s) =>
        new(s) { Foreground = InkMuted };

    /// <summary>A run in an arbitrary colour (e.g. the best-core star).</summary>
    public static System.Windows.Documents.Run TipColor(string s, Brush brush) =>
        new(s) { Foreground = brush, FontWeight = FontWeights.Bold };

    public static TextBlock Text(string text, double size, Brush brush, bool mono = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontFamily = mono ? Mono : Ui,
    };

    /// <summary>Rate in KiB/MiB/GiB (binary, 1024) or KB/MB/GB (decimal, 1000), per the caller.</summary>
    public static string Bytes(double bytesPerSec, bool binary = true)
    {
        double k = binary ? 1024 : 1000, m = k * k, g = m * k;
        (string ku, string mu, string gu) = binary ? ("KiB", "MiB", "GiB") : ("KB", "MB", "GB");
        return bytesPerSec >= g ? $"{bytesPerSec / g:F2} {gu}/s"
             : bytesPerSec >= m ? $"{bytesPerSec / m:F1} {mu}/s"
             : $"{bytesPerSec / k:F1} {ku}/s";
    }

    public static string BytesShort(double bytesPerSec, bool binary = true)
    {
        double k = binary ? 1024 : 1000, m = k * k, g = m * k;
        return bytesPerSec >= g ? $"{bytesPerSec / g:F1}G"
             : bytesPerSec >= m ? $"{bytesPerSec / m:F1}M"
             : $"{bytesPerSec / k:F0}K";
    }

    public static string Gib(ulong bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1}";

    /// <summary>Memory size number in GiB (binary, 1024) or GB (decimal, 1000), per the flag.</summary>
    public static string MemVal(ulong bytes, bool binary) =>
        (bytes / (binary ? 1024.0 * 1024 * 1024 : 1000.0 * 1000 * 1000)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The matching unit label for <see cref="MemVal"/>.</summary>
    public static string MemUnit(bool binary) => binary ? "GiB" : "GB";
}
