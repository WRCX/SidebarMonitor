using System.Windows;
using System.Windows.Controls;
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

    public static TextBlock Text(string text, double size, Brush brush, bool mono = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontFamily = mono ? Mono : Ui,
    };

    public static string Bytes(double bytesPerSec)
    {
        const double K = 1024, M = K * 1024, G = M * 1024;
        return bytesPerSec >= G ? $"{bytesPerSec / G:F2} GiB/s"
             : bytesPerSec >= M ? $"{bytesPerSec / M:F1} MiB/s"
             : $"{bytesPerSec / K:F1} KiB/s";
    }

    public static string BytesShort(double bytesPerSec)
    {
        const double K = 1024, M = K * 1024, G = M * 1024;
        return bytesPerSec >= G ? $"{bytesPerSec / G:F1}G"
             : bytesPerSec >= M ? $"{bytesPerSec / M:F1}M"
             : $"{bytesPerSec / K:F0}K";
    }

    public static string Gib(ulong bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1}";
}
