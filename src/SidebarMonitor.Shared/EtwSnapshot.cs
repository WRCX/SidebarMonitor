using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SidebarMonitor.Shared;

public static class EtwLayout
{
    /// <summary>'SBME' little-endian.</summary>
    public const uint Signature = 0x454D4253;
    public const uint Version = 16;
    /// <summary>Global\ on purpose: there is exactly ONE elevated helper per machine (the NT Kernel
    /// Logger session it owns is single-instance machine-wide), and every user's session must see
    /// it. Creating a Global\ map needs SeCreateGlobalPrivilege — the helper is elevated, so it has
    /// it; unelevated readers in any session can open it fine. Local\ made the helper invisible to
    /// every session but its own: with fast user switching the second user's UI said "sin helper"
    /// while a helper was running, or a second helper hijacked the first one's kernel session.</summary>
    public const string MapName = @"Global\SidebarMonitor.Etw";

    /// <summary>Segments drawn per core bar. Beyond this, the rest folds into "otros".</summary>
    public const int TopPerCore = 3;
    public const int MaxNetProcs = 8;

    /// <summary>Reserved PIDs the UI paints with a dedicated colour rather than a series slot.</summary>
    public const int PidIdle = 0;
    public const int PidSystem = 4;
}

/// <summary>One process's share of a core, already grouped by name.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ProcShare
{
    public Name32 Name;
    /// <summary>Share of the core's NON-IDLE samples, 0..100.</summary>
    public float Pct;
    /// <summary>True for the System/kernel bucket, which the UI colours specially.</summary>
    public byte IsKernel;
}

[InlineArray(EtwLayout.TopPerCore)] public struct ProcShareArray { private ProcShare _element0; }
[InlineArray(SnapshotLayout.MaxCores)] public struct CoreShareArray { private ProcShareArray _element0; }

[StructLayout(LayoutKind.Sequential)]
public struct NetProcInfo
{
    public Name32 Name;
    public double RxBytesPerSec, TxBytesPerSec;
}

[InlineArray(EtwLayout.MaxNetProcs)] public struct NetProcArray { private NetProcInfo _element0; }

