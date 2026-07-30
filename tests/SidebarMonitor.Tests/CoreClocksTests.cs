using SidebarMonitor.Etw;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Pins one invariant: <b>"GHz máx" is the maximum of the per-core clocks the UI shows.</b>
///
/// It did not hold. The maximum came from the AMD SDK's per-core requested clock while the core rows
/// came from PDH's nominal × % Processor Performance — two unsynchronised estimates of different
/// quantities — so on a 7800X3D a core row could read ABOVE the maximum printed above it. Before 1.5.0
/// the maximum was a max() over PDH too, which hid the split; removing that clock's upward bias
/// exposed it. Both now come from one array in one function, and these tests are what keeps them there.
/// </summary>
[Trait("Category", "Unit")]
public class CoreClocksTests
{
    private static (float Best, float[] PerCore) Publish(double[] cores, int? count = null)
    {
        var perCore = new float[16];
        float best = CoreClocks.Publish(cores, count ?? cores.Length, perCore);
        return (best, perCore);
    }

    /// <summary>The invariant itself, on a plausible 7800X3D window: one core boosting, the rest low.</summary>
    [Fact]
    public void Best_is_the_max_of_the_published_rows()
    {
        var (best, rows) = Publish([5037, 4925, 4200, 4200, 3800, 3800, 550, 550]);

        Assert.Equal(5037, best);
        Assert.Equal(best, rows.Where(v => !float.IsNaN(v)).Max());
    }

    /// <summary>
    /// The reason this is one function. A glitch dropped from the maximum but published to its row
    /// would beat the maximum again — the original bug in a new costume.
    /// </summary>
    [Fact]
    public void A_glitch_read_is_dropped_from_the_rows_too()
    {
        var (best, rows) = Publish([9999, 4900, 4200, 4200]);

        Assert.Equal(4900, best);
        Assert.True(float.IsNaN(rows[0]), "the 9999 MHz glitch was published to its row");
        Assert.Equal(best, rows.Where(v => !float.IsNaN(v)).Max());
    }

    /// <summary>A parked core reads 0, which is not a clock — it must not become a 0 MHz row.</summary>
    [Fact]
    public void An_unreadable_core_is_NaN_not_zero()
    {
        var (_, rows) = Publish([4900, 0, 4200]);

        Assert.True(float.IsNaN(rows[1]));
    }

    /// <summary>Cores beyond the SDK's count hold stale garbage; only coreCount entries are real.</summary>
    [Fact]
    public void Only_the_reported_cores_are_published()
    {
        var (best, rows) = Publish([4900, 4800, 5900, 5900, 5900, 5900, 5900, 5900], count: 2);

        Assert.Equal(4900, best);
        Assert.True(float.IsNaN(rows[2]), "a core past coreCount was published");
        Assert.Equal(best, rows.Where(v => !float.IsNaN(v)).Max());
    }

    /// <summary>Every row NaN and a 0 maximum, rather than a stale reading, when the SDK gave nothing.</summary>
    [Fact]
    public void Nothing_readable_is_zero_and_all_NaN()
    {
        var (best, rows) = Publish([0, 0, 0, 0]);

        Assert.Equal(0, best);
        Assert.All(rows, v => Assert.True(float.IsNaN(v)));
    }

    /// <summary>The rows are wiped every window: yesterday's clock must not linger on a core the SDK
    /// has stopped reporting.</summary>
    [Fact]
    public void Rows_are_cleared_before_filling()
    {
        var perCore = new float[16];
        CoreClocks.Publish([4900, 4800, 4700, 4600], 4, perCore);
        CoreClocks.Publish([4900, 4800], 2, perCore);

        Assert.True(float.IsNaN(perCore[2]), "a row from the previous window survived");
        Assert.True(float.IsNaN(perCore[3]));
    }

    /// <summary>A 16-core part must not walk past the fixed-width arrays the snapshot carries.</summary>
    [Fact]
    public void More_cores_than_the_array_holds_is_clamped()
    {
        var perCore = new float[16];
        var cores = Enumerable.Range(0, 24).Select(i => 4000.0 + i).ToArray();

        float best = CoreClocks.Publish(cores, 24, perCore);

        Assert.Equal(4015, best);   // index 15 is the last one that fits
        Assert.Equal(best, perCore.Where(v => !float.IsNaN(v)).Max());
    }
}
