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

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>
    /// Which adapter the default route would pick for an outside address. Guessing by
    /// "has a gateway" misfires: Tailscale and the Hyper-V switches also have one.
    /// </summary>
    private static uint PrimaryInterfaceIndex()
    {
        // 8.8.8.8 — the value is a palindrome, so byte order does not matter here.
        return GetBestInterface(0x08080808, out uint index) == 0 ? index : 0;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int intervalMs = IntArg(args, "--interval=", 1000);
        // Processes are the dominant cost (NtQuerySystemInformation, ~6-16 ms) and the least
        // time-sensitive number on screen, so they sample every Nth tick by default.
        int procEvery = IntArg(args, "--proc-every=", 3);
        bool verbose = args.Contains("--verbose");
        bool groupProcs = !args.Contains("--no-group");

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

            // Disk identity never changes while we run; a single collect gives us the instances.
            pdh.Collect();
            var inventory = DiskInventory.Enumerate(pdh.DiskRead().Select(d => d.Instance));

            // NVMe drive temperature we read ourselves (unelevated), no HWiNFO. SATA still needs
            // HWiNFO (ATA pass-through wants admin).
            using var diskTemps = new DiskTemps(inventory.Where(kv => kv.Value.Bus == "NVMe").Select(kv => kv.Key));

            Console.WriteLine($"Agente en marcha. {SnapshotLayout.MapName}, {writer.SizeBytes} B, cada {intervalMs} ms.");
            Console.WriteLine($"  HWiNFO: {(hwi is not null ? $"OK ({hwi.CpuName})" : $"no disponible - {hwiError}")}");
            Console.WriteLine($"  NVML:   {(nvml is not null ? $"OK ({nvml.Count} GPU)" : $"no disponible - {nvmlError}")}");
            Console.WriteLine($"  Discos: {inventory.Count} identificados");
            foreach (var (idx, id) in inventory.OrderBy(kv => kv.Key))
                Console.WriteLine($"          [{idx}] {id.Label,-18} {id.Model,-24} {id.Media,-7} {id.Bus,-6} {id.SizeBytes / 1e12:F2} TB");
            Console.WriteLine($"  Procesos: {(groupProcs ? "agrupados por nombre" : "sin agrupar")}");
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

            // HWiNFO's SHM can freeze (the free build disables it after 12 h) or be replaced (a
            // restart makes a new mapping). Track poll_time; when it stalls, the readings are stale
            // and periodically re-open in case HWiNFO came back with a fresh object and new indices.
            long lastPoll = hwi?.PollTime ?? 0;
            long lastPollChange = Stopwatch.GetTimestamp();
            long lastHwiReopen = Stopwatch.GetTimestamp();

            while (!quit.IsSet)
            {
                long t0 = Stopwatch.GetTimestamp();

                if (etw is null && Stopwatch.GetElapsedTime(lastEtwProbe).TotalSeconds >= 5)
                {
                    lastEtwProbe = Stopwatch.GetTimestamp();
                    etw = EtwChannel.TryOpenReader(out _);
                    if (etw is not null) Console.WriteLine("ETW: helper detectado.");
                }

                // Is HWiNFO still updating? poll_time not advancing for >8 s means frozen.
                bool hwiLive = false;
                if (hwi is not null)
                {
                    long poll = hwi.PollTime;
                    if (poll != lastPoll) { lastPoll = poll; lastPollChange = Stopwatch.GetTimestamp(); }
                    hwiLive = Stopwatch.GetElapsedTime(lastPollChange).TotalSeconds < 8;
                }

                // Frozen or absent: try re-opening every 15 s. Catches HWiNFO restarting (new SHM
                // object, new reading order) and the user re-enabling Shared Memory Support.
                if ((hwi is null || !hwiLive) && Stopwatch.GetElapsedTime(lastHwiReopen).TotalSeconds >= 15)
                {
                    lastHwiReopen = Stopwatch.GetTimestamp();
                    var fresh = HwiSensors.TryOpen(out _);
                    if (fresh is not null)
                    {
                        hwi?.Dispose();
                        hwi = fresh;
                        lastPoll = hwi.PollTime;
                        lastPollChange = Stopwatch.GetTimestamp();
                        hwiLive = false;   // confirmed live only once poll_time advances
                    }
                }

                pdh.Collect();
                snapshot.TimestampUtcTicks = DateTime.UtcNow.Ticks;
                snapshot.SampleIntervalSec = intervalMs / 1000.0;
                snapshot.HwiNfoAvailable = hwi is not null;
                snapshot.HwiNfoLive = hwiLive;

                FillCpu(ref snapshot.Cpu, pdh, hwiLive ? hwi : null);
                FillMem(ref snapshot.Mem, pdh);
                FillGpus(ref snapshot, nvml);
                FillDisks(ref snapshot, pdh, inventory, diskTemps, hwiLive ? hwi : null);

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
                if (ticks % procEvery == 0) FillProcs(ref snapshot, procs, groupProcs);

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
        s.CpuFromAmd = false;
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

        // AMD SDK CPU sensors override HWiNFO's when present: they don't have the 12 h freeze and
        // work with HVCI. FreqBest stays PDH-derived (the SDK Fmax is the boost bin, not live).
        if (e.CpuSdkOk != 0)
        {
            s.Cpu.TempC = (float)e.CpuTempC;
            s.Cpu.PackagePowerW = e.CpuPackageW;
            s.CpuFromAmd = true;
        }
        return etw;
    }

    // The processor name string, read once from the registry — authoritative and unelevated, so
    // the CPU name never depends on HWiNFO either.
    private static readonly string CpuNameString = ReadCpuName();

    private static string ReadCpuName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static void FillCpu(ref CpuInfo cpu, PdhQuery pdh, HwiSensors? hwi)
    {
        NameField.Set(ref cpu.Name, CpuNameString);
        cpu.TotalUsagePct = Clamp100(pdh.CpuTotalPct);
        cpu.FreqBestMhz = (float)pdh.CpuFrequencyMhz(CpuFreqMode.Best);
        cpu.FreqMeanMhz = (float)pdh.CpuFrequencyMhz(CpuFreqMode.Mean);
        cpu.FreqMedianMhz = (float)pdh.CpuFrequencyMhz(CpuFreqMode.Median);
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

    private static void FillDisks(ref Snapshot s, PdhQuery pdh, Dictionary<int, DiskIdentity> inventory, DiskTemps diskTemps, HwiSensors? hwi)
    {
        var read = pdh.DiskRead();
        var write = pdh.DiskWrite();
        var queue = pdh.DiskQueue();
        var idle = pdh.DiskIdle();

        int n = 0;
        for (int i = 0; i < read.Count && n < SnapshotLayout.MaxDisks; i++)
        {
            string instance = read[i].Instance;
            if (instance.Contains("_Total", StringComparison.Ordinal)) continue;

            ref var d = ref s.Disks[n];
            NameField.Set(ref d.Name, instance);
            d.ReadBytesPerSec = read[i].Value;
            d.WriteBytesPerSec = i < write.Count ? write[i].Value : 0;
            d.QueueLength = i < queue.Count ? queue[i].Value : 0;
            d.ActivePct = i < idle.Count ? Clamp100(100.0 - idle[i].Value) : float.NaN;

            int space = instance.IndexOf(' ');
            string head = space < 0 ? instance : instance[..space];
            if (int.TryParse(head, out int index) && inventory.TryGetValue(index, out var id))
            {
                NameField.Set(ref d.Label, id.Label);
                NameField.Set(ref d.Volumes, id.Volumes);
                NameField.Set(ref d.Model, id.Model);
                NameField.Set(ref d.Bus, id.Bus);
                d.Media = id.Media;
                d.SizeBytes = id.SizeBytes;
                d.VolumeCount = (byte)Math.Clamp(id.VolumeCount, 0, 255);
                d.IsRemovable = (byte)(id.Removable ? 1 : 0);
                d.IsVirtual = (byte)(id.Virtual ? 1 : 0);
                d.IsSystem = (byte)(id.System ? 1 : 0);

                // Our own NVMe reading first; HWiNFO only as fallback (SATA, or NVMe if the IOCTL
                // ever fails). This is the first sensor we fully own — no HWiNFO for NVMe temp.
                double temp = diskTemps.TempC(index);
                if (double.IsNaN(temp)) temp = hwi?.DriveTempC(id.Model) ?? double.NaN;
                d.TempC = (float)temp;
            }
            else
            {
                d.TempC = float.NaN;
            }

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

        uint primary = PrimaryInterfaceIndex();

        for (int i = 0; i < nics.Length; i++)
        {
            var now = Stats(nics[i]);
            ref var nic = ref s.Nics[i];
            NameField.Set(ref nic.Name, nics[i].Name);
            nic.RxBytesPerSec = (now.Rx - prev[i].Rx) / secs;
            nic.TxBytesPerSec = (now.Tx - prev[i].Tx) / secs;
            nic.LinkBitsPerSec = (ulong)Math.Max(0, nics[i].Speed);
            nic.IsPrimary = InterfaceIndex(nics[i]) == primary && primary != 0;
            prev[i] = now;
        }
        s.NicCount = nics.Length;
    }

    private static uint InterfaceIndex(NetworkInterface nic)
    {
        try { return (uint)nic.GetIPProperties().GetIPv4Properties().Index; }
        catch { return 0; }   // IPv6-only adapter, or no IPv4 properties
    }

    private static void FillProcs(ref Snapshot s, Processes procs, bool group)
    {
        var top = procs.Top(SnapshotLayout.MaxProcs, group);
        for (int i = 0; i < top.Count; i++)
        {
            ref var p = ref s.Procs[i];
            NameField.Set(ref p.Name, top[i].Name);
            p.Pid = top[i].Pid;
            p.CpuPct = top[i].CpuPct;
            p.WorkingSet = top[i].WorkingSet;
            p.Threads = top[i].Threads;
            p.Instances = top[i].Instances;
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
