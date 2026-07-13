using SidebarMonitor.Shared;
using Xunit;

namespace SidebarMonitor.Tests;

[Trait("Category", "Unit")]
public class ArgParseTests
{
    [Fact]
    public void Reads_int_value() => Assert.Equal(5, ArgParse.Int(["--seconds=5"], "--seconds=", 0));

    [Fact]
    public void Falls_back_when_absent() => Assert.Equal(3, ArgParse.Int([], "--seconds=", 3));

    [Fact]
    public void Falls_back_on_non_numeric() => Assert.Equal(3, ArgParse.Int(["--seconds=abc"], "--seconds=", 3));

    [Fact]
    public void Falls_back_on_empty_value() => Assert.Equal(3, ArgParse.Int(["--seconds="], "--seconds=", 3));

    [Fact]
    public void Accepts_negative() => Assert.Equal(-2, ArgParse.Int(["--x=-2"], "--x=", 0));

    [Fact]
    public void First_match_wins() => Assert.Equal(100, ArgParse.Int(["--w=100", "--w=200"], "--w=", 0));
}
