using System.Diagnostics;
using System.Globalization;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Client;

/// <summary>Stand-in for the UI: proves the snapshot survives the trip through shared memory.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var reader = SnapshotChannel.TryOpenReader(out string? error);
        if (reader is null)
        {
            Console.Error.WriteLine($"No se pudo abrir el snapshot: {error}");
            Console.Error.WriteLine("Arranca primero SidebarMonitor.Agent.");
            return 2;
        }

        using (reader)
        {
            if (args.Contains("--bench")) return Bench(reader);

            bool watch = args.Contains("--watch");
            do
            {
                if (!reader.TryRead(out var s))
                {
                    Console.Error.WriteLine("lectura rota tras 128 intentos (el agente publica demasiado rapido)");
                    return 3;
                }

                if (watch) Console.Clear();
                Print(s);
                if (watch) Thread.Sleep(1000);
            } while (watch && !(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape));
        }

        return 0;
    }

    private static int Bench(SeqLockReader<Snapshot> reader)
    {
        const int iters = 100_000;
        for (int i = 0; i < 1000; i++) reader.TryRead(out _);

        int torn = 0;
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            if (!reader.TryRead(out _)) torn++;
        sw.Stop();
        long after = GC.GetTotalAllocatedBytes(precise: true);

        Console.WriteLine("=== Coste de leer el snapshot desde la UI ===");
        Console.WriteLine($"  {sw.Elapsed.TotalNanoseconds / iters:F0} ns por lectura   ({iters:N0} lecturas)");
        Console.WriteLine($"  {after - before} bytes asignados en total");
        Console.WriteLine($"  {torn} lecturas abortadas por escritura en curso");
        return 0;
    }

    private static void Print(Snapshot s)
    {
        var ci = CultureInfo.InvariantCulture;
        const double G = 1024.0 * 1024 * 1024;

        string hwiState = !s.HwiNfoAvailable ? "no" : s.HwiNfoLive ? "si" : "EN PAUSA";
        Console.WriteLine($"=== {new DateTime(s.TimestampUtcTicks, DateTimeKind.Utc).ToLocalTime():HH:mm:ss} " +
                          $"(cada {s.SampleIntervalSec:F1} s, HWiNFO {hwiState}, " +
                          $"ETW {(s.EtwAvailable ? "si" : "no")}) ===");
        Console.WriteLine();

        ref var c = ref s.Cpu;
        Console.WriteLine($"CPU  {NameField.Get(ref c.Name)}");
        Console.WriteLine(string.Create(ci, $"  uso {c.TotalUsagePct,5:F1} %   {c.FreqBestMhz / 1000,4:F2} GHz máx / {c.FreqMeanMhz / 1000:F2} media / {c.FreqMedianMhz / 1000:F2} mediana   {c.PackagePowerW,5:F1} W   {c.TempC,4:F1} °C"));

        if (s.EtwAvailable)
        {
            // Height from PDH, owner from ETW: the profiler undersamples idle cores, so its
            // sample counts can say WHO but never HOW MUCH.
            var parts = new List<string>(3);
            for (int i = 0; i < c.CoreCount; i++)
            {
                parts.Clear();
                // A ref local (into the inline array) cannot cross a lambda, so build by hand.
                ref var owners = ref s.CoreOwners[i];
                for (int k = 0; k < EtwLayout.TopPerCore; k++)
                {
                    ref var o = ref owners[k];
                    if (o.Pct <= 0) continue;
                    parts.Add(string.Create(ci, $"{NameField.Get(ref o.Name)} {o.Pct:F0}%"));
                }
                string who = s.CoreOwnerSamples[i] == 0 ? "(sin muestras)" : string.Join("  ", parts);
                Console.WriteLine(string.Create(ci, $"  core {i,2}  {c.CoreUsagePct[i],5:F1}%  {who}"));
            }
        }
        else
        {
            Console.Write("  cores ");
            for (int i = 0; i < c.CoreCount; i++) Console.Write(string.Create(ci, $"{c.CoreUsagePct[i],5:F0}"));
            Console.WriteLine();
        }
        Console.WriteLine();

        Console.WriteLine(string.Create(ci, $"RAM   {s.Mem.PhysUsed / G,5:F2} / {s.Mem.PhysTotal / G:F2} GiB      commit {s.Mem.CommitUsed / G:F2} / {s.Mem.CommitTotal / G:F2} GiB"));
        Console.WriteLine();

        for (int i = 0; i < s.GpuCount; i++)
        {
            ref var g = ref s.Gpus[i];
            Console.WriteLine($"GPU  {NameField.Get(ref g.Name)}");
            Console.WriteLine(string.Create(ci, $"  carga {g.LoadPct,4:F0} %   {g.PowerW,5:F1} W   {g.TempC,4:F0} °C   fan {g.FanPct,3} %   {g.CoreClockMhz} / {g.MemClockMhz} MHz   x{g.PcieWidth}"));
            Console.WriteLine(string.Create(ci, $"  VRAM {g.VramUsed / G,5:F2} / {g.VramTotal / G:F2} GiB"));
        }
        Console.WriteLine();

        for (int i = 0; i < s.NicCount; i++)
        {
            ref var n = ref s.Nics[i];
            string mark = n.IsPrimary ? " «primaria»" : "";
            Console.WriteLine(string.Create(ci, $"NET  {NameField.Get(ref n.Name),-24} DL {n.RxBytesPerSec / 1024,8:F1} KiB/s   UL {n.TxBytesPerSec / 1024,8:F1} KiB/s   link {n.LinkBitsPerSec / 1e6:F0} Mbps{mark}"));
        }
        for (int i = 0; i < s.NetProcCount; i++)
        {
            ref var np = ref s.NetProcs[i];
            Console.WriteLine(string.Create(ci, $"  ↳  {NameField.Get(ref np.Name),-22} ↓{np.RxBytesPerSec / 1024,8:F1} KiB/s   ↑{np.TxBytesPerSec / 1024,8:F1} KiB/s"));
        }
        Console.WriteLine();

        for (int i = 0; i < s.DiskCount; i++)
        {
            ref var d = ref s.Disks[i];
            string label = NameField.Get(ref d.Label);
            string temp = float.IsNaN(d.TempC) ? "  n/d" : string.Create(ci, $"{d.TempC,3:F0}°C");
            Console.WriteLine(string.Create(ci,
                $"DISK {NameField.Get(ref d.Model),-24} {d.Media,-7} {NameField.Get(ref d.Bus),-5} {d.SizeBytes / 1e12,5:F1} TB  {temp}  " +
                $"R {d.ReadBytesPerSec / 1024,8:F1} KiB/s  W {d.WriteBytesPerSec / 1024,8:F1} KiB/s"));
            Console.WriteLine($"       {NameField.Get(ref d.Volumes)}");
            _ = label;
        }
        Console.WriteLine();

        Console.WriteLine($"TOP  ({s.TotalProcesses} procesos, {s.TotalThreads} threads)");
        Console.WriteLine($"  {"proceso",-28} {"inst",5} {"CPU",7}  {"RAM",10}  thr");
        for (int i = 0; i < s.ProcCount; i++)
        {
            ref var p = ref s.Procs[i];
            Console.WriteLine(string.Create(ci, $"  {NameField.Get(ref p.Name),-28} {p.Instances,5} {p.CpuPct,6:F2} %  {p.WorkingSet / 1024.0 / 1024,8:F1} MiB  {p.Threads,4}"));
        }
    }
}
