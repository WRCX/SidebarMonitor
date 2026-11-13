using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The RAM in use, split by what holds it — every figure measured, none estimated. The bar: the
/// biggest processes in all-different colours, then Windows in the kernel's neutral, then a grey tail.
/// The tooltip names the tail: the next processes by name, the mapped files (DLLs, executables), the
/// memory processes share, each from the memory manager's own page database (sampled by the elevated
/// helper, as RAMMap reads it). Without the helper that part cannot be split, and says so instead of
/// guessing.
/// </summary>
internal static class RamBreakdown
{
    /// <summary>Processes with a coloured segment of their own; more is a row of slivers.</summary>
    private const int BarMax = 6;

    /// <summary>Most processes the legend names; the tooltip lists them all.</summary>
    private const int LegendMax = 4;

    /// <summary>Below this a piece of the RAM is not worth its own tooltip line.</summary>
    private const ulong MinLine = 32UL << 20;

    /// <summary>The kernel's resident RAM: the page database's figure when the helper provides it
    /// (pools, page tables, driver-locked pages, stacks, metadata), else the counters' shorter one.</summary>
    private static ulong KernelBytes(in MemInfo m) => m.PageUse.Available ? m.PageUse.Kernel : m.KernelBytes;

    /// <summary>The written-to pages waiting to go to disk, from the same page-database pass as the rest
    /// when there is one: the counter, read at another moment, differed from it by 345 MiB on the dev
    /// desktop, and that difference sat in "unclassified".</summary>
    private static ulong ModifiedBytes(in MemInfo m) => m.PageUse.Available ? m.PageUse.Modified : m.ModifiedBytes;

    /// <summary>Windows' share: its own processes (System, Memory Compression…), the kernel, the file
    /// cache, and the written-to pages waiting to go to disk.</summary>
    public static ulong WindowsBytes(in MemInfo m) => m.OsProcsBytes + KernelBytes(m) + m.CacheBytes + ModifiedBytes(m);

    /// <summary>Files mapped by processes — DLLs, executables, memory-mapped data. The page database
    /// counts the file cache as mapped file too; it is in Windows' share, so it comes off here.</summary>
    private static ulong MappedFileBytes(in MemInfo m) =>
        m.PageUse.MappedFile > m.CacheBytes ? m.PageUse.MappedFile - m.CacheBytes : 0;

    private static ulong ProcessBytes(in MemInfo m)
    {
        ulong sum = m.OtherProcsBytes;
        for (int i = 0; i < Count(m); i++) sum += m.Procs[i].PrivateBytes;
        return sum;
    }

    /// <summary>What is in use and none of the measured parts covers. With the helper this is only
    /// the difference between two moments of sampling; without it, everything the counters can't split.</summary>
    private static ulong Unsplit(in MemInfo m)
    {
        ulong known = ProcessBytes(m) + WindowsBytes(m);
        if (m.PageUse.Available) known += MappedFileBytes(m) + m.PageUse.Shareable + m.PageUse.Other;
        return m.PhysUsed > known ? m.PhysUsed - known : 0;
    }

    private static int Count(in MemInfo m) => Math.Clamp(m.ProcCount, 0, SnapshotLayout.MaxMemProcs);
    private static int BarCount(in MemInfo m) => Math.Min(Count(m), BarMax);

    // By value: NameField reads through a ref, which a copy allows and a readonly element does not.
    private static string Name(MemProcInfo p) => Theme.NormalizeProcess(NameField.Get(ref p.Name));

    private static string Label(MemProcInfo p) => p.Instances > 1 ? $"{Name(p)} ×{p.Instances}" : Name(p);

    /// <summary>All-different colours for the processes on the bar, biggest first keeping its usual hue.</summary>
    private static ProcessColorMap Colors(in MemInfo m)
    {
        var names = new string[BarCount(m)];
        for (int i = 0; i < names.Length; i++) names[i] = Name(m.Procs[i]);
        return Theme.DistinctProcessColors(names);
    }

    /// <summary>The bar's coloured segments, as fractions of all RAM: processes, then Windows. The rest
    /// of the fill (smaller processes, mapped files, shared memory) is the bar's grey tail.</summary>
    public static (Brush Fill, double Fraction)[] Segments(in MemInfo m)
    {
        if (m.PhysTotal == 0) return [];
        double total = m.PhysTotal;
        int n = BarCount(m);
        var colors = Colors(m);
        var segs = new (Brush, double)[n + 1];
        for (int i = 0; i < n; i++) segs[i] = (colors.For(Name(m.Procs[i])), m.Procs[i].PrivateBytes / total);
        segs[n] = (Theme.KernelFill, WindowsBytes(m) / total);
        return segs;
    }

