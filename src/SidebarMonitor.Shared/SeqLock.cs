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
    private readonly Mutex _singleton;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SeqLockHeader* _header;
    private readonly T* _payload;

    public static int HeaderSize => Unsafe.SizeOf<SeqLockHeader>();
    public static int PayloadSize => Unsafe.SizeOf<T>();
    public static int TotalSize => HeaderSize + PayloadSize;

    public int SizeBytes => TotalSize;

    /// <summary>
    /// Single-writer is enforced by a named mutex, NOT by the map's existence. That distinction
    /// matters: a reader (the UI) keeps the named map alive after the writer dies, so a fresh
    /// writer must be able to reuse it — CreateNew would fail ("already exists") and the agent
    /// could never restart while the UI is open. CreateOrOpen reuses the reader-held map; the
    /// mutex is what actually rejects a second live writer (and is released when its process dies,
    /// abandoned or not, so a crashed writer never blocks the next one).
    ///
    /// The agent/UI map stays Local\ (a Global\ file mapping needs SeCreateGlobalPrivilege, which
    /// an unelevated process does not hold — and per-session is what a per-user agent+UI pair
    /// wants). The elevated helper's map IS Global\ with <paramref name="worldReadable"/> so every
    /// user's session can read the one machine-wide helper.
    /// </summary>
    public SeqLockWriter(string mapName, uint signature, uint version, bool worldReadable = false)
    {
        try
        {
            _singleton = new Mutex(false, mapName + ".writer");
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but was created by ANOTHER user (its default DACL doesn't include
            // us): a live writer in someone else's session. Same meaning as losing the WaitOne.
            throw new IOException("Ya hay un escritor para " + mapName + " (de otro usuario)");
        }
        bool owned;
        try { owned = _singleton.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }   // previous writer crashed; we take over
        if (!owned)
        {
            _singleton.Dispose();
            throw new IOException("Ya hay un escritor para " + mapName);
        }

        _mmf = MemoryMappedFile.CreateOrOpen(mapName, TotalSize);
        // A Global\ map created by an elevated writer gets the creator's default DACL, which locks
        // OTHER users' (unelevated) readers out. Explicitly grant BUILTIN\Users read — that is the
        // whole point of publishing machine-wide. Admins/SYSTEM/owner keep full control, so a
        // successor writer (also elevated) can still CreateOrOpen the lingering map.
        if (worldReadable) KernelObjectAcl.GrantUsersRead(_mmf.SafeMemoryMappedFileHandle);
        _view = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _header = (SeqLockHeader*)p;
        _payload = (T*)(p + HeaderSize);

        // Mark the sequence odd first so any reader mid-copy retries while we re-stamp the header.
        Volatile.Write(ref _header->Sequence, _header->Sequence | 1);
        Thread.MemoryBarrier();
        _header->Signature = signature;
        _header->Version = version;
        _header->PayloadSize = PayloadSize;
        Thread.MemoryBarrier();
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
        try { _singleton.ReleaseMutex(); } catch { }
        _singleton.Dispose();
    }

}

/// <summary>Kernel-object DACL helper: BUILTIN\Users read, SYSTEM/Administrators/owner full.
/// Lives outside <see cref="SeqLockWriter{T}"/> because DllImport can't sit in a generic type.</summary>
internal static class KernelObjectAcl
{
    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out nint descriptor, out uint size);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(
        Microsoft.Win32.SafeHandles.SafeMemoryMappedFileHandle handle, uint securityInformation, nint descriptor);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint mem);

    public static void GrantUsersRead(Microsoft.Win32.SafeHandles.SafeMemoryMappedFileHandle handle)
    {
        const uint SddlRevision1 = 1, DaclSecurityInformation = 4;
        // GR (generic read) maps to SECTION_MAP_READ|query for section objects — exactly what
        // SeqLockReader's OpenExisting(Read) needs, from any user's session.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GR;;;BU)", SddlRevision1, out nint sd, out _))
            return;   // best-effort: same-user setups never needed the grant
        try { SetKernelObjectSecurity(handle, DaclSecurityInformation, sd); }
        finally { LocalFree(sd); }
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
