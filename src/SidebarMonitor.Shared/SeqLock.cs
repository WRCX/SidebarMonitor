using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace SidebarMonitor.Shared;

/// <summary>
/// Seqlock header. <see cref="Sequence"/> is odd while a write is in flight; a reader that sees
/// the same even value before and after its copy knows the copy was not torn. Readers never
/// block the writer, and there is no mutex to leak if either side dies.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct SeqLockHeader
{
    public uint Signature;
    public uint Version;
    public int Sequence;
    public int PayloadSize;
}

/// <summary>Single writer over a named shared-memory block.</summary>
public sealed unsafe class SeqLockWriter<T> : IDisposable where T : unmanaged
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SeqLockHeader* _header;
    private readonly T* _payload;

    public static int HeaderSize => Unsafe.SizeOf<SeqLockHeader>();
    public static int PayloadSize => Unsafe.SizeOf<T>();
    public static int TotalSize => HeaderSize + PayloadSize;

    public int SizeBytes => TotalSize;

    /// <summary>
    /// CreateNew, not CreateOrOpen: a second writer must fail loudly rather than race.
    /// The map lives under Local\ because creating a Global\ kernel object needs
    /// SeCreateGlobalPrivilege, which an unelevated process does not hold.
    /// </summary>
    public SeqLockWriter(string mapName, uint signature, uint version)
    {
        _mmf = MemoryMappedFile.CreateNew(mapName, TotalSize);
        _view = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _header = (SeqLockHeader*)p;
        _payload = (T*)(p + HeaderSize);

        _header->Signature = signature;
        _header->Version = version;
        _header->PayloadSize = PayloadSize;
        Volatile.Write(ref _header->Sequence, 0);
    }

    public void Publish(in T value)
    {
        int seq = _header->Sequence;

        Volatile.Write(ref _header->Sequence, seq + 1);   // odd: write in progress
        Thread.MemoryBarrier();

        *_payload = value;

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

/// <summary>Many readers. Never blocks the writer; retries if it catches a write in progress.</summary>
public sealed unsafe class SeqLockReader<T> : IDisposable where T : unmanaged
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SeqLockHeader* _header;
    private readonly T* _payload;

    private SeqLockReader(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* p)
    {
        _mmf = mmf;
        _view = view;
        _header = (SeqLockHeader*)p;
        _payload = (T*)(p + SeqLockWriter<T>.HeaderSize);
    }

    /// <summary>Returns null when nobody is publishing, or when the layout does not match.</summary>
    public static SeqLockReader<T>? TryOpen(string mapName, uint signature, uint version, out string? error)
    {
        error = null;
        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
        }
        catch (FileNotFoundException)
        {
            error = "nadie esta publicando";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"sin permiso: {ex.Message}";
            return null;
        }

        var view = mmf.CreateViewAccessor(0, SeqLockWriter<T>.TotalSize, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);

        var hdr = (SeqLockHeader*)p;
        if (hdr->Signature != signature) error = $"firma inesperada 0x{hdr->Signature:X8}";
        else if (hdr->Version != version) error = $"version {hdr->Version}, esperada {version}";
        else if (hdr->PayloadSize != SeqLockWriter<T>.PayloadSize)
            error = $"payload de {hdr->PayloadSize} B, esperado {SeqLockWriter<T>.PayloadSize} B";

        if (error is not null)
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            mmf.Dispose();
            return null;
        }

        return new SeqLockReader<T>(mmf, view, p);
    }

    public bool TryRead(out T value)
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
            int before = Volatile.Read(ref _header->Sequence);
            if ((before & 1) != 0) { Thread.SpinWait(8); continue; }   // writer mid-flight

            Thread.MemoryBarrier();
            value = *_payload;
            Thread.MemoryBarrier();

            if (Volatile.Read(ref _header->Sequence) == before) return true;
        }

        value = default;
        return false;   // publisher is faster than we can copy; caller retries next frame
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
