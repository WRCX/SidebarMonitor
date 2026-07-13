using SidebarMonitor.Etw;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// The THM_TCON_CUR_TMP decode (SMN 0x00059800): bits [31:21] are Tctl in 0.125 °C steps, bit 19
/// selects the -49..206 °C range (subtract 49). The reference raw value below was captured live on
/// a 7840HS (Phoenix) and cross-checked against the 0.125 °C/LSB scale.
/// </summary>
public class PawnIoCpuTests
{
    [Fact]
    public void DecodeTctl_PlainRange_ScalesBy0125()
    {
        // 400 steps * 0.125 = 50.0 °C, range-select bit clear.
        ulong raw = 400UL << 21;
        Assert.Equal(50.0, PawnIoCpu.DecodeTctl(raw));
    }

    [Fact]
    public void DecodeTctl_RangeSelSet_Subtracts49()
    {
        ulong raw = (400UL << 21) | (1UL << 19);
        Assert.Equal(1.0, PawnIoCpu.DecodeTctl(raw));
    }

    [Fact]
    public void DecodeTctl_CapturedPhoenixValue_DecodesTo5675()
    {
        // Captured on the 7840HS: 0x69CB0000 → 846 steps = 105.75, range bit set → 56.75 °C.
        Assert.Equal(56.75, PawnIoCpu.DecodeTctl(0x69CB0000UL));
    }

    [Fact]
    public void DecodeTctl_IgnoresBitsOutsideTheField()
    {
        // Junk in bits 18..0 (range-select bit 19 clear) must not leak into the temperature.
        ulong raw = (400UL << 21) | 0x5FFFFUL;
        Assert.Equal(50.0, PawnIoCpu.DecodeTctl(raw));
    }

    [Fact]
    public void DecodeTctl_MaxField_DoesNotOverflow()
    {
        // All 11 bits set: 2047 * 0.125 = 255.875 (implausible; the caller rejects >=150).
        ulong raw = 0x7FFUL << 21;
        Assert.Equal(255.875, PawnIoCpu.DecodeTctl(raw));
    }

    // ── PM_Table (Phoenix 0x4C0007) — values below were captured live on the 7840HS ─────────────

    private static float[] PhoenixTable()
    {
        var t = new float[24];
        t[0] = 45f; t[1] = 40.5f;      // STAPM limit/value
        t[2] = 45f; t[3] = 43.373f;    // fast PPT limit/value (== socket power)
        t[4] = 45f; t[5] = 34.911f;    // slow PPT limit/value
        t[8] = 70f; t[9] = 31.578f;    // VDD TDC limit/value
        t[16] = 100f; t[17] = 77.196f; // THM limit/value
        return t;
    }

    [Fact]
    public void MapPmTable_Phoenix_FillsPowerFields()
    {
        var d = default(PawnIoCpu.Data);
        Assert.True(PawnIoCpu.TryMapPmTable(0x4C0007, PhoenixTable(), ref d));
        Assert.True(d.HasPower);
        Assert.Equal(43.373f, d.PackageW);
        Assert.Equal(100f * 43.373f / 45f, d.PptPct, 3);
        Assert.Equal(100f * 31.578f / 70f, d.TdcPct, 3);
        Assert.Equal(100f, d.TjMaxC);
    }

    [Fact]
    public void MapPmTable_UnknownVersion_RefusesToGuess()
    {
        // 0x4C0005 exists in the wild (early Phoenix AGESA) but RyzenAdj has no offsets for it.
        var d = default(PawnIoCpu.Data);
        Assert.False(PawnIoCpu.TryMapPmTable(0x4C0005, PhoenixTable(), ref d));
        Assert.False(d.HasPower);
        Assert.False(PawnIoCpu.KnownPmTableVersion(0x4C0005));
        Assert.False(PawnIoCpu.KnownPmTableVersion(0));
    }

    [Theory]
    [InlineData(0x370005ul)]   // Renoir
    [InlineData(0x400005ul)]   // Cezanne
    [InlineData(0x450005ul)]   // Rembrandt
    [InlineData(0x4C0008ul)]   // Hawk Point
    public void MapPmTable_ClassicApuHeader_SameOffsetsAsPhoenix(ulong version)
    {
        var d = default(PawnIoCpu.Data);
        Assert.True(PawnIoCpu.TryMapPmTable(version, PhoenixTable(), ref d));
        Assert.Equal(43.373f, d.PackageW);
        Assert.Equal(100f * 31.578f / 70f, d.TdcPct, 3);
        Assert.Equal(100f, d.TjMaxC);
    }

    [Fact]
    public void MapPmTable_StrixPoint_TdcMoved()
    {
        var t = new float[24];
        t[2] = 54f; t[3] = 40f;          // fast PPT
        t[12] = 70f; t[13] = 35f;        // TDC pair moved down on Strix
        t[16] = 100f;
        var d = default(PawnIoCpu.Data);
        Assert.True(PawnIoCpu.TryMapPmTable(0x5D0008, t, ref d));
        Assert.Equal(40f, d.PackageW);
        Assert.Equal(50f, d.TdcPct);
        Assert.Equal(100f, d.TjMaxC);
    }

    [Fact]
    public void MapPmTable_RavenRidge_TdcAt6_NoTjmax()
    {
        var t = new float[24];
        t[2] = 25f; t[3] = 12.5f;
        t[6] = 45f; t[7] = 9f;           // Zen1 APU TDC pair
        t[16] = 100f;                     // whatever sits here on Zen1 is NOT the THM limit
        var d = default(PawnIoCpu.Data);
        Assert.True(PawnIoCpu.TryMapPmTable(0x1E0004, t, ref d));
        Assert.Equal(12.5f, d.PackageW);
        Assert.Equal(20f, d.TdcPct);
        Assert.Equal(0f, d.TjMaxC);      // deliberately not trusted on this family
    }

    [Fact]
    public void MapPmTable_GarbledRead_IsRejected()
    {
        // Zeroed table (failed DMA / short read): a zero PPT limit must not divide or publish.
        var d = default(PawnIoCpu.Data);
        Assert.False(PawnIoCpu.TryMapPmTable(0x4C0007, new float[24], ref d));
        Assert.False(d.HasPower);
    }

    [Fact]
    public void MapPmTable_AbsurdThmLimit_DropsTjmaxKeepsPower()
    {
        var t = PhoenixTable();
        t[16] = 4000f;   // garbage where the THM limit should be
        var d = default(PawnIoCpu.Data);
        Assert.True(PawnIoCpu.TryMapPmTable(0x4C0007, t, ref d));
        Assert.Equal(0f, d.TjMaxC);
        Assert.Equal(43.373f, d.PackageW);
    }
}
