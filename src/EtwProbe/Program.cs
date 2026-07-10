using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace EtwProbe;

/// <summary>
/// Feasibility probe for the two things no ordinary Windows API exposes:
///   1. which process is running on each core (sampled profiler, ~1 kHz per core)
///   2. network bytes attributed per process (TcpIp/UdpIp kernel events)
///
/// Uses the sampled profiler rather than context switches on purpose: CSwitch fires on every
/// scheduling decision (~100k events/s on a busy box) while Profile fires at a fixed rate per
/// core. For "who owns this core right now" the sample counts are exactly the answer, and the
/// event volume is bounded and predictable.
///
/// Requires elevation: creating a kernel ETW session needs SeSystemProfilePrivilege.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Redirecting stdout across an elevation boundary does not work, so write the report
        // ourselves when asked. Everything below just goes to Console.
        string? outPath = args.FirstOrDefault(x => x.StartsWith("--out="))?["--out=".Length..];
        StreamWriter? file = null;
        if (outPath is not null)
        {
            file = new StreamWriter(outPath, append: false) { AutoFlush = true };
            Console.SetOut(file);
            Console.SetError(file);
        }

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EXCEPCION: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
        finally { file?.Dispose(); }
    }

    private static int Run(string[] args)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("Hace falta elevacion: una sesion ETW de kernel requiere SeSystemProfilePrivilege.");
                return 1;
            }
        }

        int seconds = 5;
        string? a = args.FirstOrDefault(x => x.StartsWith("--seconds="));
        if (a is not null) int.TryParse(a["--seconds=".Length..], out seconds);

        int cores = Environment.ProcessorCount;

        // core -> pid -> sample count
        var perCore = new Dictionary<int, int>[cores];
        for (int i = 0; i < cores; i++) perCore[i] = [];

        var netRx = new Dictionary<int, long>();
        var netTx = new Dictionary<int, long>();
        long samples = 0, netEvents = 0;

        Console.WriteLine($"Sesion de kernel activa {seconds}s sobre {cores} cores.");
        Console.WriteLine();

        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
        session.StopOnDispose = true;

        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Profile |
            KernelTraceEventParser.Keywords.NetworkTCPIP);

        session.Source.Kernel.PerfInfoSample += data =>
        {
            int cpu = data.ProcessorNumber;
            if ((uint)cpu >= (uint)cores) return;
            samples++;
            var map = perCore[cpu];
            map[data.ProcessID] = map.GetValueOrDefault(data.ProcessID) + 1;
        };

        session.Source.Kernel.TcpIpRecv += d => { netEvents++; Bump(netRx, d.ProcessID, d.size); };
        session.Source.Kernel.TcpIpSend += d => { netEvents++; Bump(netTx, d.ProcessID, d.size); };
        session.Source.Kernel.UdpIpRecv += d => { netEvents++; Bump(netRx, d.ProcessID, d.size); };
        session.Source.Kernel.UdpIpSend += d => { netEvents++; Bump(netTx, d.ProcessID, d.size); };

        using var self = Process.GetCurrentProcess();
        var cpuBefore = self.TotalProcessorTime;

        var stop = new Timer(_ => session.Source.StopProcessing(), null, seconds * 1000, Timeout.Infinite);
        var sw = Stopwatch.StartNew();
        session.Source.Process();     // blocks until StopProcessing
        sw.Stop();
        stop.Dispose();

        // The cost of running the session is what decides whether this helper can live
        // permanently in the background, so measure it rather than assume.
        self.Refresh();
        double cpuMs = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        Console.WriteLine($"=== Coste del helper ETW ===");
        Console.WriteLine($"    CPU: {cpuMs:F0} ms en {sw.Elapsed.TotalMilliseconds:F0} ms de reloj " +
                          $"= {cpuMs / sw.Elapsed.TotalMilliseconds * 100:F2} % de un core " +
                          $"({cpuMs / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F3} % de la maquina)");
        Console.WriteLine($"    Working set: {self.WorkingSet64 / 1024.0 / 1024:F1} MiB");
        Console.WriteLine();

        var names = new Dictionary<int, string>();
        string Name(int pid)
        {
            if (pid == 0) return "Idle";
            if (pid == 4) return "System/kernel";
            if (names.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById(pid).ProcessName; } catch { n = $"pid{pid}"; }
            return names[pid] = n;
        }

        Console.WriteLine($"=== {samples:N0} muestras de perfil, {netEvents:N0} eventos de red en {sw.Elapsed.TotalSeconds:F1}s ===");
        Console.WriteLine($"    ~{samples / sw.Elapsed.TotalSeconds / cores:F0} muestras/s por core");
        Console.WriteLine();

        Console.WriteLine("=== Quien ocupa cada core (top 3 por numero de muestras) ===");
        for (int c = 0; c < cores; c++)
        {
            int total = perCore[c].Values.Sum();
            if (total == 0) { Console.WriteLine($"  core {c,2}: sin muestras"); continue; }

            var top = perCore[c].OrderByDescending(kv => kv.Value).Take(3);
            int idle = perCore[c].GetValueOrDefault(0);
            string busy = $"{100.0 * (total - idle) / total,5:F1}% ocupado";
            string who = string.Join("  ", top.Select(kv => $"{Name(kv.Key)} {100.0 * kv.Value / total:F0}%"));
            Console.WriteLine($"  core {c,2}: {busy}   {who}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Ancho de banda por proceso ===");
        var pids = netRx.Keys.Union(netTx.Keys)
            .OrderByDescending(p => netRx.GetValueOrDefault(p) + netTx.GetValueOrDefault(p))
            .Take(10);
        foreach (int pid in pids)
        {
            double rx = netRx.GetValueOrDefault(pid) / sw.Elapsed.TotalSeconds;
            double tx = netTx.GetValueOrDefault(pid) / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  {Name(pid),-24} ↓{rx / 1024,9:F1} KiB/s   ↑{tx / 1024,9:F1} KiB/s");
        }
        if (netRx.Count == 0 && netTx.Count == 0) Console.WriteLine("  (sin trafico durante la captura)");

        return 0;

        static void Bump(Dictionary<int, long> map, int pid, int size) =>
            map[pid] = map.GetValueOrDefault(pid) + size;
    }
}
