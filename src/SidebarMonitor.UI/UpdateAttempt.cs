using System.Globalization;
using System.IO;

namespace SidebarMonitor.UI;

/// <summary>
/// The record of the last in-place update hand-off. It lives on disk because the process that starts
/// an update is the very process msiexec replaces — nothing held in memory survives to see how it
/// went. <see cref="Updater.ApplyAsync"/> writes it just before handing off, the relauncher script
/// rewrites it with msiexec's exit code, and whichever UI comes back afterwards reads it.
///
/// It exists to break a retry loop. The script used to start the UI whatever msiexec returned, so a
/// failed install came back as the OLD version, which checked for updates on load, found the same
/// newer release, and started the whole thing over — download, msiexec, relaunch — with no backoff and
/// no limit. Seen updating 1.4.9 → 1.5.0 on 2026-07-25: five cycles of the sidebar appearing and
/// vanishing, fresh msiexec and wscript processes each time, and HKLM\Software\SidebarMonitor\Version
/// still reading 1.4.9.0 at the end of it. The package was fine; the handling was not.
///
/// So an attempt is now remembered: a version whose install failed is never installed automatically
/// again, and the reason is shown instead of being thrown away. Pressing «Actualizar» still retries —
/// a person asking again is not a loop.
///
/// Plain "key=value" lines rather than JSON, because the other writer is VBScript.
/// </summary>
internal static class UpdateAttempt
{
    public enum Stage
    {
        /// <summary>Nothing on record — or the file is unreadable, which comes to the same thing.</summary>
        None,
        /// <summary>Handed off to the script; msiexec has not reported back yet.</summary>
        Running,
        /// <summary>msiexec returned something other than success. <see cref="Record.ExitCode"/> says what.</summary>
        Failed,
        /// <summary>msiexec reported success (0, or 3010/1641 = installed, wants a reboot).</summary>
        Done,
    }

    public sealed record Record(Stage Stage, Version? Version, int ExitCode, DateTime WhenUtc, int Pid, bool Notified);

    private static readonly Record Nothing = new(Stage.None, null, 0, DateTime.MinValue, 0, false);

    /// <summary>
    /// How long a <see cref="Stage.Running"/> record is believed on its own. The PID check below is the
    /// real test; this only covers the script dying so hard it never wrote a result (and a PID that has
    /// since been recycled onto some other wscript). Generous, because msiexec can legitimately sit for
    /// minutes on a UAC prompt nobody is looking at.
    /// </summary>
    private static readonly TimeSpan MaxHandoff = TimeSpan.FromMinutes(30);

    /// <summary>Where the downloaded MSI, the relauncher script, this record and the msiexec log live.</summary>
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor", "update");

    /// <summary>msiexec's verbose log, written on every attempt. An update that fails on somebody
    /// else's machine is otherwise unreproducible, and this costs one file in a folder that already
    /// holds a 20 MB MSI.</summary>
    public static string MsiLogPath => Path.Combine(Dir, "msi-install.log");

    /// <summary>The record itself. Public because the relauncher script is generated with this path
    /// baked in — the script is the only writer that knows how msiexec ended.</summary>
    public static string StatePath => Path.Combine(Dir, "last-attempt.txt");

    /// <summary>
    /// Where the ELEVATED half of the hand-off drops msiexec's exit code, for the unelevated half to
    /// pick up. Two processes are needed because elevation and a return value are mutually exclusive
    /// here: ShellExecute("runas") can elevate but neither waits nor reports, so the elevated cmd
    /// writes the number down and the script waits for the file. Deleted before each attempt — a
    /// leftover from last time would be read as this time's result.
    /// </summary>
    public static string ResultPath => Path.Combine(Dir, "msiexec-exitcode.txt");

    public static Record Read()
    {
        try { return File.Exists(StatePath) ? Parse(File.ReadAllText(StatePath)) : Nothing; }
        catch { return Nothing; }
    }

