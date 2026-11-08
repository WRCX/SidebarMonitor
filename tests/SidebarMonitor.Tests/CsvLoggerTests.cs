using System.Text;
using SidebarMonitor.Shared;
using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

[Trait("Category", "Unit")]
public class CsvLoggerTests
{
    // The exact header every version before configurable metrics wrote, for a 2-core CPU. Scripts
    // parse these files, so the default selection must keep producing it byte for byte.
    private const string LegacyHeader2Cores =
        "timestamp_utc,cpu_pct,cpu_ghz,cpu_watts,cpu_temp_c,cpu_tjmax_c,ppt_pct,tdc_pct,edc_pct,best_core," +
        "ram_used_gib,ram_total_gib,commit_used_gib," +
        "gpu0_load_pct,gpu0_temp_c,gpu0_watts,gpu0_vram_used_gib," +
        "net_rx_bps,net_tx_bps,disk_read_bps,disk_write_bps" +
        ",core0_pct,core1_pct,core0_ghz,core1_ghz,core0_temp_c,core1_temp_c,core0_c0_pct,core1_c0_pct";

    [Fact]
    public void Default_selection_keeps_the_legacy_header()
        => Assert.Equal(LegacyHeader2Cores, CsvLogger.BuildHeader(CsvLogger.Resolve(null), 2));

    [Fact]
    public void Resolve_follows_catalog_order_and_drops_unknown_keys()
        => Assert.Equal(["cpu_temp", "fan"], CsvLogger.Resolve(["fan", "nope", "CPU_TEMP"]));

    [Fact]
    public void Empty_selection_records_only_the_timestamp()
        => Assert.Equal("timestamp_utc", CsvLogger.BuildHeader(CsvLogger.Resolve([]), 8));

    [Fact]
    public void Fan_cells_are_blank_when_there_is_no_source()
    {
        var s = new Snapshot();
        s.Cpu.FanPct = 38f;
        s.Cpu.FanRpm = float.NaN;
        Assert.Equal(",38,", Row(ref s, "fan"));
    }

    [Fact]
    public void Throttle_uses_the_status_bits_and_the_limit_percentages()
    {
        var s = new Snapshot();
        s.Cpu.TempC = float.NaN;
        s.Cpu.ThrottleFlags = 0b010;    // Intel power-limit bit
        s.Cpu.TdcPct = 99.5f;           // AMD-style current cap
        Assert.Equal(",0,1,1", Row(ref s, "throttle"));
    }

    [Fact]
    public void Throttle_flags_thermal_within_three_degrees_of_tjmax()
    {
        var s = new Snapshot();
        s.Cpu.TjMaxC = 100f;
        s.Cpu.TempC = 97.5f;
        Assert.Equal(",1,0,0", Row(ref s, "throttle"));
    }

    [Fact]
    public void Gpu_fan_is_blank_without_vendor_telemetry()
    {
        var s = new Snapshot { GpuCount = 1 };
        s.Gpus[0].FanPct = 0;           // iGPU: never filled, must not read as "fan stopped"
        Assert.Equal(",", Row(ref s, "gpu0_fan"));

        s.Gpus[0].HasDetail = 1;
        s.Gpus[0].FanPct = 45;
        Assert.Equal(",45", Row(ref s, "gpu0_fan"));
    }

    private static string Row(ref Snapshot s, string key)
    {
        var sb = new StringBuilder();
        CsvLogger.AppendValues(sb, key, ref s, 0);
        return sb.ToString();
    }
}
