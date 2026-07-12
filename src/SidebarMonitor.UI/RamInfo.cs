using System.Globalization;
using System.Management;
using System.Text;

namespace SidebarMonitor.UI;

/// <summary>
/// Static memory-module info from SMBIOS/DMI via WMI (Win32_PhysicalMemory) — no ring0, no elevation.
/// Gives each stick's size, type (DDR4/DDR5), configured speed (MT/s), slot and manufacturer/part.
/// Read once on a background thread (the first WMI call is slow); the layout never changes at runtime.
/// The parsed sticks are cached so the summary/detail can be reformatted for binary vs decimal units
/// without re-querying. Fails soft to empty if WMI is unavailable.
/// </summary>
internal static class RamInfo
{
    private readonly record struct Stick(ulong Bytes, string Type, uint Speed, string Maker, string Part, string Slot);

    private static readonly List<Stick> _sticks = [];
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static bool Loaded { get; private set; }
    public static bool HasData => _sticks.Count > 0;

    /// <summary>Query WMI off the UI thread, then invoke <paramref name="onLoaded"/> on it.</summary>
    public static void LoadAsync(Action onLoaded)
    {
        var thread = new Thread(() =>
        {
            try { Query(); } catch { /* WMI absent or blocked: stay empty */ }
            Loaded = true;
            try { onLoaded(); } catch { }
        })
        { IsBackground = true, Name = "RamInfo" };
        thread.Start();
    }

    /// <summary>Compact one-liner, e.g. "2× 16 GiB DDR5-6000".</summary>
    public static string SummaryText()
    {
        if (_sticks.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var g in _sticks.GroupBy(s => (s.Bytes, s.Type, s.Speed)).OrderByDescending(g => g.Count()))
        {
            if (sb.Length > 0) sb.Append("  +  ");
            var (bytes, type, speed) = g.Key;
            sb.Append(Ci, $"{g.Count()}× {Size(bytes)}");
            if (type.Length > 0) sb.Append(Ci, $" {type}");
            if (speed > 0) sb.Append(Ci, $"-{speed}");
        }
        return sb.ToString();
    }

    /// <summary>One line per stick: slot, size, type, speed, maker/part.</summary>
    public static string DetailText()
    {
        if (_sticks.Count == 0) return "";
        var det = new StringBuilder();
        foreach (var s in _sticks)
        {
            if (det.Length > 0) det.Append('\n');
            det.Append(Ci, $"{(s.Slot.Length > 0 ? s.Slot : Loc.T("Módulo"))}: {Size(s.Bytes)}");
            if (s.Type.Length > 0) det.Append(Ci, $" {s.Type}");
            if (s.Speed > 0) det.Append(Ci, $"-{s.Speed} MT/s");
            string who = string.Join(' ', new[] { s.Maker, s.Part }.Where(x => x.Length > 0));
            if (who.Length > 0) det.Append(Ci, $"  {who}");
        }
        return det.ToString();
    }

    private static void Query()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber, DeviceLocator, BankLabel FROM Win32_PhysicalMemory");
        foreach (ManagementBaseObject mo in searcher.Get())
        {
            ulong cap = ToU64(mo["Capacity"]);
            if (cap == 0) continue;
            uint configured = ToU32(mo["ConfiguredClockSpeed"]);
            uint rated = ToU32(mo["Speed"]);
            string type = DdrType(ToU32(mo["SMBIOSMemoryType"]));
            string maker = (mo["Manufacturer"] as string ?? "").Trim();
            string part = (mo["PartNumber"] as string ?? "").Trim();
            string dev = (mo["DeviceLocator"] as string ?? "").Trim();
            string bank = (mo["BankLabel"] as string ?? "").Trim();
            // Boards often report DeviceLocator per-channel ("DIMM 0" on both channels); BankLabel
            // (the channel) is what tells the two apart, so include both when they add information.
            string slot = string.Join(" · ", new[] { bank, dev }.Where(x => x.Length > 0).Distinct());
            _sticks.Add(new Stick(cap, type, configured > 0 ? configured : rated, maker, part, slot));
        }
        // Stable order by slot for the detail listing.
        _sticks.Sort((a, b) => string.Compare(a.Slot, b.Slot, StringComparison.OrdinalIgnoreCase));
    }

    // A RAM stick's capacity is inherently a power of two (a "16 GB" module is 16 GiB). Always show
    // it in binary GiB so it reads as its real, marketed size (16 GiB) — never 17.2 GB decimal.
    private static string Size(ulong bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", Ci) + " GiB";

    private static string DdrType(uint smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "DDR5",
        _ => "",
    };

    private static ulong ToU64(object? o) => o is not null && ulong.TryParse(o.ToString(), out ulong v) ? v : 0;
    private static uint ToU32(object? o) => o is not null && uint.TryParse(o.ToString(), out uint v) ? v : 0;
}