    /// <summary>Records "we are about to hand off to msiexec for <paramref name="version"/>". Written
    /// BEFORE the script is launched, so the script's own rewrite always wins the race.</summary>
    public static void BeginRunning(Version version) =>
        Write(new Record(Stage.Running, version, 0, DateTime.UtcNow, 0, false));

    /// <summary>Patches in the relauncher's PID — unless it has already finished and written a result,
    /// which a fast failure (1618, 1925) genuinely can do before this line runs.</summary>
    public static void SetRunnerPid(int pid)
    {
        var a = Read();
        if (a.Stage == Stage.Running) Write(a with { Pid = pid });
    }

    /// <summary>The failure has been shown to the user; don't announce it again on every start.</summary>
    public static void MarkNotified()
    {
        var a = Read();
        if (a.Stage != Stage.None) Write(a with { Notified = true });
    }

    /// <summary>Forgets the attempt entirely (the update landed some other way, so the marker would
    /// only suppress a perfectly good version).</summary>
    public static void Clear()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// True while a hand-off is genuinely still in flight. Starting a second msiexec against a first
    /// one earns 1618 ("another installation is already in progress") — a failure, and failures are
    /// what fed the loop. A record with no PID yet is treated as running: that window is milliseconds
    /// wide and erring towards "wait" costs nothing.
    /// </summary>
    public static bool InFlight() => InFlight(Read());

    private static bool InFlight(Record a)
    {
        if (a.Stage != Stage.Running) return false;
        if (DateTime.UtcNow - a.WhenUtc > MaxHandoff) return false;   // script died without writing back
        return a.Pid == 0 || IsRunner(a.Pid);
    }

    /// <summary>
    /// Why the automatic path must not install <paramref name="version"/> again by itself — or null if
    /// there is no reason to hold back: nothing on record for it, or an attempt genuinely still in
    /// flight. The null/non-null is the decision; the text is what the user gets told.
    ///
    /// Deliberately wider than "the last attempt failed". An attempt that was handed off and never
    /// reported back is not evidence that retrying is safe, and it is the same shape of unknown that
    /// the old code resolved by trying again immediately, forever.
    /// </summary>
    public static string? WhyNotAgain(Version version)
    {
        var a = Read();
        if (a.Stage == Stage.None || !SameVersion(a.Version, version) || InFlight(a)) return null;
        return Reason(a);
    }

    /// <summary>How a finished attempt ended, in words. Never empty: an attempt nobody can explain is
    /// still an attempt not to repeat automatically.</summary>
    internal static string Reason(Record a) => a.Stage switch
    {
        Stage.Failed => Describe(a.ExitCode),
        // msiexec reported success, yet we are being asked about this version again — so it is not
        // what ended up running. Rare and confusing; repeating it automatically only repeats that.
        Stage.Done => Loc.T("msiexec informó de éxito pero la versión no cambió"),
        // Handed off and never heard from again: the script died somewhere between msiexec and writing
        // its result. We cannot know how it went, and guessing "fine, try again" is how the loop began.
        _ => Loc.T("el intento anterior no llegó a informar del resultado"),
    };

    /// <summary>
    /// Why msiexec gave up, in words. This is the "why" the old silent relaunch threw away: the exit
    /// code was never read by anyone, so five failed installs in a row looked identical to five
    /// successful ones. The codes below are the ones this hand-off can realistically produce.
    /// </summary>
    public static string Describe(int code) => code switch
    {
        1602 => Loc.T("se canceló el aviso de Windows (UAC)"),
        // 1603 is "something failed inside the install" and says nothing by itself — the error that
        // actually stopped it is a line in the verbose log. Never report the wrapper when the log
        // holds the cause: the real failure here came back as 1603 with 1925 inside it.
        1603 => InnerError() switch
        {
            0 => Loc.T("error irrecuperable durante la instalación"),
            1603 => Loc.T("error irrecuperable durante la instalación"),
            int inner => Describe(inner),
        },
        Updater.ExitTimedOut => Loc.T("la instalación no terminó en {0} minutos", Updater.WaitSeconds / 60),
        1618 => Loc.T("ya había otra instalación en curso"),
        1619 or 1620 => Loc.T("el paquete descargado no se pudo abrir"),
        1638 => Loc.T("ya hay otra versión instalada"),
        // A per-machine MSI cannot elevate when it is told to show no UI: with /qn from an unelevated
        // caller, Windows Installer has no way to raise the consent prompt. Measured on 2026-07-30 —
        // this is what the automatic path was hitting, and it took the installed version down with it.
        1925 => Loc.T("una instalación silenciosa no puede pedir permisos de administrador"),
        _ => Loc.T("msiexec devolvió el error {0}", code),
    };

