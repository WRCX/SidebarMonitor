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

        int intervalMs = ArgParse.Int(args, "--interval=", 1000);
        // Processes are the dominant cost (NtQuerySystemInformation, ~6-16 ms) and the least
        // time-sensitive number on screen, so they sample every Nth tick by default.
        int procEvery = ArgParse.Int(args, "--proc-every=", 3);
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
            var nvml = Nvml.TryOpen(out string? nvmlError);

            // AMD GPU telemetry (temp/power/fan/clocks/VRAM) via ADLX — for a Radeon dGPU or Ryzen
            // iGPU, what NVML is to NVIDIA. Unelevated, ships with the driver; null on a non-AMD box.
            var adlx = Adlx.TryOpen(out string? adlxError);

            // The physical adapters (discrete + iGPU) with their LUIDs, so the GPU Engine counter can
            // be split per GPU. Enumerated once — adapters don't come and go during a session.
            var gpuAdapters = GpuAdapters.Enumerate(SnapshotLayout.MaxGpus);

            // Disk identity never changes while we run; a single collect gives us the instances.
            pdh.Collect();
            var inventory = DiskInventory.Enumerate(pdh.DiskRead().Select(d => d.Instance));

            // NVMe drive temperature we read ourselves, unelevated. SATA temps and CPU temp/power
            // come from the elevated helper. No HWiNFO anywhere.
            using var diskTemps = new DiskTemps(inventory.Where(kv => kv.Value.Bus == "NVMe").Select(kv => kv.Key));

            Console.WriteLine($"Agente en marcha. {SnapshotLayout.MapName}, {writer.SizeBytes} B, cada {intervalMs} ms.");
            Console.WriteLine($"  CPU:    {CpuNameString}");
            Console.WriteLine($"  NVML:   {(nvml is not null ? $"OK ({nvml.Count} GPU)" : $"no disponible - {nvmlError}")}");
            Console.WriteLine($"  ADLX:   {(adlx is not null ? $"OK ({adlx.Count} GPU AMD)" : $"no disponible - {adlxError}")}");
            Console.WriteLine($"  GPUs:   {(gpuAdapters.Count > 0 ? string.Join(", ", gpuAdapters.Select(a => $"{a.Name}{(a.Integrated ? " [iGPU]" : "")}")) : "D3DKMT sin adaptadores (fallback NVML)")}");
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

            // Drive temps the elevated helper reads (SATA + NVMe via the storage stack), by
            // physical disk number. Updated from the ETW snapshot each tick; FillDisks uses it as
            // the fallback for disks our own unelevated NVMe IOCTL can't read.
            var etwDiskTemps = new float[SnapshotLayout.MaxDisks];
            Array.Fill(etwDiskTemps, float.NaN);
            long ticks = 0;
            double worstMs = 0;

            // The elevated helper is optional and can come and go. Re-probe periodically instead
            // of deciding once at startup.
            SeqLockReader<EtwSnapshot>? etw = EtwChannel.TryOpenReader(out _);
            Console.WriteLine($"  ETW:    {(etw is not null ? "OK (helper elevado presente)" : "no disponible - lanza SidebarMonitor.Etw para CPU temp/vatios y colores por proceso")}");
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

                FillCpu(ref snapshot.Cpu, pdh);
                FillMem(ref snapshot.Mem, pdh);
                FillGpus(ref snapshot, nvml, adlx, gpuAdapters, pdh, procs);
                FillDisks(ref snapshot, pdh, inventory, diskTemps, etwDiskTemps);

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

                etw = MergeEtw(ref snapshot, etw, etwDiskTemps);

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
            nvml?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// Copies the elevated helper's attribution into our snapshot, if it is publishing. Returns
    /// the reader, or null if the helper died (a stale map keeps its last values forever, so the
    /// timestamp is what tells us it is gone).
    /// </summary>
    private static SeqLockReader<EtwSnapshot>? MergeEtw(ref Snapshot s, SeqLockReader<EtwSnapshot>? etw, float[] diskTemps)
    {
        s.EtwAvailable = false;
        s.CpuFromAmd = false;
        s.CpuFromPawnIo = false;
        if (etw is null) { Array.Fill(diskTemps, float.NaN); return null; }

        if (!etw.TryRead(out var e)) return etw;   // caught it mid-write; try again next tick

        var age = DateTime.UtcNow - new DateTime(e.TimestampUtcTicks, DateTimeKind.Utc);
        if (age > TimeSpan.FromSeconds(5))
        {
            etw.Dispose();
            Array.Fill(diskTemps, float.NaN);
            Console.WriteLine("ETW: el helper dejo de publicar.");
            return null;
        }

        for (int i = 0; i < diskTemps.Length; i++) diskTemps[i] = e.DiskTempsC[i];

        s.CoreOwners = e.Cores;
        s.CoreOwnerSamples = e.CoreSamples;
        s.CoreDetail = e.CoreDetail;
        s.NetProcCount = e.NetProcCount;
        s.NetProcs = e.NetProcs;
        s.Frame = e.Frame;   // game frame-timing (PresentMon), or empty when off/idle
        s.EtwAvailable = true;

        // CPU temp and package power come solely from the AMD SDK (via the helper): it works with
        // HVCI. FreqBest stays PDH-derived (the SDK Fmax is the boost bin, not the live clock).
        if (e.CpuSdkOk != 0)
        {
            s.Cpu.TempC = (float)e.CpuTempC;
            s.Cpu.PackagePowerW = e.CpuPackageW;
            s.Cpu.VidV = e.CpuVidV;
            s.Cpu.TjMaxC = e.CpuTjMaxC;
            // "GHz máx" = the highest current core clock from either source: the SDK's best-core
            // dCurrentFreq catches the real single-core boost (~5040) that PDH's averaged % Processor
            // Performance smooths away, while the max() keeps it from ever reading below the per-core
            // rows (which are PDH) at an instant the SDK sampled lower.
            if (e.CpuBestFreqMhz > 0) s.Cpu.FreqBestMhz = Math.Max(s.Cpu.FreqBestMhz, e.CpuBestFreqMhz);
            s.Cpu.PptPct = e.CpuPptPct;
            s.Cpu.TdcPct = e.CpuTdcPct;
            s.Cpu.EdcPct = e.CpuEdcPct;
            s.Cpu.BestCore = e.CpuBestCore;
            s.Cpu.SecondCore = e.CpuSecondCore;
            s.Cpu.PhysicalCores = e.CpuPhysicalCores;

            // Map the SDK's per-PHYSICAL-core temps onto the logical rows (both SMT siblings share
            // their physical core's temperature).
            int phys = e.CpuPhysicalCores;
            int tpc = phys > 0 ? Math.Max(1, s.Cpu.CoreCount / phys) : 1;
            for (int i = 0; i < s.Cpu.CoreCount && i < SnapshotLayout.MaxCores; i++)
            {
                // Clamp to 15: CpuCoreTempsC/C0Pct are 16-wide, so a CPU reporting >16 physical cores
                // (Threadripper/EPYC) must never index past the inline array.
                int p = Math.Min(15, phys > 0 ? Math.Min(phys - 1, i / tpc) : i);
                s.Cpu.CoreTempC[i] = e.CpuCoreTempsC[p];
                // C0 residency maps the same way: both SMT siblings inherit the physical core's
                // awake-fraction, so a parked physical core shows both its logical rows asleep.
                s.Cpu.CoreC0Pct[i] = e.CpuCoreC0Pct[p];
            }

            s.CpuFromAmd = true;
        }
        else
        {
            s.Cpu.BestCore = s.Cpu.SecondCore = -1;
        }

        // PawnIO: the helper already wrote these over the SDK fields, but the block above only
        // copies when the AMD SDK is up — and on laptops (mobile APUs) the SDK never is. Take
        // exactly the fields PawnIO owns this window (bit 0: Tctl; bit 1: PM_Table power).
        s.CpuPmTableVersion = e.CpuPmTableVersion;
        if ((e.CpuPawnIoOk & 1) != 0)
        {
            s.Cpu.TempC = (float)e.CpuTempC;
            s.CpuFromPawnIo = true;
        }
        if ((e.CpuPawnIoOk & 2) != 0)
        {
            s.Cpu.PackagePowerW = e.CpuPackageW;
            s.Cpu.PptPct = e.CpuPptPct;
            s.Cpu.TdcPct = e.CpuTdcPct;
            s.Cpu.TjMaxC = e.CpuTjMaxC;
        }
        return etw;
    }

    // The processor name string, read once from the registry — authoritative and unelevated.
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

    private static void FillCpu(ref CpuInfo cpu, PdhQuery pdh)
    {
        NameField.Set(ref cpu.Name, CpuNameString);
        cpu.TotalUsagePct = Clamp100(pdh.CpuTotalPct);
        cpu.FreqBestMhz = (float)pdh.CpuFrequencyMhz(CpuFreqMode.Best);
        cpu.FreqMeanMhz = (float)pdh.CpuFrequencyMhz(CpuFreqMode.Mean);
        cpu.FreqMedianMhz = (float)pdh.CpuFrequencyMhz(CpuFreqMode.Median);
        // Temp, power, voltage, Tjmax and the best-core ranking come from the AMD SDK in MergeEtw;
        // default to "unknown" so a missing helper never leaves a stale star or reading on screen.
        cpu.PackagePowerW = float.NaN;
        cpu.TempC = float.NaN;
        cpu.VidV = 0;
        cpu.TjMaxC = 0;
        cpu.LimitMhz = 0;
        cpu.PptPct = cpu.TdcPct = cpu.EdcPct = 0;
        cpu.BestCore = cpu.SecondCore = -1;
        cpu.PhysicalCores = 0;

        int n = 0;
        foreach (var s in pdh.CpuPerCore())
        {
            // Instances are "<group>,<core>" plus the "_Total" aggregates, which we skip.
            if (s.Instance.Contains("_Total", StringComparison.Ordinal)) continue;
            if (n >= SnapshotLayout.MaxCores) break;
            cpu.CoreUsagePct[n++] = Clamp100(s.Value);
        }
        cpu.CoreCount = n;

        // Per-core clock = nominal × that core's % Processor Performance. Same instance order as
        // the usage counter above (same PDH object), so the index lines up with CoreUsagePct.
        double nominal = pdh.NominalMhz;
        int f = 0;
        foreach (var s in pdh.CpuPerCorePerf())
        {
            if (s.Instance.Contains("_Total", StringComparison.Ordinal)) continue;
            if (f >= SnapshotLayout.MaxCores) break;
            cpu.CoreFreqMhz[f++] = double.IsNaN(s.Value) || double.IsNaN(nominal) ? float.NaN : (float)(nominal * s.Value / 100.0);
        }

        // Per-core temp and C0 residency come from the AMD SDK in MergeEtw; sentinel until mapped in.
        for (int i = 0; i < SnapshotLayout.MaxCores; i++) { cpu.CoreTempC[i] = float.NaN; cpu.CoreC0Pct[i] = -1f; }
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

    /// <summary>
    /// Builds the GPU list from the vendor-neutral D3DKMT adapters (both the discrete card and the
    /// iGPU), attaching NVML's rich telemetry to the NVIDIA one and deriving the rest from the GPU
    /// Engine counter. Falls back to NVML-only, single GPU, if adapter enumeration came up empty.
    /// </summary>
    private static void FillGpus(ref Snapshot s, Nvml? nvml, Adlx? adlx, List<GpuAdapters.Adapter> adapters, PdhQuery pdh, Processes procs)
    {
        if (adapters.Count == 0)
        {
            s.GpuCount = nvml?.Count ?? 0;
            for (int i = 0; i < s.GpuCount; i++)
            {
                s.Gpus[i] = default;
                nvml!.Fill(i, ref s.Gpus[i]);
                s.Gpus[i].HasDetail = 1;
            }
            FillEngines(ref s, pdh, procs, adapters);
            return;
        }

        s.GpuCount = adapters.Count;
        int nvmlNext = 0;
        int amdNext = 0;
        for (int i = 0; i < adapters.Count; i++)
        {
            ref var g = ref s.Gpus[i];
            g = default;
            var a = adapters[i];

            bool isNvidia = a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            bool isAmd = a.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                      || a.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
            if (isNvidia && nvml is not null && nvmlNext < nvml.Count)
            {
                nvml.Fill(nvmlNext++, ref g);   // NVML fills the name and every telemetry field
                g.HasDetail = 1;
            }
            else
            {
                NameField.Set(ref g.Name, a.Name);
                g.LoadPct = float.NaN;   // derived from the engines below unless ADLX fills it
                g.TempC = g.PowerW = float.NaN;
                g.HasDetail = 0;

                // ADLX gives an AMD dGPU/iGPU the same rich telemetry NVML gives NVIDIA.
                if (isAmd && adlx is not null && adlx.Fill(a.Name, amdNext++, ref g))
                {
                    if (a.DedicatedVram > 0) g.VramTotal = a.DedicatedVram;   // used comes from ADLX
                    g.HasDetail = 1;
                }
            }
            g.IsIntegrated = (byte)(a.Integrated ? 1 : 0);
        }

        FillEngines(ref s, pdh, procs, adapters);
    }

    /// <summary>
    /// What each GPU is doing and who's driving it, from the "GPU Engine" counter. Every instance is
    /// "pid_&lt;n&gt;_luid_0x&lt;hi&gt;_0x&lt;lo&gt;_..._engtype_&lt;type&gt;". We route each sample to the GPU whose
    /// LUID matches, fold the engtype into a curated slot, and track the busiest process per GPU. An
    /// adapter with no NVML load (the iGPU) takes its utilisation from its busiest engine.
    /// </summary>
    private static void FillEngines(ref Snapshot s, PdhQuery pdh, Processes procs, List<GpuAdapters.Adapter> adapters)
    {
        if (s.GpuCount == 0) return;
        var eng = pdh.GpuEngine();
        if (eng.Count == 0) return;

        var byPid = new Dictionary<int, float>[s.GpuCount];
        for (int i = 0; i < s.GpuCount; i++) byPid[i] = new Dictionary<int, float>(16);

        foreach (var item in eng)
        {
            if (double.IsNaN(item.Value) || item.Value <= 0) continue;
            int gi = GpuIndexForInstance(item.Instance, adapters, s.GpuCount);
            if (gi < 0) continue;

            float v = (float)item.Value;
            int slot = EngSlot(EngType(item.Instance));
            ref var g = ref s.Gpus[gi];
            g.Engines[slot] = Math.Min(100, g.Engines[slot] + v);

            int pid = EngPid(item.Instance);
            if (pid > 0) byPid[gi][pid] = byPid[gi].GetValueOrDefault(pid) + v;
        }

        for (int i = 0; i < s.GpuCount; i++)
        {
            ref var g = ref s.Gpus[i];
            int topPid = 0; float topV = 0;
            foreach (var kv in byPid[i]) if (kv.Value > topV) { topV = kv.Value; topPid = kv.Key; }
            NameField.Set(ref g.TopProc, topPid > 0 ? procs.NameFor(topPid) ?? string.Empty : string.Empty);
            g.TopProcPct = Math.Min(100, topV);

            if (g.HasDetail == 0)
            {
                float max = 0;
                for (int e = 0; e < GpuEngines.Count; e++) max = Math.Max(max, g.Engines[e]);
                g.LoadPct = max;
            }
        }
    }

    /// <summary>Which GPU (index into s.Gpus) a "GPU Engine" instance belongs to, matched by LUID.
    /// With no adapter list (fallback), everything folds into GPU 0. -1 = no match.</summary>
    private static int GpuIndexForInstance(string inst, List<GpuAdapters.Adapter> adapters, int gpuCount)
    {
        if (adapters.Count == 0) return 0;

        int k = inst.IndexOf("luid_", StringComparison.Ordinal);
        if (k < 0) return -1;
        k += 5;
        int end = inst.IndexOf("_phys", k, StringComparison.Ordinal);
        ReadOnlySpan<char> luid = end < 0 ? inst.AsSpan(k) : inst.AsSpan(k, end - k);   // "0xHIGH_0xLOW"
        int us = luid.IndexOf('_');
        if (us < 0) return -1;
        if (!TryHex(luid[..us], out uint high) || !TryHex(luid[(us + 1)..], out uint low)) return -1;

        for (int i = 0; i < gpuCount && i < adapters.Count; i++)
            if (adapters[i].LuidLow == low && (uint)adapters[i].LuidHigh == high) return i;
        return -1;
    }

    private static bool TryHex(ReadOnlySpan<char> s, out uint value)
    {
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static int EngSlot(string engtype) => engtype.ToLowerInvariant() switch
    {
        "3d" => GpuEngines.Idx3D,
        "compute" => GpuEngines.IdxCompute,
        "videodecode" => GpuEngines.IdxDecode,
        "videoencode" => GpuEngines.IdxEncode,
        "video" => GpuEngines.IdxVideo,
        "copy" => GpuEngines.IdxCopy,
        "vr" => GpuEngines.IdxVR,
        _ => GpuEngines.IdxOther,   // high, timer, security, legacyoverlay, ofa_*, …
    };

    private static int EngPid(string inst)
    {
        int i = inst.IndexOf("pid_", StringComparison.Ordinal);
        if (i < 0) return 0;
        i += 4;
        int j = i;
        while (j < inst.Length && char.IsAsciiDigit(inst[j])) j++;
        return int.TryParse(inst.AsSpan(i, j - i), out int p) ? p : 0;
    }

    private static string EngType(string inst)
    {
        int i = inst.IndexOf("engtype_", StringComparison.Ordinal);
        return i < 0 ? "" : inst[(i + 8)..];
    }

    private static void FillDisks(ref Snapshot s, PdhQuery pdh, Dictionary<int, DiskIdentity> inventory, DiskTemps diskTemps, float[] helperTemps)
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

                // Our own unelevated NVMe IOCTL first; the elevated helper's storage-stack read
                // (SATA, by physical index) as fallback. No HWiNFO anywhere — this closes it out.
                double temp = diskTemps.TempC(index);
                if (double.IsNaN(temp) && (uint)index < (uint)helperTemps.Length) temp = helperTemps[index];
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
            // Clamp negatives: a counter wrap (32-bit driver rollover) or an interface reset makes
            // now < prev, which would otherwise show a huge negative spike.
            nic.RxBytesPerSec = Math.Max(0, now.Rx - prev[i].Rx) / secs;
            nic.TxBytesPerSec = Math.Max(0, now.Tx - prev[i].Tx) / secs;
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

}
