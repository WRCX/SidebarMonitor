using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Pins click semantics for the collapse tab and the minimise button: only a press followed by a
/// release on the same element counts. A bare release — what reopened the collapsed panel after an
/// RDP connect/disconnect — must not.
/// </summary>
[Trait("Category", "Unit")]
public class ClickGateTests
{
    [Fact]
    public void A_release_with_no_press_is_not_a_click()
        => Assert.False(new ClickGate().Release());

    [Fact]
    public void Press_then_release_is_a_click()
    {
        var g = new ClickGate();
        g.Press();
        Assert.True(g.Release());
    }

    [Fact]
    public void Leaving_between_press_and_release_cancels_the_click()
    {
        var g = new ClickGate();
        g.Press();
        g.Leave();
        Assert.False(g.Release());
    }

    [Fact]
    public void One_press_buys_exactly_one_click()
    {
        // A second, stray release right after a real click must not count too.
        var g = new ClickGate();
        g.Press();
        Assert.True(g.Release());
        Assert.False(g.Release());
    }
}