    /// <summary>
    /// The error msiexec logged before giving up, dug out of the verbose log ("Error 1925. You do not
    /// have sufficient privileges…"). 0 when there is no log or nothing to find. Reads only the tail:
    /// the fatal error is logged near the end, and these logs run to megabytes.
    /// </summary>
    private static int InnerError()
    {
        try
        {
            if (!File.Exists(MsiLogPath)) return 0;
            var tail = new Queue<string>();
            foreach (var line in File.ReadLines(MsiLogPath))
            {
                tail.Enqueue(line);
                if (tail.Count > 400) tail.Dequeue();
            }
            foreach (var line in tail.Reverse())
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"\bError (\d{4})\b");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n != 1603) return n;
            }
        }
        catch { /* diagnostics must never be the thing that breaks */ }
        return 0;
    }

    // ── Storage ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compares major.minor.build only, like <see cref="Updater.IsNewer"/> — the record may
    /// hold "1.5.0" from a release tag while the caller has "1.5.0.0" from the assembly.</summary>
    internal static bool SameVersion(Version? a, Version? b) =>
        a is not null && b is not null && a.Major == b.Major && a.Minor == b.Minor
        && Math.Max(0, a.Build) == Math.Max(0, b.Build);

    /// <summary>Lenient by design: the script writes only version/stage/exit, and a half-written file
    /// after a crash should read as "no usable record", never throw.</summary>
    internal static Record Parse(string text)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        var stage = kv.GetValueOrDefault("stage") switch
        {
            "running" => Stage.Running,
            "failed" => Stage.Failed,
            "done" => Stage.Done,
            _ => Stage.None,
        };
        if (stage == Stage.None) return Nothing;

        return new Record(
            stage,
            Version.TryParse(kv.GetValueOrDefault("version"), out var v) ? v : null,
            Int(kv, "exit"),
            When(Long(kv, "when")),
            Int(kv, "pid"),
            kv.GetValueOrDefault("notified") == "1");

        static int Int(Dictionary<string, string> kv, string key) =>
            int.TryParse(kv.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        static long Long(Dictionary<string, string> kv, string key) =>
            long.TryParse(kv.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;
        static DateTime When(long ticks) =>
            ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
    }

    internal static string Render(Record a) =>
        $"version={a.Version?.ToString() ?? ""}\n" +
        $"stage={a.Stage.ToString().ToLowerInvariant()}\n" +
        $"exit={a.ExitCode.ToString(CultureInfo.InvariantCulture)}\n" +
        $"when={a.WhenUtc.Ticks.ToString(CultureInfo.InvariantCulture)}\n" +
        $"pid={a.Pid.ToString(CultureInfo.InvariantCulture)}\n" +
        $"notified={(a.Notified ? "1" : "0")}\n";

    private static void Write(Record a)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StatePath, Render(a));
        }
        catch { /* non-fatal: at worst we forget an attempt and offer the same version again */ }
    }

    /// <summary>Is that PID still our relauncher? Checking the name as well as the PID keeps a recycled
    /// PID from reading as "an update is running" forever.</summary>
    private static bool IsRunner(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals("wscript", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // gone, or not ours to look at
    }
}
