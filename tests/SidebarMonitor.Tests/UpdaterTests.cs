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

    // Every release through 1.4.8 shipped its MSI as "SidebarMonitor-<version>.msi", while the updater
    // only ever looked for the bare "SidebarMonitor.msi" that CI produces. No asset matched, so the
    // apply path fell through to opening the browser and the in-app update never actually installed
    // anything. Both spellings have to match — and the flavours must not bleed into each other.
    [Theory]
    [InlineData("SidebarMonitor.msi", "full", true)]
    [InlineData("SidebarMonitor-1.4.8.msi", "full", true)]        // released by hand
    [InlineData("SidebarMonitor-1.4.8.0.msi", "full", true)]      // 4-part version
    [InlineData("sidebarmonitor-1.4.8.MSI", "full", true)]        // case-insensitive
    [InlineData("SidebarMonitor-lite.msi", "full", false)]        // lite MSI must not feed a full install
    [InlineData("SidebarMonitor-lite-1.4.8.msi", "full", false)]
    [InlineData("SidebarMonitor-lite.msi", "lite", true)]
    [InlineData("SidebarMonitor-lite-1.4.8.msi", "lite", true)]
    [InlineData("SidebarMonitor.msi", "lite", false)]             // full MSI must not feed a lite install
    [InlineData("SidebarMonitor-1.4.8.msi", "lite", false)]
    [InlineData("SidebarMonitor.msi.sig", "full", false)]         // not an MSI
    [InlineData("SidebarMonitor-1.4.8.zip", "full", false)]
    [InlineData("SomethingElse.msi", "full", false)]
    [InlineData("SidebarMonitor-nightly.msi", "full", false)]     // "-nightly" is not a version
    [InlineData(null, "full", false)]
    public void IsFlavorAsset_matches_both_bare_and_versioned_names(string? name, string flavor, bool expected)
        => Assert.Equal(expected, Updater.IsFlavorAsset(name, flavor));

    [Theory]
    [InlineData(1, 2, 3, "v1.2.3")]
    [InlineData(1, 2, 0, "v1.2.0")]
    public void Format_is_v_major_minor_patch(int a, int b, int c, string expected)
        => Assert.Equal(expected, Updater.Format(new Version(a, b, c)));

    [Fact]
    public void Format_clamps_missing_build_to_zero()
        => Assert.Equal("v1.2.0", Updater.Format(new Version(1, 2)));
}