    /// <summary>"● vmmemWSL 1.1G  ● claude 1.0G  ● Windows 4.9G": the colours named.</summary>
    public static void Legend(TextBlock legend, in MemInfo m, bool binary)
    {
        legend.Inlines.Clear();
        var colors = Colors(m);
        int n = Math.Min(BarCount(m), LegendMax);
        for (int i = 0; i < n; i++)
        {
            Dot(legend.Inlines, colors.For(Name(m.Procs[i])), i > 0);
            legend.Inlines.Add(new Run($"{Name(m.Procs[i])} {Size(m.Procs[i].PrivateBytes, binary)}"));
        }
        if (WindowsBytes(m) > 0)
        {
            Dot(legend.Inlines, Theme.KernelFill, n > 0);
            legend.Inlines.Add(new Run($"Windows {Size(WindowsBytes(m), binary)}"));
        }
    }

    /// <summary>Every piece of the RAM in use, in bar order — the coloured processes, Windows broken
    /// down, then the grey tail opened up. Each line is RAM actually occupied, counted once.</summary>
    public static object Tip(in MemInfo m, bool binary)
    {
        var tb = Theme.TipBlock();
        tb.Inlines.Add(Theme.TipHead(Loc.T("RAM en uso {0}", Size(m.PhysUsed, binary))));

        var colors = Colors(m);
        for (int i = 0; i < BarCount(m); i++)
        {
            var p = m.Procs[i];
            Line(tb, colors.For(Name(p)), $"{Label(p)}  {Size(p.PrivateBytes, binary)}");
        }

        Line(tb, Theme.KernelFill, $"Windows  {Size(WindowsBytes(m), binary)}");
        Dim(tb, Loc.T("núcleo {0} · caché {1} · por escribir {2} · procesos {3}",
            Size(KernelBytes(m), binary), Size(m.CacheBytes, binary), Size(ModifiedBytes(m), binary), Size(m.OsProcsBytes, binary)));
        if (m.PageUse.Available)
        {
            var u = m.PageUse;
            Dim(tb, Loc.T("núcleo: pools {0} · tablas de páginas {1} · drivers {2} · metadatos {3} · pilas {4}",
                Size(u.PagedPool + u.NonpagedPool, binary), Size(u.PageTable + u.SystemPte, binary),
                Size(u.DriverLocked, binary), Size(u.Metafile, binary), Size(u.KernelStack, binary)));
        }

        // The smaller processes: named, three to a line, then the count and sum of the rest.
        ulong smaller = m.OtherProcsBytes;
        var names = new List<string>();
        for (int i = BarCount(m); i < Count(m); i++)
        {
            smaller += m.Procs[i].PrivateBytes;
            names.Add($"{Label(m.Procs[i])} {Size(m.Procs[i].PrivateBytes, binary)}");
        }
        if (smaller > 0)
        {
            Line(tb, Theme.OtherFill, Loc.T("más procesos  {0}", Size(smaller, binary)));
            for (int k = 0; k < names.Count; k += 3) Dim(tb, string.Join(" · ", names.Skip(k).Take(3)));
            if (m.OtherProcsCount > 0)
                Dim(tb, Loc.T("y {0} procesos más  {1}", m.OtherProcsCount, Size(m.OtherProcsBytes, binary)));
        }

        if (m.PageUse.Available)
        {
            Line(tb, Theme.OtherFill, Loc.T("DLL y archivos mapeados  {0}", Size(MappedFileBytes(m), binary)));
            Dim(tb, Loc.T("en RAM una sola vez, aunque los usen varios procesos"));
            Line(tb, Theme.OtherFill, Loc.T("memoria compartida entre procesos  {0}", Size(m.PageUse.Shareable, binary)));
            if (m.PageUse.Other >= MinLine)
                Line(tb, Theme.OtherFill, Loc.T("páginas grandes y AWE  {0}", Size(m.PageUse.Other, binary)));
            ulong rest = Unsplit(m);
            if (rest >= MinLine) Line(tb, Theme.OtherFill, Loc.T("sin clasificar  {0}", Size(rest, binary)));
        }
        else
        {
            Line(tb, Theme.OtherFill, Loc.T("sin desglosar  {0}", Size(Unsplit(m), binary)));
            Dim(tb, Loc.T("DLL, memoria compartida y parte del núcleo: desglosarlo necesita el helper"));
        }
        return tb;
    }

    private static void Line(TextBlock tb, Brush dot, string text)
    {
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(new Run("● ") { Foreground = dot });
        tb.Inlines.Add(new Run(text));
    }

    private static void Dim(TextBlock tb, string text)
    {
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(Theme.TipDim("   " + text));
    }

    private static void Dot(InlineCollection inlines, Brush dot, bool gap)
    {
        if (gap) inlines.Add(new Run("  "));
        inlines.Add(new Run("● ") { Foreground = dot });
    }

    /// <summary>"4.9G" from a gigabyte up, "350M" below, in the panel's chosen units.</summary>
    private static string Size(ulong bytes, bool binary)
    {
        double g = binary ? 1024.0 * 1024 * 1024 : 1e9, mb = binary ? 1024.0 * 1024 : 1e6;
        return bytes >= g
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / g:F1}{(binary ? "G" : "g")}")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / mb:F0}{(binary ? "M" : "m")}");
    }
}
