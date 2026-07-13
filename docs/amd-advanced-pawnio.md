# AMD "advanced" CPU sensors via PawnIO (Tctl + SMU PM_Table) — design & plan

> **Status: part 1 (Tctl) IMPLEMENTED and verified on a 7840HS (Phoenix), 2026-07-13.** The helper's
> `PawnIoCpu.cs` loads PawnIO's **signed `RyzenSMU.bin` module** (from `namazso/PawnIO.Modules` —
> release PawnIO only loads modules signed by that project, so we use theirs instead of compiling our
> own as originally planned) and reads Tctl each window, gated by the "Sensores CPU avanzados"
> toggle (Settings → Diagnóstico → `ConsentMarker.AmdAdvancedEnabled`). The reach turned out to be
> bigger than the "+3 °C" framing below: **on mobile APUs the Ryzen Master SDK doesn't work at all**,
> so on laptops this is the *only* CPU temperature source. Part 2 (PM_Table: power/limits) is still
> design-only — though note `RyzenSMU.bin` already exports `ioctl_resolve/update/read_pm_table`, so
> only the per-generation offset maps remain on our side. See also the sibling
> [Intel plan](intel-pawnio.md); both share the PawnIO infrastructure.

## What we already have (no ring0)

The Ryzen Master SDK, via the elevated helper, gives: **die-average temperature** (~62 °C), package
power (**PPT/TDC/EDC**), per-core clock/temperature/**C0** residency, and the best-boosting cores. That
covers almost everything, HVCI-safe, with a signed AMD driver. This plan does **not** replace it.

## What only ring0 (the SMU) can add

| "Better" metric | Where it lives | Gain |
|---|---|---|
| **Tctl/Tdie** (the "CPU temperature" HWiNFO shows — the hotspot) | SMU thermal register (SMN) | ~+3 °C over our die-average; the number enthusiasts compare |
| **Frequency Limit — Global** (the dynamic boost ceiling that drops under all-core load) | SMU **PM_Table** | The real boost cap; we currently approximate it with the *achieved* peak |
| Finer per-core / effective clock, SoC voltage, FCLK, etc. | SMU PM_Table | Nice-to-have detail |

The SDK's `dTemperature` is the die-average, not Tctl; the SDK has **no** field for the global
frequency limit (measured empirically — see the project notes). Both require reading the SMU.

## The clean route: PawnIO (signed, HVCI-safe)

Same engine as the Intel plan: **[PawnIO](https://github.com/namazso/PawnIO)** — a signed kernel driver
that runs small user modules to do ring0 IO. No WinRing0, HVCI-safe, opt-in (the user installs PawnIO).
Two ring0 operations are needed, both within PawnIO's remit:

### 1. Tctl — SMN read via the northbridge indirect registers

AMD's SMU registers are reached through the PCI indirect pair on device `0:0.0`: write the 32-bit SMN
address to config register **0x60**, read the value from **0x64** (with a lock, since it's a shared
window). The thermal register `THM_TCON_CUR_TMP` (SMN `0x00059800` on Zen; the offset moved across
families) holds the current Tctl:

```
raw    = smn_read(0x00059800)
tctl   = ((raw >> 21) & 0x7FF) * 0.125        // 0.125 °C/LSB
// some SKUs report a +49 °C "tctl offset" (Threadripper/old X-parts) — subtract if the range bit is set
```

A PawnIO module exposes `smn_read(addr)` (do the 0x60/0x64 dance under a spinlock) and the C# side reads
Tctl each window. This alone closes the ~3 °C gap to HWiNFO.

### 2. PM_Table — SMU mailbox + physical-memory read

The rich metrics (the global frequency limit, effective clocks, SoC voltage, FCLK) live in the SMU's
**PM_Table**, a struct the SMU DMAs into system RAM. Reading it:

1. Ask the SMU for the table's physical address via its **mailbox** (MP1/RSMU): write the command +
   args to the mailbox SMN registers, poll the response. Commands and mailbox addresses are
   per-SMU-generation.
2. Once (the address is stable per boot), then each window **read physical memory** at that address and
   pull fields by offset.

```
addr   = smu_mailbox(CMD_GET_PM_TABLE_ADDR)      // per-generation command id
pm     = phys_read(addr, tableSize)              // PawnIO physical-memory read
limit  = pm[OFFSET_FREQ_LIMIT_GLOBAL]            // offset per PM_Table version
```

**This is the fragile part.** The PM_Table **layout and version differ by CPU family and even AGESA**
(Zen 2 / Zen 3 / Zen 4 / Zen 5 each have their own struct, and the offsets are reverse-engineered — this
is exactly what LibreHardwareMonitor and HWiNFO maintain by hand). Supporting it means a per-version
offset map and keeping up with new silicon. That is the whole reason the ROI is questionable.

## Architecture fit

Identical to the AMD SDK path: it lives in the **elevated helper**, gated by consent, publishing into
`EtwSnapshot`. **Mostly no contract change** — `TjMaxC`/`LimitMhz` fields already exist; Tctl would
override the die-average `CpuTempC` (or add a separate field), and the global frequency limit would fill
`CpuInfo.LimitMhz` (already in the contract, currently unused). The UI already has the labels.

Gate: an "advanced AMD sensors (PawnIO)" opt-in, alongside the AMD-SDK consent — only loads when PawnIO
is installed, else silently keeps the SDK-only behaviour.

## Reference: PawnIO C# interop

Same as the Intel plan (`PawnIo` over `PawnIOLib.dll`: `pawnio_open`/`load`/`execute`/`close`). The
module would export `ioctl_smn_read` (Tctl) and `ioctl_read_pm_table`; the helper reads Tctl each window
and re-reads the PM_Table address once per boot.

## Caveats / why it's low priority

- **Modest gains**: +~3 °C on temperature (and the project measured that the die-avg→Tctl gap under load
  is only ~1.5–3 °C and does **not** hide throttle), plus the boost-ceiling number (already approximated
  by the achieved peak). For most users the SDK data is enough.
- **PM_Table is a maintenance treadmill**: per-family, reverse-engineered offsets; breaks on new silicon
  until updated. This is the single biggest reason to hold off.
- **Opt-in PawnIO**: a separate signed driver install; keeps AMD-via-SDK as the frictionless default.
- **SMN window contention**: the 0x60/0x64 pair is shared; must lock, and play nice with anything else
  (Ryzen Master GUI) poking the SMU at the same time.

## Verdict

Viable and clean (HVCI-safe, PawnIO), and it would make the AMD readout match HWiNFO exactly. But it is
**premium-plus, low-ROI, high-maintenance** — build it only if users specifically ask for exact Tctl /
the live global boost ceiling, and be ready to maintain the PM_Table offsets per CPU generation. Until
then, the Ryzen Master SDK path (already shipped) is the right default.
