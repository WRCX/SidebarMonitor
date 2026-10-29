using SidebarMonitor.Shared;
using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>SYSTEM_BATTERY_STATE decoding: the sentinels, and a sign that firmware does not always get right.</summary>
[Trait("Category", "Unit")]
public class BatteryDecodeTests
{
    [Fact]
    public void A_desktop_has_no_battery_and_no_numbers()
    {
        var b = BatteryDecode.Decode(acOnline: true, present: false, false, false, 0, 0, 0, 0);
        Assert.Equal(0, b.Present);
        Assert.True(float.IsNaN(b.Pct) && float.IsNaN(b.RateW) && float.IsNaN(b.FullWh));
        Assert.Equal(-1, b.SecondsLeft);
    }

    [Fact]
    public void Discharging_reads_negative_watts_and_windows_time_to_empty()
    {
        var b = BatteryDecode.Decode(false, true, false, true, 50_000, 25_000, unchecked((uint)-12_500), 7200);
        Assert.Equal(50f, b.Pct);
        Assert.Equal(-12.5f, b.RateW);
        Assert.Equal(25f, b.RemainingWh);
        Assert.Equal(7200, b.SecondsLeft);
    }

    /// <summary>Some firmware reports the discharge rate as a positive magnitude. The flags win.</summary>
    [Fact]
    public void A_positive_rate_while_discharging_is_still_a_drain()
        => Assert.Equal(-12.5f, BatteryDecode.Decode(false, true, false, true, 50_000, 25_000, 12_500, 7200).RateW);

    [Fact]
    public void Without_windows_estimate_time_to_empty_comes_from_capacity_over_rate()
        => Assert.Equal(7200, BatteryDecode.Decode(false, true, false, true, 50_000, 25_000, unchecked((uint)-12_500),
                                                   BatteryDecode.UnknownTime).SecondsLeft);

    [Fact]
    public void Charging_estimates_time_to_full()
    {
        var b = BatteryDecode.Decode(true, true, true, false, 50_000, 40_000, 20_000, BatteryDecode.UnknownTime);
        Assert.Equal(20f, b.RateW);
        Assert.Equal(1800, b.SecondsLeft);   // 10 Wh missing at 20 W
    }

    [Fact]
    public void Unknown_rate_and_capacity_stay_unknown_not_zero()
    {
        var b = BatteryDecode.Decode(false, true, false, true, BatteryDecode.UnknownCapacity, BatteryDecode.UnknownCapacity,
                                     BatteryDecode.UnknownRate, BatteryDecode.UnknownTime);
        Assert.True(float.IsNaN(b.Pct) && float.IsNaN(b.RateW) && float.IsNaN(b.RemainingWh));
        Assert.Equal(-1, b.SecondsLeft);
    }

    [Fact]
    public void Remaining_above_full_after_calibration_reads_100()
        => Assert.Equal(100f, BatteryDecode.Decode(true, true, false, false, 50_000, 50_400, 0, BatteryDecode.UnknownTime).Pct);

    [Theory]
    [InlineData(30, "<1 min")]
    [InlineData(45 * 60, "45 min")]
    [InlineData(3 * 3600 + 5 * 60, "3 h 05 min")]
    public void Durations_read_in_hours_and_minutes(int seconds, string expected)
        => Assert.Equal(expected, BatterySection.Duration(seconds));
}

