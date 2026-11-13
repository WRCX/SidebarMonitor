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
    private readonly IntPtr _poolNonpaged, _poolPagedResident, _driverResident, _kernelCodeResident, _cacheResident, _modified;
    private readonly IntPtr _diskRead, _diskWrite, _diskQueue, _diskIdle;
    private readonly IntPtr _gpuEngine, _gpuMemLocal, _gpuMemNonLocal, _gpuMemDedicated, _gpuMemShared;

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

        // What Windows itself holds in RAM, for the RAM bar's Windows segment. The *Resident* variants:
        // the paged pool's full size includes pages that are out on disk and use no RAM at all.
        _poolNonpaged = Add(@"\Memory\Pool Nonpaged Bytes");
        _poolPagedResident = Add(@"\Memory\Pool Paged Resident Bytes");
        _driverResident = Add(@"\Memory\System Driver Resident Bytes");
        _kernelCodeResident = Add(@"\Memory\System Code Resident Bytes");
        _cacheResident = Add(@"\Memory\System Cache Resident Bytes");
        _modified = Add(@"\Memory\Modified Page List Bytes");
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

        // Per-process GPU memory, same "pid_N_luid_..." instances. Local = the adapter's own memory,
        // Non Local = GPU-visible system RAM. ~120 instances on the dev desktop, most of them non-zero.
        _gpuMemLocal = Add(@"\GPU Process Memory(*)\Local Usage");
        _gpuMemNonLocal = Add(@"\GPU Process Memory(*)\Non Local Usage");
        // An iGPU's segments are all "local" (it has no memory of its own to be non-local to), so its
        // per-process VRAM comes from these instead — see FillEngines.
        _gpuMemDedicated = Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _gpuMemShared = Add(@"\GPU Process Memory(*)\Shared Usage");

        PdhCollectQueryData(_query);   // rate counters need a baseline
    }

    private IntPtr Add(string path)
    {
        uint rc = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr counter);
        return rc == 0 ? counter : IntPtr.Zero;
    }

    public void Collect()
    {
        PdhCollectQueryData(_query);
        _perfPerCore = null;   // the cached per-core performance belongs to the previous sample
    }

    // "% Processor Performance" per core is wanted three times a tick (best and median clock, and the
    // per-core rows); read it once per Collect instead of formatting the same array three times.
    private List<InstanceSample>? _perfPerCore;
    private List<InstanceSample> PerfPerCore() => _perfPerCore ??= Array(_cpuPerfPerCore);

    /// <summary>Capped at 100: a usage bar cannot be more than full.</summary>
    public double CpuTotalPct => Scalar(_cpuTotal);

    public double CommittedBytes => Scalar(_committed);

    /// <summary>The kernel's resident RAM: nonpaged pool, resident paged pool, drivers, kernel code.</summary>
    public double KernelResidentBytes =>
        Bytes(_poolNonpaged) + Bytes(_poolPagedResident) + Bytes(_driverResident) + Bytes(_kernelCodeResident);

    /// <summary>The file cache's resident pages (the system working set's file pages).</summary>
    public double CacheResidentBytes => Bytes(_cacheResident);

    /// <summary>The modified list: written-to pages of any kind awaiting write-back to disk.</summary>
    public double ModifiedBytes => Bytes(_modified);

    /// <summary>A byte counter, uncapped, and 0 rather than NaN when absent so the sums above survive one
    /// missing counter (System Code Resident Bytes reads 0 on some builds, and may not exist on others).</summary>
    private static double Bytes(IntPtr counter)
    {
        double v = Scalar(counter, cap: false);
        return double.IsNaN(v) || v < 0 ? 0 : v;
    }

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
            var cores = new List<double>(Environment.ProcessorCount);
            foreach (var s in PerfPerCore())
                if (!double.IsNaN(s.Value) && !s.Instance.Contains("_Total", StringComparison.Ordinal))
                    cores.Add(s.Value);
            if (cores.Count == 0) return double.NaN;

            if (mode == CpuFreqMode.Best) perf = cores.Max();
            else { cores.Sort(); perf = cores[cores.Count / 2]; }   // median
        }

        return double.IsNaN(perf) ? double.NaN : nominal * perf / 100.0;
    }

    public List<InstanceSample> CpuPerCore() => Array(_cpuPerCore);
    /// <summary>Per-core "% Processor Performance"; × nominal / 100 gives each core's clock.</summary>
    public List<InstanceSample> CpuPerCorePerf() => PerfPerCore();
    public List<InstanceSample> DiskRead() => Array(_diskRead);
    public List<InstanceSample> DiskWrite() => Array(_diskWrite);
    public List<InstanceSample> DiskQueue() => Array(_diskQueue);
    public List<InstanceSample> DiskIdle() => Array(_diskIdle);

    /// <summary>
    /// Only the GPU Engine instances doing work this sample (value &gt; 0) — the only ones the agent
    /// uses. Every process that ever touched the GPU keeps an instance per engine per adapter: 751 on
    /// the dev desktop, of which 5 were non-zero, and building a string for each of the other 746 was
    /// 173 KiB of garbage a second, 93 % of everything the PDH tick allocated.
    /// </summary>
    public List<InstanceSample> GpuEngineActive() => Array(_gpuEngine, activeOnly: true);

    /// <summary>False on machines without the GPU Engine counter set.</summary>
    public bool HasGpuEngine => _gpuEngine != IntPtr.Zero;

    /// <summary>Per-process bytes in the adapter's own memory, non-zero instances only. Uncapped: these
    /// are byte counts, and the default format would clamp any of them to 100.</summary>
    public List<InstanceSample> GpuMemLocalActive() => Array(_gpuMemLocal, activeOnly: true, cap: false);

    /// <summary>Per-process bytes of GPU-visible system RAM, non-zero instances only.</summary>
    public List<InstanceSample> GpuMemNonLocalActive() => Array(_gpuMemNonLocal, activeOnly: true, cap: false);

    /// <summary>Per-process bytes in dedicated segments (an iGPU's carve-out), non-zero instances only.</summary>
    public List<InstanceSample> GpuMemDedicatedActive() => Array(_gpuMemDedicated, activeOnly: true, cap: false);

    /// <summary>Per-process bytes in shared segments (system RAM the GPU uses), non-zero instances only.</summary>
    public List<InstanceSample> GpuMemSharedActive() => Array(_gpuMemShared, activeOnly: true, cap: false);

    private static double Scalar(IntPtr counter, bool cap = true)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        uint fmt = cap ? PdhFmtDouble : PdhFmtDouble | PdhFmtNoCap100;
        uint rc = PdhGetFormattedCounterValue(counter, fmt, out _, out var v);
        return rc == 0 ? v.DoubleValue : double.NaN;
    }

    /// <param name="activeOnly">Skip instances whose value is NaN or ≤ 0 before their name is decoded:
    /// the name is the expensive part, and it is never needed for an idle instance.</param>
    private static List<InstanceSample> Array(IntPtr counter, bool activeOnly = false, bool cap = true)
    {
        var result = new List<InstanceSample>();
        if (counter == IntPtr.Zero) return result;

        uint fmt = cap ? PdhFmtDouble : PdhFmtDouble | PdhFmtNoCap100;
        uint size = 0;
        PdhGetFormattedCounterArrayW(counter, fmt, ref size, out _, IntPtr.Zero);
        if (size == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, fmt, ref size, out uint count, buf) != 0)
                return result;

            for (int i = 0; i < count; i++)
            {
                // A plain read of the blittable struct: PtrToStructure goes through the marshaller and
                // allocated for every one of the 751 GPU Engine items, active or not.
                CounterValueItem item;
                unsafe { item = ((CounterValueItem*)buf)[i]; }
                if (activeOnly && !(item.Value.DoubleValue > 0)) continue;   // also drops NaN
                string name = Marshal.PtrToStringUni(item.NamePtr) ?? "?";
                result.Add(new InstanceSample(name, item.Value.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public void Dispose() => PdhCloseQuery(_query);
}
