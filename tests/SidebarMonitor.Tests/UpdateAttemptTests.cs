using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Guards the record that ends the update retry loop. The pieces worth pinning are the ones split
/// across two languages: C# writes the record and VBScript rewrites it, so the two have to agree on
/// the format, and the version comparison has to survive a release tag ("1.5.0") meeting an assembly
/// version ("1.5.0.0"). Nothing here touches the disk — only Parse/Render and the generated script.
/// </summary>
[Trait("Category", "Unit")]
public class UpdateAttemptTests
{
    [Fact]
    public void Render_and_Parse_round_trip()
    {
        var when = new DateTime(2026, 7, 25, 16, 43, 20, DateTimeKind.Utc);
        var a = new UpdateAttempt.Record(UpdateAttempt.Stage.Failed, new Version(1, 5, 0), 1618, when, 4242, true);

        Assert.Equal(a, UpdateAttempt.Parse(UpdateAttempt.Render(a)));
    }

    /// <summary>The relauncher writes exactly these three keys — nothing else it could compute would
    /// be worth the VBScript. The missing keys must read as harmless defaults, not throw.</summary>
    [Fact]
    public void Parse_accepts_the_three_keys_the_script_writes()
    {
        var a = UpdateAttempt.Parse("version=1.5.0\nstage=failed\nexit=1602\n");

        Assert.Equal(UpdateAttempt.Stage.Failed, a.Stage);
        Assert.Equal(new Version(1, 5, 0), a.Version);
        Assert.Equal(1602, a.ExitCode);
        Assert.Equal(DateTime.MinValue, a.WhenUtc);
        Assert.Equal(0, a.Pid);
        Assert.False(a.Notified);
    }

    [Fact]
    public void Parse_tolerates_CRLF_and_blank_lines()
    {
        var a = UpdateAttempt.Parse("\r\nversion=1.5.0\r\nstage=done\r\nexit=0\r\n\r\n");

        Assert.Equal(UpdateAttempt.Stage.Done, a.Stage);
        Assert.Equal(new Version(1, 5, 0), a.Version);
    }

    // A record with no usable stage is no record: a truncated or foreign file must never read as
    // "an update is running", which would block every future update for half an hour.
    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("version=1.5.0\n")]
    [InlineData("stage=\n")]
    public void Parse_of_nonsense_is_no_record(string text)
        => Assert.Equal(UpdateAttempt.Stage.None, UpdateAttempt.Parse(text).Stage);

    [Fact]
    public void Parse_ignores_an_absurd_timestamp_rather_than_throwing()
        => Assert.Equal(DateTime.MinValue,
            UpdateAttempt.Parse("stage=running\nwhen=-99999999999999999999\n").WhenUtc);

    /// <summary>The record holds the release tag's version, the caller usually has the assembly's
    /// four-part one. If these stopped matching, a failed version would be auto-retried forever —
    /// which is the whole bug.</summary>
    [Theory]
    [InlineData("1.5.0", "1.5.0.0", true)]
    [InlineData("1.5.0", "1.5.0", true)]
    [InlineData("1.5", "1.5.0", true)]        // a two-part tag means build 0
    [InlineData("1.5.0", "1.5.1", false)]
    [InlineData("1.5.0", "1.6.0", false)]
    public void SameVersion_compares_major_minor_build(string a, string b, bool expected)
        => Assert.Equal(expected, UpdateAttempt.SameVersion(Version.Parse(a), Version.Parse(b)));

    [Fact]
    public void SameVersion_of_an_unknown_version_is_false()
        => Assert.False(UpdateAttempt.SameVersion(null, new Version(1, 5, 0)));

    // Every code the hand-off can realistically produce says something specific; the fallback carries
    // the number so an unmapped failure is still actionable.
    [Theory]
    [InlineData(1602)]
    [InlineData(1618)]
    [InlineData(1925)]
    public void Describe_names_the_common_failures(int code)
        => Assert.DoesNotContain(code.ToString(), UpdateAttempt.Describe(code));

    [Fact]
    public void Describe_falls_back_to_the_raw_code()
        => Assert.Contains("1234", UpdateAttempt.Describe(1234));

    /// <summary>Every stage that is not "still in flight" has to produce a reason to hold back —
    /// including the two that are not plain failures. A null anywhere here is a version the automatic
    /// path would install again on the next check, which is the loop.</summary>
    [Fact]
    public void Every_finished_stage_has_something_to_say()
    {
        // Running is in the list on purpose: a record that was handed off and never reported back is
        // finished as far as anyone can tell, and it needs a reason too.
        foreach (var stage in new[] { UpdateAttempt.Stage.Failed, UpdateAttempt.Stage.Done, UpdateAttempt.Stage.Running })
        {
            var a = new UpdateAttempt.Record(stage, new Version(1, 5, 0), 1618, DateTime.MinValue, 0, false);
            Assert.False(string.IsNullOrWhiteSpace(UpdateAttempt.Reason(a)), $"no reason for {stage}");
        }
    }
}

