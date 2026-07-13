using System.IO;
using SidebarMonitor.Shared;
using Xunit;

namespace SidebarMonitor.Tests;

[Trait("Category", "Unit")]
public class SeqLockTests
{
    private struct Payload
    {
        public int A;
        public long B;
        public double C;
    }

    private static string UniqueMap() => "SbmTest_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Writer_reader_roundtrip_preserves_the_struct()
    {
        string map = UniqueMap();
        using var w = new SeqLockWriter<Payload>(map, 0xABCD, 1);
        w.Publish(new Payload { A = 42, B = 9_000_000_000L, C = 3.14159 });

        using var r = SeqLockReader<Payload>.TryOpen(map, 0xABCD, 1, out var err);
        Assert.Null(err);
        Assert.NotNull(r);
        Assert.True(r!.TryRead(out var p));
        Assert.Equal(42, p.A);
        Assert.Equal(9_000_000_000L, p.B);
        Assert.Equal(3.14159, p.C, 5);
    }

    [Fact]
    public void Reader_rejects_wrong_version()
    {
        string map = UniqueMap();
        using var w = new SeqLockWriter<Payload>(map, 0xABCD, 1);
        var r = SeqLockReader<Payload>.TryOpen(map, 0xABCD, 2, out var err);   // version 2 != 1
        Assert.Null(r);
        Assert.NotNull(err);
    }

    [Fact]
    public void Reader_rejects_wrong_signature()
    {
        string map = UniqueMap();
        using var w = new SeqLockWriter<Payload>(map, 0xABCD, 1);
        var r = SeqLockReader<Payload>.TryOpen(map, 0x1234, 1, out var err);
        Assert.Null(r);
        Assert.NotNull(err);
    }

    [Fact]
    public void Latest_publish_wins()
    {
        string map = UniqueMap();
        using var w = new SeqLockWriter<Payload>(map, 0xABCD, 1);
        using var r = SeqLockReader<Payload>.TryOpen(map, 0xABCD, 1, out _);
        w.Publish(new Payload { A = 1 });
        w.Publish(new Payload { A = 2 });
        Assert.True(r!.TryRead(out var p));
        Assert.Equal(2, p.A);
    }

    [Fact]
    public void Second_writer_on_another_thread_is_rejected()
    {
        string map = UniqueMap();
        using var w1 = new SeqLockWriter<Payload>(map, 0xABCD, 1);

        // The single-writer mutex is owned by this thread; a second writer must come from another
        // thread to see it as taken (a named mutex is recursive for its owning thread).
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { using var w2 = new SeqLockWriter<Payload>(map, 0xABCD, 1); }
            catch (Exception e) { caught = e; }
        });
        t.Start();
        t.Join();
        Assert.IsType<IOException>(caught);
    }
}