/// <summary>
/// What only an elevated ETW kernel session can tell us.
///
/// The shares are over each core's non-idle samples on purpose. The sampled profiler emits
/// nothing while a core sleeps in a deep C-state, so idle is systematically undercounted and
/// a sample-derived "busy %" is a lie. These numbers answer WHO owns the core, never HOW MUCH:
/// the bar's height still comes from PDH, and these only colour its segments.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct EtwSnapshot
{
    public long TimestampUtcTicks;
    public double WindowSeconds;

    public int CoreCount;
    public CoreShareArray Cores;

    /// <summary>Non-idle sample count per core; 0 means "no data, do not colour".</summary>
    public CoreSampleArray CoreSamples;

    /// <summary>Per core, the parent + command line of the PID owning most of its samples. Only the
    /// elevated helper can read the command line of system processes (svchost), so it fills this for
    /// the UI's core tooltip. Empty when unknown.</summary>
    public CoreDetailArray CoreDetail;

    public int NetProcCount;
    public NetProcArray NetProcs;

    // ---- From the AMD Ryzen Master SDK, when the helper has it (needs admin + AMD driver) ----

    /// <summary>True when the AMD SDK is providing CPU sensors; then HWiNFO is not needed for CPU.</summary>
    public byte CpuSdkOk;
    public double CpuTempC;
    public float CpuPackageW;
    /// <summary>Best-core current clock (MHz) straight from the AMD SDK's per-core dCurrentFreq — the
    /// actual boost clock HWiNFO shows (reaches ~5040 on a 7800X3D). Windows' % Processor Performance
    /// averages the sample and undershoots the brief single-core boost, so the agent prefers this.</summary>
    public float CpuBestFreqMhz;

    /// <summary>Average core voltage (Vcore / VID), volts. 0 = not read.</summary>
    public float CpuVidV;
    /// <summary>Thermal limit (cHTC), °C — the temperature the CPU throttles at. 0 = unknown.</summary>
    public float CpuTjMaxC;
    /// <summary>Limit utilisation (HWiNFO's "Limits"): PPT power, TDC/EDC current, as % of cap.</summary>
    public float CpuPptPct, CpuTdcPct, CpuEdcPct;
    /// <summary>Cores that boost highest (derived from observed peak clock), as PHYSICAL core
    /// indices. -1 = unknown. The UI maps them onto its logical rows via CpuPhysicalCores.</summary>
    public int CpuBestCore;
    public int CpuSecondCore;
    /// <summary>Physical core count the SDK reports (8 for a 7800X3D), so the UI can map a physical
    /// best-core index onto its logical (SMT) rows. 0 = unknown.</summary>
    public int CpuPhysicalCores;

    /// <summary>Per-PHYSICAL-core temperature (°C) from the AMD SDK. The agent maps these onto the
    /// logical rows. NaN/0 when unavailable.</summary>
    public PhysCoreTempArray CpuCoreTempsC;

    /// <summary>Per-PHYSICAL-core C0 (active) state residency %, from the AMD SDK (dState): how much
    /// of the sample the core was awake. ~0 = parked/asleep — the "Sleep" state Ryzen Master shows.
    /// The agent maps these onto the logical rows. -1 when unavailable.</summary>
    public PhysCoreTempArray CpuCoreC0Pct;

    // ---- From PawnIO's signed RyzenSMU module, when installed + opted in (advanced sensors) ----

    /// <summary>Bitmask of what PawnIO provided this window. Bit 0: CpuTempC holds Tctl (the hotspot
    /// HWiNFO shows) instead of the SDK's die-average. Bit 1: the power fields (CpuPackageW,
    /// CpuPptPct, CpuTdcPct, CpuTjMaxC) came from the SMU's PM_Table — only set when the SDK isn't
    /// providing them. Bit 2: the clock fields — CpuLimitMhz holds the SMU's dynamic global boost
    /// ceiling and CpuBestFreqMhz was raised to the highest per-core EFFECTIVE clock from the table
    /// (the number HWiNFO calls "Core Effective Clock"). Independent of CpuSdkOk on purpose: the
    /// Ryzen Master SDK doesn't read mobile APUs, so on laptops PawnIO is the only source.</summary>
    public byte CpuPawnIoOk;

    /// <summary>PM_Table version the SMU reported (0 = PawnIO closed or table unresolved). Nonzero
    /// with CpuPawnIoOk bit 1 clear = "we can read this CPU's table but don't know its layout yet" —
    /// what the diagnostics dump + GitHub issue flow exists to fix.</summary>
    public ulong CpuPmTableVersion;

    /// <summary>The SMU's live global frequency limit (MHz) — the dynamic boost ceiling that sags
    /// under all-core load (HWiNFO's "Frequency Limit - Global"). From the PM_Table on versions
    /// whose layout maps it (CpuPawnIoOk bit 2). 0 = unknown. Fills Snapshot.Cpu.LimitMhz.</summary>
    public float CpuLimitMhz;

    // ---- From PawnIO's signed IntelMSR module, when installed + opted in (Intel CPUs only) ----

    /// <summary>Bitmask of what the Intel MSR path provided this window. Bit 0: CpuTempC holds the
    /// hottest logical-core temperature (Tjmax − IA32_THERM_STATUS readout), CpuTjMaxC holds the real
    /// Tjmax, and CpuCoreTempsC[i] holds the per-LOGICAL-core temperature (1:1, not the AMD physical
    /// mapping). Bit 1: CpuPackageW holds RAPL package power. 0 = Intel path closed or read failed.
    /// Mutually exclusive with the AMD sources in practice (a CPU is one vendor).</summary>
    public byte CpuIntelOk;

    /// <summary>Active throttle cap as flags (bit0 thermal, bit1 power, bit2 current), from Intel's
    /// IA32_THERM_STATUS / IA32_PACKAGE_THERM_STATUS status bits. 0 on AMD (limiter comes from the
    /// PPT/TDC/EDC percentages there).</summary>
    public byte CpuThrottleFlags;

    /// <summary>Fan duty % (0..100). On HP gaming laptops it's CpuFanRpm over the fan curve's top
    /// speed; elsewhere it's the elevated helper's embedded-controller read (PawnIO + the per-model
    /// NBFC register map). NaN when unsupported/off. Fills Snapshot.Cpu.FanPct.</summary>
    public float CpuFanPct;

    /// <summary>CPU fan RPM from the HP WMI BIOS interface (Victus/OMEN gaming laptops, whose fan
    /// speed is not exposed via an EC register). NaN when the machine has no HP WMI fan source or the
    /// fan opt-in is off. Preferred over CpuFanPct on HP laptops. Fills Snapshot.Cpu.FanRpm.</summary>
    public float CpuFanRpm;

    /// <summary>Drive temperatures (°C) by physical disk number, from the storage stack (admin).
    /// NaN = unknown. Covers the SATA disks the agent's unelevated NVMe path can't reach, closing
    /// the last thing HWiNFO was for.</summary>
    public DiskTempArray DiskTempsC;

    /// <summary>Foreground game frame-timing (FPS/frametime/lows/GPU busy/latency/stutter), measured
    /// by PresentMon (ETW, no injection). App empty = nothing presenting or the feature is off.</summary>
    public FrameInfo Frame;
}

[InlineArray(SnapshotLayout.MaxDisks)] public struct DiskTempArray { private float _element0; }

[InlineArray(SnapshotLayout.MaxCores)] public struct CoreSampleArray { private int _element0; }
[InlineArray(16)] public struct PhysCoreTempArray { private float _element0; }   // AMD SDK caps at 16 physical cores
[InlineArray(SnapshotLayout.MaxCores)] public struct CoreDetailArray { private Name160 _element0; }

public static class EtwChannel
{
    public static SeqLockWriter<EtwSnapshot> CreateWriter() =>
        new(EtwLayout.MapName, EtwLayout.Signature, EtwLayout.Version, worldReadable: true);

    public static SeqLockReader<EtwSnapshot>? TryOpenReader(out string? error) =>
        SeqLockReader<EtwSnapshot>.TryOpen(EtwLayout.MapName, EtwLayout.Signature, EtwLayout.Version, out error);
}
