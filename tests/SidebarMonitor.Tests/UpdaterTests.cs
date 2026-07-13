using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

[Trait("Category", "Unit")]
public class UpdaterTests
{
    [Theory]
    [InlineData("1.2.0", "1.2.1", true)]
    [InlineData("1.2.1", "1.2.1", false)]
    [InlineData("1.2.9", "1.3.0", true)]
    [InlineData("1.3.0", "1.2.9", false)]   // args are (current, latest) below → latest not newer
    public void IsNewer_compares_major_minor_build(string current, string latest, bool expected)
        => Assert.Equal(expected, Updater.IsNewer(Version.Parse(latest), Version.Parse(current)));

    [Fact]
    public void IsNewer_ignores_revision()
        => Assert.False(Updater.IsNewer(new Version(1, 2, 0, 9), new Version(1, 2, 0, 1)));

    [Theory]
    [InlineData("v1.2.3", true, 1, 2, 3)]
    [InlineData("1.2.3", true, 1, 2, 3)]
    [InlineData("v1.2.3-rc1", true, 1, 2, 3)]   // pre-release suffix stripped
    [InlineData("v1.2", true, 1, 2, -1)]
    [InlineData("vX.Y", false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    public void TryParseTag_handles_prefix_and_prerelease(string tag, bool ok, int maj, int min, int build)
    {
        Assert.Equal(ok, Updater.TryParseTag(tag, out var v));
        if (ok)
        {
            Assert.Equal(maj, v.Major);
            Assert.Equal(min, v.Minor);
            Assert.Equal(build, v.Build);
        }
    }

    [Theory]
    [InlineData("https://github.com/WRCX/SidebarMonitor/releases/download/v1/SidebarMonitor.msi", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/x", true)]
    [InlineData("http://github.com/WRCX/x.msi", false)]          // not HTTPS
    [InlineData("https://evil.com/x.msi", false)]                // wrong host
    [InlineData("https://github.com.evil.com/x.msi", false)]     // host-confusion: NOT github.com
    [InlineData("https://notgithub.com/x.msi", false)]           // suffix-confusion: not ".github.com"
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void IsTrustedGitHubUrl_only_accepts_https_github(string? url, bool expected)
        => Assert.Equal(expected, Updater.IsTrustedGitHubUrl(url));

    [Theory]
    [InlineData(1, 2, 3, "v1.2.3")]
    [InlineData(1, 2, 0, "v1.2.0")]
    public void Format_is_v_major_minor_patch(int a, int b, int c, string expected)
        => Assert.Equal(expected, Updater.Format(new Version(a, b, c)));

    [Fact]
    public void Format_clamps_missing_build_to_zero()
        => Assert.Equal("v1.2.0", Updater.Format(new Version(1, 2)));
}
