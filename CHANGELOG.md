# Changelog

All notable changes to SidebarMonitor are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Versions track `Version` / `FileVersion`
(`AssemblyVersion` is intentionally pinned and does **not** move per release).

## [Unreleased]

## [1.4.0] — 2026-07-13

CPU sensors for **Intel** laptops/desktops via PawnIO (temperature, RAPL power, real per-core boost
clock, throttle/limits) and **laptop fan %** via the embedded controller — the coverage half that the
AMD-only paths couldn't reach. Plus wider AMD PM_Table generation support. All opt-in and HVCI-safe;
Intel verified on an i7-4700HQ, fan verified on an ASUS N56JR.

### Added

- **PM_Table power on (almost) every Ryzen APU generation**, not just Phoenix: the per-version
  offset maps for Raven Ridge/Picasso/Dali, Renoir/Lucienne, Cezanne, Rembrandt, Hawk Point and
  Strix/Krackan Point were ported (as data) from the tables the
  [RyzenAdj](https://github.com/FlyGoat/RyzenAdj) community maintains — which independently agree
  with the map we derived live on the 7840HS. Unknown versions still degrade to Tctl-only, never
  guessing offsets. (On Zen1 APUs the THM-limit slot is not trustworthy, so those keep the generic
  temperature thresholds.)
- **"Copy sensors diagnostics" button** (Settings → Diagnostics, AMD): copies a plain-text report —
  sensor states, PM_Table version and a dump of the table — for the new **"CPU power support"
  GitHub issue template**. This is the community path for mapping new CPU generations: explicit,
  manual, nothing is ever sent automatically (see PRIVACY.md). The unelevated UI asks the elevated
  helper for the dump through a request file next to the consent markers.
- Debug overlay now shows the PM_Table version (`PawnIO✓ PM:0x4C0007`).
  Contracts bumped: `Snapshot` v23, `EtwSnapshot` v12 (`CpuPmTableVersion`).
- **CPU sensors on Intel via PawnIO** — the coverage other half of the laptop story. Intel ships no
  monitoring SDK, so an opt-in "CPU sensors (PawnIO)" toggle (Settings → Diagnostics, Intel only)
  makes the elevated helper load PawnIO's signed **IntelMSR** module and read **per-core temperature**
  (`IA32_THERM_STATUS` 0x19C = Tjmax − readout, Tjmax from `MSR_TEMPERATURE_TARGET` 0x1A2) plus
  **package power** from **RAPL** (`MSR_RAPL_POWER_UNIT` 0x606 × Δ`MSR_PKG_ENERGY_STATUS` 0x611 / Δt,
  32-bit wrap-safe). Per-core reads pin the reading thread to each logical core; no PCI mutex is
  needed (MSR reads don't share a config window like the AMD SMU). Requires
  [PawnIO](https://pawnio.eu) installed; without it the toggle degrades softly to PDH-only. Same
  fetched `IntelMSR.bin` (LGPL-2.1, `namazso/PawnIO.Modules`); PawnIO's driver is never
  redistributed. **Verified live on an i7-4700HQ (Haswell)**: 71 → 81 °C and 23 → 41 W across idle
  and all-core load, end-to-end through the helper→agent→UI contract. Contracts bumped: `Snapshot`
  v24 (`CpuFromIntel`), `EtwSnapshot` v13 (`CpuIntelOk` bitmask).
  - **Real per-core boost clock on Intel** via `IA32_APERF`/`IA32_MPERF` × the base ratio from
    `MSR_PLATFORM_INFO` — the achieved turbo (verified ~3.2 GHz all-core / ~3.0 idle on the 4700HQ,
    matching `MSR_TURBO_RATIO_LIMIT`), which PDH's averaged `% Processor Performance` smooths away.
    The "mejor núcleo" boost label now applies on Intel too.
  - **Real throttle/limits on Intel**, replacing the AMD-only heuristic: the active binding cap comes
    straight from the `IA32_THERM_STATUS`/`IA32_PACKAGE_THERM_STATUS` status bits (thermal / power /
    current), and package power is shown as a % of **PL1** (`MSR_PKG_POWER_LIMIT`) — the Intel
    analogue of AMD's PPT% (verified 76% under load / 9% idle against the 47 W TDP). The throttle
    indicator (POT/CORR/TÉRM) and the limits line now work on Intel.
  - **Fan speed tile, fixed for every platform**, next to the temperature — shows the fan **duty %**
    where the laptop is supported and **"—"** otherwise. Contracts bumped again: `Snapshot` v25
    (`ThrottleFlags`, `FanPct`), `EtwSnapshot` v14 (`CpuThrottleFlags`, `CpuFanPct`).
- **Laptop fan monitoring via PawnIO (embedded controller)** — a new opt-in "Ventilador vía PawnIO
  (experimental)" toggle (Settings → Diagnostics, every machine) makes the elevated helper load
  PawnIO's signed **LpcACPIEC** module and read the fan level straight from the ACPI **embedded
  controller** (the standard 0x66/0x62 read handshake, serialized on `Global\Access_EC`, read-only —
  the fan is never written). Which EC register holds the fan level, and its range, is per laptop
  model: that mapping is a **fact table of 305 models derived from NoteBook FanControl** (nbfc-linux,
  GPL-3.0) — `FanDb.tsv`, embedded — matched against the machine's DMI model string. Unlisted model =
  "—". **Verified live on an ASUS N56JR** (EC register 0x97, level 0..8): 25% idle → 62% under load,
  end-to-end. Vendor-agnostic (AMD + Intel laptops). Best-effort and flagged as such: a community map
  can point at the wrong register on an unverified model. Needs [PawnIO](https://pawnio.eu) installed;
  `LpcACPIEC.bin` is fetched by `native/PawnIO/fetch.ps1` alongside the other modules.

## [1.3.0] — 2026-07-13

CPU sensors for Ryzen laptops via PawnIO: temperature (Tctl) and package power on mobile APUs,
which the Ryzen Master SDK cannot read. Opt-in, HVCI-safe, verified on a 7840HS (Phoenix).

### Added

- **CPU temperature on Ryzen laptops** (and exact Tctl on desktop) via **PawnIO**: an opt-in
  "Advanced CPU sensors" toggle (Settings → Diagnostics, AMD only) makes the elevated helper load
  PawnIO's signed **RyzenSMU** module and read **Tctl** straight from the SMU's thermal register
  (SMN `0x00059800`, serialized on the conventional `Global\Access_PCI` mutex). This is the first
  CPU-temperature source that works on mobile APUs (Phoenix/7040 etc.), which the Ryzen Master
  Monitoring SDK cannot read; on desktop it replaces the SDK's die-average with the hotspot HWiNFO
  shows. Requires [PawnIO](https://pawnio.eu) installed (a signed, HVCI-safe driver); without it the
  toggle degrades softly to the previous behaviour. The module binary (LGPL-2.1, from
  `namazso/PawnIO.Modules`) is fetched by `native/PawnIO/fetch.ps1` and ships with its license text;
  PawnIO's driver itself is never redistributed. Verified on a 7840HS against the live SMU readout.
  Contracts bumped: `Snapshot` v22 (`CpuFromPawnIo` for the debug overlay), `EtwSnapshot` v11
  (`CpuPawnIoOk` bitmask).
- **CPU package power on Ryzen laptops** via the same PawnIO path: the SMU's **PM_Table**
  (`ioctl_resolve/update/read_pm_table` on the signed module) provides package watts, PPT/TDC usage
  and the real 100 °C throttle limit — so the temperature colour thresholds and the "Límites" row
  now work on laptops too. Only on the PM_Table layout validated empirically on the 7840HS
  (Phoenix, version `0x4C0007`, map documented in `docs/amd-advanced-pawnio.md`); any other version
  degrades to Tctl-only. On desktops with the Ryzen Master SDK the SDK's power figures stay
  authoritative — PawnIO only ever overrides the fields it owns.

### Maintainability (no behaviour change)

- **`MainWindow.cs` split from ~1600 lines to ~690** — the update code (`MainWindow.Updates.cs`),
  window placement + drag (`MainWindow.Placement.cs`), section-building + layout helpers
  (`MainWindow.Build.cs`), the context-menu builder (`MainWindow.Menu.cs`), and the section-state /
  Settings-facade / test hooks (`MainWindow.Settings.cs`) each moved into a `partial` file. The main
  file now holds just the constructor/setup and the `Tick` hot path. Pure code movement, no logic change.
- Removed confirmed-dead members (`ProcessNames.OnStop`, `ServiceMap.Services`, `RamInfo.Loaded`);
  made `TestFakeFps` an `init` property.
- Deduplicated the `IntArg` CLI helper (was copied verbatim in all three entry points) into
  `Shared.ArgParse.Int`; named the duplicated CPU-temperature thresholds (`Theme.TempHotMarginC` etc.)
  so the whole-CPU and per-core severity checks share one source.

## [1.2.4] — 2026-07-13

Security, correctness and performance hardening from a full audit, plus a maintainability refactor. No
user-facing feature changes.

### Security

- Updater validates the download URL is **HTTPS from a GitHub host**, and derives the local MSI filename
  from a fixed constant rather than the (attacker-influenceable) release URL.

### Fixed

- **Helper**: the publish timer is now **non-reentrant** — an overlong callback (e.g. a slow WMI query)
  can no longer let two threads race the single-writer seqlock and tear the published snapshot.
- **CPUs with >16 physical cores** (Threadripper/EPYC): per-core temperature/C0 mapping is clamped to the
  16-wide array — no out-of-bounds read.
- **Updater**: a reentrancy guard prevents a double-apply or a concurrent check nulling the pending
  release mid-install; pre-release tags (`v1.2.3-rc1`) now parse; the download body has a timeout; a
  glitch >6 GHz clock read no longer pins the session boost peak.
- **Config**: `ui.json` is written **atomically** (temp file + swap) with a `.bak` fallback, so a crash
  mid-write can't silently reset every setting.
- **CSV logging**: the header waits for a populated core count (no zero-core columns); a NIC counter wrap
  no longer produces a negative-rate spike.
- GPU detail fields show "—" instead of "NaN" when a sensor is unsupported.

### Performance

- O(1) section lookup (was a LINQ scan ~18×/tick); cached frozen pens and typefaces in the chart
  renderers instead of rebuilding them every frame.

### Maintainability

- Split the update code out of `MainWindow.cs` into a partial file; centralized the version-string
  format.

## [1.2.3] — 2026-07-13

### Changed

- **In-app update UX.** The default *Update now* flow now shows a **confirmation dialog** that reassures
  nothing is lost — your settings, layout and history live in `%LOCALAPPDATA%` and the installer never
  touches them — and then gives **live progress** (Downloading %… → Installing…) in the Updates panel
  before the app closes and relaunches on the new version. The silent, zero-friction auto-install from
  1.2.2 stays available but is clearly the **opt-in, non-default** choice (Settings → Updates).

### Added

- Download-progress percentage reported during an in-app update.

## [1.2.2] — 2026-07-13

### Added

- **Zero-friction automatic updates** — new *Install automatically (silent)* option (Settings →
  Updates). When on, the app detects a newer release on startup and downloads + installs it silently
  (`msiexec /qn` — no notification, no dialog, no progress window, no browser) and relaunches the
  panel. Opt-in; truly hands-off on machines where elevation is silent (UAC off / set to elevate
  without prompting). The previous notify-and-click flow remains the default.

## [1.2.1] — 2026-07-13

Maintenance release. No functional changes versus the 1.2.0 release build — it exists as a distinct
version so the **in-app auto-updater** can deliver the fixes below to installs still on an earlier
1.2.0 build (the updater only offers a strictly higher version).

### Fixed (carried from 1.2.0, now updater-deliverable)

- **Installer autostart** — UI Run key moved from HKCU to HKLM so a per-machine (SYSTEM) MSI writes it
  to the right place and the UI autostarts at logon.
- **Borderless window frame / right-click** — force `SWP_FRAMECHANGED` after `WS_THICKFRAME` so the
  frame commits (no stray border strip, hit-tests aligned, right-click/context-menu work).

## [1.2.0] — 2026-07-13

First public release.

### Features

- **HVCI-safe native sidebar** — ships no kernel driver of its own, so it keeps working with Memory
  Integrity / Core Isolation enabled (where WinRing0-based monitors broke). CPU, per-core, GPU, memory,
  network, disks, processes and game FPS in one lean panel.
- **Deep Ryzen telemetry** via the AMD Ryzen Master Monitoring SDK — per-core frequency, temperature and
  C0/sleep residency; best-core marker; power caps (PPT/TDC/EDC); throttle state.
- **GPU: NVIDIA (NVML) + AMD (ADLX)** full telemetry, plus per-engine activity for any GPU via D3DKMT;
  multi-adapter aware.
- **Game FPS** — FPS, frametime, 1% and 0.1% lows via Intel's PresentMon (ETW, no injection, no
  anti-cheat trouble).
- **Native & lightweight** — NativeAOT sampling agent (~2 MB); three-process architecture
  (unelevated agent + optional elevated helper + WPF panel) over lock-free shared memory.
- Settings window; English/Spanish UI; per-section refresh; docked/floating/resizable placement;
  RAM module info (model/type/speed via WMI); CSV logging; first-run consent (AMD SDK EULA on AMD
  systems / Intel ring0 notice); graceful degradation on Intel / no-NVIDIA / no-SDK.
- **WiX MSI installer** in two flavours — **full** (bundles AMD's sensor SDK, works offline) and
  **lite** (ships no AMD binaries; uses the SDK you install yourself).
- **Opt-in auto-update** (default off) against the GitHub Releases API; **no telemetry**.

### Fixed

- **Installer autostart** — the UI's Run key was written under HKCU, but a per-machine MSI is installed
  by the msiexec SYSTEM service, so it landed in SYSTEM's profile and the UI never autostarted for the
  user. Moved to **HKLM Run** (written correctly by SYSTEM, autostarts for every user at logon).
- **Borderless window frame / right-click** — `WS_THICKFRAME` was added without the `SWP_FRAMECHANGED`
  that Win32 requires to commit a frame-style change, so after rapid relaunch/re-placement the thick
  sizing border could stay painted (a visible edge strip) and the client area stayed inset, offsetting
  every hit-test (right-click landed wrong / the context menu wouldn't open). The frame recalc is now
  forced at init.

> Not code-signed yet, so Windows SmartScreen may warn on first run — choose **More info → Run anyway**.

[Unreleased]: https://github.com/WRCX/SidebarMonitor/compare/v1.2.4...HEAD
[1.2.4]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.4
[1.2.3]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.3
[1.2.2]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.2
[1.2.1]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.1
[1.2.0]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.0
