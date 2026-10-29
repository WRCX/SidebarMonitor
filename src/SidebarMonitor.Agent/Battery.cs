using System.Runtime.InteropServices;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Agent;

/// <summary>
/// The battery, via CallNtPowerInformation(SystemBatteryState). Unelevated and a single call for every
/// pack, with no battery-device enumeration — what the shell's own flyout reads. On a desktop it says
/// BatteryPresent = 0 and the UI shows nothing. Decoding (sentinels, sign) lives in
/// <see cref="BatteryDecode"/> so it can be tested without a battery.
/// </summary>
internal static class Battery
{
    private const int SystemBatteryState = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryStateRaw
    {
        public byte AcOnLine, BatteryPresent, Charging, Discharging;
        public byte Spare0, Spare1, Spare2, Tag;
        public uint MaxCapacity, RemainingCapacity, Rate, EstimatedTime, DefaultAlert1, DefaultAlert2;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength,
                                                      out SystemBatteryStateRaw output, uint outputLength);

    public static void Fill(ref BatteryInfo b)
    {
        uint rc = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, out var r,
                                         (uint)Marshal.SizeOf<SystemBatteryStateRaw>());
        b = rc == 0
            ? BatteryDecode.Decode(r.AcOnLine != 0, r.BatteryPresent != 0, r.Charging != 0, r.Discharging != 0,
                                   r.MaxCapacity, r.RemainingCapacity, r.Rate, r.EstimatedTime)
            : BatteryDecode.Decode(false, false, false, false, 0, 0, 0, 0);   // no answer = no battery
    }
}
