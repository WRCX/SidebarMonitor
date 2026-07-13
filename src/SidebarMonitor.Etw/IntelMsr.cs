using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SidebarMonitor.Etw;

/// <summary>
/// Intel CPU temperature and package power via PawnIO's signed <c>IntelMSR</c> module. Intel ships no
/// public monitoring SDK (XTU reads through its own private driver), so on Intel this is the ONLY
/// clean, HVCI-safe route to CPU temp/watts — the counterpart of <see cref="RyzenSdk"/>+<see
/// cref="PawnIoCpu"/> on AMD. Opt-in (the user installs PawnIO and flips the toggle) and fails soft:
/// without PawnIO, the module, or an Intel CPU, <see cref="TryOpen"/> returns null and the helper
/// behaves exactly as before (temp/power show "—").
///
/// Reads three things through the module's <c>ioctl_read_msr</c> (verified on an i7-4700HQ, Haswell):
///   • Tjmax          — MSR_TEMPERATURE_TARGET (0x1A2), bits [23:16], once (stable per boot).
///   • per-core temp  — IA32_THERM_STATUS (0x19C), bits [22:16] = °C below Tjmax, read on each
///                      logical core by pinning this thread to it (PawnIO runs the IOCTL on the
///                      caller's current processor — confirmed: the per-core values differ and track
///                      load independently).
///   • package power  — RAPL: MSR_RAPL_POWER_UNIT (0x606) energy unit × ΔMSR_PKG_ENERGY_STATUS
///                      (0x611) / Δt, differenced against the previous publish window (32-bit wrap).
/// Unlike the AMD SMU path there is no shared PCI config window, so no <c>Global\Access_PCI</c> mutex
/// is needed — MSR reads are independent per core.
/// </summary>
internal sealed class IntelMsr : IDisposable
{
    private const uint IA32_THERM_STATUS         = 0x19C;
    private const uint IA32_PACKAGE_THERM_STATUS = 0x1B1;
    private const uint MSR_TEMPERATURE_TARGET    = 0x1A2;
    private const uint MSR_RAPL_POWER_UNIT       = 0x606;
    private const uint MSR_PKG_POWER_LIMIT       = 0x610;
    private const uint MSR_PKG_ENERGY_STATUS     = 0x611;
    private const uint MSR_PLATFORM_INFO         = 0x0CE;
    private const uint IA32_MPERF                = 0x0E7;
    private const uint IA32_APERF                = 0x0E8;

    /// <summary>Per-logical-core temperature cap (matches EtwSnapshot.CpuCoreTempsC width).</summary>
    private const int MaxCores = 16;

    /// <summary>Active-throttle flags (mirror EtwSnapshot.CpuThrottleFlags / CpuInfo.ThrottleFlags).</summary>
    private const byte ThrThermal = 1, ThrPower = 2, ThrCurrent = 4;

    public struct Data
    {
        public double TempC;          // hottest logical core, °C
        public float TjMaxC;          // throttle temperature, 0 = unknown
        public int CoreCount;         // logical cores read into CoreTempsC
        public float[] CoreTempsC;    // per-logical-core temperature (NaN where a read failed)
        public bool HasPower;         // false on the first window (no delta yet) or absent RAPL
        public float PackageW;        // RAPL package power
        public float PptPct;          // package power as % of PL1 (the RAPL power limit), 0 = unknown
        public float BestFreqMhz;     // highest real per-core clock (APERF/MPERF), 0 until 2nd window
        public byte ThrottleFlags;    // active caps: bit0 thermal, bit1 power, bit2 current
    }

    private const string Dll = "PawnIOLib";

