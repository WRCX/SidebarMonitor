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
}
