# Intel CPU sensors via PawnIO — design & plan

> **Status: BUILT and VALIDATED on real Intel hardware (2026-07-13).** Shipped in `[Unreleased]`.
> The live implementation is `src/SidebarMonitor.Etw/IntelMsr.cs` (mirrors `PawnIoCpu.cs`): it loads
> the signed **`IntelMSR.bin`**, reads Tjmax + per-core `IA32_THERM_STATUS` + RAPL package power, and
> the helper fills the same `EtwSnapshot` CPU fields the AMD SDK does, gated by
> `ConsentMarker.IntelSensorsEnabled` (Settings → Diagnostics → "Sensores CPU (PawnIO)", Intel only).
> The agent's `MergeEtw` copies them 1:1 (`CpuIntelOk` bitmask → `CpuFromIntel`). Contracts:
> `EtwSnapshot` v13, `Snapshot` v24.
>
> **Verified end-to-end on an i7-4700HQ (Haswell, 4c/8t):** Tjmax 100 °C; per-core temps that differ
> per core and track load (60 → 72 °C hottest core idle→load; package temp consistent); RAPL package
> power 3 → 36 W standalone and 23 → 41 W through the full helper→agent→UI pipeline under all-core
> load. This answered the design's open questions: PawnIO **does** run the MSR IOCTL on the caller's
> pinned processor (per-core affinity works), and Haswell exposes all three RAPL domains
> (package/PP0/DRAM). The reference sketches below are kept for context; the real code is the source
> of truth.
>
> **Note on the module:** release PawnIO only loads modules **signed by the PawnIO.Modules project**,
> so we use the official signed **`IntelMSR.bin`** (fetched by `native/PawnIO/fetch.ps1`). Its
> **verified export is `ioctl_read_msr`** (1 in = MSR index, 1 out = 64-bit value); `main()` gates on
> Intel + x64 and refuses to load elsewhere. Do NOT compile the `intel_msr.p` sketch below.

## Why PawnIO (and not Intel XTU)

Intel's per-core temperature and package power live in **model-specific registers (MSRs)**, reachable
only from ring 0. Unlike AMD — which ships the signed Ryzen Master Monitoring SDK — **Intel offers no
public SDK** for third parties to read these. Intel XTU reads them through *its own* undocumented
kernel driver; there is no supported API for us to call, so XTU is a dead end.

