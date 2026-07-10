using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Agent;

internal static class Program
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

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int intervalMs = IntArg(args, "--interval=", 1000);
        // Processes are the dominant cost (NtQuerySystemInformation, ~6-16 ms) and the least
        // time-sensitive number on screen, so they sample every Nth tick by default.
        int procEvery = IntArg(args, "--proc-every=", 3);
        bool verbose = args.Contains("--verbose");

        SeqLockWriter<Snapshot> writer;
        try
        {
            writer = SnapshotChannel.CreateWriter();
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Ya hay un agente corriendo (la memoria compartida existe).");
            return 1;
        }

        using (writer)
        using (var pdh = new PdhQuery())
        using (var procs = new Processes())
        {
            var hwi = HwiSensors.TryOpen(out string? hwiError);
            var nvml = Nvml.TryOpen(out string? nvmlError);

            Console.WriteLine($"Agente en marcha. {SnapshotLayout.MapName}, {writer.SizeBytes} B, cada {intervalMs} ms.");
            Console.WriteLine($"  HWiNFO: {(hwi is not null ? $"OK ({hwi.CpuName})" : $"no disponible - {hwiError}")}");
            Console.WriteLine($"  NVML:   {(nvml is not null ? $"OK ({nvml.Count} GPU)" : $"no disponible - {nvmlError}")}");
            Console.WriteLine("Ctrl+C para parar.");
            Console.WriteLine();

            using var quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };

            var nics = ActiveNics();
            var prevNet = nics.Select(Stats).ToArray();
            long prevNetStamp = Stopwatch.GetTimestamp();
            long lastNicRefresh = prevNetStamp;

            var snapshot = default(Snapshot);
            long ticks = 0;
            double worstMs = 0;

            // The elevated helper is optional and can come and go. Re-probe periodically instead
            // of deciding once at startup.
            SeqLockReader<EtwSnapshot>? etw = EtwChannel.TryOpenReader(out _);
            Console.WriteLine($"  ETW:    {(etw is not null ? "OK (helper elevado presente)" : "no disponible - lanza SidebarMonitor.Etw para colores por proceso")}");
            Console.WriteLine();
            long lastEtwProbe = Stopwatch.GetTimestamp();

            while (!quit.IsSet)
            {
                long t0 = Stopwatch.GetTimestamp();

                if (etw is null && Stopwatch.GetElapsedTime(lastEtwProbe).TotalSeconds >= 5)
                {
                    lastEtwProbe = Stopwatch.GetTimestamp();
                    etw = EtwChannel.TryOpenReader(out _);
                    if (etw is not null) Console.WriteLine("ETW: helper detectado.");
                }

                pdh.Collect();
                snapshot.TimestampUtcTicks = DateTime.UtcNow.Ticks;
                snapshot.SampleIntervalSec = intervalMs / 1000.0;
                snapshot.HwiNfoAvailable = hwi is not null;

                FillCpu(ref snapshot.Cpu, pdh, hwi);
                FillMem(ref snapshot.Mem, pdh);
                FillGpus(ref snapshot, nvml);
                FillDisks(ref snapshot, pdh);

                // Adapters come and go (VPN up, dock unplugged); rescanning every tick is wasteful.
                if (Stopwatch.GetElapsedTime(lastNicRefresh).TotalSeconds >= 30)
                {
                    nics = ActiveNics();
                    prevNet = nics.Select(Stats).ToArray();
                    prevNetStamp = Stopwatch.GetTimestamp();
                    lastNicRefresh = Stopwatch.GetTimestamp();
                }
                FillNics(ref snapshot, nics, ref prevNet, ref prevNetStamp);

                // On skipped ticks the previous top list stays in the snapshot untouched.
                if (ticks % procEvery == 0) FillProcs(ref snapshot, procs);

                etw = MergeEtw(ref snapshot, etw);

                writer.Publish(snapshot);

                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                worstMs = Math.Max(worstMs, ms);
                ticks++;

                if (verbose)
                    Console.WriteLine($"tick {ticks,5}  {ms,6:F2} ms (peor {worstMs:F2})  cpu {snapshot.Cpu.TotalUsagePct,5:F1}%  " +
                                      $"{snapshot.Cpu.PackagePowerW,5:F1} W  gpu {(snapshot.GpuCount > 0 ? snapshot.Gpus[0].PowerW : 0),5:F1} W");

                quit.Wait(Math.Max(1, intervalMs - (int)ms));
            }

            Console.WriteLine();
            Console.WriteLine($"Parado tras {ticks} ticks. Peor muestreo: {worstMs:F2} ms.");
            hwi?.Dispose();
            nvml?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// Copies the elevated helper's attribution into our snapshot, if it is publishing. Returns
    /// the reader, or null if the helper died (a stale map keeps its last values forever, so the
    /// timestamp is what tells us it is gone).
    /// </summary>
    private static SeqLockReader<EtwSnapshot>? MergeEtw(ref Snapshot s, SeqLockReader<EtwSnapshot>? etw)
    {
        s.EtwAvailable = false;
        if (etw is null) return null;

        if (!etw.TryRead(out var e)) return etw;   // caught it mid-write; try again next tick

        var age = DateTime.UtcNow - new DateTime(e.TimestampUtcTicks, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(5))
        {
            etw.Dispose();
            Console.WriteLine("ETW: el helper dejo de publicar.");
            return null;
        }

        s.CoreOwners = e.Cores;
        s.CoreOwnerSamples = e.CoreSamples;
        s.NetProcCount = e.NetProcCount;
        s.NetProcs = e.NetProcs;
        s.EtwAvailable = true;
        return etw;
    }

    private static void FillCpu(ref CpuInfo cpu, PdhQuery pdh, HwiSensors? hwi)
    {
        NameField.Set(ref cpu.Name, hwi?.CpuName ?? "CPU");
        cpu.TotalUsagePct = Clamp100(pdh.CpuTotalPct);
        cpu.FrequencyMhz = (float)pdh.CpuFrequencyMhz;
        cpu.PackagePowerW = (float)(hwi?.PackagePowerW ?? double.NaN);
        cpu.TempC = (float)(hwi?.CpuTempC ?? double.NaN);

        int n = 0;
        foreach (var s in pdh.CpuPerCore())
        {
            // Instances are "<group>,<core>" plus the "_Total" aggregates, which we skip.
            if (s.Instance.Contains("_Total", StringComparison.Ordinal)) continue;
            if (n >= SnapshotLayout.MaxCores) break;
            cpu.CoreUsagePct[n++] = Clamp100(s.Value);
        }
        cpu.CoreCount = n;
    }

    /// <summary>
    /// "% Processor Utility" is measured against the nominal frequency, so a boosting core
    /// legitimately reports 105 %. PDH does not cap it — clearing PDH_FMT_NOCAP100 changes
    /// nothing, verified. A usage bar cannot be more than full, so we clamp it ourselves.
    /// The real clock still comes through untouched via "% Processor Performance".
    /// </summary>
    private static float Clamp100(double v) =>
        double.IsNaN(v) ? float.NaN : (float)Math.Clamp(v, 0, 100);

    private static void FillMem(ref MemInfo mem, PdhQuery pdh)
    {
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return;

        mem.PhysTotal = m.ullTotalPhys;
        mem.PhysUsed = m.ullTotalPhys - m.ullAvailPhys;
        mem.CommitTotal = m.ullTotalPageFile;
        mem.CommitUsed = (ulong)Math.Max(0, pdh.CommittedBytes);
    }

    private static void FillGpus(ref Snapshot s, Nvml? nvml)
    {
        s.GpuCount = nvml?.Count ?? 0;
        for (int i = 0; i < s.GpuCount; i++) nvml!.Fill(i, ref s.Gpus[i]);
    }

    private static void FillDisks(ref Snapshot s, PdhQuery pdh)
    {
        var read = pdh.DiskRead();
        var write = pdh.DiskWrite();
        var queue = pdh.DiskQueue();

        int n = 0;
        for (int i = 0; i < read.Count && n < SnapshotLayout.MaxDisks; i++)
        {
            if (read[i].Instance.Contains("_Total", StringComparison.Ordinal)) continue;

            ref var d = ref s.Disks[n];
            NameField.Set(ref d.Name, read[i].Instance);
            d.ReadBytesPerSec = read[i].Value;
            d.WriteBytesPerSec = i < write.Count ? write[i].Value : 0;
            d.QueueLength = i < queue.Count ? queue[i].Value : 0;
            n++;
        }
        s.DiskCount = n;
    }

    private static NetworkInterface[] ActiveNics() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Take(SnapshotLayout.MaxNics)
            .ToArray();

    private static (long Rx, long Tx) Stats(NetworkInterface n)
    {
        var s = n.GetIPStatistics();
        return (s.BytesReceived, s.BytesSent);
    }

    private static void FillNics(ref Snapshot s, NetworkInterface[] nics, ref (long Rx, long Tx)[] prev, ref long prevStamp)
    {
        double secs = Stopwatch.GetElapsedTime(prevStamp).TotalSeconds;
        prevStamp = Stopwatch.GetTimestamp();
        if (secs <= 0) secs = 1;

        for (int i = 0; i < nics.Length; i++)
        {
            var now = Stats(nics[i]);
            ref var nic = ref s.Nics[i];
            NameField.Set(ref nic.Name, nics[i].Name);
            nic.RxBytesPerSec = (now.Rx - prev[i].Rx) / secs;
            nic.TxBytesPerSec = (now.Tx - prev[i].Tx) / secs;
            nic.LinkBitsPerSec = (ulong)Math.Max(0, nics[i].Speed);
            prev[i] = now;
        }
        s.NicCount = nics.Length;
    }

    private static void FillProcs(ref Snapshot s, Processes procs)
    {
        var top = procs.Top(SnapshotLayout.MaxProcs);
        for (int i = 0; i < top.Count; i++)
        {
            ref var p = ref s.Procs[i];
            NameField.Set(ref p.Name, top[i].Name);
            p.Pid = top[i].Pid;
            p.CpuPct = top[i].CpuPct;
            p.WorkingSet = top[i].WorkingSet;
            p.Threads = top[i].Threads;
        }
        s.ProcCount = top.Count;
        s.TotalProcesses = procs.TotalProcesses;
        s.TotalThreads = procs.TotalThreads;
    }

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}
