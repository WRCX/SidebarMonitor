using System.Diagnostics;
using System.IO;
using SidebarMonitor.UI;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// Runs the generated relauncher through the real VBScript interpreter. Everything about this script
/// resists testing — it runs after the process that wrote it is gone, on a machine mid-upgrade, and
/// its one job is to report a failure that by definition did not happen on the machine writing the
/// test. So the msiexec call is swapped for a command with a chosen exit code, and what is checked is
/// the seam that actually broke: the script reads the code, writes it down in the shape C# parses,
/// and does that before starting anything.
///
/// Integration rather than Unit because it spawns cscript, which a locked-down CI agent may refuse:
/// `dotnet test --filter Category=Integration`. Needs no network, no elevation, installs nothing.
/// </summary>
[Trait("Category", "Integration")]
public class RelauncherScriptRunTests
{
    [Theory]
    [InlineData(0, "done")]
    [InlineData(3010, "done")]      // installed, wants a reboot
    [InlineData(1641, "done")]      // installed, reboot already starting
    [InlineData(1602, "failed")]    // UAC prompt dismissed
    [InlineData(1618, "failed")]    // another install in progress
    [InlineData(1925, "failed")]    // insufficient privileges
    [InlineData(1603, "failed")]    // the wrapper code that hides the real one
    public void Records_msiexec_exit_code_and_verdict(int code, string expectedStage)
    {
        string dir = Path.Combine(Path.GetTempPath(), "SidebarMonitorRelauncher", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string state = Path.Combine(dir, "last-attempt.txt");
            string result = Path.Combine(dir, "msiexec-exitcode.txt");
            string marker = Path.Combine(dir, "ui-started.txt");

            // The real script with three substitutions: the result and record paths move into a temp
            // directory, the elevating ShellExecute becomes an UNELEVATED cmd running the same command
            // line (a test cannot answer a UAC prompt), and the UI it would start becomes a file. What
            // is under test is everything else — the wait loop, the numeric parse, the verdict, the
            // record format, the ordering — all of it the generator's own.
            var script = Updater.BuildRelauncher("ignored.msi", "/qb", "ignored.exe", new Version(1, 5, 0))
                .Select(l => l.Replace(UpdateAttempt.StatePath, state))
                .Select(l => l.Replace(UpdateAttempt.ResultPath, result))
                // `cmd /c exit N & echo ...` would never echo: exit ends that cmd on the spot. A NESTED
                // cmd sets the errorlevel and returns, exactly as msiexec does in the real script.
                .Select(l => l.StartsWith("app.ShellExecute", StringComparison.Ordinal)
                    ? $"sh.Run \"cmd /v:on /c cmd /c exit {code} & echo !errorlevel!> \"\"{result}\"\"\", 0, False"
                    : l)
                .Select(l => l.StartsWith("sh.Run \"\"\"", StringComparison.Ordinal)
                    ? $"fso.CreateTextFile(\"{marker}\", True).Close"
                    : l)
                // A bug in the wait loop must fail the test in seconds, not sit out the real 15-minute
                // budget seven times over.
                .Select(l => l.Replace($"Do While waited < {Updater.WaitSeconds}", "Do While waited < 20"))
                .ToArray();

            string vbs = Path.Combine(dir, "apply-update.vbs");
            File.WriteAllLines(vbs, script);

            using var p = Process.Start(new ProcessStartInfo("cscript.exe", $"//Nologo \"{vbs}\"")
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })!;
            string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);

            // A syntax error is the failure this test exists to catch, and cscript reports it here.
            Assert.True(string.IsNullOrWhiteSpace(err), "cscript complained:\n" + err + "\n\n" + string.Join("\n", script));
            Assert.True(File.Exists(state), "the script wrote no record");

            var rec = UpdateAttempt.Parse(File.ReadAllText(state));
            Assert.Equal(expectedStage, rec.Stage.ToString().ToLowerInvariant());
            // The code survived the whole round trip: cmd -> file -> wait loop -> CLng -> record.
            Assert.Equal(code, rec.ExitCode);
            Assert.Equal(new Version(1, 5, 0), rec.Version);

            // Started either way: leaving the machine with no sidebar and no explanation would be a
            // worse failure than the loop. What stops the retry is the record above, not withholding it.
            Assert.True(File.Exists(marker), "the script did not start the UI");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
