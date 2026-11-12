using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// The RAM bar split by who holds the memory: the biggest processes in all-different colours (each its
/// usual hue unless a bigger one already took it), then Windows as the kernel's neutral, then a grey tail for
/// every smaller process and the pages processes share (DLLs, shared memory) — which no single process
/// owns, so they cannot honestly be given to one.
/// </summary>
internal static class RamBreakdown
{
    /// <summary>Most processes the legend names; the tooltip lists them all.</summary>
    private const int LegendMax = 4;

    /// <summary>Windows' share: its own processes (System, Memory Compression…), the kernel, the cache.</summary>
    public static ulong WindowsBytes(in MemInfo m) => m.OsProcsBytes + m.KernelBytes + m.CacheBytes;

    /// <summary>What is in use but owned by nobody we can name: shared pages, page tables, locked memory.</summary>
    public static ulong SharedBytes(in MemInfo m)
    {
        ulong named = WindowsBytes(m) + m.OtherProcsBytes;
        for (int i = 0; i < Count(m); i++) named += m.Procs[i].PrivateBytes;
        return m.PhysUsed > named ? m.PhysUsed - named : 0;
    }

    private static int Count(in MemInfo m) => Math.Clamp(m.ProcCount, 0, SnapshotLayout.MaxMemProcs);

    // By value: NameField reads through a ref, which a copy allows and a readonly element does not.
    private static string Name(MemProcInfo p) => Theme.NormalizeProcess(NameField.Get(ref p.Name));

    /// <summary>All-different colours for the bar's processes, biggest first keeping its usual hue.</summary>
    private static ProcessColorMap Colors(in MemInfo m)
    {
        var names = new string[Count(m)];
        for (int i = 0; i < names.Length; i++) names[i] = Name(m.Procs[i]);
        return Theme.DistinctProcessColors(names);
    }

    /// <summary>The bar's coloured segments, as fractions of all RAM: processes, then Windows.</summary>
    public static (Brush Fill, double Fraction)[] Segments(in MemInfo m)
    {
        if (m.PhysTotal == 0) return [];
        double total = m.PhysTotal;
        int n = Count(m);
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
        int n = Math.Min(Count(m), LegendMax);
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

    /// <summary>Every piece of the RAM in use, in bar order, Windows broken down into its parts.</summary>
    public static object Tip(in MemInfo m, bool binary)
    {
        var tb = Theme.TipBlock();
        tb.Inlines.Add(Theme.TipHead(Loc.T("RAM en uso {0}", Size(m.PhysUsed, binary))));

        var colors = Colors(m);
        for (int i = 0; i < Count(m); i++)
        {
            var p = m.Procs[i];
            string times = p.Instances > 1 ? $" ×{p.Instances}" : "";
            Line(tb, colors.For(Name(p)), $"{Name(p)}{times}  {Size(p.PrivateBytes, binary)}");
        }

        Line(tb, Theme.KernelFill, $"Windows  {Size(WindowsBytes(m), binary)}");
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(Theme.TipDim("   " + Loc.T("núcleo {0} · caché {1} · procesos {2}",
            Size(m.KernelBytes, binary), Size(m.CacheBytes, binary), Size(m.OsProcsBytes, binary))));

        Line(tb, Theme.OtherFill, Loc.T("otros procesos  {0}", Size(m.OtherProcsBytes, binary)));
        Line(tb, Theme.OtherFill, Loc.T("compartida y sin atribuir  {0}", Size(SharedBytes(m), binary)));
        return tb;
    }

    private static void Line(TextBlock tb, Brush dot, string text)
    {
        tb.Inlines.Add(new LineBreak());
        tb.Inlines.Add(new Run("● ") { Foreground = dot });
        tb.Inlines.Add(new Run(text));
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
