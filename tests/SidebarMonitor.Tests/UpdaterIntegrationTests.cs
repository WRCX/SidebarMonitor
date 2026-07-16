using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Hits the real GitHub API (network + a published release required), so it stays out of CI's
/// Category=Unit filter and is run by hand: `dotnet test --filter Category=Integration`.
///
/// It exists because the unit tests could not have caught the bug it guards. They pinned
/// <see cref="Updater.IsFlavorAsset"/> against names we invented in the test file, while the actual
/// releases were named something else entirely — so the updater silently found no asset and sent
/// every user to the browser instead of installing. That mismatch is only visible by asking the real
/// release what its MSI is called.
/// </summary>
[Trait("Category", "Integration")]
public class UpdaterIntegrationTests
{
    [Fact]
    public async Task CheckAsync_finds_the_msi_asset_on_the_latest_release()
    {
        var rel = await Updater.CheckAsync("full");

        Assert.NotNull(rel);   // null = offline / rate-limited / no releases; re-run before believing a failure
        Assert.NotNull(rel!.HtmlUrl);
        // The point of the test: an asset was matched. Without it the UI cannot self-update at all.
        Assert.True(rel.AssetUrl is not null,
            $"No MSI asset matched on release {Updater.Format(rel.Version)}. The in-app updater would " +
            "fall back to opening the browser. Check the asset's name against Updater.IsFlavorAsset.");
        Assert.True(Updater.IsTrustedGitHubUrl(rel.AssetUrl));
    }
}
