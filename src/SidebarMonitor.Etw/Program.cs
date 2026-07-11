using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Etw;

/// <summary>
/// The optional, elevated half of the agent. Runs a kernel ETW session and publishes the two
/// things no ordinary API exposes: who owns each core, and network bytes per process.
///
/// Everything else keeps working without it. The agent stays unelevated and AOT; it merges this
/// map into its own snapshot when it is present.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Redirecting stdout across an elevation boundary does not work, so write our own log
        // when asked (diagnostics).
        string? outPath = args.FirstOrDefault(x => x.StartsWith("--out="))?["--out=".Length..];
        if (outPath is not null)
        {
            var file = new StreamWriter(outPath, append: false) { AutoFlush = true };
            Console.SetOut(file);
        }

        using (var id = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.Error.WriteLine("Hace falta elevacion: una sesion ETW de kernel requiere SeSystemProfilePrivilege.");
                return 1;
            }
        }

        int windowMs = IntArg(args, "--window=", 1000);
        int stallSec = IntArg(args, "--stall=", 20);   // seconds of "NIC busy, ETW silent" before restart
        bool verbose = args.Contains("--verbose");
        int cores = Environment.ProcessorCount;

        SeqLockWriter<EtwSnapshot> writer;
        try
        {
            writer = EtwChannel.CreateWriter();
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Ya hay un helper ETW corriendo.");
            return 1;
        }

        // CPU temp/power from AMD's SDK (needs admin, which we have). Optional: if the SDK/driver
        // is absent, the agent keeps using HWiNFO.
        var ryzen = RyzenSdk.TryOpen(out string? ryzenErr);
        Console.WriteLine($"AMD Ryzen SDK: {(ryzen is not null ? "OK" : $"no disponible - {ryzenErr}")}");

        // Drive temps (SATA + NVMe) from the storage stack; needs admin, which we have.
        using var diskTemps = new DiskTempsWmi();
        Console.WriteLine("Temps de disco: por WMI (storage reliability counter)");

        var names = new ProcessNames();
        var gate = new Lock();

        // Touched only under `gate`: the ETW callbacks run on the session thread, the publish
        // timer on another.
        var coreCounts = new Dictionary<string, int>[cores];
        for (int i = 0; i < cores; i++) coreCounts[i] = new Dictionary<string, int>(16);
        var netRx = new Dictionary<string, long>(32);
        var netTx = new Dictionary<string, long>(32);
        long windowStart = Stopwatch.GetTimestamp();
        long droppedSamples = 0;

        long perfSamples = 0, netEvents = 0;

        // The classic kernel NetworkTCPIP provider stops delivering events after a long uptime
        // (seen after hours: TcpIp/UdpIp events dry up while profiling keeps flowing), which froze
        // the per-process bandwidth. The session lives inside a loop so a watchdog can recreate it.
        // Profiling never stops, so "perf samples advancing but net events flat" is a real stall.
        TraceEventSession? current = null;
        bool quit = false;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit = true;
            try { current?.Source.StopProcessing(); } catch { }
        };

        void Net(Dictionary<string, long> map, int pid, int size)
        {
            Interlocked.Increment(ref netEvents);
            string name = names.Get(pid);
            lock (gate) map[name] = map.GetValueOrDefault(name) + size;
        }

        TraceEventSession NewSession()
        {
            var s = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            s.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Profile |
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.Process);

            s.Source.Kernel.ProcessStart += names.OnStart;
            s.Source.Kernel.ProcessDCStart += names.OnStart;   // rundown of already-running processes

            s.Source.Kernel.PerfInfoSample += d =>
            {
                Interlocked.Increment(ref perfSamples);
                int cpu = d.ProcessorNumber;
                if ((uint)cpu >= (uint)cores) { Interlocked.Increment(ref droppedSamples); return; }
                string name = names.Get(d.ProcessID);
                lock (gate)
                {
                    var map = coreCounts[cpu];
                    map[name] = map.GetValueOrDefault(name) + 1;
                }
            };

            s.Source.Kernel.TcpIpRecv += d => Net(netRx, d.ProcessID, d.size);
            s.Source.Kernel.TcpIpSend += d => Net(netTx, d.ProcessID, d.size);
            s.Source.Kernel.UdpIpRecv += d => Net(netRx, d.ProcessID, d.size);
            s.Source.Kernel.UdpIpSend += d => Net(netTx, d.ProcessID, d.size);
            return s;
        }

        var snapshot = default(EtwSnapshot);
        long published = 0;

        using var publish = new Timer(_ =>
        {
            lock (gate)
            {
                double seconds = Stopwatch.GetElapsedTime(windowStart).TotalSeconds;
                windowStart = Stopwatch.GetTimestamp();
                if (seconds <= 0) return;

                snapshot.TimestampUtcTicks = DateTime.UtcNow.Ticks;
                snapshot.WindowSeconds = seconds;
                snapshot.CoreCount = cores;

                FillCores(ref snapshot, coreCounts, cores);
                FillNet(ref snapshot, netRx, netTx, seconds);

                foreach (var m in coreCounts) m.Clear();
                netRx.Clear();
                netTx.Clear();
            }

            // AMD CPU sensors read outside the gate (the SDK has its own locking).
            if (ryzen is not null && ryzen.TryRead(out var rm))
            {
                snapshot.CpuSdkOk = 1;
                snapshot.CpuTempC = rm.TempC;
                snapshot.CpuPackageW = rm.PackageW;
                snapshot.CpuFmaxMhz = rm.FmaxMhz;
            }
            else snapshot.CpuSdkOk = 0;

            diskTemps.Fill(ref snapshot);

            writer.Publish(snapshot);
            published++;

            if (verbose)
            {
                string netTop = snapshot.NetProcCount > 0
                    ? $"{NameField.Get(ref snapshot.NetProcs[0].Name)} ↓{snapshot.NetProcs[0].RxBytesPerSec / 1024:F1}K ↑{snapshot.NetProcs[0].TxBytesPerSec / 1024:F1}K"
                    : "(nada)";
                Console.WriteLine($"pub {published,4}  {snapshot.WindowSeconds:F2}s  " +
                                  $"netEvents_total={Interlocked.Read(ref netEvents)}  netProcs={snapshot.NetProcCount}  top: {netTop}");
            }
        }, null, windowMs, windowMs);

        Console.WriteLine($"Helper ETW en marcha. {EtwLayout.MapName}, {writer.SizeBytes} B, ventana {windowMs} ms, {cores} cores.");
        Console.WriteLine("Ctrl+C para parar.");
        Console.WriteLine();

        // Watchdog: the definitive stall signal isn't "no net events" (the network may just be
        // idle) — it's "the NIC is moving bytes but ETW produced no net events". Compare our own
        // event counter against the interface byte counters (iphlpapi, no admin). When traffic
        // flows with no events for `stallSec`, the provider has wedged; recreate the session.
        long netAtCheck = 0;
        ulong nicAtCheck = NicTotalBytes();
        long stalledSince = 0;   // 0 = healthy
        using var watchdog = new Timer(_ =>
        {
            long net = Interlocked.Read(ref netEvents);
            ulong nic = NicTotalBytes();
            bool eventsMoving = net != netAtCheck;
            bool trafficMoving = nic > nicAtCheck + 65536;   // >64 KiB since last check
            netAtCheck = net; nicAtCheck = nic;

            if (eventsMoving || !trafficMoving)
            {
                stalledSince = 0;   // healthy, or genuinely idle — either way not a stall
                return;
            }

            // Traffic is flowing but ETW is silent.
            if (stalledSince == 0) stalledSince = Stopwatch.GetTimestamp();
            else if (Stopwatch.GetElapsedTime(stalledSince).TotalSeconds > stallSec)
            {
                Console.WriteLine("watchdog: la NIC mueve datos pero ETW no emite eventos de red; recreando sesion.");
                stalledSince = 0;
                try { current?.Source.StopProcessing(); } catch { }
            }
        }, null, 5000, 5000);

        int restarts = 0;
        while (!quit)
        {
            using var session = NewSession();
            current = session;
            try { session.Source.Process(); }   // blocks until StopProcessing (quit or watchdog)
            catch (Exception ex) { Console.WriteLine($"sesion cayo: {ex.Message}"); }
            current = null;
            if (!quit) { restarts++; Console.WriteLine($"--- sesion recreada (#{restarts}) ---"); }
        }

        ryzen?.Dispose();
        writer.Dispose();
        Console.WriteLine($"Parado tras {published} publicaciones, {restarts} reinicios de sesion.");
        return 0;
    }

    /// <summary>
    /// Shares are computed over NON-IDLE samples. The sampled profiler emits nothing while a
    /// core sleeps in a deep C-state, so total sample count is not proportional to time and any
    /// "busy %" derived from it would be wrong. Consumers take the height from PDH.
    /// </summary>
    private static void FillCores(ref EtwSnapshot s, Dictionary<string, int>[] counts, int cores)
    {
        for (int c = 0; c < cores; c++)
        {
            ref var slot = ref s.Cores[c];
            for (int k = 0; k < EtwLayout.TopPerCore; k++) slot[k] = default;

            int nonIdle = 0;
            foreach (var (name, n) in counts[c])
                if (name != "Idle") nonIdle += n;

            s.CoreSamples[c] = nonIdle;
            if (nonIdle == 0) continue;

            var top = counts[c]
                .Where(kv => kv.Key != "Idle")
                .OrderByDescending(kv => kv.Value)
                .Take(EtwLayout.TopPerCore);

            int k2 = 0;
            foreach (var (name, n) in top)
            {
                ref var ps = ref slot[k2++];
                NameField.Set(ref ps.Name, name);
                ps.Pct = 100f * n / nonIdle;
                ps.IsKernel = (byte)(name == "System" ? 1 : 0);
            }
        }
    }

    private static void FillNet(ref EtwSnapshot s, Dictionary<string, long> rx, Dictionary<string, long> tx, double seconds)
    {
        var top = rx.Keys.Union(tx.Keys)
            .Select(n => (Name: n, Rx: rx.GetValueOrDefault(n), Tx: tx.GetValueOrDefault(n)))
            .Where(t => t.Rx + t.Tx > 0)
            .OrderByDescending(t => t.Rx + t.Tx)
            .Take(EtwLayout.MaxNetProcs)
            .ToArray();

        for (int i = 0; i < top.Length; i++)
        {
            ref var np = ref s.NetProcs[i];
            NameField.Set(ref np.Name, top[i].Name);
            np.RxBytesPerSec = top[i].Rx / seconds;
            np.TxBytesPerSec = top[i].Tx / seconds;
        }
        s.NetProcCount = top.Length;
    }

    /// <summary>Sum of RX+TX bytes across up, non-loopback interfaces. The ground truth the ETW
    /// event stream is supposed to track; if this moves and the events don't, ETW has stalled.</summary>
    private static ulong NicTotalBytes()
    {
        ulong total = 0;
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var s = nic.GetIPStatistics();
                total += (ulong)Math.Max(0, s.BytesReceived) + (ulong)Math.Max(0, s.BytesSent);
            }
        }
        catch { /* transient */ }
        return total;
    }

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}
