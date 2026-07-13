# Changelog

All notable changes to SidebarMonitor are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Versions track `Version` / `FileVersion`
(`AssemblyVersion` is intentionally pinned and does **not** move per release).

## [Unreleased]

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
