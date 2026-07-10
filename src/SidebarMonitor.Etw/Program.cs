using System.Diagnostics;
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

        using (var id = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.Error.WriteLine("Hace falta elevacion: una sesion ETW de kernel requiere SeSystemProfilePrivilege.");
                return 1;
            }
        }

        int windowMs = IntArg(args, "--window=", 1000);
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

        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Profile |
            KernelTraceEventParser.Keywords.NetworkTCPIP |
            KernelTraceEventParser.Keywords.Process);

        session.Source.Kernel.ProcessStart += names.OnStart;
        session.Source.Kernel.ProcessDCStart += names.OnStart;   // rundown of already-running processes

        session.Source.Kernel.PerfInfoSample += d =>
        {
            int cpu = d.ProcessorNumber;
            if ((uint)cpu >= (uint)cores) { Interlocked.Increment(ref droppedSamples); return; }
            string name = names.Get(d.ProcessID);
            lock (gate)
            {
                var map = coreCounts[cpu];
                map[name] = map.GetValueOrDefault(name) + 1;
            }
        };

        void Net(Dictionary<string, long> map, int pid, int size)
        {
            string name = names.Get(pid);
            lock (gate) map[name] = map.GetValueOrDefault(name) + size;
        }

        session.Source.Kernel.TcpIpRecv += d => Net(netRx, d.ProcessID, d.size);
        session.Source.Kernel.TcpIpSend += d => Net(netTx, d.ProcessID, d.size);
        session.Source.Kernel.UdpIpRecv += d => Net(netRx, d.ProcessID, d.size);
        session.Source.Kernel.UdpIpSend += d => Net(netTx, d.ProcessID, d.size);

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

            writer.Publish(snapshot);
            published++;

            if (verbose)
            {
                ref var top = ref snapshot.Cores[0];
                Console.WriteLine($"pub {published,4}  ventana {snapshot.WindowSeconds:F2}s  " +
                                  $"core0: {NameField.Get(ref top[0].Name)} {top[0].Pct:F0}%  " +
                                  $"({snapshot.CoreSamples[0]} muestras)  net procs {snapshot.NetProcCount}");
            }
        }, null, windowMs, windowMs);

        Console.WriteLine($"Helper ETW en marcha. {EtwLayout.MapName}, {writer.SizeBytes} B, ventana {windowMs} ms, {cores} cores.");
        Console.WriteLine("Ctrl+C para parar.");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; session.Source.StopProcessing(); };
        session.Source.Process();   // blocks until StopProcessing

        writer.Dispose();
        Console.WriteLine($"Parado tras {published} publicaciones. Muestras descartadas: {droppedSamples}.");
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

    private static int IntArg(string[] args, string prefix, int fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return a is not null && int.TryParse(a[prefix.Length..], out int v) ? v : fallback;
    }
}
