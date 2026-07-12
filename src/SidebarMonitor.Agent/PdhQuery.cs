using System.Runtime.InteropServices;
using SidebarMonitor.Shared;

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
    private readonly IntPtr _cpuTotal, _cpuPerf, _cpuPerfPerCore, _cpuFreq, _cpuPerCore;
    private readonly IntPtr _committed;
    private readonly IntPtr _diskRead, _diskWrite, _diskQueue, _diskIdle;
    private readonly IntPtr _gpuEngine;

    public PdhQuery()
    {
        uint rc = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (rc != 0) throw new InvalidOperationException($"PdhOpenQuery fallo: 0x{rc:X8}");

        // % Processor Utility, not % Processor Time: the latter undercounts under turbo.
        _cpuTotal = Add(@"\Processor Information(_Total)\% Processor Utility");
        _cpuPerf = Add(@"\Processor Information(_Total)\% Processor Performance");
        _cpuPerfPerCore = Add(@"\Processor Information(*)\% Processor Performance");
        _cpuFreq = Add(@"\Processor Information(_Total)\Processor Frequency");
        _cpuPerCore = Add(@"\Processor Information(*)\% Processor Utility");
        _committed = Add(@"\Memory\Committed Bytes");
        _diskRead = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _diskQueue = Add(@"\PhysicalDisk(*)\Current Disk Queue Length");

        // Task Manager's "Active time" is 100 - % Idle Time. "% Disk Time" is not it: it counts
        // queued requests and routinely reads far above 100 %.
        _diskIdle = Add(@"\PhysicalDisk(*)\% Idle Time");

        // Per-process, per-engine GPU utilisation. Instances are "pid_N_..._engtype_3D" etc.;
        // aggregating them tells us what the GPU is doing and who's driving it. Absent on machines
        // without the GPU counter set — then it just returns nothing.
        _gpuEngine = Add(@"\GPU Engine(*)\Utilization Percentage");

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

    /// <summary>The rated (base) frequency. "% Processor Performance" is a percentage of this.</summary>
    public double NominalMhz => Scalar(_cpuFreq);

    /// <summary>
    /// Effective clock: nominal × how hard the cores clock. Aggregated per the mode — best core
    /// shows the boost bin a game actually reaches, mean/median the whole-package picture.
    ///
    /// Must NOT cap the performance percentage: it is measured against the nominal frequency, so
    /// 120 % is exactly how a 5.05 GHz boost on a 4.2 GHz base shows up. Capping pins it to base.
    /// </summary>
    public double CpuFrequencyMhz(CpuFreqMode mode)
    {
        double nominal = NominalMhz;
        if (double.IsNaN(nominal)) return double.NaN;

        double perf;
        if (mode == CpuFreqMode.Mean)
        {
            perf = Scalar(_cpuPerf, cap: false);
        }
        else
        {
            var cores = Array(_cpuPerfPerCore)
                .Where(s => !s.Instance.Contains("_Total", StringComparison.Ordinal))
                .Select(s => s.Value)
                .Where(v => !double.IsNaN(v))
                .ToList();
            if (cores.Count == 0) return double.NaN;

            if (mode == CpuFreqMode.Best) perf = cores.Max();
            else { cores.Sort(); perf = cores[cores.Count / 2]; }   // median
        }

        return double.IsNaN(perf) ? double.NaN : nominal * perf / 100.0;
    }

    public List<InstanceSample> CpuPerCore() => Array(_cpuPerCore);
    /// <summary>Per-core "% Processor Performance"; × nominal / 100 gives each core's clock.</summary>
    public List<InstanceSample> CpuPerCorePerf() => Array(_cpuPerfPerCore);
    public List<InstanceSample> DiskRead() => Array(_diskRead);
    public List<InstanceSample> DiskWrite() => Array(_diskWrite);
    public List<InstanceSample> DiskQueue() => Array(_diskQueue);
    public List<InstanceSample> DiskIdle() => Array(_diskIdle);
    public List<InstanceSample> GpuEngine() => Array(_gpuEngine);

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
