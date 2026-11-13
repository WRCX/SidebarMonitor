using System.Diagnostics;
using System.Runtime.InteropServices;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Etw;

/// <summary>
/// RAMMap's "Use Counts" — how much of the RAM in use is private, mapped files, shared memory, page
/// tables, pools, driver-locked pages, kernel stacks — read from the memory manager's page database
/// through the same Superfetch PFN query RAMMap uses. Needs SeProfileSingleProcessPrivilege, which an
/// elevated token holds (disabled) and this enables.
/// <para>
/// Sampled, not exhaustive: RAMMap walks every page (~600 ms on 32 GiB) and keeps each one's detail;
/// this asks about one page in <see cref="Stride"/> and only tallies use × list. Measured on 32 GiB:
/// 41 ms per pass, every category within 1 % of the full walk. The starting page rotates each pass, so
/// over <see cref="Stride"/> passes every page is visited. Runs on its own timer, never under the
/// publish lock, so ETW event processing never waits on it.
/// </para>
/// </summary>
internal sealed unsafe class PageUseScanner : IDisposable
{
    private const int Stride = 16;
    private const int Batch = 16384;
    private const int PeriodMs = 5000;

    private const int SystemSuperfetchInformation = 79;
    private const uint SuperfetchVersion = 45;
    private const uint SuperfetchMagic = 0x6B756843;   // 'kuhC'
    private const uint SuperfetchPfnQuery = 6;
    private const uint SuperfetchMemoryRangesQuery = 17;

    // PF_PFN_PRIO_REQUEST: Version, RequestFlags, PfnCount @8, SYSTEM_MEMORY_LIST_INFORMATION (176 bytes)
    // @16, then MMPFN_IDENTITY[] @192, 24 bytes each: frame info (u1) @0, PageFrameIndex @8, u2 @16.
    private const int RequestHeader = 192;
    private const int IdentitySize = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct SuperfetchInfo { public uint Version, Magic, InfoClass; public void* Data; public uint Length; }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int cls, void* buf, int len, out int ret);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int len, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivilege { public int Count; public long Luid; public int Attributes; }

    private readonly List<(long Base, long Count)> _ranges = [];
    private readonly Timer? _timer;
    private readonly long[] _counts = new long[PageUseDecode.UseCount * PageUseDecode.ListCount];
    private byte* _request;
    private int _offset;
    private int _scanning;
    private PageUseInfo _latest;

    /// <summary>Why it is not running, for the diagnostics line; empty when it is.</summary>
    public string Status { get; private set; } = "";

    public PageUseScanner()
    {
        try
        {
            if (!EnablePrivilege("SeProfileSingleProcessPrivilege")) { Status = "sin privilegio"; return; }
            if (!ReadRanges()) { Status = "sin rangos de memoria"; return; }
            _request = (byte*)NativeMemory.AllocZeroed(RequestHeader + IdentitySize * Batch);
            _timer = new Timer(_ => Scan(), null, 0, PeriodMs);
        }
        catch (Exception e) { Status = e.GetType().Name; }
    }

    /// <summary>The last pass's result; not available until the first pass completes.</summary>
    public PageUseInfo Latest { get { lock (_counts) return _latest; } }

    private void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            Array.Clear(_counts);
            int start = _offset;
            _offset = (_offset + 1) % Stride;

            int n = 0;
            bool ok = true;
            foreach (var (b, count) in _ranges)
                for (long pfn = b + start; pfn < b + count && ok; pfn += Stride)
                {
                    *(long*)(_request + RequestHeader + n * IdentitySize + 8) = pfn;
                    if (++n == Batch) { ok = Flush(n); n = 0; }
                }
            if (ok && n > 0) ok = Flush(n);
            if (!ok) return;   // keep the previous pass rather than publish a partial one

            var info = PageUseDecode.ToInfo(_counts, Stride, DateTime.UtcNow.Ticks);
            lock (_counts) _latest = info;
        }
        catch { /* a failed pass leaves the previous result; the next one tries again */ }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    private bool Flush(int n)
    {
        *(uint*)_request = 1;                  // Version
        *(uint*)(_request + 4) = 1;            // RequestFlags
        *(long*)(_request + 8) = n;            // PfnCount
        if (Query(SuperfetchPfnQuery, _request, (uint)(RequestHeader + IdentitySize * n)) != 0) return false;
        for (int i = 0; i < n; i++)
        {
            ulong* frame = (ulong*)(_request + RequestHeader + i * IdentitySize);
            PageUseDecode.Tally(*frame, _counts);
            *frame = 0;
        }
        return true;
    }

    private static uint Query(uint infoClass, void* data, uint length)
    {
        var info = new SuperfetchInfo { Version = SuperfetchVersion, Magic = SuperfetchMagic, InfoClass = infoClass, Data = data, Length = length };
        return NtQuerySystemInformation(SystemSuperfetchInformation, &info, sizeof(SuperfetchInfo), out _);
    }

    /// <summary>The physical memory ranges, read once (they don't change while running). Version 1 of
    /// the structure on older builds, version 2 (an extra Flags field) on current ones.</summary>
    private bool ReadRanges()
    {
        const int size = 1 << 16;
        byte* buf = (byte*)NativeMemory.AllocZeroed(size);
        try
        {
            *(uint*)buf = 1;   // v1: { Version, RangeCount, ranges @8 }
            if (Query(SuperfetchMemoryRangesQuery, buf, size) == 0) { Read(buf + 4, buf + 8); return _ranges.Count > 0; }
            NativeMemory.Clear(buf, size);
            *(uint*)buf = 2;   // v2: { Version, Flags, RangeCount, ranges @16 }
            if (Query(SuperfetchMemoryRangesQuery, buf, size) == 0) { Read(buf + 8, buf + 16); return _ranges.Count > 0; }
            return false;
        }
        finally { NativeMemory.Free(buf); }

        void Read(byte* countAt, byte* rangesAt)
        {
            uint n = Math.Min(*(uint*)countAt, (uint)((size - 16) / 16));
            for (int i = 0; i < n; i++)
                _ranges.Add((*(long*)(rangesAt + i * 16), *(long*)(rangesAt + i * 16 + 8)));
        }
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028 /* ADJUST_PRIVILEGES | QUERY */, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) return false;
            var tp = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
            // Succeeds even when the token lacks the privilege; ERROR_NOT_ALL_ASSIGNED (1300) says so.
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        // Wait out a pass in flight before freeing the buffer it writes into.
        while (Volatile.Read(ref _scanning) == 1) Thread.Sleep(5);
        if (_request != null) { NativeMemory.Free(_request); _request = null; }
    }
}
