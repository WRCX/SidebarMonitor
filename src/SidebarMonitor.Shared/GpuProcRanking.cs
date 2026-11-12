namespace SidebarMonitor.Shared;

/// <summary>
/// Turns per-pid GPU readings into the handful of rows the GPU section shows. Pure, so the rules are
/// unit-tested without PDH: which processes earn a row, how instances fold, and in what order.
/// </summary>
public static class GpuProcRanking
{
    /// <summary>One pid's reading on one GPU: its busiest engine's % and its two memory counters.</summary>
    public readonly record struct PidUsage(string Name, float Pct, ulong Local, ulong NonLocal);

    /// <summary>One process name's row, instances folded.</summary>
    public readonly record struct Row(string Name, float Pct, ulong Local, ulong NonLocal);

    /// <summary>Below this a process is not "using" the GPU: rounding noise from the 1 s counter.</summary>
    public const float MinPct = 0.5f;

    /// <summary>VRAM below this is invisible on a bar a few hundred pixels wide holding 8–24 GiB.</summary>
    public const ulong MinBytes = 32UL << 20;

    /// <summary>
    /// Folds the readings by name (chrome's GPU process and its helpers are one row), then picks up to
    /// <paramref name="max"/> rows: the busiest by utilisation first, at most <c>max - 2</c> of them, and
    /// the rest of the slots for the biggest VRAM holders not already in, and any slot still free for the
    /// next busiest. The VRAM pass is what keeps an idle model server or a browser parked on 2 GiB on the
    /// bar while a game takes every busy slot.
    /// The result is ordered by utilisation, then VRAM, so the busiest process is always first.
    /// </summary>
    public static List<Row> Rank(IEnumerable<PidUsage> perPid, int max)
    {
        var byName = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in perPid)
        {
            if (string.IsNullOrEmpty(u.Name)) continue;
            byName[u.Name] = byName.TryGetValue(u.Name, out var r)
                ? new Row(r.Name, Math.Min(100, r.Pct + u.Pct), r.Local + u.Local, r.NonLocal + u.NonLocal)
                : new Row(u.Name, Math.Min(100, u.Pct), u.Local, u.NonLocal);
        }

        var picked = new List<Row>(max);
        if (max <= 0) return picked;

        int busySlots = Math.Max(1, max - 2);
        foreach (var r in byName.Values.Where(r => r.Pct >= MinPct).OrderByDescending(r => r.Pct).Take(busySlots))
            picked.Add(r);

        foreach (var r in byName.Values
                     .Where(r => r.Local + r.NonLocal >= MinBytes && !picked.Contains(r))
                     .OrderByDescending(r => r.Local + r.NonLocal))
        {
            if (picked.Count >= max) break;
            picked.Add(r);
        }

        // Slots the VRAM pass left empty go back to the next busiest.
        foreach (var r in byName.Values.Where(r => r.Pct >= MinPct && !picked.Contains(r)).OrderByDescending(r => r.Pct))
        {
            if (picked.Count >= max) break;
            picked.Add(r);
        }

        picked.Sort((a, b) => b.Pct != a.Pct ? b.Pct.CompareTo(a.Pct) : (b.Local + b.NonLocal).CompareTo(a.Local + a.NonLocal));
        return picked;
    }
}
