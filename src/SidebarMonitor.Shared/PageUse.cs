using System.Runtime.InteropServices;

namespace SidebarMonitor.Shared;

/// <summary>
/// The RAM in use by page type — RAMMap's "Use Counts", active pages only — sampled by the elevated
/// helper from the memory manager's own page database. What no counter gives: how much of the RAM is
/// mapped files (DLLs, executables), memory shared between processes, page tables, driver-locked pages
/// or kernel stacks. Without it, those were one unexplained remainder. Bytes; all zero (and
/// <see cref="TimestampUtcTicks"/> 0) when the helper is not providing it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PageUseInfo
{
    public long TimestampUtcTicks;
    public ulong Private, MappedFile, Shareable, PageTable, PagedPool, NonpagedPool, SystemPte,
                 DriverLocked, KernelStack, Metafile, Other;
    /// <summary>Pages on the modified list (written-to pages awaiting write-back), any type.</summary>
    public ulong Modified;

    public readonly bool Available => TimestampUtcTicks != 0;

    /// <summary>The kernel's share: pools, page tables (process and system), driver-locked pages,
    /// kernel stacks and file-system metadata.</summary>
    public readonly ulong Kernel => PagedPool + NonpagedPool + PageTable + SystemPte + DriverLocked + KernelStack + Metafile;
}

/// <summary>
/// Decoding the memory manager's per-page record (MEMORY_FRAME_INFORMATION) into <see cref="PageUseInfo"/>.
/// Pure, so the bit layout and the tallying are unit-tested; the query itself lives in the helper.
/// </summary>
public static class PageUseDecode
{
    // MMPFNUSE_*: the page's use, bits 0-3.
    public const int UsePrivate = 0, UseFile = 1, UsePagefileMapped = 2, UsePageTable = 3, UsePagedPool = 4,
                     UseNonPagedPool = 5, UseSystemPte = 6, UseSessionPrivate = 7, UseMetafile = 8, UseAwe = 9,
                     UseDriverLock = 10, UseKernelStack = 11;
    public const int UseCount = 16;

    // MMLISTS: the list the page is on, bits 4-6.
    public const int ListZero = 0, ListFree = 1, ListStandby = 2, ListModified = 3, ListModifiedNoWrite = 4,
                     ListBad = 5, ListActive = 6, ListTransition = 7;
    public const int ListCount = 8;

    public static int Use(ulong frameInfo) => (int)(frameInfo & 0xF);
    public static int List(ulong frameInfo) => (int)((frameInfo >> 4) & 0x7);

    /// <summary>Counts one page into a [use, list] tally.</summary>
    public static void Tally(ulong frameInfo, long[] counts) => counts[Use(frameInfo) * ListCount + List(frameInfo)]++;

    /// <summary>
    /// Turns a [use, list] page tally into bytes, scaled by the sampling stride (one page in
    /// <paramref name="stride"/> was looked at). Active pages by use; the modified lists summed.
    /// </summary>
    public static PageUseInfo ToInfo(long[] counts, int stride, long timestampUtcTicks)
    {
        ulong Active(int use) => (ulong)Math.Max(0, counts[use * ListCount + ListActive]) * (ulong)stride * 4096;

        var info = new PageUseInfo
        {
            TimestampUtcTicks = timestampUtcTicks,
            Private = Active(UsePrivate) + Active(UseSessionPrivate),
            MappedFile = Active(UseFile),
            Shareable = Active(UsePagefileMapped),
            PageTable = Active(UsePageTable),
            PagedPool = Active(UsePagedPool),
            NonpagedPool = Active(UseNonPagedPool),
            SystemPte = Active(UseSystemPte),
            DriverLocked = Active(UseDriverLock),
            KernelStack = Active(UseKernelStack),
            Metafile = Active(UseMetafile),
        };
        for (int u = 0; u < UseCount; u++)
        {
            if (u is UsePrivate or UseSessionPrivate or UseFile or UsePagefileMapped or UsePageTable or UsePagedPool
                  or UseNonPagedPool or UseSystemPte or UseDriverLock or UseKernelStack or UseMetafile) continue;
            info.Other += Active(u);   // AWE, large pages and whatever later builds add
        }
        for (int u = 0; u < UseCount; u++)
            info.Modified += (ulong)(counts[u * ListCount + ListModified] + counts[u * ListCount + ListModifiedNoWrite])
                             * (ulong)stride * 4096;
        return info;
    }
}