/// <summary>
/// The relauncher script, which nothing else can test: it runs after the process that wrote it is
/// gone, on a machine that is mid-upgrade. Its bugs are quoting bugs and "nobody read the exit code"
/// bugs, and both are visible in the text.
/// </summary>
[Trait("Category", "Unit")]
public class RelauncherScriptTests
{
    private static string Script(string msi = @"C:\Users\Ada Lovelace\AppData\Local\SidebarMonitor\update\SidebarMonitor.msi",
                                 string flag = "/qb",
                                 string ui = @"C:\Program Files\SidebarMonitor\SidebarMonitor.UI.exe")
        => string.Join("\n", Updater.BuildRelauncher(msi, flag, ui, new Version(1, 5, 0)));

    /// <summary>Paths with spaces are the normal case (Program Files, and any user with a space in
    /// their name), and VBScript escapes a quote by doubling it.</summary>
    [Fact]
    public void Quotes_every_path_it_passes_to_the_shell()
    {
        var s = Script();

        Assert.Contains(@"""""C:\Users\Ada Lovelace\AppData\Local\SidebarMonitor\update\SidebarMonitor.msi""""", s);
        Assert.Contains(@"""""""C:\Program Files\SidebarMonitor\SidebarMonitor.UI.exe""""""", s);
    }

    /// <summary>
    /// It must ELEVATE. A per-machine MSI run unelevated dies at InstallFinalize with 1925 whatever /q
    /// level it is given, which is what made the automatic install a permanent no-op.
    /// </summary>
    [Fact]
    public void Elevates_msiexec()
    {
        var s = Script();

        Assert.Contains("\"runas\"", s);
        Assert.Contains("app.ShellExecute \"cmd.exe\"", s);
    }

    /// <summary>
    /// ShellExecute("runas") neither waits nor returns a code, so the exit code comes back through a
    /// file — and it has to be DELAYED expansion. %errorlevel% is substituted while cmd parses the
    /// line, before msiexec runs, so it would report the previous command's result forever.
    /// </summary>
    [Fact]
    public void Brings_the_exit_code_back_through_a_file()
    {
        var s = Script();

        Assert.Contains("/v:on", s);
        Assert.Contains("!errorlevel!", s);
        Assert.DoesNotContain("%errorlevel%", s);
        Assert.Contains("rc = CLng(line)", s);
        Assert.Contains("f.WriteLine \"exit=\" & rc", s);
    }

    /// <summary>A leftover code from the previous attempt would be read as this attempt's result —
    /// a success recorded for an install that never ran.</summary>
    [Fact]
    public void Deletes_a_stale_result_before_starting()
    {
        var s = Script();

        Assert.Contains("If fso.FileExists(res) Then fso.DeleteFile res, True", s);
        Assert.True(s.IndexOf("fso.DeleteFile res", StringComparison.Ordinal)
                  < s.IndexOf("app.ShellExecute", StringComparison.Ordinal),
            "the stale result must be deleted BEFORE msiexec is launched");
    }

