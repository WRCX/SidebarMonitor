using SidebarMonitor.Shared;
using Xunit;
using static SidebarMonitor.Shared.PageUseDecode;

namespace SidebarMonitor.Tests;

/// <summary>Reading the memory manager's per-page record into the RAM's use counts.</summary>
[Trait("Category", "Unit")]
public class PageUseDecodeTests
{
    private static ulong Frame(int use, int list, ulong highBits = 0) => (uint)use | ((ulong)(uint)list << 4) | highBits;

    /// <summary>Use in bits 0-3, list in bits 4-6 — and the priority and flag bits above them, which
    /// the kernel does set, must not leak into either.</summary>
    [Fact]
    public void Use_and_list_come_from_the_low_bits_only()
    {
        ulong f = Frame(UsePagefileMapped, ListActive, highBits: 0xE000_0000_0000_0180UL);
        Assert.Equal(UsePagefileMapped, Use(f));
        Assert.Equal(ListActive, List(f));
    }

    /// <summary>
    /// Only ACTIVE pages are the RAM in use by a category. Standby is cache that is free to take — 9 GiB
    /// of it on the dev desktop, which counted as "mapped file" would claim most of the machine — and
    /// modified pages are reported on their own.
    /// </summary>
    [Fact]
    public void Only_active_pages_count_toward_a_use_and_modified_is_apart()
    {
        var counts = new long[UseCount * ListCount];
        Tally(Frame(UseFile, ListActive), counts);
        Tally(Frame(UseFile, ListStandby), counts);
        Tally(Frame(UseFile, ListStandby), counts);
        Tally(Frame(UsePrivate, ListModified), counts);

        var info = ToInfo(counts, stride: 1, timestampUtcTicks: 1);
        Assert.Equal(4096UL, info.MappedFile);
        Assert.Equal(0UL, info.Private);
        Assert.Equal(4096UL, info.Modified);
    }

    /// <summary>One page looked at in sixteen stands for sixteen pages.</summary>
    [Fact]
    public void A_sampled_count_is_scaled_by_the_stride()
    {
        var counts = new long[UseCount * ListCount];
        for (int i = 0; i < 10; i++) Tally(Frame(UseNonPagedPool, ListActive), counts);

        Assert.Equal(10UL * 16 * 4096, ToInfo(counts, 16, 1).NonpagedPool);
    }

    /// <summary>The kernel's share is every kernel-owned use — what the counters could not see
    /// (page tables, locked pages, stacks) included, which is the point of reading the pages.</summary>
    [Fact]
    public void The_kernel_share_adds_every_kernel_use()
    {
        var counts = new long[UseCount * ListCount];
        foreach (int use in new[] { UsePagedPool, UseNonPagedPool, UsePageTable, UseSystemPte, UseDriverLock, UseKernelStack, UseMetafile })
            Tally(Frame(use, ListActive), counts);
        Tally(Frame(UsePrivate, ListActive), counts);

        var info = ToInfo(counts, 1, 1);
        Assert.Equal(7UL * 4096, info.Kernel);
        Assert.True(info.Available);
    }
}
