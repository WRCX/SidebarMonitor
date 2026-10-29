using System.Globalization;
using SidebarMonitor.Etw;
using Xunit;

namespace SidebarMonitor.Tests;

/// <summary>
/// <see cref="FrameStats.TryParseRow"/> replaced a <c>Split(',')</c> per PresentMon row with a walk over
/// the commas. It must read every row exactly as the Split version did — including the rows it rejected —
/// so the reference below is that original code, kept verbatim.
/// </summary>
[Trait("Category", "Unit")]
public class FrameStatsParseTests
{
    // A PresentMon 2.x --v2_metrics header (column set and order as the CLI writes it).
    private const string Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode," +
        "FrameType,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,VideoBusy,DisplayLatency," +
        "DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency,ClickToPhotonLatency";

    private static readonly string[] Wanted =
        ["Application", "ProcessID", "FrameTime", "DisplayedTime", "GPUBusy", "AnimationError", "ClickToPhotonLatency"];

    private static int[] Columns(string header)
    {
        var cols = header.Split(',');
        return Wanted.Select(w => Array.FindIndex(cols, c => c.Trim().Equals(w, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // ── the pre-change ParseRow, verbatim apart from returning instead of storing ──
    private static (int pid, string app, float frame, float disp, float gpu, float anim, float lat)? Reference(string line, int[] col)
    {
        if (col.Length == 0 || col[0] < 0 || col[2] < 0) return null;
        var f = line.Split(',');
        if (f.Length <= col[2]) return null;
        if (!int.TryParse(Get(f, col[1]), out int pid)) return null;
        float frame = Num(Get(f, col[2]));
        if (float.IsNaN(frame) || frame <= 0) return null;
        return (pid, Get(f, col[0]), frame, Num(Get(f, col[3])), Num(Get(f, col[4])), Num(Get(f, col[5])), Num(Get(f, col[6])));
    }
    private static string Get(string[] f, int i) => i >= 0 && i < f.Length ? f[i] : "";
    private static float Num(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;

    public static TheoryData<string> Rows => new()
    {
        // an ordinary frame
        "Cyberpunk2077.exe,12345,0x0000021F,DXGI,0,0,1,Hardware: Independent Flip,Application,1234.5,6.94,5.1,1.8,0.4,6.2,6.6,0.3,0,14.2,6.95,0.42,1234.6,0,NA,31.7",
        // metrics PresentMon writes as NA (not displayed, no click)
        "game.exe,42,0x1,DXGI,0,0,0,Composed: Flip,Application,10,16.7,10,6,1,15,15.5,1,0,NA,NA,NA,NA,0,NA,NA",
        // whitespace around numbers (both parsers allow it)
        "game.exe, 42 ,0x1,DXGI,0,0,0,x,y,10, 8.3 ,1,1,1,1, 7.9 ,1,0,1, 8.4 ,-0.5,1,0,NA, 22 ",
        // rejected: frame time 0, negative, missing, unparseable
        "game.exe,42,0x1,DXGI,0,0,0,x,y,10,0,1,1,1,1,1,1,0,1,1,1,1,0,NA,1",
        "game.exe,42,0x1,DXGI,0,0,0,x,y,10,-3,1,1,1,1,1,1,0,1,1,1,1,0,NA,1",
        "game.exe,42,0x1,DXGI,0,0,0,x,y,10,,1,1,1,1,1,1,0,1,1,1,1,0,NA,1",
        "game.exe,42,0x1,DXGI,0,0,0,x,y,10,NA,1,1,1,1,1,1,0,1,1,1,1,0,NA,1",
        // rejected: bad pid
        "game.exe,notapid,0x1,DXGI,0,0,0,x,y,10,8,1,1,1,1,1,1,0,1,1,1,1,0,NA,1",
        // short rows: ends right after the frame time; ends before it
        "game.exe,42,0x1,DXGI,0,0,0,x,y,10,8",
        "game.exe,42,0x1,DXGI",
        // empty app name, trailing comma, empty line
        ",42,0x1,DXGI,0,0,0,x,y,10,8,1,1,1,1,1,1,0,1,1,1,1,0,NA,1,",
        "",
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Parses_every_row_exactly_as_the_split_version_did(string line)
    {
        var col = Columns(Header);
        var want = Reference(line, col);
        bool ok = FrameStats.TryParseRow(line, col, out var r);

        Assert.Equal(want.HasValue, ok);
        if (!ok) return;
        var w = want!.Value;
        Assert.Equal(w.pid, r.Pid);
        Assert.Equal(w.app, line[r.App]);
        Assert.Equal(w.frame, r.Frame);
        Assert.Equal(w.disp, r.Disp);
        Assert.Equal(w.gpu, r.Gpu);
        Assert.Equal(w.anim, r.Anim);
        Assert.Equal(w.lat, r.Lat);
    }

    [Fact]
    public void A_header_without_the_frame_time_column_rejects_every_row()
    {
        var col = Columns(Header.Replace("FrameTime", "Something"));
        Assert.False(FrameStats.TryParseRow("game.exe,42,0x1,DXGI,0,0,0,x,y,10,8,1", col, out _));
    }

    [Fact]
    public void Parsing_a_row_allocates_nothing()
    {
        var col = Columns(Header);
        string line = (string)Rows.Cast<object[]>().First()[0];
        FrameStats.TryParseRow(line, col, out _);   // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) FrameStats.TryParseRow(line, col, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
