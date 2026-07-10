using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace SidebarMonitor.Shared;

internal static unsafe class MapMath
{
    public static readonly int HeaderSize = Unsafe.SizeOf<SnapshotHeader>();
    public static readonly int PayloadSize = Unsafe.SizeOf<Snapshot>();
    public static readonly int TotalSize = HeaderSize + PayloadSize;
}

/// <summary>The agent side. One writer, many readers.</summary>
public sealed unsafe class SnapshotWriter : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly SnapshotHeader* _header;
    private readonly Snapshot* _payload;

    public int SizeBytes => MapMath.TotalSize;

    public SnapshotWriter()
    {
        // CreateNew, not CreateOrOpen: a second agent must fail loudly rather than race.
        _mmf = MemoryMappedFile.CreateNew(SnapshotLayout.MapName, MapMath.TotalSize);
        _view = _mmf.CreateViewAccessor(0, MapMath.TotalSize, MemoryMappedFileAccess.ReadWrite);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p;
        _header = (SnapshotHeader*)p;
        _payload = (Snapshot*)(p + MapMath.HeaderSize);

        _header->Signature = SnapshotLayout.Signature;
        _header->Version = SnapshotLayout.Version;
        _header->PayloadSize = MapMath.PayloadSize;
        Volatile.Write(ref _header->Sequence, 0);
    }

    public void Publish(in Snapshot snapshot)
    {
        int seq = _header->Sequence;

        Volatile.Write(ref _header->Sequence, seq + 1);   // odd: write in progress
        Thread.MemoryBarrier();

        *_payload = snapshot;

        Thread.MemoryBarrier();
        Volatile.Write(ref _header->Sequence, seq + 2);   // even: consistent again
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}

/// <summary>The UI side. Never blocks the agent; retries if it catches a write in progress.</summary>
public sealed unsafe class SnapshotReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SnapshotHeader* _header;
    private readonly Snapshot* _payload;

    private SnapshotReader(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* p)
    {
        _mmf = mmf;
        _view = view;
        _header = (SnapshotHeader*)p;
        _payload = (Snapshot*)(p + MapMath.HeaderSize);
    }

    /// <summary>Returns null when the agent is not running.</summary>
    public static SnapshotReader? TryOpen(out string? error)
    {
        error = null;
        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(SnapshotLayout.MapName, MemoryMappedFileRights.Read);
        }
        catch (FileNotFoundException)
        {
            error = "el agente no esta corriendo";
            return null;
        }

        var view = mmf.CreateViewAccessor(0, MapMath.TotalSize, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);

        var hdr = (SnapshotHeader*)p;
        if (hdr->Signature != SnapshotLayout.Signature)
        {
            error = $"firma inesperada 0x{hdr->Signature:X8}";
        }
        else if (hdr->Version != SnapshotLayout.Version)
        {
            error = $"version {hdr->Version}, esperada {SnapshotLayout.Version}";
        }
        else if (hdr->PayloadSize != MapMath.PayloadSize)
        {
            error = $"payload de {hdr->PayloadSize} B, esperado {MapMath.PayloadSize} B";
        }

        if (error is not null)
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            mmf.Dispose();
            return null;
        }

        return new SnapshotReader(mmf, view, p);
    }

    public bool TryRead(out Snapshot snapshot)
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
            int before = Volatile.Read(ref _header->Sequence);
            if ((before & 1) != 0) { Thread.SpinWait(8); continue; }   // writer mid-flight

            Thread.MemoryBarrier();
            snapshot = *_payload;
            Thread.MemoryBarrier();

            if (Volatile.Read(ref _header->Sequence) == before) return true;
        }

        snapshot = default;
        return false;   // agent is publishing faster than we can copy; caller retries next frame
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
