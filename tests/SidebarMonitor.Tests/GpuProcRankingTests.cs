using SidebarMonitor.Shared;
using Xunit;
using static SidebarMonitor.Shared.GpuProcRanking;

namespace SidebarMonitor.Tests;

/// <summary>Which processes the GPU section shows, from the per-pid readings the agent gathers.</summary>
[Trait("Category", "Unit")]
public class GpuProcRankingTests
{
    private const ulong MiB = 1UL << 20, GiB = 1UL << 30;

    /// <summary>Chrome's GPU process and its helpers are one row, not five that each look idle.</summary>
    [Fact]
    public void Instances_of_one_process_fold_into_one_row()
    {
        var rows = Rank([
            new("chrome.exe", 3, 200 * MiB, 10 * MiB),
            new("chrome.exe", 2, 100 * MiB, 0),
            new("CHROME.EXE", 0, 50 * MiB, 0),
        ], 6);

        var r = Assert.Single(rows);
        Assert.Equal(5, r.Pct);
        Assert.Equal(350 * MiB, r.Local);
        Assert.Equal(10 * MiB, r.NonLocal);
    }

    /// <summary>A process can be at most the whole GPU, however many instances it sums.</summary>
    [Fact]
    public void Folded_utilisation_never_passes_100()
        => Assert.Equal(100, Rank([new("game.exe", 70, 0, 0), new("game.exe", 60, 0, 0)], 6)[0].Pct);

    /// <summary>
    /// The VRAM slots are what keep an idle model server on the bar while busy processes fill every
    /// utilisation slot — the case per-process VRAM is for.
    /// </summary>
    [Fact]
    public void An_idle_VRAM_holder_keeps_a_slot_when_busy_processes_crowd_in()
    {
        var rows = Rank([
            new("a.exe", 50, 0, 0), new("b.exe", 40, 0, 0), new("c.exe", 30, 0, 0),
            new("d.exe", 20, 0, 0), new("e.exe", 10, 0, 0), new("f.exe", 5, 0, 0),
            new("llama-server.exe", 0, 9 * GiB, 0),
        ], 6);

        Assert.Equal(6, rows.Count);
        Assert.Contains(rows, r => r.Name == "llama-server.exe");
        Assert.Equal(["a.exe", "b.exe", "c.exe", "d.exe"], rows.Take(4).Select(r => r.Name));
    }

    /// <summary>Busiest first, then the biggest holders among the idle ones.</summary>
    [Fact]
    public void Rows_are_ordered_by_utilisation_then_VRAM()
    {
        var rows = Rank([
            new("small.exe", 0, 100 * MiB, 0),
            new("game.exe", 97, 7 * GiB, 0),
            new("big.exe", 0, 2 * GiB, 0),
            new("dwm.exe", 2, 300 * MiB, 0),
        ], 6);

        Assert.Equal(["game.exe", "dwm.exe", "big.exe", "small.exe"], rows.Select(r => r.Name));
    }

    /// <summary>Counter noise and crumbs of memory are not "using the GPU".</summary>
    [Fact]
    public void Noise_earns_no_row()
        => Assert.Empty(Rank([new("idle.exe", 0.2f, 4 * MiB, 1 * MiB), new("", 50, GiB, 0)], 6));
}
