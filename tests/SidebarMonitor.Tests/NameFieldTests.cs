using SidebarMonitor.Shared;
using Xunit;

namespace SidebarMonitor.Tests;

[Trait("Category", "Unit")]
public class NameFieldTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("café ° ★ ↓")]   // non-ASCII UTF-8, fits in 32 bytes
    public void Roundtrips_utf8(string s)
    {
        var f = default(Name32);
        NameField.Set(ref f, s);
        Assert.Equal(s, NameField.Get(ref f));
    }

    [Fact]
    public void Truncates_and_stays_in_bounds_when_too_long()
    {
        var f = default(Name32);
        string big = new string('x', 200);
        NameField.Set(ref f, big);
        string got = NameField.Get(ref f);
        Assert.True(got.Length <= 32);          // never reads past the 32-byte field
        Assert.StartsWith("xxxx", got);
    }

    [Fact]
    public void Full_field_reserves_a_null_terminator_and_stays_bounded()
    {
        // Set always reserves the last byte for a NUL (writes into dst[..size-1]), so a 32-char input
        // stores at most 31 — which means Get can never run off the end looking for the terminator.
        var f = default(Name32);
        NameField.Set(ref f, new string('a', 32));
        Assert.Equal(31, NameField.Get(ref f).Length);
    }

    [Fact]
    public void Larger_fields_hold_longer_names()
    {
        var f = default(Name160);
        string s = "AMD Ryzen 7 7800X3D 8-Core Processor";
        NameField.Set(ref f, s);
        Assert.Equal(s, NameField.Get(ref f));
    }
}
