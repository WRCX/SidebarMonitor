using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NativeProbe;

/// <summary>
/// Viability probe for everything that does NOT need HWiNFO: CPU, RAM, disks, network,
/// processes (Windows APIs) and GPU (NVML). Runs unelevated, ships no kernel driver.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var total = Stopwatch.StartNew();

        Section("CPU (PDH, contadores en ingles sobre Windows en espanol)");
        Pdh.Demo();

        Section("Memoria (GlobalMemoryStatusEx)");
        Mem.Demo();

        Section("Red (GetIfEntry2 via NetworkInformation)");
        Net.Demo();

        Section("Top procesos (NtQuerySystemInformation, 1 sola llamada)");
        Proc.Demo();

        Section("GPU (NVML)");
        Nvml.Demo();

        Console.WriteLine();
        Console.WriteLine($"Total del arranque completo (incl. ventanas de muestreo): {total.Elapsed.TotalMilliseconds:F0} ms");
        return 0;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }
}

// ---------------------------------------------------------------- PDH

internal static class Pdh
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? src, IntPtr userData, out IntPtr query);

    // The key call: takes the ENGLISH counter path on any locale. PdhAddCounterW would
    // require "\Informacion del procesador(_Total)\..." on this machine.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValueItem
    {
        public IntPtr NamePtr;
        public PdhCounterValue Value;
    }

    public static void Demo()
    {
        uint rc = PdhOpenQueryW(null, IntPtr.Zero, out IntPtr q);
        if (rc != 0) { Console.WriteLine($"  PdhOpenQuery fallo: 0x{rc:X8}"); return; }

        // % Processor Utility, not % Processor Time: the latter undercounts with turbo/boost.
        Add(q, @"\Processor Information(_Total)\% Processor Utility", out IntPtr cUtil);
        Add(q, @"\Processor Information(_Total)\% Processor Performance", out IntPtr cPerf);
        Add(q, @"\Processor Information(*)\% Processor Utility", out IntPtr cPerCore);
        Add(q, @"\Memory\Committed Bytes", out IntPtr cCommitted);
        Add(q, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", out IntPtr cDiskR);
        Add(q, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", out IntPtr cDiskW);
        Add(q, @"\PhysicalDisk(_Total)\Current Disk Queue Length", out IntPtr cDiskQ);

        // Rate counters need two samples separated in time.
        PdhCollectQueryData(q);
        Thread.Sleep(1000);
        var sw = Stopwatch.StartNew();
        PdhCollectQueryData(q);
        sw.Stop();

        Console.WriteLine($"  PdhCollectQueryData (todos los contadores de golpe): {sw.Elapsed.TotalMicroseconds:F0} us");
        Console.WriteLine();
        Console.WriteLine($"  CPU total .......... {Read(cUtil):F1} %   (% Processor Utility)");

        double perf = Read(cPerf);
        Console.WriteLine($"  Rendimiento ........ {perf:F1} %   -> ~{4.2 * perf / 100.0:F2} GHz sobre una base de 4.2 GHz");
        Console.WriteLine($"  Committed .......... {Read(cCommitted) / 1024.0 / 1024 / 1024:F2} GiB");
        Console.WriteLine($"  Disco lectura ...... {Read(cDiskR) / 1024.0 / 1024:F2} MiB/s");
        Console.WriteLine($"  Disco escritura .... {Read(cDiskW) / 1024.0 / 1024:F2} MiB/s");
        Console.WriteLine($"  Cola de disco ...... {Read(cDiskQ):F2}");

        // Wildcard instance expansion: one counter, N cores.
        var cores = ReadArray(cPerCore);
        Console.WriteLine();
        Console.WriteLine($"  Por core ({cores.Count} instancias, incluye _Total):");
        foreach (var (name, v) in cores.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"    {name,-10} {v,6:F1} %");

        PdhCloseQuery(q);
    }

    private static void Add(IntPtr q, string path, out IntPtr counter)
    {
        uint rc = PdhAddEnglishCounterW(q, path, IntPtr.Zero, out counter);
        if (rc != 0) Console.WriteLine($"  [!] no se pudo anadir '{path}': 0x{rc:X8}");
    }

    private static double Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        uint rc = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v);
        return rc == 0 ? v.DoubleValue : double.NaN;
    }

    private static Dictionary<string, double> ReadArray(IntPtr counter)
    {
        var result = new Dictionary<string, double>();
        if (counter == IntPtr.Zero) return result;

        uint size = 0, count = 0;
        PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
        if (size == 0) return result;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf);
            if (rc != 0) return result;

            int stride = Marshal.SizeOf<PdhCounterValueItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhCounterValueItem>(buf + i * stride);
                string name = Marshal.PtrToStringUni(item.NamePtr) ?? "?";
                result[name] = item.Value.DoubleValue;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }
}