    [DllImport(Dll)] private static extern int pawnio_open(out nint handle);
    [DllImport(Dll)] private static extern int pawnio_load(nint handle, byte[] blob, nuint size);
    [DllImport(Dll)] private static extern int pawnio_execute(
        nint handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);
    [DllImport(Dll)] private static extern int pawnio_close(nint handle);

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint hThread, nuint mask);

    static IntelMsr()
    {
        // PawnIOLib.dll lives in PawnIO's install dir (not on PATH); map the import to the absolute
        // path. PawnIoCpu registers an identical resolver for this same assembly — only one may be
        // set, so swallow the double-registration (the existing one does the same lookup).
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(IntelMsr).Assembly, (name, _, _) =>
                name == Dll && NativeLibrary.TryLoad(InstalledLibPath, out nint lib) ? lib : nint.Zero);
        }
        catch (InvalidOperationException) { /* PawnIoCpu already registered an equivalent resolver */ }
    }

    private static string InstalledLibPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

    private nint _handle;
    private readonly ulong[] _in = new ulong[1];
    private readonly ulong[] _out = new ulong[1];
    private readonly float[] _coreTemps = new float[MaxCores];
    private readonly ulong[] _lastAperf = new ulong[MaxCores];   // previous-window APERF per core
    private readonly ulong[] _lastMperf = new ulong[MaxCores];   // previous-window MPERF per core
    private bool _haveClockBase;      // true once _lastAperf/_lastMperf hold a prior sample
    private int _tjMax = 100;         // default; overwritten from 0x1A2 at open when plausible
    private double _baseMhz = 2400;   // base (non-turbo) frequency; APERF/MPERF ratio scales off it
    private double _energyUnit;        // joules per RAPL tick, 0 = no RAPL on this part
    private double _pl1W;              // package power limit PL1, watts; 0 = unknown (no PptPct then)
    private ulong _lastEnergy;         // previous window's 32-bit package energy
    private long _lastStamp;           // Stopwatch timestamp of that read, 0 = none yet

    public bool IsOpen { get; private set; }
    public float TjMaxC => _tjMax;

    private IntelMsr() { }

    /// <summary>
    /// Null (with a human-readable reason) when PawnIO isn't installed, the module blob isn't next to
    /// the helper, or the CPU isn't Intel/x64 (the signed module's own main() checks CPUID and refuses
    /// to load elsewhere).
    /// </summary>
    public static IntelMsr? TryOpen(out string? error)
    {
        error = null;
        string binPath = Path.Combine(AppContext.BaseDirectory, "IntelMSR.bin");
        if (!File.Exists(InstalledLibPath)) { error = "PawnIO no instalado (falta PawnIOLib.dll)"; return null; }
        if (!File.Exists(binPath)) { error = "IntelMSR.bin no encontrado junto al helper"; return null; }

        var p = new IntelMsr();
        try
        {
            int hr = pawnio_open(out p._handle);
            if (hr != 0) { error = $"pawnio_open devolvio 0x{hr:X8}"; return null; }

            byte[] blob = File.ReadAllBytes(binPath);
            hr = pawnio_load(p._handle, blob, (nuint)blob.Length);
            if (hr != 0)
            {
                // The module's main() rejects non-Intel / non-x64 with STATUS_NOT_SUPPORTED.
                pawnio_close(p._handle);
                error = $"pawnio_load devolvio 0x{hr:X8} (CPU no Intel/x64?)";
                return null;
            }

            p.IsOpen = true;

            // Tjmax once — it's fixed per boot. bits [23:16] of MSR_TEMPERATURE_TARGET.
            if (p.ReadMsr(MSR_TEMPERATURE_TARGET, out ulong tt))
            {
                int tj = (int)((tt >> 16) & 0xFF);
                if (tj is > 50 and < 130) p._tjMax = tj;
            }

            // RAPL units once. bits [12:8] = energy unit (1/2^u joules); bits [3:0] = power unit
            // (1/2^u watts), used to turn the PL1 raw limit into watts.
            double powerUnitW = 0;
            if (p.ReadMsr(MSR_RAPL_POWER_UNIT, out ulong unitRaw))
            {
                p._energyUnit = 1.0 / (1u << (int)((unitRaw >> 8) & 0x1F));
                powerUnitW = 1.0 / (1u << (int)(unitRaw & 0xF));
            }

            // Package power limit PL1 (bits [14:0] × power unit) → watts, so package power can be
            // expressed as a % of its cap (the Intel analogue of AMD's PPT%). Enabled bit [15].
            if (powerUnitW > 0 && p.ReadMsr(MSR_PKG_POWER_LIMIT, out ulong plRaw) && (plRaw & (1UL << 15)) != 0)
            {
                double pl1 = (plRaw & 0x7FFF) * powerUnitW;
                if (pl1 is > 5 and < 500) p._pl1W = pl1;
            }

            // Base (max non-turbo) frequency from MSR_PLATFORM_INFO bits [15:8] × 100 MHz. MPERF
            // ticks at this rate, so real clock = base × ΔAPERF/ΔMPERF.
            if (p.ReadMsr(MSR_PLATFORM_INFO, out ulong plat))
            {
                int ratio = (int)((plat >> 8) & 0xFF);
                if (ratio is > 4 and < 120) p._baseMhz = ratio * 100.0;
            }

            return p;
        }
        catch (DllNotFoundException) { error = "PawnIOLib.dll no cargable"; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// One publish window's worth of Intel sensors: per-core temperature (always, when at least one
    /// core reads) and RAPL package power (from the second window on). Returns false when not a single
    /// core read back — then this window is temp-less, never stale.
    /// </summary>
    public bool TryRead(out Data data)
    {
        data = default;
        if (!IsOpen) return false;

        try
        {
            int cores = Math.Min(Environment.ProcessorCount, MaxCores);
            for (int i = 0; i < _coreTemps.Length; i++) _coreTemps[i] = float.NaN;

            // Per-core reads on a dedicated thread: SetThreadAffinityMask pins whoever calls it (the
            // GetCurrentThread pseudo-handle is per-caller), so this keeps the publish thread from
            // being stranded on one core if anything throws mid-loop. One pass reads all three
            // per-core MSRs (temp, APERF, MPERF) on each pinned core.
            float[] temps = _coreTemps;
            int hottest = int.MinValue;
            byte thr = 0;
            var aperf = new ulong[cores];
            var mperf = new ulong[cores];
            var t = new Thread(() =>
            {
                nint self = GetCurrentThread();
                for (int i = 0; i < cores; i++)
                {
                    nuint prev = SetThreadAffinityMask(self, (nuint)(1UL << i));
                    if (prev == 0) continue;   // affinity rejected: leave NaN
                    if (ReadMsr(IA32_THERM_STATUS, out ulong v) && (v & (1UL << 31)) != 0)
                    {
                        int temp = _tjMax - (int)((v >> 16) & 0x7F);
                        if (temp is > 0 and < 150)
                        {
                            temps[i] = temp;
                            if (temp > hottest) hottest = temp;
                        }
                        thr |= ThrottleBits(v);
                    }
                    ReadMsr(IA32_APERF, out aperf[i]);
                    ReadMsr(IA32_MPERF, out mperf[i]);
                    SetThreadAffinityMask(self, prev);
                }
            });
            t.Start();
            t.Join();

            if (hottest == int.MinValue) return false;   // nothing read — skip this window

            data.CoreCount = cores;
            data.CoreTempsC = _coreTemps;
            data.TempC = hottest;
            data.TjMaxC = _tjMax;

            // Package-wide throttle status (adds power/thermal caps the per-core register may not show).
            if (ReadMsr(IA32_PACKAGE_THERM_STATUS, out ulong pkgThr) && (pkgThr & (1UL << 31)) != 0)
                thr |= ThrottleBits(pkgThr);
            data.ThrottleFlags = thr;

            // Real per-core clock via APERF/MPERF, differenced against the previous window. Highest
            // core = the achieved boost clock (the single-core turbo PDH's averaged % smooths away).
            if (_haveClockBase)
            {
                double best = 0;
                for (int i = 0; i < cores; i++)
                {
                    ulong da = aperf[i] - _lastAperf[i], dm = mperf[i] - _lastMperf[i];
                    if (dm > 0)
                    {
                        double mhz = _baseMhz * da / dm;
                        if (mhz > best && mhz < 12000) best = mhz;   // guard a wrapped/garbage delta
                    }
                }
                data.BestFreqMhz = (float)best;
            }
            for (int i = 0; i < cores; i++) { _lastAperf[i] = aperf[i]; _lastMperf[i] = mperf[i]; }
            _haveClockBase = true;

            // RAPL package power: difference this window's accumulating energy against the last.
            if (_energyUnit > 0 && ReadMsr(MSR_PKG_ENERGY_STATUS, out ulong eRaw))
            {
                ulong e = eRaw & 0xFFFF_FFFF;   // energy status is 32-bit, wraps
                long now = Stopwatch.GetTimestamp();
                if (_lastStamp != 0)
                {
                    ulong de = e >= _lastEnergy ? e - _lastEnergy : (0x1_0000_0000UL - _lastEnergy + e);
                    double dt = (now - _lastStamp) / (double)Stopwatch.Frequency;
                    if (dt > 0)
                    {
                        float w = (float)(de * _energyUnit / dt);
                        if (w is >= 0 and < 1000)
                        {
                            data.HasPower = true;
                            data.PackageW = w;
                            // Package power as % of PL1 — the Intel counterpart of AMD's PPT%.
                            if (_pl1W > 0) data.PptPct = Math.Clamp((float)(100.0 * w / _pl1W), 0f, 200f);
                        }
                    }
                }
                _lastEnergy = e;
                _lastStamp = now;
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Maps the *active* (not sticky-log) status bits of IA32_THERM_STATUS / IA32_PACKAGE_THERM_STATUS
    /// onto our throttle flags. Bit 0 = thermal status (PROCHOT now), bit 2 = PROCHOT/FORCEPR asserted,
    /// bit 4 = critical temperature → thermal; bit 10 = power-limitation status → power; bit 12 =
    /// current-limit status → current. The odd bits (1/3/5/11/13, the sticky logs) are ignored — they
    /// latch a past event, not what's binding right now.
    /// </summary>
    private static byte ThrottleBits(ulong v)
    {
        byte f = 0;
        if ((v & (1UL << 0)) != 0 || (v & (1UL << 2)) != 0 || (v & (1UL << 4)) != 0) f |= ThrThermal;
        if ((v & (1UL << 10)) != 0) f |= ThrPower;
        if ((v & (1UL << 12)) != 0) f |= ThrCurrent;
        return f;
    }

    private bool ReadMsr(uint msr, out ulong value)
    {
        _in[0] = msr;
        int rc = pawnio_execute(_handle, "ioctl_read_msr", _in, 1, _out, 1, out _);
        value = _out[0];
        return rc == 0;
    }

    public void Dispose()
    {
        if (IsOpen) { try { pawnio_close(_handle); } catch { } IsOpen = false; }
    }
}
