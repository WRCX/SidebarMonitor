using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace SidebarMonitor.UI;

/// <summary>
/// Optional, opt-in update check against GitHub Releases, plus one-click apply. Applying downloads the
/// MSI of the <em>same flavour</em> the user installed (read from the registry the MSI wrote) and hands
/// off to <c>msiexec</c>, which does a major upgrade: it stops all three processes, replaces the files
/// atomically, recreates the scheduled task + Run key, and a hidden relauncher brings the UI back — so
/// autostart survives and there's no re-setup, and no contract mismatch can linger (all three update
/// together). The only network call is to GitHub's public API; nothing about the user is sent.
/// </summary>
internal static class Updater
{
    // Placeholder — set to the real GitHub owner/repo once confirmed.
    private const string Owner = "WRCX";
    private const string Repo = "SidebarMonitor";

    /// <summary>How the app is installed. <see cref="FromMsi"/> is false for a dev/LocalAppData install,
    /// where in-place MSI update doesn't apply (we just link to the release instead).</summary>
    public sealed record Install(string? Path, string Flavor, bool FromMsi);

    public sealed record Release(Version Version, string Notes, string HtmlUrl, string? AssetUrl);

    /// <summary>Reads HKLM\Software\SidebarMonitor (written by the MSI). Absent → not MSI-installed.</summary>
    public static Install CurrentInstall()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"Software\SidebarMonitor");
            string? path = k?.GetValue("InstallPath") as string;
            string flavor = (k?.GetValue("Flavor") as string) ?? "full";
            return new Install(path, flavor, path is not null);
        }
        catch { return new Install(null, "full", false); }
    }

    public static Version CurrentVersion()
    {
        var s = typeof(Updater).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
    }

    /// <summary>Queries the latest release and returns it if it parses; null on any failure (offline,
    /// rate-limited, no releases yet). Picks the asset matching <paramref name="flavor"/>.</summary>
    public static async Task<Release?> CheckAsync(string flavor, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SidebarMonitor-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var stream = await http.GetStreamAsync(url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            string html = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            string wanted = flavor == "lite" ? "SidebarMonitor-lite.msi" : "SidebarMonitor.msi";
            string? asset = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase))
                        asset = a.GetProperty("browser_download_url").GetString();

            return new Release(ver, notes, html, asset);
        }
        catch { return null; }
    }

    /// <summary>Compares major.minor.build only (revision is ignored; AssemblyVersion is pinned).</summary>
    public static bool IsNewer(Version latest, Version current)
    {
        static Version N(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
        return N(latest) > N(current);
    }

    /// <summary>
    /// Downloads the release's MSI and hands off to msiexec via a hidden relauncher, then invokes
    /// <paramref name="shutdown"/> so the UI exits and msiexec can replace it. Returns false if there's
    /// nothing to apply (no asset / not MSI-installed); the caller should open <see cref="Release.HtmlUrl"/>
    /// in that case. One UAC prompt appears (per-machine MSI).
    /// </summary>
    public static async Task<bool> ApplyAsync(Release r, Install install, Action shutdown, CancellationToken ct = default)
    {
        if (!install.FromMsi || install.Path is null || r.AssetUrl is null) return false;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor", "update");
        Directory.CreateDirectory(dir);
        string msi = Path.Combine(dir, Path.GetFileName(new Uri(r.AssetUrl).LocalPath));

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SidebarMonitor-Updater");
            await using var src = await http.GetStreamAsync(r.AssetUrl, ct);
            await using var dst = File.Create(msi);
            await src.CopyToAsync(dst, ct);
        }

        // A detached, hidden relauncher: run msiexec (UAC prompt, waits for it), then start the new UI.
        // wscript with window style 0 keeps it console-less; it is not killed when this UI exits.
        string ui = Path.Combine(install.Path, "SidebarMonitor.UI.exe");
        string vbs = Path.Combine(dir, "apply-update.vbs");
        await File.WriteAllLinesAsync(vbs, new[]
        {
            "Set sh = CreateObject(\"WScript.Shell\")",
            $"sh.Run \"msiexec /i \"\"{msi}\"\" /qb\", 1, True",
            $"sh.Run \"\"\"{ui}\"\"\", 0, False",
        }, ct);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "wscript.exe", $"//B //Nologo \"{vbs}\"") { UseShellExecute = false });

        shutdown();
        return true;
    }
}
