using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SidebarMonitor.Agent;

internal readonly record struct ProcSample(int Pid, string Name, long Cpu100Ns, int Threads, long WorkingSet);

/// <summary>
/// One NtQuerySystemInformation call returns every process with its kernel+user times, so CPU
/// share is a delta between two calls. PDH would need one counter per process and would hand
/// back ambiguous instance names like "chrome#7".
///
/// This is the most expensive collector by far (6-16 ms for ~350 processes), so the agent is
/// free to sample it less often than the sensors.
/// </summary>
internal sealed class Processes : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int cls, IntPtr buf, uint len, out uint retLen);

    // x64 offsets into SYSTEM_PROCESS_INFORMATION.
    private const int OffNextEntry = 0, OffThreadCount = 4, OffUserTime = 40, OffKernelTime = 48;
    private const int OffImageNameLen = 56, OffImageNameBuf = 64, OffUniquePid = 80, OffWorkingSet = 144;

    private readonly int _cores = Environment.ProcessorCount;
    private Dictionary<int, ProcSample> _previous = [];
    private long _previousTimestamp;

    private IntPtr _buffer = Marshal.AllocHGlobal(1 << 20);
    private uint _bufferSize = 1 << 20;

    public int TotalProcesses { get; private set; }
    public int TotalThreads { get; private set; }

    /// <summary>Process name for a pid from the last enumeration, or null. Used to name the top GPU
    /// process, whose pid comes from a different (GPU) counter.</summary>
    public string? NameFor(int pid) => _previous.TryGetValue(pid, out var p) ? p.Name : null;

    public readonly record struct TopRow(string Name, int Pid, float CpuPct, ulong WorkingSet, int Threads, int Instances);

    /// <summary>
    /// Top processes by CPU share. Returns empty on the first call: a delta needs two samples.
    ///
    /// When <paramref name="group"/> is set, processes sharing a name are folded into one row
    /// and their CPU, memory and threads summed — chrome with 40 renderers reads as one entry,
    /// which is the only way that list is legible.
    /// </summary>
    public List<TopRow> Top(int count, bool group)
    {
        long now = Stopwatch.GetTimestamp();
        var current = Sample();

        TotalProcesses = current.Count;
        TotalThreads = 0;

        double elapsed = _previousTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds;

        var rows = new List<TopRow>(current.Count);
        var grouped = group ? new Dictionary<string, TopRow>(current.Count, StringComparer.OrdinalIgnoreCase) : null;

        foreach (var (pid, cur) in current)
        {
            TotalThreads += cur.Threads;

            // pid 0 is the Idle process: its "CPU" is just unused cores.
            if (pid == 0 || elapsed <= 0 || !_previous.TryGetValue(pid, out var prev)) continue;

            float pct = (float)((cur.Cpu100Ns - prev.Cpu100Ns) / 1e7 / elapsed / _cores * 100.0);

            if (grouped is null)
            {
                rows.Add(new TopRow(cur.Name, pid, pct, (ulong)cur.WorkingSet, cur.Threads, 1));
                continue;
            }

            if (grouped.TryGetValue(cur.Name, out var acc))
                grouped[cur.Name] = acc with
                {
                    Pid = 0,                                     // no single owner any more
                    CpuPct = acc.CpuPct + pct,
                    WorkingSet = acc.WorkingSet + (ulong)cur.WorkingSet,
                    Threads = acc.Threads + cur.Threads,
                    Instances = acc.Instances + 1,
                };
            else
                grouped[cur.Name] = new TopRow(cur.Name, pid, pct, (ulong)cur.WorkingSet, cur.Threads, 1);
        }

        _previous = current;
        _previousTimestamp = now;

        if (grouped is not null) rows.AddRange(grouped.Values);

        rows.Sort((a, b) => b.CpuPct.CompareTo(a.CpuPct));
        if (rows.Count > count) rows.RemoveRange(count, rows.Count - count);
        return rows;
    }

    private Dictionary<int, ProcSample> Sample()
    {
        uint rc;
        while (true)
        {
            rc = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out uint needed);
            if (rc != StatusInfoLengthMismatch) break;
            _bufferSize = Math.Max(needed + 8192, _bufferSize * 2);
            _buffer = Marshal.ReAllocHGlobal(_buffer, (nint)_bufferSize);
        }

        var result = new Dictionary<int, ProcSample>(512);
        if (rc != 0) return result;

        IntPtr p = _buffer;
        while (true)
        {
            int next = Marshal.ReadInt32(p, OffNextEntry);
            int threads = Marshal.ReadInt32(p, OffThreadCount);
            long user = Marshal.ReadInt64(p, OffUserTime);
            long kernel = Marshal.ReadInt64(p, OffKernelTime);
            int pid = (int)Marshal.ReadIntPtr(p, OffUniquePid);
            long ws = (long)Marshal.ReadIntPtr(p, OffWorkingSet);

            short nameLen = Marshal.ReadInt16(p, OffImageNameLen);
            IntPtr namePtr = Marshal.ReadIntPtr(p, OffImageNameBuf);
            string name = namePtr != IntPtr.Zero && nameLen > 0
                ? Marshal.PtrToStringUni(namePtr, nameLen / 2)
                : (pid == 0 ? "Idle" : "System");

            result[pid] = new ProcSample(pid, name, user + kernel, threads, ws);

            if (next == 0) break;
            p += next;
        }
        return result;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
