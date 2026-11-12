namespace SidebarMonitor.Shared;

/// <summary>
/// Splits the processes' private working sets into what the RAM bar draws: the biggest few by name,
/// everything else as one sum, and Windows' own processes apart (they belong to the Windows segment,
/// not to the list). Pure, so the rules are unit-tested without a process snapshot.
/// </summary>
public static class MemProcRanking
{
    public readonly record struct Row(string Name, ulong Bytes, int Instances);

    public readonly record struct Split(List<Row> Top, ulong OtherBytes, ulong OsBytes);

    /// <summary>
    /// Windows' own processes. Memory Compression holds other processes' pages, compressed, so it can
    /// be the biggest "process" on the machine; listing it next to the apps read as an app eating RAM.
    /// The Idle process is not a process at all and owns nothing.
    /// </summary>
    private static readonly HashSet<string> OsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Secure System", "Registry", "Memory Compression",
    };

    public static bool IsOs(string name) => OsNames.Contains(name);

    /// <summary>Below this a process is not worth a segment: a sliver on a bar holding 16–64 GiB.</summary>
    public const ulong MinBytes = 64UL << 20;

    public static Split Rank(IEnumerable<(string Name, ulong Bytes)> perProcess, int max)
    {
        var byName = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        ulong os = 0;
        foreach (var (name, bytes) in perProcess)
        {
            if (string.IsNullOrEmpty(name) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsOs(name)) { os += bytes; continue; }
            byName[name] = byName.TryGetValue(name, out var r)
                ? r with { Bytes = r.Bytes + bytes, Instances = r.Instances + 1 }
                : new Row(name, bytes, 1);
        }

        var sorted = byName.Values.OrderByDescending(r => r.Bytes).ToList();
        var top = sorted.Where(r => r.Bytes >= MinBytes).Take(Math.Max(0, max)).ToList();
        ulong other = 0;
        foreach (var r in sorted.Skip(top.Count)) other += r.Bytes;
        return new Split(top, other, os);
    }
}