// ---------------------------------------------------------------- Memory

internal static class Mem
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public static void Demo()
    {
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var sw = Stopwatch.StartNew();
        bool ok = GlobalMemoryStatusEx(ref m);
        sw.Stop();
        if (!ok) { Console.WriteLine("  fallo"); return; }

        const double G = 1024.0 * 1024 * 1024;
        Console.WriteLine($"  llamada: {sw.Elapsed.TotalMicroseconds:F1} us");
        Console.WriteLine($"  Fisica ..... {(m.ullTotalPhys - m.ullAvailPhys) / G:F2} usados / {m.ullTotalPhys / G:F2} GiB   ({m.dwMemoryLoad} %)");
        Console.WriteLine($"  Disponible . {m.ullAvailPhys / G:F2} GiB");
        Console.WriteLine($"  Commit ..... {(m.ullTotalPageFile - m.ullAvailPageFile) / G:F2} / {m.ullTotalPageFile / G:F2} GiB");
    }
}

// ---------------------------------------------------------------- Network

internal static class Net
{
    public static void Demo()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToArray();

        var before = nics.Select(n => n.GetIPStatistics()).Select(s => (s.BytesReceived, s.BytesSent)).ToArray();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(1000);
        sw.Stop();
        var after = nics.Select(n => n.GetIPStatistics()).Select(s => (s.BytesReceived, s.BytesSent)).ToArray();

        double secs = sw.Elapsed.TotalSeconds;
        for (int i = 0; i < nics.Length; i++)
        {
            double dn = (after[i].BytesReceived - before[i].BytesReceived) / secs;
            double up = (after[i].BytesSent - before[i].BytesSent) / secs;
            string speed = nics[i].Speed > 0 ? $"{nics[i].Speed / 1_000_000.0:F0} Mbps" : "?";
            Console.WriteLine($"  {Trunc(nics[i].Name, 22),-22} link {speed,-10} DL {dn / 1024:F1} KiB/s   UL {up / 1024:F1} KiB/s");
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";
}

// ---------------------------------------------------------------- Processes

internal static class Proc
{
    private const int SystemProcessInformation = 5;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int cls, IntPtr buf, uint len, out uint retLen);

    // x64 offsets into SYSTEM_PROCESS_INFORMATION.
    private const int OffNextEntry = 0;
    private const int OffThreadCount = 4;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageNameLen = 56;
    private const int OffImageNameBuf = 64;
    private const int OffUniquePid = 80;
    private const int OffWorkingSet = 144;

    private record struct Snap(long Cpu100Ns, int Threads, long WorkingSet, string Name);

    public static void Demo()
    {
        int cores = Environment.ProcessorCount;

        var t0 = Stopwatch.GetTimestamp();
        var a = Sample(out double firstCallMs, out int bufKiB);
        Thread.Sleep(1000);
        var b = Sample(out double secondCallMs, out _);
        double wallSecs = Stopwatch.GetElapsedTime(t0).TotalSeconds;

        Console.WriteLine($"  1 llamada = {b.Count} procesos, buffer {bufKiB} KiB, {secondCallMs:F2} ms");
        Console.WriteLine($"  (PDH por proceso necesitaria ~{b.Count} contadores y nombres tipo 'chrome#7')");
        Console.WriteLine();

        var rows = new List<(string Name, int Pid, double Cpu, long Ws, int Threads)>();
        foreach (var (pid, cur) in b)
        {
            if (!a.TryGetValue(pid, out var prev)) continue;
            double cpu = (cur.Cpu100Ns - prev.Cpu100Ns) / 1e7 / wallSecs / cores * 100.0;
            rows.Add((cur.Name, pid, cpu, cur.WorkingSet, cur.Threads));
        }

        Console.WriteLine($"  {"Proceso",-28} {"PID",7} {"CPU%",7} {"WorkSet",12} {"Thr",5}");
        foreach (var r in rows.OrderByDescending(r => r.Cpu).ThenByDescending(r => r.Ws).Take(12))
            Console.WriteLine($"  {r.Name,-28} {r.Pid,7} {r.Cpu,7:F2} {r.Ws / 1024.0 / 1024,9:F1} MiB {r.Threads,5}");

        Console.WriteLine();
        Console.WriteLine($"  Total: {rows.Count} procesos, {rows.Sum(r => r.Threads)} threads");
    }

