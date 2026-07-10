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
