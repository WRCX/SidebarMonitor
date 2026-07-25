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
                                 string flag = "/qb", int show = 1,
                                 string ui = @"C:\Program Files\SidebarMonitor\SidebarMonitor.UI.exe")
        => string.Join("\n", Updater.BuildRelauncher(msi, flag, show, ui, new Version(1, 5, 0)));

    /// <summary>Paths with spaces are the normal case (Program Files, and any user with a space in
    /// their name), and VBScript escapes a quote by doubling it.</summary>
    [Fact]
    public void Quotes_every_path_it_passes_to_the_shell()
    {
        var s = Script();

        Assert.Contains(@"""""C:\Users\Ada Lovelace\AppData\Local\SidebarMonitor\update\SidebarMonitor.msi""""", s);
        Assert.Contains(@"""""""C:\Program Files\SidebarMonitor\SidebarMonitor.UI.exe""""""", s);
    }

    /// <summary>bWaitOnReturn=True is what makes sh.Run return the exit code at all — and reading it
    /// is the entire fix. Without the assignment the script is the old one that could not tell a
    /// failed install from a successful one.</summary>
    [Fact]
    public void Waits_for_msiexec_and_keeps_its_exit_code()
    {
        var s = Script();

        Assert.Contains("rc = sh.Run(", s);
        Assert.Contains(", True)", s);
        Assert.Contains("f.WriteLine \"exit=\" & rc", s);
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

    /// <summary>The silent path asks for no msiexec UI and a hidden window; the visible one shows the
    /// basic progress bar. Getting these crossed makes an "automatic" update pop a window, or a manual
    /// one look like nothing happened.</summary>
    [Theory]
    [InlineData("/qn", 0)]
    [InlineData("/qb", 1)]
    public void Passes_the_quiet_flag_and_window_style_through(string flag, int show)
    {
        var s = Script(flag: flag, show: show);

        Assert.Contains($" {flag} ", s);
        Assert.Contains($", {show}, True)", s);
    }
}