    private static Dictionary<int, Snap> Sample(out double ms, out int bufKiB)
    {
        uint size = 1 << 20;
        IntPtr buf = IntPtr.Zero;
        uint rc;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            buf = buf == IntPtr.Zero ? Marshal.AllocHGlobal((int)size) : Marshal.ReAllocHGlobal(buf, (nint)size);
            rc = NtQuerySystemInformation(SystemProcessInformation, buf, size, out uint need);
            if (rc != STATUS_INFO_LENGTH_MISMATCH) break;
            size = Math.Max(need + 8192, size * 2);
        }
        sw.Stop();
        ms = sw.Elapsed.TotalMilliseconds;
        bufKiB = (int)(size / 1024);

        var result = new Dictionary<int, Snap>(512);
        if (rc != 0) { Marshal.FreeHGlobal(buf); return result; }

        try
        {
            IntPtr p = buf;
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
                string name = (namePtr != IntPtr.Zero && nameLen > 0)
                    ? Marshal.PtrToStringUni(namePtr, nameLen / 2)
                    : (pid == 0 ? "Idle" : $"pid{pid}");

                result[pid] = new Snap(user + kernel, threads, ws, name);

                if (next == 0) break;
                p += next;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }
}

// ---------------------------------------------------------------- NVML

internal static class Nvml
{
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr dev, byte[] name, uint len);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out MemoryInfo mem);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint percent);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEncoderUtilization(IntPtr dev, out uint util, out uint period);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr dev, out uint width);

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public ulong Total, Free, Used; }

    public static void Demo()
    {
        int rc = nvmlInit_v2();
        if (rc != 0) { Console.WriteLine($"  nvmlInit fallo: {rc} (driver NVIDIA no presente?)"); return; }

        try
        {
            nvmlDeviceGetCount_v2(out uint n);
            Console.WriteLine($"  GPUs NVIDIA detectadas: {n}");

            for (uint i = 0; i < n; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr dev) != 0) continue;

                var nameBuf = new byte[96];
                nvmlDeviceGetName(dev, nameBuf, (uint)nameBuf.Length);
                string name = System.Text.Encoding.ASCII.GetString(nameBuf).TrimEnd('\0');

                var sw = Stopwatch.StartNew();
                nvmlDeviceGetUtilizationRates(dev, out var util);
                nvmlDeviceGetMemoryInfo(dev, out var mem);
                nvmlDeviceGetTemperature(dev, 0, out uint temp);
                nvmlDeviceGetPowerUsage(dev, out uint mw);
                nvmlDeviceGetClockInfo(dev, 0, out uint gfxClk);
                nvmlDeviceGetClockInfo(dev, 1, out uint smClk);
                nvmlDeviceGetClockInfo(dev, 2, out uint memClk);
                sw.Stop();

                int fanRc = nvmlDeviceGetFanSpeed(dev, out uint fan);
                int encRc = nvmlDeviceGetEncoderUtilization(dev, out uint enc, out _);
                int pcieRc = nvmlDeviceGetCurrPcieLinkWidth(dev, out uint width);

                const double G = 1024.0 * 1024 * 1024;
                Console.WriteLine($"  [{i}] {name}");
                Console.WriteLine($"      lectura completa: {sw.Elapsed.TotalMicroseconds:F0} us");
                Console.WriteLine($"      GPU load ..... {util.Gpu} %      Mem controller {util.Memory} %");
                Console.WriteLine($"      VRAM ......... {mem.Used / G:F2} / {mem.Total / G:F2} GiB");
                Console.WriteLine($"      Temp ......... {temp} C");
                Console.WriteLine($"      Potencia ..... {mw / 1000.0:F2} W");
                Console.WriteLine($"      Relojes ...... core {gfxClk} MHz   shader {smClk} MHz   mem {memClk} MHz");
                Console.WriteLine($"      Fan .......... {(fanRc == 0 ? fan + " %" : "n/d")}");
                Console.WriteLine($"      Encoder ...... {(encRc == 0 ? enc + " %" : "n/d")}");
                Console.WriteLine($"      PCIe width ... {(pcieRc == 0 ? "x" + width : "n/d")}");
            }
        }
        finally { nvmlShutdown(); }
    }
}
