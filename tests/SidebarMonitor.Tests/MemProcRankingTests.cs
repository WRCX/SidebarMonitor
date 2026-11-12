using SidebarMonitor.Shared;
using Xunit;
using static SidebarMonitor.Shared.MemProcRanking;

namespace SidebarMonitor.Tests;

/// <summary>How the RAM bar splits the processes' private working sets.</summary>
[Trait("Category", "Unit")]
public class MemProcRankingTests
{
    private const ulong MiB = 1UL << 20, GiB = 1UL << 30;

    /// <summary>Forty chrome renderers are one app, one colour, one row.</summary>
    [Fact]
    public void Instances_fold_into_one_row_by_name()
    {
        var split = Rank([("chrome.exe", 300 * MiB), ("chrome.exe", 200 * MiB), ("CHROME.EXE", 100 * MiB)], 6);

        var r = Assert.Single(split.Top);
        Assert.Equal(600 * MiB, r.Bytes);
        Assert.Equal(3, r.Instances);
    }

    /// <summary>
    /// Memory Compression holds other processes' pages and is often the biggest "process" there is;
    /// listed next to the apps it read as an app eating RAM. It, and the kernel's own processes, go
    /// to the Windows share instead.
    /// </summary>
    [Fact]
    public void Windows_own_processes_go_to_the_Windows_share_not_the_list()
    {
        var split = Rank([("Memory Compression", 1300 * MiB), ("System", 20 * MiB), ("Registry", 80 * MiB),
                          ("Secure System", 140 * MiB), ("game.exe", 2 * GiB)], 6);

        Assert.Equal(["game.exe"], split.Top.Select(r => r.Name));
        Assert.Equal(1540 * MiB, split.OsBytes);
    }

    /// <summary>Biggest first; the rest, however many, is one sum; nothing is counted twice.</summary>
    [Fact]
    public void Biggest_first_and_the_rest_summed()
    {
        var split = Rank([("a", 1 * GiB), ("b", 3 * GiB), ("c", 2 * GiB), ("d", 500 * MiB), ("e", 100 * MiB)], 3);

        Assert.Equal(["b", "c", "a"], split.Top.Select(r => r.Name));
        Assert.Equal(600 * MiB, split.OtherBytes);
    }

    /// <summary>A sliver is no segment: small processes go to the rest even with slots free.</summary>
    [Fact]
    public void Processes_below_the_threshold_go_to_the_rest()
    {
        var split = Rank([("big", 1 * GiB), ("tiny", 10 * MiB), ("Idle", 0)], 6);

        Assert.Equal(["big"], split.Top.Select(r => r.Name));
        Assert.Equal(10 * MiB, split.OtherBytes);
    }
}
