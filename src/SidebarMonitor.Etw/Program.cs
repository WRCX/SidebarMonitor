using System.Diagnostics;
using System.IO;
using System.Management;
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

        int windowMs = ArgParse.Int(args, "--window=", 1000);
        int stallSec = ArgParse.Int(args, "--stall=", 20);   // seconds of "NIC busy, ETW silent" before restart
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

        // CPU temp/power from AMD's SDK (needs admin, which we have). Gated three ways: only on an
        // AMD CPU, only after the user accepted AMD's EULA in the UI (marker file), and skippable via
        // --no-sdk. On anything else it stays closed and the agent keeps using HWiNFO/PDH. Failing
        // soft here is the whole degradation story on Intel and on AMD-before-consent.
        RyzenSdk? ryzen = null;
        bool awaitingConsent = false;   // AMD CPU, eligible, but EULA not yet accepted in the UI
        bool forceSdk = args.Contains("--force-sdk");
        if (args.Contains("--no-sdk"))
        {
            Console.WriteLine("AMD Ryzen SDK: desactivado por --no-sdk.");
        }
        else if (!CpuVendor.IsAmd && !forceSdk)
        {
            Console.WriteLine($"AMD Ryzen SDK: omitido (CPU {(CpuVendor.VendorId is { Length: > 0 } v ? v : "desconocida")}, no AMD). " +
                              "temp/vatios por SDK no disponibles en esta plataforma.");
        }
        else if (!ConsentMarker.AmdSdkAccepted && !forceSdk)
        {
            awaitingConsent = true;
            Console.WriteLine("AMD Ryzen SDK: en espera (EULA de AMD no aceptada todavia en la UI). Usando HWiNFO/PDH.");
        }
        else
        {
            ryzen = RyzenSdk.TryOpen(out string? ryzenErr);
            Console.WriteLine($"AMD Ryzen SDK: {(ryzen is not null ? "OK" : $"no disponible - {ryzenErr}")}");
        }

        // Advanced CPU sensors via PawnIO (Tctl straight from the SMU), opt-in. Opened lazily from
        // the publish timer (the marker can appear/disappear at any time), so here just the gates:
        // AMD only (the RyzenSMU module refuses anything else anyway) and skippable via --no-pawnio.
        PawnIoCpu? pawnIo = null;
        string? pawnIoErr = null;   // last open failure, surfaced in the diagnostics dump
        bool pawnIoTried = false;   // one open attempt per marker transition, not one per second
        bool noPawnIo = args.Contains("--no-pawnio");
        if (noPawnIo)
            Console.WriteLine("PawnIO: desactivado por --no-pawnio.");
        else if (!CpuVendor.IsAmd)
            Console.WriteLine("PawnIO: omitido (CPU no AMD; el modulo RyzenSMU es solo Ryzen).");
        else
            Console.WriteLine($"PawnIO: {(ConsentMarker.AmdAdvancedEnabled ? "opt-in activo, se abrira en la primera ventana" : "en espera de opt-in (sensores avanzados)")}.");

        // Intel CPU sensors via PawnIO (per-core temp via IA32_THERM_STATUS + RAPL package power),
        // opt-in. Same lazy-open-from-the-timer shape as the AMD PawnIO path: here just the gates.
        // Intel only (the IntelMSR module refuses anything else) and skippable via --no-intel; a
        // --force-intel bypasses the consent marker for testing (mirrors --force-sdk).
        IntelMsr? intelMsr = null;
        string? intelErr = null;
        bool intelTried = false;
        bool noIntel = args.Contains("--no-intel");
        bool forceIntel = args.Contains("--force-intel");
        if (noIntel)
            Console.WriteLine("Intel MSR: desactivado por --no-intel.");
        else if (!CpuVendor.IsIntel && !forceIntel)
            Console.WriteLine("Intel MSR: omitido (CPU no Intel; el modulo IntelMSR es solo Intel).");
        else
            Console.WriteLine($"Intel MSR (PawnIO): {(ConsentMarker.IntelSensorsEnabled || forceIntel ? "opt-in activo, se abrira en la primera ventana" : "en espera de opt-in (sensores Intel)")}.");

        // Laptop fan (%) via PawnIO's LpcACPIEC + the per-model NBFC register map. Vendor-agnostic,
        // opt-in, best-effort (a community map can point at the wrong register on an unverified model).
        // Lazy-opened from the timer like the other PawnIO paths; here just the gates and DMI model.
        EcFan? ecFan = null;
        string? ecFanErr = null;
        bool ecFanTried = false;
        bool noFan = args.Contains("--no-fan");
        bool forceFan = args.Contains("--force-fan");
        string[] fanModels = DmiModels();
        if (noFan)
            Console.WriteLine("Ventilador (EC/PawnIO): desactivado por --no-fan.");
        else
            Console.WriteLine($"Ventilador (EC/PawnIO): {(ConsentMarker.FanPawnIoEnabled || forceFan ? "opt-in activo, se abrira en la primera ventana" : "en espera de opt-in (ventilador)")} (modelo DMI: {(fanModels.Length > 0 ? string.Join(" / ", fanModels) : "desconocido")}).");

        // Drive temps (SATA + NVMe) from the storage stack; needs admin, which we have.
        using var diskTemps = new DiskTempsWmi();
        Console.WriteLine("Temps de disco: por WMI (storage reliability counter)");

        var names = new ProcessNames();
        var details = new ProcessDetails();
        details.Refresh();
        var gate = new Lock();

        // Touched only under `gate`: the ETW callbacks run on the session thread, the publish
        // timer on another.
        var coreCounts = new Dictionary<string, int>[cores];
        for (int i = 0; i < cores; i++) coreCounts[i] = new Dictionary<string, int>(16);
        // Samples per PID per core, to find the single process that owns each core (for its detail).
        var pidCounts = new Dictionary<int, int>[cores];
        for (int i = 0; i < cores; i++) pidCounts[i] = new Dictionary<int, int>(16);
        var dominantPid = new int[cores];
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
            string name = names.GetDisplay(pid);
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
                string name = names.GetDisplay(d.ProcessID);
                lock (gate)
                {
                    var map = coreCounts[cpu];
                    map[name] = map.GetValueOrDefault(name) + 1;
                    var pmap = pidCounts[cpu];
                    pmap[d.ProcessID] = pmap.GetValueOrDefault(d.ProcessID) + 1;
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
        float fanPct = float.NaN;   // last good EC fan duty %; kept across a contended window

        // Running peak clock per physical core, to derive the best/second-best (highest-boosting)
        // cores. Persisted across launches and never reset: the CPPC preferred cores are fixed, so
        // the accumulated peaks converge on them and the star stops jumping each run (like HWiNFO,
        // which reads the static CPPC ranking directly — we can't, so we learn it from the peaks).
        var corePeak = CorePeakStore.Load();

        // Game frame-timing via the bundled PresentMon CLI (only spawns while the marker is present).
        using var frameStats = new FrameStats(AppContext.BaseDirectory);

        int publishing = 0;
        using var publish = new Timer(_ =>
        {
            // System.Threading.Timer is reentrant: if a callback outlasts the window (WMI can stall), a
            // second fires on another pool thread and two writers would race Publish and tear the
            // seqlock payload. Drop any overlapping tick — a skipped window is harmless.
            if (Interlocked.Exchange(ref publishing, 1) == 1) return;
            try
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

                // The PID that owns most of each core's samples — its detail goes in the tooltip.
                for (int i = 0; i < cores; i++)
                {
                    int top = 0, topN = 0;
                    foreach (var kv in pidCounts[i]) if (kv.Value > topN) { topN = kv.Value; top = kv.Key; }
                    dominantPid[i] = top;
                    pidCounts[i].Clear();
                }

                foreach (var m in coreCounts) m.Clear();
                netRx.Clear();
                netTx.Clear();
            }

            // Resolve the dominant PIDs' parent + command line outside the gate (WMI is slow-ish).
            for (int i = 0; i < cores; i++)
                NameField.Set(ref snapshot.CoreDetail[i], details.Detail(dominantPid[i]));

            // Consent can arrive after we start (first-run: UI shows the EULA while we're already
            // up). Poll the marker and open the SDK the moment the user accepts — no helper restart.
            if (awaitingConsent && ConsentMarker.AmdSdkAccepted)
            {
                awaitingConsent = false;
                ryzen = RyzenSdk.TryOpen(out string? lateErr);
                Console.WriteLine($"AMD Ryzen SDK: EULA aceptada -> {(ryzen is not null ? "abierto" : $"no disponible - {lateErr}")}");
            }

            // AMD CPU sensors read outside the gate (the SDK has its own locking).
            if (ryzen is not null && ryzen.TryRead(out var rm))
            {
                snapshot.CpuSdkOk = 1;
                snapshot.CpuTempC = rm.TempC;
                snapshot.CpuPackageW = rm.PackageW;
                // Best-core current clock from the SDK's per-core dCurrentFreq — the real boost clock
                // (reaches ~5040), which Windows' averaged % Processor Performance never catches.
                double bestCur = 0;
                // Ignore glitch reads (>6 GHz is impossible): a stray spike would otherwise pin the UI's
                // session boost-peak forever and skew the boost ratio.
                for (int i = 0; i < rm.CoreCount && i < 16; i++)
                    if (rm.CoreFreqMhz[i] < 6000) bestCur = Math.Max(bestCur, rm.CoreFreqMhz[i]);
                snapshot.CpuBestFreqMhz = (float)bestCur;
                snapshot.CpuVidV = rm.VidV;
                snapshot.CpuTjMaxC = rm.TjMaxC;
                snapshot.CpuPptPct = rm.PptPct;
                snapshot.CpuTdcPct = rm.TdcPct;
                snapshot.CpuEdcPct = rm.EdcPct;
                snapshot.CpuPhysicalCores = Math.Min(rm.CoreCount, 16);   // CpuCoreTempsC/C0Pct are 16-wide
                int ntemp = Math.Min(rm.CoreCount, 16);
                for (int i = 0; i < ntemp; i++) snapshot.CpuCoreTempsC[i] = (float)rm.CoreTempC[i];
                // C0 residency is indexed by physical core (uncompacted in the shim), so fill all 16.
                for (int i = 0; i < 16; i++) snapshot.CpuCoreC0Pct[i] = (float)rm.CoreC0Pct[i];

                // Best / second-best physical core = the two with the highest peak clock. The SDK
                // doesn't expose the CPPC ranking, but tracking peaks converges to the same cores.
                int nc = Math.Min(rm.CoreCount, corePeak.Length);
                for (int i = 0; i < nc; i++)
                    // Cap out glitches (a stray 6+ GHz reading would pin the star to a wrong core forever).
                    if (rm.CoreFreqMhz[i] > corePeak[i] && rm.CoreFreqMhz[i] < 6000) corePeak[i] = rm.CoreFreqMhz[i];
                int best = -1, second = -1;
                for (int i = 0; i < nc; i++)
                {
                    if (best < 0 || corePeak[i] > corePeak[best]) { second = best; best = i; }
                    else if (second < 0 || corePeak[i] > corePeak[second]) second = i;
                }
                snapshot.CpuBestCore = best;
                snapshot.CpuSecondCore = second;
            }
            else snapshot.CpuSdkOk = 0;

            // PawnIO (advanced sensors, opt-in): Tctl from the SMU. The marker is polled each window
            // so the toggle works hot, like the SDK consent and FPS. As the later source it overrides
            // only the field it owns: the temperature (Tctl over the SDK's die-average — and on
            // laptops, where the SDK can't read mobile APUs, the only CPU temperature there is).
            if (!noPawnIo && CpuVendor.IsAmd)
            {
                bool wanted = ConsentMarker.AmdAdvancedEnabled;
                if (wanted && pawnIo is null && !pawnIoTried)
                {
                    pawnIoTried = true;
                    pawnIo = PawnIoCpu.TryOpen(out string? pawnErr);
                    pawnIoErr = pawnErr;
                    Console.WriteLine($"PawnIO RyzenSMU: {(pawnIo is not null
                        ? $"abierto (Tctl via SMN; PM_Table v0x{pawnIo.PmTableVersion:X} {(pawnIo.PmTableSupported ? "-> potencia" : "sin mapa de offsets, solo temp")})"
                        : $"no disponible - {pawnErr}")}");
                }
                else if (!wanted)
                {
                    if (pawnIo is not null) { pawnIo.Dispose(); pawnIo = null; Console.WriteLine("PawnIO RyzenSMU: cerrado (opt-out)."); }
                    pawnIoTried = false;   // allow one fresh attempt if the user opts back in
                }
            }
            // On-demand PM_Table dump for the "support my CPU" GitHub-issue flow: the UI drops a
            // request file, we answer next to it. Served even (especially) on unmapped versions.
            if (DiagBridge.DumpRequested)
                DiagBridge.WriteDump(pawnIo is not null
                    ? pawnIo.DumpDiagnostics()
                    : $"pawnio=cerrado ({pawnIoErr ?? "opt-in apagado o driver no instalado"})\n");

            snapshot.CpuPmTableVersion = pawnIo?.PmTableVersion ?? 0;
            snapshot.CpuPawnIoOk = 0;
            if (pawnIo is not null && pawnIo.TryRead(out var pawnData))
            {
                snapshot.CpuPawnIoOk = 1;
                snapshot.CpuTempC = pawnData.TctlC;
                // Power from the PM_Table, but only where the SDK isn't providing it (laptops):
                // on desktop the SDK's PPT/TDC/Tjmax stay authoritative and PawnIO owns Tctl only.
                if (pawnData.HasPower && snapshot.CpuSdkOk == 0)
                {
                    snapshot.CpuPawnIoOk |= 2;
                    snapshot.CpuPackageW = pawnData.PackageW;
                    snapshot.CpuPptPct = pawnData.PptPct;
                    snapshot.CpuTdcPct = pawnData.TdcPct;
                    snapshot.CpuTjMaxC = pawnData.TjMaxC;
                }
            }

            // Intel CPU sensors (opt-in): per-core temp + RAPL package power. The marker is polled
            // each window so the toggle works hot. Intel has no SDK block above, so this is the sole
            // CPU temp/power source on Intel — it fills the same EtwSnapshot fields the AMD SDK does.
            if (!noIntel && (CpuVendor.IsIntel || forceIntel))
            {
                bool wanted = forceIntel || ConsentMarker.IntelSensorsEnabled;
                if (wanted && intelMsr is null && !intelTried)
                {
                    intelTried = true;
                    intelMsr = IntelMsr.TryOpen(out intelErr);
                    Console.WriteLine($"Intel MSR (PawnIO): {(intelMsr is not null
                        ? $"abierto (Tjmax {intelMsr.TjMaxC:F0} C, temp por-nucleo + potencia RAPL)"
                        : $"no disponible - {intelErr}")}");
                }
                else if (!wanted)
                {
                    if (intelMsr is not null) { intelMsr.Dispose(); intelMsr = null; Console.WriteLine("Intel MSR: cerrado (opt-out)."); }
                    intelTried = false;   // allow one fresh attempt if the user opts back in
                }
            }

            snapshot.CpuIntelOk = 0;
            snapshot.CpuThrottleFlags = 0;
            if (intelMsr is not null && intelMsr.TryRead(out var intelData))
            {
                snapshot.CpuIntelOk = 1;
                snapshot.CpuTempC = intelData.TempC;
                snapshot.CpuTjMaxC = intelData.TjMaxC;
                snapshot.CpuThrottleFlags = intelData.ThrottleFlags;
                snapshot.CpuPhysicalCores = 0;   // Intel fills per-LOGICAL temps directly (no phys map)
                int n = Math.Min(intelData.CoreCount, 16);
                for (int i = 0; i < n; i++) snapshot.CpuCoreTempsC[i] = intelData.CoreTempsC[i];
                // Real per-core boost clock (APERF/MPERF); 0 on the first window (no delta yet).
                if (intelData.BestFreqMhz > 0) snapshot.CpuBestFreqMhz = intelData.BestFreqMhz;
                if (intelData.HasPower)
                {
                    snapshot.CpuIntelOk |= 2;
                    snapshot.CpuPackageW = intelData.PackageW;
                    snapshot.CpuPptPct = intelData.PptPct;   // package power as % of PL1
                }
            }

            // Laptop fan % via the EC (opt-in). Marker polled each window so the toggle works hot.
            if (!noFan)
            {
                bool wantFan = forceFan || ConsentMarker.FanPawnIoEnabled;
                if (wantFan && ecFan is null && !ecFanTried)
                {
                    ecFanTried = true;
                    ecFan = EcFan.TryOpen(fanModels, out ecFanErr);
                    Console.WriteLine($"Ventilador (EC/PawnIO): {(ecFan is not null ? $"abierto (modelo {ecFan.Model})" : $"no disponible - {ecFanErr}")}");
                }
                else if (!wantFan && ecFan is not null)
                {
                    ecFan.Dispose(); ecFan = null; ecFanTried = false; fanPct = float.NaN;
                    Console.WriteLine("Ventilador (EC/PawnIO): cerrado (opt-out).");
                }
            }
            if (ecFan is not null)
            {
                float p = ecFan.TryReadPct();
                if (!float.IsNaN(p)) fanPct = p;   // keep last good if this window was contended
            }
            snapshot.CpuFanPct = fanPct;

            diskTemps.Fill(ref snapshot);

            // Game frame-timing (PresentMon), opt-in via the marker the UI writes.
            frameStats.SetEnabled(ConsentMarker.FpsEnabled);
            snapshot.Frame = frameStats.Poll();

            writer.Publish(snapshot);
            published++;

            // Re-read the SCM (svchost→service) and the WMI process details every ~20 windows so
            // late-started processes get picked up. Cheap, and off the sample hot path.
            if (published % 20 == 0) { names.RefreshServices(); details.Refresh(); }
            if (published % 60 == 0) CorePeakStore.Save(corePeak);   // persist the learned best-core peaks

            if (verbose)
            {
                string netTop = snapshot.NetProcCount > 0
                    ? $"{NameField.Get(ref snapshot.NetProcs[0].Name)} ↓{snapshot.NetProcs[0].RxBytesPerSec / 1024:F1}K ↑{snapshot.NetProcs[0].TxBytesPerSec / 1024:F1}K"
                    : "(nada)";
                Console.WriteLine($"pub {published,4}  {snapshot.WindowSeconds:F2}s  " +
                                  $"netEvents_total={Interlocked.Read(ref netEvents)}  netProcs={snapshot.NetProcCount}  top: {netTop}");
            }
            }
            finally { Volatile.Write(ref publishing, 0); }
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

        CorePeakStore.Save(corePeak);
        ecFan?.Dispose();
        intelMsr?.Dispose();
        pawnIo?.Dispose();
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

    /// <summary>The machine's DMI identifiers (system model + baseboard product) to match against the
    /// NBFC fan-register map. NBFC keys on the DMI product name; we try both and dedupe.</summary>
    private static string[] DmiModels()
    {
        var list = new List<string>(2);
        void Add(string q, string prop)
        {
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {q}");
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                        if (mo[prop] is string m && !string.IsNullOrWhiteSpace(m) && !list.Contains(m.Trim()))
                            list.Add(m.Trim());
            }
            catch { /* WMI hiccup — the fan path just stays unsupported */ }
        }
        Add("Win32_ComputerSystem", "Model");
        Add("Win32_BaseBoard", "Product");
        return list.ToArray();
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

}

/// <summary>
/// Persists the per-physical-core peak clocks so the best/second-core ranking survives restarts and
/// converges on the fixed CPPC preferred cores, instead of being re-learned (and jumping) each run.
/// </summary>
internal static class CorePeakStore
{
    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor", "corepeak.dat");

    public static double[] Load()
    {
        var a = new double[16];
        try
        {
            if (File.Exists(Path))
            {
                var parts = File.ReadAllText(Path).Split(',', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < a.Length && i < parts.Length; i++)
                    if (double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 && v < 6000)
                        a[i] = v;
            }
        }
        catch { /* first run or unreadable */ }
        return a;
    }

    public static void Save(double[] peak)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, string.Join(",", peak.Select(v => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
        }
        catch { /* non-fatal */ }
    }
}
