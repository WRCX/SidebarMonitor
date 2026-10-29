namespace SidebarMonitor.Shared;

/// <summary>
/// SYSTEM_BATTERY_STATE (CallNtPowerInformation level 5) → <see cref="BatteryInfo"/>. Pure, so the
/// sentinel handling can be tested on a machine with no battery; the agent does the P/Invoke.
/// </summary>
public static class BatteryDecode
{
    // BATTERY_UNKNOWN_* from poclass.h. Capacity and time use all-ones; rate uses the sign bit alone.
    public const uint UnknownCapacity = 0xFFFFFFFF;
    public const uint UnknownTime = 0xFFFFFFFF;
    public const uint UnknownRate = 0x80000000;

    /// <param name="rateRaw">Declared DWORD but signed: negative while discharging. mW.</param>
    /// <param name="maxMWh">Full-charge capacity, mWh.</param>
    /// <param name="remainingMWh">Remaining capacity, mWh.</param>
    /// <param name="estimatedSec">Windows' smoothed time to empty (only meaningful on battery).</param>
    public static BatteryInfo Decode(bool acOnline, bool present, bool charging, bool discharging,
                                     uint maxMWh, uint remainingMWh, uint rateRaw, uint estimatedSec)
    {
        var b = new BatteryInfo
        {
            Present = (byte)(present ? 1 : 0),
            AcOnline = (byte)(acOnline ? 1 : 0),
            Charging = (byte)(charging ? 1 : 0),
            Discharging = (byte)(discharging ? 1 : 0),
            Pct = float.NaN, RateW = float.NaN, RemainingWh = float.NaN, FullWh = float.NaN,
            SecondsLeft = -1,
        };
        if (!present) return b;

        bool haveMax = maxMWh != UnknownCapacity && maxMWh > 0;
        bool haveRem = remainingMWh != UnknownCapacity;
        if (haveMax) b.FullWh = maxMWh / 1000f;
        if (haveRem) b.RemainingWh = remainingMWh / 1000f;
        // Some packs report remaining a little above full right after calibration; a gauge is never
        // over 100 %.
        if (haveMax && haveRem) b.Pct = Math.Clamp(100f * remainingMWh / maxMWh, 0f, 100f);

        // Zero is a real reading (idle on AC, or a full pack); only the sentinel is unknown. The sign
        // must agree with the state flags: some firmware reports the discharge rate as a positive
        // magnitude, and a "+12 W" while the flags say discharging would read as charging.
        int rate = unchecked((int)rateRaw);
        if (rateRaw != UnknownRate)
        {
            float w = Math.Abs(rate) / 1000f;
            b.RateW = discharging ? -w : charging ? w : rate / 1000f;
        }

        if (discharging)
        {
            if (estimatedSec != UnknownTime) b.SecondsLeft = (int)Math.Min(estimatedSec, int.MaxValue);
            else if (haveRem && b.RateW < 0) b.SecondsLeft = (int)(remainingMWh / 1000f / -b.RateW * 3600f);
        }
        else if (charging && haveMax && haveRem && b.RateW > 0 && remainingMWh < maxMWh)
        {
            // Windows gives no time-to-full. A linear estimate overshoots a little near the top (the
            // charger tapers), which the UI says by prefixing it with "~".
            b.SecondsLeft = (int)((maxMWh - remainingMWh) / 1000f / b.RateW * 3600f);
        }
        return b;
    }
}
