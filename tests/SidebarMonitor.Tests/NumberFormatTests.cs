using System.Globalization;
using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Every number on the panel uses "." whatever the OS locale. The byte-rate helpers and
/// <see cref="Loc.T(string, object[])"/> followed the current culture instead, so on a Spanish Windows
/// the same section read "R8,3M" next to "6.9T/12.0T", and "VRAM 1,5 / 16,0 GiB" sat under RAM's
/// "20.4 / 31.1 GiB".
/// </summary>
[Trait("Category", "Unit")]
public class NumberFormatTests
{
    /// <summary>Runs <paramref name="body"/> under a comma-decimal culture, restoring the old one.</summary>
    private static void UnderSpanish(Action body)
    {
        var (cur, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("es-ES");
            body();
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (cur, ui); }
    }

    [Fact]
    public void Byte_rates_use_a_dot_under_a_comma_locale() => UnderSpanish(() =>
    {
        Assert.Equal("1.5 MiB/s", Theme.Bytes(1.5 * 1024 * 1024));
        Assert.Equal("370.7 KB/s", Theme.Bytes(370_700, binary: false));
        Assert.Equal("2.50 GiB/s", Theme.Bytes(2.5 * 1024 * 1024 * 1024));
        Assert.Equal("8.3M", Theme.BytesShort(8.3 * 1024 * 1024));
        Assert.Equal("1.5", Theme.Gib(1UL << 30 | 1UL << 29));
    });

    [Fact]
    public void Localised_templates_format_numbers_invariant() => UnderSpanish(() =>
        Assert.Equal("cola 0.25", Loc.T("{0} {1:F2}", "cola", 0.25)));
}
