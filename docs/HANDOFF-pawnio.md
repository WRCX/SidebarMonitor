# Handoff — implement PawnIO CPU sensors (new session, on the AMD 7840HS laptop)

> **STATUS 2026-07-13 — steps 1 & 2 DONE on the 7840HS.** PawnIO was already installed (2.2.0);
> the interop + **AMD Tctl via SMN** shipped in **v1.3.0**: `PawnIoCpu.cs` in the
> helper loads the **signed `RyzenSMU.bin`** from `namazso/PawnIO.Modules` (release PawnIO only loads
> modules signed by that project — don't compile your own), reads `THM_TCON_CUR_TMP` under the
> `Global\Access_PCI` mutex, and publishes over `EtwSnapshot.CpuTempC` (`CpuPawnIoOk`, etw v10, snap
> v22). Gated by Settings → Diagnóstico → "Sensores CPU avanzados (PawnIO)". Verified live: 51–57 °C
> on this laptop where temp was "—". **Step 4 (AMD power) is ALSO DONE for Phoenix**: the PM_Table
> (version `0x4C0007`) was mapped empirically on this laptop (offsets + method in
> `docs/amd-advanced-pawnio.md`) and the helper now publishes package W, PPT/TDC % and the real
> 100 °C THM limit on laptops (etw v11; SDK stays authoritative on desktops). Verified live:
> ~34 W / 62 °C end-to-end. Remaining: **step 3** (Intel MSR, on the 7700K — see the updated note in
> `docs/intel-pawnio.md`), AMD PM_Table maps for other families (Strix etc.), and **installing a
> 1.3.0 MSI on this laptop** (the running install is 1.2.4, which predates the feature; flip the
> toggle after installing).

This file is the context carrier for a **new Claude Code session on a different machine** (the 7840HS
laptop). The prior work happened on the 7800X3D desktop; Claude's local memory there does **not** travel,
so everything you need is captured here + in the repo. On the laptop: `git clone
https://github.com/WRCX/SidebarMonitor`, then paste the starting prompt at the bottom.

## Why we're doing this

CPU **temp/power don't show on the 7840HS**. Root cause: the **AMD Ryzen Master Monitoring SDK is
desktop-Ryzen-only** — it doesn't read mobile APUs ("Phoenix" / 7040 series). So on any laptop the CPU
`temp/W` come up "—". Verify on the laptop with the debug overlay (Settings → Diagnóstico, or `--verbose`):
you'll see `vendor=AMD · helper✓ · SDK✗`. (The Radeon 780M iGPU **does** work via ADLX; only CPU is blank.)

**Reach argument (why this matters):** desktop-socketed Ryzen is a small slice of the market. Laptops
(AMD mobile + Intel) dominate, and the SDK covers none of their CPUs. **PawnIO** (a signed, HVCI-safe
kernel driver that runs small user modules for ring0 IO) is the only clean path to CPU temp/power on
**all x86** — Intel via MSR, AMD (mobile + desktop) via the SMU. One PawnIO layer unlocks ~the whole PC
market. This reframes PawnIO from "premium optional" to **the coverage feature**.

## The plan (already designed — read these)

- `docs/intel-pawnio.md` — Intel via MSR: temp `IA32_THERM_STATUS` (0x19C, bits 22:16 below Tjmax),
  Tjmax `MSR_TEMPERATURE_TARGET` (0x1A2), power via RAPL (`MSR_RAPL_POWER_UNIT` 0x606 +
  `MSR_PKG_ENERGY_STATUS` 0x611, power = ΔE·unit/Δt, 32-bit wrap). Pawn module exports `ioctl_read_msr`;
  C# `IntelMsr` reads per-core temp via thread affinity. XTU is NOT usable (no public SDK).
- `docs/amd-advanced-pawnio.md` — AMD via the SMU: **Tctl** through the northbridge PCI indirect pair
  (write SMN addr to config 0x60, read 0x64, under a lock), `THM_TCON_CUR_TMP` SMN 0x00059800,
  `tctl = ((raw>>21)&0x7FF)*0.125`. Power/limit live in the **PM_Table** (SMU mailbox → physical-memory
  read; per-generation offsets, fragile — do this last).
- `docs/AUDIT-2026-07-13.md` §"PawnIO readiness" — the **recommended seam**: do NOT add an
  `ISensorSource` interface. Mirror the existing `RyzenSdk` shape: a `PawnIoCpu` class
  (`TryOpen(out err)/TryRead(out data)/Dispose`), a `ConsentMarker.AmdAdvancedEnabled` gate polled each
  window, and **one override block right after the SDK block** in the helper's publish timer (later
  source overrides only the fields it owns: Tctl over die-average, and `CpuInfo.LimitMhz`). One contract
  field + an `EtwLayout.Version` bump. `LimitMhz` is already reserved for it.