The clean, HVCI-safe route is **[PawnIO](https://github.com/namazso/PawnIO)**: a **signed** kernel
driver that embeds a Pawn virtual machine and runs small user-supplied "modules" to do ring-0 IO
(MSR reads, etc.). It's on Microsoft's good side (signed, not on the vulnerable-driver blocklist),
which is exactly the property that makes SidebarMonitor work under Memory Integrity. UXTU (Universal
x86 Tuning Utility) already migrated from WinRing0 to PawnIO for this reason.

Trade-off vs AMD: PawnIO is a **separate signed driver the user installs**, so Intel CPU sensors stay
a second-class, opt-in path — AMD Ryzen remains the premium "it just works" experience.

## The MSRs we need

| Quantity | MSR | Bits | Notes |
|---|---|---|---|
| Per-core temperature | `IA32_THERM_STATUS` (0x19C) | [22:16] "Digital Readout" = °C **below** Tjmax | Read on each core (set thread affinity). Valid when bit 31 set. |
| Tjmax (throttle temp) | `MSR_TEMPERATURE_TARGET` (0x1A2) | [23:16] | Usually 100 °C. Package-wide. |
| Package temperature | `IA32_PACKAGE_THERM_STATUS` (0x1B1) | [22:16] below Tjmax | Optional; die/package hotspot. |
| RAPL energy unit | `MSR_RAPL_POWER_UNIT` (0x606) | [12:8] → energy unit = 1/2^ESU joules | Read once. |
| Package energy | `MSR_PKG_ENERGY_STATUS` (0x611) | [31:0] accumulating | Package power = ΔEnergy·unit / Δt. 32-bit, wraps. |
| (opt) IA cores energy | `MSR_PP0_ENERGY_STATUS` (0x639) | [31:0] | Core-domain power. |

Actual temperature = `Tjmax − DigitalReadout`. Package watts = `(energy₂ − energy₁) · energyUnit / Δseconds`.

## Architecture fit

This slots in exactly where the AMD SDK does, in the **elevated helper** (`SidebarMonitor.Etw`), which
already runs at Highest and already publishes CPU temp/power into `EtwSnapshot`:

```
CpuVendor.Detect() == Intel
        │
        ├─ PawnIO present + consented ──► IntelMsr (below) reads MSRs each window
        │                                   → EtwSnapshot.CpuTempC / CpuPackageW / CpuCoreTempsC
        └─ not present ─────────────────► current behaviour: temp/power show "—"
```

No contract change: `EtwSnapshot` already carries `CpuTempC`, `CpuPackageW`, `CpuCoreTempsC[]`,
`TjMaxC`. The agent's `MergeEtw` already copies them to `Snapshot.Cpu`. The UI already renders them
and gates AMD-only extras (throttle/PPT/best-core) behind `CpuFromAmd`, so Intel just fills the basic
temp/power fields.

## The PawnIO module (generic MSR read)

Per-core reads are done by the **caller** setting thread affinity and then executing a one-MSR read,
so the module stays trivial (`msr_read` runs on whichever core the calling thread is pinned to).

```pawn
// intel_msr.p  — compile with pawncc to intel_msr.bin
#include <pawnio.inc>

// in[0] = MSR index; out[0] = raw 64-bit value (read on the current processor).
DEFINE_IOCTL_SIZED(ioctl_read_msr, 1, 1)
{
    new value;
    new NTSTATUS:status = msr_read(in[0], value);
    out[0] = value;
    return status;
}

NTSTATUS:main()   { return STATUS_SUCCESS; }
public NTSTATUS:unload() { return STATUS_SUCCESS; }
```

## The C# side (helper) — reference sketch

```csharp
// PawnIo.cs — P/Invoke over PawnIOLib.dll (ships with PawnIO). Signatures per the current
// PawnIOLib.h — VERIFY against the installed version; the API has been stable as open/load/execute/close.
using System.Runtime.InteropServices;

internal sealed class PawnIo : IDisposable
{
    [DllImport("PawnIOLib.dll")] static extern int pawnio_open(out nint handle);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_load(nint handle, byte[] blob, nuint size);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_execute(
        nint handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);
    [DllImport("PawnIOLib.dll")] static extern int pawnio_close(nint handle);

    readonly nint _h;
    PawnIo(nint h) => _h = h;

    public static PawnIo? TryLoad(byte[] moduleBlob)   // intel_msr.bin, embedded as a resource
    {
        try
        {
            if (pawnio_open(out var h) != 0) return null;           // 0 == STATUS_SUCCESS
            if (pawnio_load(h, moduleBlob, (nuint)moduleBlob.Length) != 0) { pawnio_close(h); return null; }
            return new PawnIo(h);
        }
        catch (DllNotFoundException) { return null; }               // PawnIO not installed
    }

    public bool ReadMsr(uint index, out ulong value)
    {
        var inp = new ulong[] { index };
        var outp = new ulong[1];
        bool ok = pawnio_execute(_h, "ioctl_read_msr", inp, 1, outp, 1, out _) == 0;
        value = outp[0];
        return ok;
    }

    public void Dispose() => pawnio_close(_h);
}
```

```csharp
// IntelMsr.cs — turn MSRs into the values the helper publishes. Per-core temp needs affinity.
internal static class IntelMsr
{
    public static bool ReadTjmax(PawnIo p, out int tjmax)
    {
        tjmax = 100;
        if (!p.ReadMsr(0x1A2, out ulong v)) return false;
        tjmax = (int)((v >> 16) & 0xFF);
        return tjmax > 0;
    }

    // Read IA32_THERM_STATUS on each logical core by pinning this thread to it in turn.
    public static float[] ReadCoreTemps(PawnIo p, int tjmax, int logicalCores)
    {
        var temps = new float[logicalCores];
        var t = new Thread(() =>
        {
            for (int i = 0; i < logicalCores; i++)
            {
                var aff = Native.SetThreadAffinity(1UL << i);       // GetCurrentThread + SetThreadAffinityMask
                if (p.ReadMsr(0x19C, out ulong v) && (v & (1u << 31)) != 0)
                    temps[i] = tjmax - (int)((v >> 16) & 0x7F);
                else temps[i] = float.NaN;
                Native.SetThreadAffinity(aff);
            }
        });
        t.Start(); t.Join();
        return temps;
    }

    // Package power from RAPL: sample energy twice, divide by elapsed time.
    public static float PackageWatts(PawnIo p, ref ulong lastEnergy, ref long lastStamp)
    {
        if (!p.ReadMsr(0x606, out ulong unitRaw)) return float.NaN;
        double energyUnit = 1.0 / (1u << (int)((unitRaw >> 8) & 0x1F));   // joules per tick
        if (!p.ReadMsr(0x611, out ulong e)) return float.NaN;
        long now = Stopwatch.GetTimestamp();
        if (lastStamp == 0) { lastEnergy = e; lastStamp = now; return float.NaN; }
        ulong de = e >= lastEnergy ? e - lastEnergy : (0x100000000UL - lastEnergy + e);   // 32-bit wrap
        double dt = (now - lastStamp) / (double)Stopwatch.Frequency;
        lastEnergy = e; lastStamp = now;
        return (float)(de * energyUnit / dt);
    }
}
```

The helper would: detect Intel → `PawnIo.TryLoad(embedded intel_msr.bin)` → each publish window fill
`EtwSnapshot.TjMaxC`, `CpuCoreTempsC[]` (and `CpuTempC` = max core), `CpuPackageW`. Everything else in
the pipeline already exists.

## Install & consent flow

The first-run dialog already has an **Intel branch** (`FirstRunDialog`, the "note about Intel CPUs"
screen). Extend it to offer enabling sensors:

- Button **"Enable Intel sensors"** → download and run the **signed PawnIO installer** from its GitHub
  releases (or launch a bundled copy), then write a consent marker like the AMD one
  (`ConsentMarker`), which the helper polls to load the module hot.
- Keep **"Continue without"** → stays in PDH-only mode (today's behaviour).

Prefer **linking to / launching PawnIO's own signed installer** over redistributing its driver, so we
don't ship someone else's kernel driver. The `intel_msr.bin` module is ours (trivial, MIT) and can be
embedded as a resource. Confirm PawnIO's licence terms before bundling anything of theirs.

## Caveats / open questions to settle on Intel hardware

- **Per-core affinity from user mode** driving `msr_read` on the pinned core — verify PawnIO executes
  the IOCTL on the caller's current processor (expected) rather than an arbitrary one.
- **RAPL availability** varies by SKU/generation; `MSR_PKG_ENERGY_STATUS` is widespread on Core, but
  guard for absent/zero. Energy unit and the 32-bit wrap must be handled (done above).
- **Tjmax** occasionally needs the per-core offset MSR on some parts; 0x1A2 is the common source.
- **PawnIOLib.h signatures** — pin them against the installed version; treat the sketch as indicative.
- Effort estimate: ~1–2 focused days on a real Intel box (module compile + interop + affinity loop +
  RAPL calibration + wiring into the helper + the consent UI), most of it verification.
