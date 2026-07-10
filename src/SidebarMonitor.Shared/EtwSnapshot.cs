using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SidebarMonitor.Shared;

public static class EtwLayout
{
    /// <summary>'SBME' little-endian.</summary>
    public const uint Signature = 0x454D4253;
    public const uint Version = 1;
    public const string MapName = @"Local\SidebarMonitor.Etw";

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

    public int NetProcCount;
    public NetProcArray NetProcs;
}

[InlineArray(SnapshotLayout.MaxCores)] public struct CoreSampleArray { private int _element0; }

public static class EtwChannel
{
    public static SeqLockWriter<EtwSnapshot> CreateWriter() =>
        new(EtwLayout.MapName, EtwLayout.Signature, EtwLayout.Version);

    public static SeqLockReader<EtwSnapshot>? TryOpenReader(out string? error) =>
        SeqLockReader<EtwSnapshot>.TryOpen(EtwLayout.MapName, EtwLayout.Signature, EtwLayout.Version, out error);
}
