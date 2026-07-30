using System.Windows.Media;
using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// The colour rules that two widgets have to agree on, which is exactly where they had drifted apart.
/// </summary>
[Trait("Category", "Unit")]
public class ThemeColorTests
{
    /// <summary>
    /// One process, one colour, however it happens to be spelled. The per-core bars name processes from
    /// ETW ("claude") while the process list names them from the OS ("claude.exe") and labels summed
    /// instances ("chrome ×42") — hashing those separately painted the same process in two colours
    /// depending on which widget was drawing it.
    /// </summary>
    [Theory]
    [InlineData("claude", "claude.exe")]
    [InlineData("chrome", "chrome ×42")]
    [InlineData("chrome", "chrome.exe ×42")]
    [InlineData("Code", "CODE.EXE")]
    [InlineData("svchost", "  svchost.exe  ")]
    public void One_process_is_one_colour_however_it_is_spelled(string a, string b)
        => Assert.Equal(Theme.ProcessColor(a), Theme.ProcessColor(b));

    /// <summary>
    /// Distinctness is deliberately NOT asserted: the process palette has seven slots, so two names
    /// colliding is expected. What must hold is that the colour is a pure function of the normalised
    /// name — stable across ticks, cores and restarts.
    /// </summary>
    [Fact]
    public void The_normalised_name_is_what_decides_the_colour()
    {
        Assert.Equal(Theme.ProcessColor("explorer"), Theme.ProcessColor("explorer"));
        Assert.Equal("chrome", Theme.NormalizeProcess("chrome.exe ×42"));
        Assert.Equal("nvidia-smi", Theme.NormalizeProcess("nvidia-smi"));
    }

    /// <summary>A name that is nothing but the suffix must not normalise to an empty key.</summary>
    [Theory]
    [InlineData(".exe", ".exe")]
    [InlineData("", "")]
    [InlineData("×3", "×3")]
    public void Normalising_never_empties_a_name_that_has_no_real_stem(string input, string expected)
        => Assert.Equal(expected, Theme.NormalizeProcess(input));

    /// <summary>
    /// The chart follows the bars until it is given a preset of its own — the default, and what the
    /// single shared palette always did. Getting this backwards would recolour every existing install.
    /// </summary>
    [Fact]
    public void The_chart_follows_the_bars_by_default()
    {
        Theme.SetCorePalette(0);
        Theme.SetGraphPalette(Theme.FollowBars);

        for (int i = 0; i < 16; i++) Assert.Equal(Theme.CoreColor(i), Theme.GraphCoreColor(i));
    }

    /// <summary>And parts company once it has one, without disturbing the bars.</summary>
    [Fact]
    public void An_independent_chart_palette_leaves_the_bars_alone()
    {
        Theme.SetCorePalette(0);
        var barsBefore = Enumerable.Range(0, 16).Select(Theme.CoreColor).ToArray();

        Theme.SetGraphPalette(1);   // high-contrast for the chart only
        var bars = Enumerable.Range(0, 16).Select(Theme.CoreColor).ToArray();
        var chart = Enumerable.Range(0, 16).Select(Theme.GraphCoreColor).ToArray();

        Assert.Equal(barsBefore, bars);
        Assert.NotEqual(bars, chart);
    }

    /// <summary>Switching the bars' palette must recolour a chart that is following it — the chart's
    /// cache is keyed by index, so a stale entry would freeze it on the previous preset.</summary>
    [Fact]
    public void A_following_chart_recolours_when_the_bars_change()
    {
        Theme.SetGraphPalette(Theme.FollowBars);
        Theme.SetCorePalette(0);
        var before = Enumerable.Range(0, 16).Select(Theme.GraphCoreColor).ToArray();

        Theme.SetCorePalette(3);   // warm
        var after = Enumerable.Range(0, 16).Select(Theme.GraphCoreColor).ToArray();

        Assert.NotEqual(before, after);
        Assert.Equal(Enumerable.Range(0, 16).Select(Theme.CoreColor), after);
    }

    /// <summary>A palette must actually span its cores: 8 cores get the whole wheel, not a slice, and
    /// no two adjacent cores may come out identical (the point of a per-core palette).</summary>
    [Fact]
    public void Adjacent_cores_are_distinguishable()
    {
        Theme.SetCorePalette(0);
        Theme.SetGraphPalette(Theme.FollowBars);
        Theme.SetCoreCount(8);

        var colors = Enumerable.Range(0, 8).Select(Theme.CoreColor).ToArray();

        for (int i = 1; i < colors.Length; i++)
            Assert.NotEqual(colors[i - 1], colors[i]);
        Assert.Equal(8, colors.Distinct().Count());

        Theme.SetCoreCount(16);   // shared static: leave it as the rest of the suite expects
    }
}
