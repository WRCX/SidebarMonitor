using System.Runtime.InteropServices;

namespace SidebarMonitor.Agent;

internal readonly record struct InstanceSample(string Instance, double Value);

/// <summary>
/// One PDH query holding every counter, collected with a single PdhCollectQueryData call.
///
/// Counters are added with PdhAddEnglishCounterW: the localized paths that PdhAddCounterW
/// expects do not exist under a Spanish Windows ("\Informacion del procesador\..."), and
/// hardcoding English paths with the plain API silently fails.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? src, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Explicit)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public IntPtr NamePtr;
        public CounterValue Value;
    }

    private readonly IntPtr _query;
    private readonly IntPtr _cpuTotal, _cpuPerf, _cpuFreq, _cpuPerCore;
    private readonly IntPtr _committed;
    private readonly IntPtr _diskRead, _diskWrite, _diskQueue, _diskIdle;

    public PdhQuery()
    {
        uint rc = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (rc != 0) throw new InvalidOperationException($"PdhOpenQuery fallo: 0x{rc:X8}");

        // % Processor Utility, not % Processor Time: the latter undercounts under turbo.
        _cpuTotal = Add(@"\Processor Information(_Total)\% Processor Utility");
        _cpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
        _cpuFreq = Add(@"\Processor Information(_Total)\Processor Frequency");
        _cpuPerCore = Add(@"\Processor Information(*)\% Processor Utility");
        _committed = Add(@"\Memory\Committed Bytes");
        _diskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskQueue = Add(@"\PhysicalDisk(*)\Current Disk Queue Length");

        // Task Manager's "Active time" is 100 - % Idle Time. "% Disk Time" is not it: it counts
        // queued requests and routinely reads far above 100 %.
        _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");

        PdhCollectQueryData(_query);   // rate counters need a baseline
    }

    private IntPtr Add(string path)
    {
        uint rc = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr counter);
        return rc == 0 ? counter : IntPtr.Zero;
    }

    public void Collect() => PdhCollectQueryData(_query);

    /// <summary>Capped at 100: a usage bar cannot be more than full.</summary>
    public double CpuTotalPct => Scalar(_cpuTotal);

    public double CommittedBytes => Scalar(_committed);

    /// <summary>
    /// Nominal MHz scaled by how hard the cores are actually clocking. This is the one counter
    /// that must NOT be capped: "% Processor Performance" is measured against the nominal
    /// frequency, so 108 % is exactly how turbo shows up, and capping it would pin the reported
    /// clock to the base clock forever.
    /// </summary>
    public double CpuFrequencyMhz
    {
        get
        {
            double nominal = Scalar(_cpuFreq), perf = Scalar(_cpuPerf, cap: false);
            return double.IsNaN(nominal) || double.IsNaN(perf) ? double.NaN : nominal * perf / 100.0;
        }
    }

    public List<InstanceSample> CpuPerCore() => Array(_cpuPerCore);
    public List<InstanceSample> DiskRead() => Array(_diskRead);
    public List<InstanceSample> DiskWrite() => Array(_diskWrite);
    public List<InstanceSample> DiskQueue() => Array(_diskQueue);
    public List<InstanceSample> DiskIdle() => Array(_diskIdle);

    private static double Scalar(IntPtr counter, bool cap = true)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        uint fmt = cap ? PdhFmtDouble : PdhFmtDouble | PdhFmtNoCap100;
        uint rc = PdhGetFormattedCounterValue(counter, fmt, out _, out var v);
        return rc == 0 ? v.DoubleValue : double.NaN;
    }

    private static List<InstanceSample> Array(IntPtr counter)
    {
        var result = new List<InstanceSample>();
        if (counter == IntPtr.Zero) return result;

        uint size = 0;
        PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out _, IntPtr.Zero);
        if (size == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out uint count, buf) != 0)
                return result;

            int stride = Marshal.SizeOf<CounterValueItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterValueItem>(buf + i * stride);
                string name = Marshal.PtrToStringUni(item.NamePtr) ?? "?";
                result.Add(new InstanceSample(name, item.Value.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);
}
