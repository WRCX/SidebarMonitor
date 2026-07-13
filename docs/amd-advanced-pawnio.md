# AMD "advanced" CPU sensors via PawnIO (Tctl + SMU PM_Table) — design & plan

> **Status: parts 1 (Tctl) AND 2 (PM_Table power, Phoenix only) IMPLEMENTED and verified on a
> 7840HS, 2026-07-13.** The helper's `PawnIoCpu.cs` loads PawnIO's **signed `RyzenSMU.bin` module**
> (from `namazso/PawnIO.Modules` — release PawnIO only loads modules signed by that project, so we
> use theirs instead of compiling our own as originally planned), reads Tctl each window, and — on
> the one PM_Table version we've mapped (see below) — package power, PPT/TDC usage and the real
> THM limit. Gated by the "Sensores CPU avanzados" toggle (Settings → Diagnóstico →
> `ConsentMarker.AmdAdvancedEnabled`). The reach turned out to be bigger than the "+3 °C" framing
> below: **on mobile APUs the Ryzen Master SDK doesn't work at all**, so on laptops this is the
> *only* source for both temperature and power. See also the sibling [Intel plan](intel-pawnio.md);
> both share the PawnIO infrastructure.

## Empirical PM_Table map — Phoenix, version 0x4C0007 (mapped on the 7840HS, 2026-07-13)

Method: three `ioctl_update/read_pm_table` captures (idle → 16-thread load → idle) and a 40 s
sampled watch; fields identified by their limits' round values and how the values track load.
Floats, interleaved limit/value pairs:

| Float idx | Field | Evidence |
|---|---|---|
| [0]/[1] | **STAPM** limit/value (W) | 45.0 fixed; value 30→40.5 under load, slow filter |
| [2]/[3] | **fast PPT** limit/value (W) | 45.0 fixed; value saturates at ~44 against it under load |
| [4]/[5] | **slow PPT** limit/value (W) | 45.0 fixed; value lags the fast one (slower filter) |
| [8]/[9] | **VDD TDC** limit/value (A) | 70.0 fixed; 18→31 A under load |
| [16]/[17] | **THM** limit/value (°C) | 100.0 fixed → the real Tjmax for the UI's colour thresholds |
| [22]-[25] | STT skin limits/values (°C) | 80.0 fixed pairs |
| [28]/[29] | voltages (V) | 1.45 / 1.13 |
| [33]/[34]/[37]/[38] | current Tctl (°C) | tracks the SMN 0x59800 readout exactly |
| [47] | socket power (W) | **identical to [3] in every capture** — what we publish as package W |
| [48] | ~4.400 GHz, constant | **NOT the global boost limit**: stays 4.4 under 1-thread load (would rise toward 5.1 if it were the dynamic ceiling). Left unpublished; `LimitMhz` stays reserved |
| [49]-[58] | per-core freq limits (GHz) | 5.125 = the SKU's max boost |
| [256+] | DPM clock tables (MHz) | 1600/1440/1309/1200... states |

Integration rule (the audit's seam): PawnIO overrides only what it owns — **Tctl always**; the
power fields **only when the SDK isn't providing them** (`CpuSdkOk == 0`, i.e. laptops). Unknown
PM_Table version → power quietly off, Tctl still works. `EtwSnapshot.CpuPawnIoOk` is the bitmask
(bit 0 temp, bit 1 power).

### Multi-family coverage (added right after)

The header layout above is not Phoenix-specific. Cross-checking against the per-version tables
[RyzenAdj](https://github.com/FlyGoat/RyzenAdj) maintains (`lib/api.c`) showed our empirical map is
the **standard APU header**: STAPM [0]/[1] and fast/slow PPT [2..5] sit at fixed offsets in *every*
known version (RyzenAdj reads them unconditionally); only TDC and the THM limit move. Ported as
data into `PawnIoCpu.TryMapPmTable`:

| Family group | Versions | TDC lim/val | THM lim |
|---|---|---|---|
| Raven Ridge / Picasso / Dali (Zen1) | 0x1E0001-5, 0x1E000A, 0x1E0101 | [6]/[7] | — (slot holds a per-core temp; not trusted) |
| Renoir/Lucienne → Hawk Point | 0x370000-5, 0x400001-5, 0x450004-5, 0x4C0006-9 | [8]/[9] | [16] |
| Strix Point / Krackan Point | 0x5D0008/9/B, 0x650005 | [12]/[13] | [16] |

Anything else (e.g. 0x4C0003-5, Van Gogh 0x3F0000, Strix Halo 0x64020C) stays Tctl-only — we never
guess offsets. New versions arrive via the community flow: the **"Copy sensors diagnostics"**
button (Settings → Diagnostics) copies the PM_Table version + a dump of the table, and the
`.github/ISSUE_TEMPLATE/pm-table-support.yml` issue asks users to paste it. Map the header from the
dump (the limit/value pairs are obvious: round limits, values tracking load), add the version to
the switch, done.

Practical notes: the first `ioctl_update_pm_table` after resolve can bounce with SMU prereq/busy
(0x8007054F) — absorb it as a warm-up call, as `TryOpen` does. And never force-kill a process that
holds PawnIO mid-ioctl: the debug driver build leaks its internal lock (every later load fails
with ERROR_BUSY until reboot).

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