[Trait("Category", "Unit")]
public class AlertRuleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);
    private static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Fires_only_after_the_condition_has_held_for_the_sustain_time()
    {
        var r = new AlertRule(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
        Assert.False(r.Update(true, false, At(0)));
        Assert.False(r.Update(true, false, At(9)));
        Assert.True(r.Update(true, false, At(10)));
    }

    [Fact]
    public void A_dip_below_the_threshold_restarts_the_sustain()
    {
        var r = new AlertRule(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
        r.Update(true, false, At(0));
        r.Update(false, false, At(5));          // a one-tick dip
        Assert.False(r.Update(true, false, At(12)));
        Assert.True(r.Update(true, false, At(22)));
    }

    [Fact]
    public void Staying_over_the_threshold_fires_once()
    {
        var r = new AlertRule(TimeSpan.Zero, TimeSpan.FromMinutes(10));
        Assert.True(r.Update(true, false, At(0)));
        for (int s = 1; s < 3600; s += 60) Assert.False(r.Update(true, false, At(s)));
    }

    [Fact]
    public void Hovering_on_the_line_does_not_re_arm_without_the_margin()
    {
        var r = new AlertRule(TimeSpan.Zero, TimeSpan.Zero);
        Assert.True(r.Update(true, false, At(0)));
        Assert.False(r.Update(false, false, At(1)));   // just under, not past the margin
        Assert.False(r.Update(true, false, At(2)));
        Assert.False(r.Update(false, true, At(3)));    // clearly back: re-armed
        Assert.True(r.Update(true, false, At(4)));
    }

    [Fact]
    public void Re_armed_but_within_the_cooldown_stays_quiet_until_it_passes()
    {
        var r = new AlertRule(TimeSpan.Zero, TimeSpan.FromMinutes(10));
        Assert.True(r.Update(true, false, At(0)));
        r.Update(false, true, At(60));
        Assert.False(r.Update(true, false, At(120)));
        Assert.True(r.Update(true, false, At(601)));
    }
}

[Trait("Category", "Unit")]
public class AlertMonitorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static (AlertMonitor M, List<string> Sent, List<(string, long, long)> Drives) Make(UiConfig cfg)
    {
        var sent = new List<string>();
        var drives = new List<(string, long, long)>();
        return (new AlertMonitor(cfg, (t, m) => sent.Add($"{t}|{m}"), () => drives), sent, drives);
    }

    private static Snapshot Cool()
    {
        var s = default(Snapshot);
        s.Cpu.TempC = 50; s.Cpu.TjMaxC = 89;
        s.Mem.PhysTotal = 100; s.Mem.PhysUsed = 50;
        return s;
    }

    [Fact]
    public void A_hot_gpu_fires_once_after_ten_seconds_naming_the_card()
    {
        Loc.Init("es");
        var (m, sent, _) = Make(new UiConfig { AlertGpuTempC = 85 });
        var s = Cool();
        s.GpuCount = 1; s.Gpus[0].HasDetail = 1; s.Gpus[0].TempC = 88;
        NameField.Set(ref s.Gpus[0].Name, "RTX 4070");
        for (int i = 0; i <= 30; i++) m.Evaluate(ref s, T0.AddSeconds(i));
        Assert.Equal(["GPU muy caliente|RTX 4070: 88 °C"], sent);
    }

    [Fact]
    public void A_gpu_without_real_telemetry_never_fires()
    {
        var (m, sent, _) = Make(new UiConfig { AlertGpuTempC = 30 });
        var s = Cool();
        s.GpuCount = 1; s.Gpus[0].HasDetail = 0; s.Gpus[0].TempC = 99;
        for (int i = 0; i <= 30; i++) m.Evaluate(ref s, T0.AddSeconds(i));
        Assert.Empty(sent);
    }

    [Fact]
    public void Cpu_alert_uses_the_panels_red_zone_relative_to_tjmax()
    {
        Loc.Init("es");
        var (m, sent, _) = Make(new UiConfig());
        var s = Cool();
        s.Cpu.TempC = 84.5f;                       // Tjmax 89 → red zone starts at 85
        for (int i = 0; i <= 20; i++) m.Evaluate(ref s, T0.AddSeconds(i));
        Assert.Empty(sent);
        s.Cpu.TempC = 86;
        for (int i = 21; i <= 40; i++) m.Evaluate(ref s, T0.AddSeconds(i));
        Assert.Single(sent);
        Assert.StartsWith("CPU muy caliente|86 °C, a 3 °C", sent[0]);
    }

    [Fact]
    public void A_switched_off_alert_stays_silent()
    {
        var (m, sent, _) = Make(new UiConfig { AlertCpuTemp = false });
        var s = Cool();
        s.Cpu.TempC = 95;
        for (int i = 0; i <= 30; i++) m.Evaluate(ref s, T0.AddSeconds(i));
        Assert.Empty(sent);
    }

    [Fact]
    public void Low_battery_only_while_discharging()
    {
        Loc.Init("es");
        var (m, sent, _) = Make(new UiConfig { AlertBatteryPct = 15 });
        var s = Cool();
        s.Battery = BatteryDecode.Decode(true, true, true, false, 50_000, 6_000, 20_000, BatteryDecode.UnknownTime);   // 12 %, charging
        m.Evaluate(ref s, T0);
        Assert.Empty(sent);
        s.Battery = BatteryDecode.Decode(false, true, false, true, 50_000, 6_000, unchecked((uint)-10_000), 2160);     // 12 %, draining
        m.Evaluate(ref s, T0.AddSeconds(1));
        Assert.Equal(["Batería baja|12 % · quedan 36 min"], sent);
    }

    [Fact]
    public void A_nearly_full_drive_is_reported_once_a_minute_at_most()
    {
        Loc.Init("es");
        var (m, sent, drives) = Make(new UiConfig { AlertDiskFreePct = 10 });
        drives.Add(("C:", 5L << 30, 100L << 30));   // 5 % free
        drives.Add(("D:", 50L << 30, 100L << 30));  // fine
        var s = Cool();
        m.Evaluate(ref s, T0);
        m.Evaluate(ref s, T0.AddSeconds(30));
        Assert.Equal(["Poco espacio en disco|C: — quedan 5.0 GiB (5 % libre)"], sent);
    }

    [Fact]
    public void Alerts_firing_together_arrive_as_one_notification()
    {
        Loc.Init("es");
        var (m, sent, drives) = Make(new UiConfig { AlertDiskFreePct = 10 });
        drives.Add(("C:", 5L << 30, 100L << 30));
        drives.Add(("D:", 2L << 30, 100L << 30));
        var s = Cool();
        m.Evaluate(ref s, T0);
        Assert.Equal(["2 alertas|Poco espacio en disco: C: — quedan 5.0 GiB (5 % libre)\n" +
                      "Poco espacio en disco: D: — quedan 2.0 GiB (2 % libre)"], sent);
    }
}

[Trait("Category", "Unit")]
public class SectionOrderTests
{
    private static readonly string[] Registered = ["cpu", "ram", "bat", "gpu", "net"];

    [Fact]
    public void No_saved_order_is_the_registration_order()
        => Assert.Equal(Registered, SectionOrder.Merge([], Registered));

    /// <summary>A section added in a later version lands after its predecessor, not at the very end.</summary>
    [Fact]
    public void A_new_section_slots_in_after_its_predecessor_in_a_saved_order()
        => Assert.Equal(["net", "cpu", "ram", "bat", "gpu"], SectionOrder.Merge(["net", "cpu", "ram", "gpu"], Registered));

    [Fact]
    public void Unknown_and_repeated_saved_keys_are_dropped()
        => Assert.Equal(Registered, SectionOrder.Merge(["cpu", "gone", "ram", "cpu", "bat", "gpu", "net"], Registered));
}
