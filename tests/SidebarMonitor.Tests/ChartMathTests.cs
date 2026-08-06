using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Pins the NaN handling that keeps a chart drawable. The agent publishes NaN on purpose to mean "PDH
/// had no reading this tick", which is a different statement from 0%, so NaN genuinely arrives — and
/// the CPU chart used to blank itself and label the axis "NaN %" until the sample scrolled out.
/// </summary>
[Trait("Category", "Unit")]
public class ChartMathTests
{
    // The bug: Math.Min/Max propagate NaN, and every comparison against NaN is false, so the intuitive
    // `hi < lo` test reports "these bounds are fine" and the NaN reaches the axis.
    [Theory]
    [InlineData(0d, 100d, true)]        // ordinary window
    [InlineData(50d, 50d, true)]        // flat line: degenerate but usable
    [InlineData(double.MaxValue, double.MinValue, false)]   // empty window, the original case
    [InlineData(double.NaN, 100d, false)]
    [InlineData(0d, double.NaN, false)]
    [InlineData(double.NaN, double.NaN, false)]
    public void BoundsUsable_rejects_empty_and_NaN(double lo, double hi, bool expected)
        => Assert.Equal(expected, ChartMath.BoundsUsable(lo, hi));

    [Fact]
    public void BoundsUsable_would_have_missed_NaN_with_the_naive_test()
    {
        // Documents precisely why the guard is written the way it is: this is the comparison the code
        // used to make, and it waves NaN straight through.
        double lo = double.NaN, hi = double.NaN;
        Assert.False(hi < lo);                        // the old guard: "bounds look fine"
        Assert.False(ChartMath.BoundsUsable(lo, hi)); // the new one: rejected
    }

    [Theory]
    [InlineData(42f, 10f, 42f)]                 // a real reading is kept
    [InlineData(0f, 90f, 0f)]                   // a real 0% is kept, not mistaken for missing
    [InlineData(float.NaN, 37f, 37f)]           // missing → hold the previous value
    [InlineData(float.NaN, 0f, 0f)]
    public void HoldIfMissing_substitutes_only_NaN(float value, float previous, float expected)
        => Assert.Equal(expected, ChartMath.HoldIfMissing(value, previous));

    [Fact]
    public void HoldIfMissing_falls_back_to_zero_when_there_is_no_previous()
        => Assert.Equal(0f, ChartMath.HoldIfMissing(float.NaN, float.NaN));

    [Fact]
    public void HoldIfMissing_never_returns_NaN()
    {
        // The whole point: whatever goes in, the buffer stays drawable.
        foreach (var v in new[] { float.NaN, 0f, 55f, 100f })
            foreach (var p in new[] { float.NaN, 0f, 20f })
                Assert.False(float.IsNaN(ChartMath.HoldIfMissing(v, p)));
    }
}
