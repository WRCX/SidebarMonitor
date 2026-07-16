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

    // ── Other logged-in users ────────────────────────────────────────────────────────────────────
    //
    // This is a PER-MACHINE install, and one machine can only run one version (a single elevated
    // helper owns the machine-unique kernel ETW session, and every session's UI reads its map — two
    // versions would mean a contract mismatch, i.e. no sensors for somebody). So an update is a
    // machine-wide act: it stops the sidebar of every other logged-in user too. That is not something
    // to do to people silently — name them, and let whoever is updating decide.

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool WTSEnumerateSessionsW(nint server, int reserved, int version, out nint sessions, out int count);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public nint WinStationName;
        public int State;   // 0 = WTSActive, 1 = WTSConnected, 4 = WTSDisconnected
    }

    /// <summary>
    /// User names of OTHER Windows users with a live session (active or merely disconnected — fast
    /// user switching leaves the session, and its UI, running). Empty when we're alone, and empty on
    /// any failure: a best-effort courtesy must never block an update.
    /// </summary>
    public static IReadOnlyList<string> OtherLoggedInUsers()
    {
        const int WtsUserName = 5, WtsActive = 0, WtsDisconnected = 4;
        var users = new List<string>();
        try
        {
            if (!WTSEnumerateSessionsW(nint.Zero, 0, 1, out nint list, out int count)) return users;
            try
            {
                int size = System.Runtime.InteropServices.Marshal.SizeOf<WtsSessionInfo>();
                int mine = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                for (int i = 0; i < count; i++)
                {
                    var s = System.Runtime.InteropServices.Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                    if (s.SessionId == mine || s.SessionId == 0) continue;          // us, and services
                    if (s.State != WtsActive && s.State != WtsDisconnected) continue; // no logged-in user
                    if (!WTSQuerySessionInformationW(nint.Zero, s.SessionId, WtsUserName, out nint buf, out _)) continue;
                    try
                    {
                        string? name = System.Runtime.InteropServices.Marshal.PtrToStringUni(buf);
                        if (!string.IsNullOrWhiteSpace(name)) users.Add(name);
                    }
                    finally { WTSFreeMemory(buf); }
                }
            }
            finally { WTSFreeMemory(list); }
        }
        catch { /* best effort */ }
        return users;
    }

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
            if (!TryParseTag(tag, out var ver)) return null;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            string html = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            // First match wins: a release can legitimately carry both spellings (CI's "SidebarMonitor.msi"
            // and a hand-uploaded "SidebarMonitor-1.4.9.msi"), and they are the same bits — but without
            // stopping, the one we'd use is whichever GitHub happened to list last.
            string? asset = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (IsFlavorAsset(a.GetProperty("name").GetString(), flavor))
                    {
                        string? u = a.GetProperty("browser_download_url").GetString();
                        if (IsTrustedGitHubUrl(u)) { asset = u; break; }   // reject non-HTTPS / non-GitHub hosts
                    }

            return new Release(ver, notes, html, asset);
        }
        catch { return null; }
    }

    /// <summary>
    /// True if a release asset is the MSI for <paramref name="flavor"/>. The name carries an OPTIONAL
    /// trailing "-&lt;version&gt;": CI names it "SidebarMonitor.msi", while releases cut by hand have
    /// shipped as "SidebarMonitor-1.4.8.msi". Both must match, or the updater finds no asset and falls
    /// back to opening the browser — which is what every release up to 1.4.8 actually did. The two
    /// flavours must never cross-match: a "lite" install may not be handed the full MSI.
    /// </summary>
    internal static bool IsFlavorAsset(string? name, string flavor)
    {
        if (name is null || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return false;
        string stem = name[..^4];
        int dash = stem.LastIndexOf('-');
        // Only a parseable version is stripped, so "-lite" survives and stays part of the flavour.
        if (dash > 0 && Version.TryParse(stem[(dash + 1)..], out _)) stem = stem[..dash];
        string wanted = flavor == "lite" ? "SidebarMonitor-lite" : "SidebarMonitor";
        return string.Equals(stem, wanted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Compares major.minor.build only (revision is ignored; AssemblyVersion is pinned).</summary>
    public static bool IsNewer(Version latest, Version current)
    {
        static Version N(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
        return N(latest) > N(current);
    }

    /// <summary>Formats a version as "vMajor.Minor.Patch" (revision ignored — AssemblyVersion is pinned).</summary>
    public static string Format(Version v) => $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    /// <summary>Parses a release tag ("v1.2.3", or "v1.2.3-rc1" with the pre-release suffix stripped).</summary>
    internal static bool TryParseTag(string tag, out Version ver)
    {
        string t = tag.TrimStart('v', 'V');
        int dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v)) { ver = v; return true; }
        ver = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Only accept an asset URL over HTTPS from a GitHub host — its MSI is later run elevated.</summary>
    internal static bool IsTrustedGitHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "github.com" || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                                || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads the release's MSI and hands off to msiexec via a hidden relauncher, then invokes
    /// <paramref name="shutdown"/> so the UI exits and msiexec can replace it. Returns false if there's
    /// nothing to apply (no asset / not MSI-installed); the caller should open <see cref="Release.HtmlUrl"/>
    /// in that case. One UAC prompt appears (per-machine MSI).
    /// </summary>
    public static async Task<bool> ApplyAsync(Release r, Install install, Action shutdown, bool silent = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!install.FromMsi || install.Path is null || r.AssetUrl is null) return false;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor", "update");
        Directory.CreateDirectory(dir);
        // Local name from our own flavour constant, never from the (attacker-influenceable) URL path.
        string msi = Path.Combine(dir, install.Flavor == "lite" ? "SidebarMonitor-lite.msi" : "SidebarMonitor.msi");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SidebarMonitor-Updater");
            // HttpClient.Timeout only bounds the headers with ResponseHeadersRead; cap the streaming
            // body separately so a stalled connection can't hang "Instalando…" forever.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            var dct = cts.Token;
            using var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, dct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(dct);
            await using var dst = File.Create(msi);
            var buf = new byte[81920];
            long got = 0; int lastPct = -1, n;
            while ((n = await src.ReadAsync(buf, dct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), dct);
                got += n;
                if (total is > 0 && progress is not null)
                {
                    int pct = (int)(got * 100 / total.Value);
                    if (pct != lastPct) { lastPct = pct; progress.Report(Loc.T("Descargando… {0}%", pct)); }
                }
            }
        }
        progress?.Report(Loc.T("Instalando…"));

        // A detached, hidden relauncher: run msiexec (elevates; a prompt appears unless elevation is
        // silent), then start the new UI. wscript with window style 0 keeps it console-less and it is
        // not killed when this UI exits. silent => /qn (no msiexec UI at all) for the zero-friction path;
        // otherwise /qb shows a basic progress bar.
        string flag = silent ? "/qn" : "/qb";
        int show = silent ? 0 : 1;
        string ui = Path.Combine(install.Path, "SidebarMonitor.UI.exe");
        string vbs = Path.Combine(dir, "apply-update.vbs");
        await File.WriteAllLinesAsync(vbs, new[]
        {
            "Set sh = CreateObject(\"WScript.Shell\")",
            $"sh.Run \"msiexec /i \"\"{msi}\"\" {flag}\", {show}, True",
            $"sh.Run \"\"\"{ui}\"\"\", 0, False",
        }, ct);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "wscript.exe", $"//B //Nologo \"{vbs}\"") { UseShellExecute = false });

        shutdown();
        return true;
    }
}
