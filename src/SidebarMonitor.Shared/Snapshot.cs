using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;

namespace SidebarMonitor.Shared;

public static class SnapshotLayout
{
    /// <summary>'SBMN' little-endian.</summary>
    public const uint Signature = 0x4E4D4253;

    /// <summary>Bump on any layout change. The reader refuses anything it does not know.</summary>
    public const uint Version = 7;

    /// <summary>
    /// Local\, not Global\. Creating a Global\ kernel object requires SeCreateGlobalPrivilege,
    /// which an unelevated process does not hold. HWiNFO gets away with Global\ because it runs
    /// elevated. Agent and UI share a session, so Local\ is all we need.
    /// </summary>
    /// <summary>
    /// No version in the name: the header carries it. A stale reader then reports "version 3,
    /// expected 2" instead of the far more confusing "nobody is publishing".
    /// </summary>
    public const string MapName = @"Local\SidebarMonitor.Snapshot";

    public const int MaxCores = 64;
    public const int MaxGpus = 2;
    public const int MaxNics = 4;
    public const int MaxDisks = 8;
    public const int MaxProcs = 16;
}

[InlineArray(32)] public struct Name32 { private byte _element0; }
[InlineArray(64)] public struct Name64 { private byte _element0; }
[InlineArray(160)] public struct Name160 { private byte _element0; }
[InlineArray(SnapshotLayout.MaxCores)] public struct CoreUsageArray { private float _element0; }
[InlineArray(SnapshotLayout.MaxGpus)] public struct GpuArray { private GpuInfo _element0; }
[InlineArray(SnapshotLayout.MaxNics)] public struct NicArray { private NicInfo _element0; }
[InlineArray(SnapshotLayout.MaxDisks)] public struct DiskArray { private DiskInfo _element0; }
[InlineArray(SnapshotLayout.MaxProcs)] public struct ProcArray { private ProcInfo _element0; }

/// <summary>How to aggregate the per-core clock into one reported number.</summary>
public enum CpuFreqMode { Best = 0, Mean = 1, Median = 2 }

[StructLayout(LayoutKind.Sequential)]
public struct CpuInfo
{
    public Name64 Name;
    public float TotalUsagePct;
    /// <summary>Best core: the highest boost bin any core reaches. Games live here.</summary>
    public float FreqBestMhz;
    public float FreqMeanMhz;
    public float FreqMedianMhz;
    public float PackagePowerW;   // NaN when HWiNFO is not available
    public float TempC;           // NaN when HWiNFO is not available
    public int CoreCount;
    public CoreUsageArray CoreUsagePct;
}

[StructLayout(LayoutKind.Sequential)]
public struct MemInfo
{
    public ulong PhysUsed, PhysTotal;
    public ulong CommitUsed, CommitTotal;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuInfo
{
    public Name64 Name;
    public float LoadPct, MemControllerPct, TempC, PowerW;
    public uint CoreClockMhz, MemClockMhz, FanPct, PcieWidth;
    public ulong VramUsed, VramTotal;
}

[StructLayout(LayoutKind.Sequential)]
public struct NicInfo
{
    public Name32 Name;
    public double RxBytesPerSec, TxBytesPerSec;
    public ulong LinkBitsPerSec;
    /// <summary>The adapter the default route actually uses, per GetBestInterface.</summary>
    public bool IsPrimary;
}

public enum DiskMedia : byte { Unknown = 0, Hdd = 1, Ssd = 2 }

[StructLayout(LayoutKind.Sequential)]
public struct DiskInfo
{
    /// <summary>The PDH instance, e.g. "2 C: E:".</summary>
    public Name32 Name;
    /// <summary>Volume labels joined, e.g. "DATOS12TB" or "Windows / juegos".</summary>
    public Name32 Label;
    /// <summary>
    /// The physical disk's volumes with per-volume used/total, e.g.
    /// "C: 210/293G · juegos 1,2/1,6T". This is what stops one disk's two partitions reading as
    /// two disks: the model is the identity, this line shows the partitions live on it.
    /// </summary>
    public Name160 Volumes;
    /// <summary>Product id from the device descriptor, e.g. "WDC WD120EFGX-68CPHN0".</summary>
    public Name64 Model;
    public Name32 Bus;
    public DiskMedia Media;
    /// <summary>USB / removable, virtual (WSL/Hyper-V vHD), or the disk holding the Windows volume.</summary>
    public byte IsRemovable, IsVirtual, IsSystem;
    /// <summary>From HWiNFO's S.M.A.R.T. sensors; NaN when unavailable.</summary>
    public float TempC;
    public ulong SizeBytes;
    public double ReadBytesPerSec, WriteBytesPerSec, QueueLength;
    /// <summary>Task Manager's "active time": 100 - % Idle Time.</summary>
    public float ActivePct;
}

[StructLayout(LayoutKind.Sequential)]
public struct ProcInfo
{
    public Name32 Name;
    /// <summary>The PID when this row is a single process; 0 when it aggregates several.</summary>
    public int Pid;
    public float CpuPct;
    public int Threads;
    public ulong WorkingSet;
    /// <summary>How many processes were folded into this row. 1 when not grouping.</summary>
    public int Instances;
}

[StructLayout(LayoutKind.Sequential)]
public struct Snapshot
{
    public long TimestampUtcTicks;
    public double SampleIntervalSec;

    public CpuInfo Cpu;
    public MemInfo Mem;

    public int GpuCount; public GpuArray Gpus;
    public int NicCount; public NicArray Nics;
    public int DiskCount; public DiskArray Disks;
    public int ProcCount; public ProcArray Procs;

    public int TotalProcesses;
    public int TotalThreads;

    public bool HwiNfoAvailable;

    // ---- Only populated when the elevated ETW helper is running ----

    public bool EtwAvailable;

    /// <summary>Who owns each core, grouped by process name. Shares are over non-idle samples.</summary>
    public CoreShareArray CoreOwners;

    /// <summary>Zero means "no attribution for this core"; do not colour it.</summary>
    public CoreSampleArray CoreOwnerSamples;

    public int NetProcCount;
    public NetProcArray NetProcs;
}

/// <summary>
/// Fixed-size UTF-8 fields, always NUL-terminated. Written without allocating so the agent's
/// per-tick path stays garbage-free even though process names change every tick.
/// </summary>
public static class NameField
{
    public static void Set<T>(ref T field, int size, ReadOnlySpan<char> value) where T : struct
    {
        Span<byte> dst = MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref field), size);
        dst.Clear();
        // Reserve the last byte for the terminator; DestinationTooSmall simply truncates,
        // which is what we want for a display field.
        Utf8.FromUtf16(value, dst[..(size - 1)], out _, out _, replaceInvalidSequences: true);
    }

    public static string Get<T>(ref T field, int size) where T : struct
    {
        ReadOnlySpan<byte> src = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref field), size);
        int nul = src.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(nul < 0 ? src : src[..nul]);
    }

    public static string Get(ref Name32 f) => Get(ref f, 32);
    public static string Get(ref Name64 f) => Get(ref f, 64);
    public static string Get(ref Name160 f) => Get(ref f, 160);
    public static void Set(ref Name32 f, ReadOnlySpan<char> v) => Set(ref f, 32, v);
    public static void Set(ref Name64 f, ReadOnlySpan<char> v) => Set(ref f, 64, v);
    public static void Set(ref Name160 f, ReadOnlySpan<char> v) => Set(ref f, 160, v);
}
