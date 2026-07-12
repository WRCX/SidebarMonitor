using System.Runtime.InteropServices;
using System.Text;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Agent;

/// <summary>
/// AMD GPU telemetry through ADLX, reached via our own AdlxShim.dll (a flat-C bridge over ADLX's
/// C++/COM API — see native/AdlxShim). ADLX ships with the AMD Adrenalin driver and needs no
/// elevation or driver of ours, so this runs in the unelevated AOT agent alongside NVML. It lights
/// up temp/power/fan/clocks/VRAM for a Radeon dGPU or Ryzen iGPU that we would otherwise only see
/// through the vendor-neutral GPU-Engine counter.
/// </summary>
internal sealed class Adlx : IDisposable
{
    // The flat record AdlxShim fills. Layout MUST match struct AdlxGpu in AdlxShim.cpp (pack 1).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct AdlxGpu
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Name;
        public double UsagePct, TempC, HotspotC, PowerW, TotalBoardW, VramMB;
        public int ClockMhz, MemClockMhz, FanRpm, FanPct, VoltageMV;
    }

    [DllImport("AdlxShim.dll")] private static extern int AdlxOpen();
    [DllImport("AdlxShim.dll")] private static extern int AdlxRead(int index, out AdlxGpu g);
    [DllImport("AdlxShim.dll")] private static extern void AdlxClose();

    public int Count { get; }

    private Adlx(int count) => Count = count;

    /// <summary>Opens ADLX and returns a handle if any AMD GPU is present, else null (no Adrenalin
    /// driver, non-AMD box, or ADLX failure) — the caller then keeps its vendor-neutral behaviour.</summary>
    public static Adlx? TryOpen(out string? error)
    {
        error = null;
        try
        {
            int n = AdlxOpen();
            if (n < 0) { error = $"AdlxOpen devolvio {n} (driver AMD ausente?)"; return null; }
            if (n == 0) { AdlxClose(); error = "sin GPUs AMD"; return null; }
            return new Adlx(n);
        }
        catch (DllNotFoundException)
        {
            error = "AdlxShim.dll no encontrada";
            return null;
        }
        catch (Exception ex)
        {
            error = $"ADLX no disponible: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Fills <paramref name="info"/> with ADLX telemetry for the adapter named <paramref name="adapterName"/>,
    /// matched by name (ADLX only lists AMD GPUs, so with a single AMD adapter the match is trivial;
    /// <paramref name="fallbackIndex"/> covers the rare case where the names differ but the ordinals line up).
    /// Returns true and sets telemetry (temp/power/fan/clocks/VRAM/usage) when a match reads successfully.
    /// </summary>
    public bool Fill(string adapterName, int fallbackIndex, ref GpuInfo info)
    {
        int hit = -1;
        AdlxGpu g = default;
        for (int i = 0; i < Count; i++)
        {
            if (AdlxRead(i, out var cur) != 0) continue;
            string name = Decode(cur.Name);
            if (NameMatches(name, adapterName)) { hit = i; g = cur; break; }
        }

        // Names didn't line up but there is exactly one AMD GPU either side: take it.
        if (hit < 0 && Count == 1 && fallbackIndex == 0 && AdlxRead(0, out g) == 0)
            hit = 0;

        if (hit < 0) return false;

        bool haveTemp = !double.IsNaN(g.TempC);
        double power = !double.IsNaN(g.TotalBoardW) ? g.TotalBoardW : g.PowerW;
        bool havePower = !double.IsNaN(power);
        if (!haveTemp && !havePower) return false;   // nothing worth showing → leave HasDetail=0

        if (!double.IsNaN(g.UsagePct)) info.LoadPct = (float)g.UsagePct;
        info.TempC = haveTemp ? (float)g.TempC : float.NaN;
        info.PowerW = havePower ? (float)power : float.NaN;
        if (g.ClockMhz >= 0) info.CoreClockMhz = (uint)g.ClockMhz;
        if (g.MemClockMhz >= 0) info.MemClockMhz = (uint)g.MemClockMhz;
        if (g.FanPct >= 0) info.FanPct = (uint)g.FanPct;
        if (!double.IsNaN(g.VramMB) && g.VramMB > 0) info.VramUsed = (ulong)(g.VramMB * 1024 * 1024);
        return true;
    }

    private static string Decode(byte[] name)
    {
        int len = System.Array.IndexOf(name, (byte)0);
        return Encoding.UTF8.GetString(name, 0, len < 0 ? name.Length : len);
    }

    /// <summary>ADLX and D3DKMT usually report the same adapter name; accept an exact hit or either
    /// string containing the other, to survive trademark-symbol / suffix differences.</summary>
    private static bool NameMatches(string adlx, string adapter)
    {
        if (adlx.Length == 0 || adapter.Length == 0) return false;
        return adlx.Equals(adapter, StringComparison.OrdinalIgnoreCase)
            || adlx.Contains(adapter, StringComparison.OrdinalIgnoreCase)
            || adapter.Contains(adlx, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => AdlxClose();
}