    /// <summary>Declining the UAC prompt makes ShellExecute raise, which is a cancel — not a crash,
    /// and not a success. 1602 is the code msiexec itself uses for it.</summary>
    [Fact]
    public void A_declined_prompt_is_recorded_as_a_cancel()
    {
        var s = Script();

        Assert.Contains("rc = 1602", s);
        Assert.Contains("On Error Resume Next", s);
        Assert.True(s.IndexOf("rc = 1602", StringComparison.Ordinal)
                  < s.IndexOf("app.ShellExecute", StringComparison.Ordinal),
            "the cancel default has to be set before the call that can fail");
    }

    /// <summary>Waiting forever would leave the record stuck at "running", which blocks every later
    /// update; the wait is bounded and the timeout is a recorded failure like any other.</summary>
    [Fact]
    public void The_wait_is_bounded()
    {
        var s = Script();

        Assert.Contains($"Do While waited < {Updater.WaitSeconds}", s);
        Assert.Contains($"rc = {Updater.ExitTimedOut}", s);
    }

    /// <summary>The panel must come back unelevated: sh.Run inherits this script's token, and this
    /// script is not the elevated half. An admin-token UI would be a lasting side effect of updating.</summary>
    [Fact]
    public void Relaunches_the_UI_unelevated()
    {
        var s = Script();

        // sh.Run inherits this script's (unelevated) token; the elevating ShellExecute is only ever
        // used for cmd.exe. An admin-token panel would be a lasting side effect of having updated.
        Assert.Matches(@"sh\.Run\s+""""""[^\n]*SidebarMonitor\.UI\.exe", s);
        Assert.DoesNotContain("app.ShellExecute \"\"\"", s);
    }

    /// <summary>3010 and 1641 mean "installed, now reboot". Treating them as failures would suppress
    /// the next update of a version that is, in fact, already installed.</summary>
    [Fact]
    public void Counts_the_reboot_exit_codes_as_success()
    {
        var s = Script();

        Assert.Contains("If rc = 0 Or rc = 3010 Or rc = 1641 Then", s);
        Assert.Contains("stage = \"done\"", s);
        Assert.Contains("stage = \"failed\"", s);
    }

    [Fact]
    public void Records_the_version_it_tried_to_install()
        => Assert.Contains("f.WriteLine \"version=1.5.0\"", Script());

    /// <summary>Always logging is what turns the next "it failed on my machine" into an answer.</summary>
    [Fact]
    public void Always_asks_msiexec_for_a_verbose_log()
        => Assert.Contains("/l*v", Script());

    /// <summary>The record has to be written before the UI is started, or the UI it starts would read
    /// the PREVIOUS attempt's verdict and act on it.</summary>
    [Fact]
    public void Writes_the_record_before_relaunching_the_UI()
    {
        var s = Script();

        Assert.True(s.IndexOf("f.Close", StringComparison.Ordinal)
                  < s.IndexOf("SidebarMonitor.UI.exe", StringComparison.Ordinal),
            "The relauncher must record msiexec's verdict before starting the UI that reads it:\n" + s);
    }

    /// <summary>
    /// The quiet level reaches msiexec. There is no window-style knob any more: the 0 passed to
    /// ShellExecute hides the cmd wrapper, not the installer, so /qb still draws its own progress bar
    /// and /qn still draws nothing. Passing 1 would only flash a console window.
    /// </summary>
    [Theory]
    [InlineData("/qn")]
    [InlineData("/qb")]
    public void Passes_the_quiet_flag_through(string flag)
    {
        var s = Script(flag: flag);

        Assert.Contains($" {flag} ", s);
        Assert.Contains("\"runas\", 0", s);
    }
}
