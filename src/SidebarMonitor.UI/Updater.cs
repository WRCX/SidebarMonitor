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

            // First match wins: a release can legitimately carry both spellings (a bare
            // "SidebarMonitor.msi" from an older build and a versioned "SidebarMonitor-1.4.9.msi"), and
            // they are the same bits — but without stopping, the one we'd use is whichever GitHub
            // happened to list last.
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
    /// trailing "-&lt;version&gt;": the build now always produces the versioned "SidebarMonitor-1.4.9.msi",
    /// but older releases shipped the bare "SidebarMonitor.msi", so both must match — otherwise the
    /// updater finds no asset and falls back to opening the browser, which is what every release up to
    /// 1.4.8 actually did (the bare name it looked for and the versioned name releases carried never
    /// met). The two flavours must never cross-match: a "lite" install may not be handed the full MSI.
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

    /// <summary>
    /// Does this process already hold administrator rights? Only used to phrase what the user is about
    /// to see: elevated, the relauncher's msiexec raises no prompt; unelevated it raises exactly one.
    /// It is NOT a gate any more — the relauncher elevates, so the install works either way.
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }   // can't tell => assume a prompt is coming, and say so
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

    /// <summary>What a call to <see cref="ApplyAsync"/> actually did.</summary>
    public enum ApplyOutcome
    {
        /// <summary>Nothing to apply here: not an MSI install, or no matching asset. The caller should
        /// open <see cref="Release.HtmlUrl"/> instead.</summary>
        NotApplicable,
        /// <summary>Downloaded and handed off; <paramref name="shutdown"/> has been called and the next
        /// thing on screen is a freshly started UI.</summary>
        Started,
        /// <summary>A previous hand-off is still in flight, so this one was not started.</summary>
        AlreadyRunning,
    }

    /// <summary>
    /// Downloads the release's MSI and hands off to msiexec via a hidden relauncher, then invokes
    /// <paramref name="shutdown"/> so the UI exits and msiexec can replace it. One UAC prompt appears
    /// (per-machine MSI). The outcome of the install itself lands in <see cref="UpdateAttempt"/>, not
    /// in the return value: by the time msiexec has an answer this process no longer exists.
    /// </summary>
    public static async Task<ApplyOutcome> ApplyAsync(Release r, Install install, Action shutdown, bool silent = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!install.FromMsi || install.Path is null || r.AssetUrl is null) return ApplyOutcome.NotApplicable;

        // Never start a second hand-off on top of a live one: that msiexec fails with 1618 ("another
        // installation is already in progress"), and a failure is precisely what used to feed the
        // relaunch-and-retry loop. Cheaper to not start than to explain afterwards.
        if (UpdateAttempt.InFlight()) return ApplyOutcome.AlreadyRunning;

        string dir = UpdateAttempt.Dir;
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
        // silent), record how it went, then start the UI. wscript with window style 0 keeps it
        // console-less and it is not killed when this UI exits. silent => /qn (no msiexec UI at all)
        // for the zero-friction path; otherwise /qb shows a basic progress bar.
        //
        // Three things this script did not use to do, all of them one bug:
        //
        //  * Read the exit code. sh.Run with bWaitOnReturn=True has always returned it and nobody
        //    looked, so a failed install was indistinguishable from a successful one — the UI came
        //    back either way, still old, and went looking for updates again.
        //  * Write it down. UpdateAttempt is what the relaunched UI reads to say "no se pudo
        //    actualizar" and, more importantly, to stop installing this version automatically.
        //  * Log. /l*v costs one file and turns "it failed on my machine" into an answer; the same MSI
        //    run by hand with /l*v installed cleanly, which is how we know the package is not at fault.
        //
        // The UI is still started on failure. Not relaunching would leave the machine with no sidebar
        // and no explanation, which is worse than the old behaviour rather than better; what breaks
        // the loop is the recorded failure, not withholding the relaunch.
        string flag = silent ? "/qn" : "/qb";
        string ui = Path.Combine(install.Path, "SidebarMonitor.UI.exe");
        string vbs = Path.Combine(dir, "apply-update.vbs");
        await File.WriteAllLinesAsync(vbs, BuildRelauncher(msi, flag, ui, r.Version), ct);

        // Recorded BEFORE the script is launched, so the script's rewrite (which carries msiexec's
        // exit code) can never be clobbered by this one.
        UpdateAttempt.BeginRunning(r.Version);
        using var runner = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "wscript.exe", $"//B //Nologo \"{vbs}\"") { UseShellExecute = false });
        if (runner is not null) UpdateAttempt.SetRunnerPid(runner.Id);

        shutdown();
        return ApplyOutcome.Started;
    }

    /// <summary>Exit code the script records when it gave up waiting for msiexec to finish.</summary>
    internal const int ExitTimedOut = -1;

    /// <summary>How long the script waits for the elevated msiexec, in seconds. Generous: a UAC prompt
    /// can sit unanswered while somebody is away from the keyboard, and an upgrade on a slow disk is
    /// minutes. Long enough that expiring means something really is wrong.</summary>
    internal const int WaitSeconds = 900;

    /// <summary>
    /// The relauncher script, line by line. Split out so a test can read it without installing
    /// anything — the quoting is the kind of thing that is only ever wrong on somebody else's path.
    /// VBScript doubles a quote to escape it, hence the <c>""</c> around every path.
    ///
    /// It elevates. This is the whole point of the round-trip through a file: a per-machine MSI must
    /// run elevated (unelevated it dies at InstallFinalize with 1925, whatever /q level it is given),
    /// and the only way to ask for elevation from here is ShellExecute with the "runas" verb — which
    /// neither waits nor returns an exit code. So the elevated side is a <c>cmd</c> that runs msiexec
    /// and writes its exit code to a file, and this side waits for the file. Delayed expansion
    /// (<c>/v:on</c> + <c>!errorlevel!</c>) is required: <c>%errorlevel%</c> would be substituted while
    /// the line is parsed, before msiexec has run, and always report the previous value.
    ///
    /// The window style is 0 for both quiet levels: that hides the <c>cmd</c>, not the installer.
    /// msiexec draws its own window, so /qb still shows its progress bar and /qn still shows nothing.
    /// </summary>
    internal static string[] BuildRelauncher(string msi, string flag, string ui, Version version)
    {
        // cmd's own quoting, nested inside a VBScript string literal, where "" is one quote.
        string inner = $"/v:on /c msiexec /i \"\"{msi}\"\" {flag} /l*v \"\"{UpdateAttempt.MsiLogPath}\"\"" +
                       $" & echo !errorlevel!> \"\"{UpdateAttempt.ResultPath}\"\"";
        return
        [
            "' Generated by SidebarMonitor. Elevates msiexec, records the result, restarts the UI.",
            "Set sh = CreateObject(\"WScript.Shell\")",
            "Set fso = CreateObject(\"Scripting.FileSystemObject\")",
            "Set app = CreateObject(\"Shell.Application\")",
            $"res = \"{UpdateAttempt.ResultPath}\"",
            "' A stale result from a previous attempt would be read as this one's.",
            "If fso.FileExists(res) Then fso.DeleteFile res, True",
            "",
            "' Declining the UAC prompt raises an error here, and that is a cancel (1602), not a crash.",
            "rc = 1602",
            "On Error Resume Next",
            $"app.ShellExecute \"cmd.exe\", \"{inner}\", \"\", \"runas\", 0",
            "asked = (Err.Number = 0)",
            "Err.Clear",
            "On Error Goto 0",
            "",
            "If asked Then",
            $"  rc = {ExitTimedOut}",
            "  waited = 0",
            $"  Do While waited < {WaitSeconds}",
            "    line = \"\"",
            "    If fso.FileExists(res) Then",
            "      On Error Resume Next",
            "      Set rf = fso.OpenTextFile(res, 1)",
            "      If Err.Number = 0 Then",
            "        If Not rf.AtEndOfStream Then line = Trim(rf.ReadLine)",
            "        rf.Close",
            "      End If",
            "      Err.Clear",
            "      On Error Goto 0",
            "    End If",
            "    ' The file appears before cmd has written to it, so an empty read means keep waiting.",
            "    If IsNumeric(line) And line <> \"\" Then",
            "      rc = CLng(line)",
            "      Exit Do",
            "    End If",
            "    WScript.Sleep 1000",
            "    waited = waited + 1",
            "  Loop",
            "End If",
            "",
            "' 0 = installed. 3010 and 1641 = installed, Windows wants a reboot. Everything else failed.",
            "If rc = 0 Or rc = 3010 Or rc = 1641 Then",
            "  stage = \"done\"",
            "Else",
            "  stage = \"failed\"",
            "End If",
            $"Set f = fso.CreateTextFile(\"{UpdateAttempt.StatePath}\", True)",
            $"f.WriteLine \"version={version}\"",
            "f.WriteLine \"stage=\" & stage",
            "f.WriteLine \"exit=\" & rc",
            "f.Close",
            "",
            "' Unelevated on purpose: this script is not elevated, so the panel inherits its own token",
            "' and never comes back running as administrator.",
            $"sh.Run \"\"\"{ui}\"\"\", 0, False",
        ];
    }
}