### Suggested order (simplest/most-robust first)
1. **Get PawnIO interop working**: install PawnIO (signed) from https://github.com/namazso/PawnIO on the
   laptop; get `PawnIOLib.dll` P/Invoke going (`pawnio_open` / `pawnio_load` / `pawnio_execute` /
   `pawnio_close`); write a trivial Pawn module that reads ONE thing and prove it returns on the 7840HS.
2. **AMD Tctl via SMN** (0x60/0x64 → 0x00059800) — simplest AMD win, gives the CPU temperature the
   laptop is missing. Test: compare against HWiNFO's "CPU (Tctl/Tdie)".
3. **Intel temp via MSR** — for the Intel machine (the 7700K). Stable offsets.
4. **Power**: Intel RAPL (clean) and AMD PM_Table (fragile, per-gen — last).

### Where the code goes
The **elevated helper** (`src/SidebarMonitor.Etw`). New `PawnIoCpu.cs` mirroring `RyzenSdk.cs`; the
native side can be a small Pawn module (`.bin`) loaded by PawnIO (no C++ shim of ours needed — PawnIO
runs the module). Gate it behind a new consent marker, opened lazily like the AMD SDK. Fill the existing
`EtwSnapshot`/`Snapshot` CPU fields (temp/power/`LimitMhz`); bump `EtwLayout.Version` if you add a field.

## Hard constraints that DON'T travel via memory — obey these

- **Privacy (firm):** public identity is the handle **`WRCX`** only. **Never** use the user's real
  name/email anywhere (code, commits, docs). Git author email is the noreply
  `49656786+WRCX@users.noreply.github.com` (set in `git config`). Copyright/Author/Company = "WRCX".
- **Never take the user's credentials.** For GitHub, device-flow `gh auth login` (the user authorizes on
  their device). The user's UAC is set to elevate-without-prompting, so elevated installs run silently.
- **AMD's proprietary bits (and PawnIO's driver) never go in the public repo.** Gitignored + fetched by a
  `fetch.ps1`. Our own object code (shims, Pawn modules we write) is fine.
- **Versioning:** every fix/feature that ships in an MSI gets a **new** `Version`/`FileVersion` in
  `Directory.Build.props` (AssemblyVersion stays PINNED at 1.1.0.0) + a CHANGELOG entry. Current latest
  release is **v1.2.4**; `main` has internal refactors + tests in `[Unreleased]` → next release is 1.2.5.
- **Architecture:** 3 processes over a seqlock shared-memory contract (Snapshot v21 / EtwSnapshot v9).
  Agent = unelevated NativeAOT; **helper = elevated (logon scheduled task)** ← PawnIO lives here; UI = WPF.
  Read `docs/ARCHITECTURE.md`.
- **Deploy on the laptop for testing:** it has no dev deployment yet. Either run `installer/build.ps1`
  + install the MSI, or run the helper directly elevated for iteration. Test target = the 7840HS itself.

## Starting prompt for the new session (paste this)

> This is the SidebarMonitor repo on my 7840HS laptop (context in `docs/HANDOFF-pawnio.md`). Read that
> handoff plus `docs/intel-pawnio.md`, `docs/amd-advanced-pawnio.md`, `docs/ARCHITECTURE.md` and the
> PawnIO-readiness section of `docs/AUDIT-2026-07-13.md`. Goal: add **PawnIO-based CPU temp/power** so
> laptops (this AMD 7840HS, and Intel) get the CPU sensors the Ryzen Master SDK can't provide. Start by
> getting PawnIO installed + a minimal `PawnIOLib.dll` interop reading one MSR/SMN register on THIS
> machine, then wire an `PawnIoCpu` source (mirroring `RyzenSdk.cs`) into the elevated helper behind a
> consent gate. First concrete target: AMD **Tctl via SMN** so the CPU temperature shows on this laptop.
> Obey the hard constraints in the handoff (privacy = WRCX only, versioning, no proprietary bins in the
> repo). Let's begin with the PawnIO interop.
