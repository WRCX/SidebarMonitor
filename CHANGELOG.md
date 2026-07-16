# Changelog

All notable changes to SidebarMonitor are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/). Versions track `Version` / `FileVersion`
(`AssemblyVersion` is intentionally pinned and does **not** move per release).

## [Unreleased]

## [1.4.9] — 2026-07-16

### Fixed

- **The in-app update never installed anything — it just opened the browser.** The updater looked for a
  release asset named exactly `SidebarMonitor.msi` (what CI produces), but every release cut by hand
  since 1.4.4 shipped as `SidebarMonitor-<version>.msi`. No asset matched, so `AssetUrl` stayed null and
  the apply path fell through to its "not an MSI install" fallback: the GitHub page opened and the user
  downloaded and ran the MSI themselves. Asset matching now accepts an optional `-<version>` suffix
  (`Updater.IsFlavorAsset`), while still refusing to feed a lite install the full MSI or vice versa. A
  `Category=Integration` test now asks the real latest release whether an asset matches, which is the
  only thing that could have caught this — the unit tests were pinning names we made up.
- **The ETW helper died seconds after the MSI installed it, and only came back at the next logon.** The
  installer writes a `helper-stop` marker to ask a running helper to quit cleanly, but nothing consumes
  it when no helper is live (a clean install, or an upgrade where `schtasks /End` already killed it).
  `RunHelperNow` then started the new helper, which read its own installer's stop request and exited.
  The helper now discards any leftover request once at startup, right after it wins the single-writer
  mutex — the point at which it is provably the only helper, so anything still there cannot have been
  meant for it (and no other instance's request can be stolen).
- **A helper that died in the session you were already in stayed dead.** Its triggers only covered
  *arriving* at a session (logon, unlock, reconnect), and nothing could revive it in place: the task is
  registered by SYSTEM, so an unelevated UI asking for `schtasks /Run` gets "access denied". The task now
  also repeats every minute, which `MultipleInstancesPolicy=IgnoreNew` makes free while the helper is
  healthy. The status line says "sin helper (reintentando…)" instead of telling you to launch it by hand.
  The helper now also refuses to start while a Windows Installer transaction is running (the
  `Global\_MSIExecute` mutex), which that watchdog makes necessary: it would otherwise start a helper in
  the middle of our own install, one minute after the installer had just stopped it, and a running
  helper maps its exe out of the folder being replaced — enough for a "files in use" prompt (naming
  whichever windows Windows Installer guesses, not the helper) and enough to strand a rollback. The
  installer cannot prevent this from its side: an unelevated action cannot disable a SYSTEM-registered
  task, so the helper is what yields.
- **Two Settings tooltips showed Spanish to English users** (the per-core bar's "what the bar measures"
  and the throttle indicator's). Both had had their Spanish text extended without updating the matching
  key in `LocStrings`, so the lookup silently fell back to the key itself. A new unit test now fails on
  any translation entry no `Loc.T` call matches — the shape of this bug — and on any translation whose
  `{0}`-style placeholders differ from its key, which would throw at runtime rather than merely read wrong.

### Changed

- **Pressing "Update" now runs the whole way through** — download, install, relaunch — instead of opening
  a confirmation box first. One UAC prompt from msiexec remains: a per-machine MSI cannot install without
  it. The warning naming other logged-in users is kept, since the update closes *their* sidebar too.
- **The automatic update check now actually repeats daily**, as the setting has always claimed. It only
  ever ran once, at window load, so a sidebar left running for weeks never checked again.
- **The installer's finish page offers to start SidebarMonitor and to see what's new.** Starting is
  ticked by default; the changelog is not, since it is a detour to a browser. A silent install (`/qn`,
  the automatic updater's path) skips the whole UI sequence and so shows neither.

## [1.4.8] — 2026-07-15

### Added

- **Laptop fan reading on HP gaming laptops (Victus/OMEN) via the HP WMI BIOS.** On these machines the
  fan speed isn't mirrored to an EC register, so the NBFC/`EcFan` map found nothing and the fan tile
  stayed on "—". A new read-only WMI reader (`WmiFan`) reads the live rpm (query `0x2D`) and the fan
  curve's top speed (query `0x2F`) for a duty %, so the CPU fan tile shows **"47% · 2700 rpm"** on HP
  gaming laptops (the % also feeds the existing "%vent" tile). No fan SET query is ever issued, so fan
  state is never changed; degrades to the EC path on non-HP machines. Verified on a Victus 16-s0xxx
  (board 8BD4). Contracts bumped: `Snapshot` v26, `EtwSnapshot` v16 (`CpuFanRpm`).
- **Per-core CPU temperatures on Ryzen mobile APUs (Phoenix)** via the SMU PM_Table. Per-core temps
  come from the AMD Ryzen Master SDK, which can't read mobile APUs — so laptops showed no per-core
  temperature. The Phoenix `0x4C0007` PM_Table exposes them at floats `[529..536]` (found empirically
  on a 7840HS: eight consecutive °C values that rise with load and converge with the package Tctl; not
  mapped by any public tool — `ryzen_monitor_ng`/`RyzenAdj` lack `0x4C0007`). The helper now fills the
  per-core temperature rows from there when the SDK isn't present. Kept to the verified `0x4C0007`
  only — its siblings stay unmapped rather than guessed. Enable via Settings → CPU graph → "Show
  temperature". No contract change (reuses `CpuCoreTempsC`).

### Changed

- **Configurable GPU sensor sampling (`--gpu-every`)** so the agent no longer wakes a sleeping dGPU
  every tick. NVML/ADLX `Fill` (temp/power/clocks/VRAM) wakes an Optimus dGPU (~12-18 ms/tick, spikes
  to ~366 ms); it now samples every Nth tick (Settings → Refresh → "GPU sensors", default 2 s), while
  the cheap PDH GPU-Engine load attribution keeps running every tick so the load graph stays smooth.

## [1.4.7] — 2026-07-14

Panel interaction fixes: resizing the sidebar no longer breaks the right-click menu or paints
artefacts along its edge. Plus the multi-user safeguards from the 1.4.6 line.

### Fixed

- **Resizing the panel is no longer done by Windows' modal sizing loop — and that fixes both of the
  panel's visual bugs at once.** They shared one root cause: `WS_THICKFRAME`, which we carried *only*
  so Windows would run that loop for us when the hit-test reported an edge. It cost us two things.
  The loop **takes the mouse capture and swallows the `WM_LBUTTONUP`** that ends the drag, so WPF's
  input stack stayed wedged believing the button was still down: after any resize, right-clicking
  opened the context menu and it **vanished again ~0.2 s later** (`Mouse.Synchronize()` was not
  enough — the only cure is never entering the loop). And `WS_THICKFRAME` makes **DWM paint its own
  window border**, which lightens whenever the panel looks active; the compositor draws it *outside*
  our client area, so no `WM_NCCALCSIZE` trick could ever hide it — hence the pale strip along the
  edge. The panel now resizes itself with ordinary WPF mouse capture (the same technique the floating
  drag already used, because this window never activates), keeping every constraint the old handler
  applied and adding a proper resize cursor. No frame, no modal loop, no border.

- **Clicking the collapsed sidebar's edge made it balloon — and silently destroyed your saved
  width.** Measured on a live panel: a mere mouse-down (no drag) on the 18 px strip's edge started
  Win32's modal sizing loop, `WM_SIZING` instantly clamped the width up to `MinPanelWidth`, and the
  strip jumped to **240×1440** for as long as the button was held. Releasing it then ran
  `OnPanelResized`, which persisted that phantom width — **636 → 240 in `ui.json`**, without the user
  ever dragging anything. A collapsed strip has a constant width, so it now offers **no resize grip
  at all** (hit-test returns `HTCLIENT`), and the resize handler refuses to persist a width measured
  while collapsed.
- **Resizing the docked panel left an edge artefact and a dead right-click.** Two independent causes,
  both measured: `WM_SIZING` forced the **full monitor** height (1440) while the panel is actually
  placed in the rect the shell grants the AppBar (**1392**, taskbar excluded), so every drag ended
  48 px taller than its own reservation — overhanging the taskbar with a stale reservation until
  something re-placed it. It now keeps the height it already has. And Win32's modal sizing loop
  swallows the `WM_LBUTTONUP` that ends the drag, so WPF still believed the left button was down and
  suppressed the context menu — right-click looked dead until you clicked elsewhere, which resynced
  it. The panel now drops the capture and resynchronises the mouse when the loop exits.

- **One sidebar per user session — no more.** The UI now takes a single-instance mutex and a second
  launch in the same session exits silently. Several paths now aim at the same UI (the HKLM Run key
  at logon, the new session task, the post-update relaunch, the Start-menu shortcut), and two UIs in
  one session fight: two AppBars each reserving desktop space, two agents (the second dies — same
  map, one writer), two tray icons, two writers to `ui.json`. The mutex lives in the **`Local\`**
  namespace, which is *per session*, so the guard is per session too: every Windows user still gets
  their own sidebar and the guard can never deny a legitimate user their instance.

### Added

- **Updating no longer pulls the sidebar out from under other logged-in users silently.** This is a
  per-machine install, so one machine runs one version (the single machine-wide helper owns the
  machine-unique kernel ETW session; two versions would mean a contract mismatch, i.e. no sensors for
  somebody). Updating therefore closes every other logged-in user's sidebar. Now: the confirm dialog
  **names them** ("Other users are logged in: …"), silent auto-install **stands down** whenever
  someone else is logged in (it is a convenience for a machine you have to yourself), and a new
  unelevated **"SidebarMonitor UI" task** (session connect/unlock triggers) hands their sidebar back
  the moment they return to their session, instead of making them log out and in. See
  `docs/multi-user.md`.

## [1.4.6] — 2026-07-14

### Fixed

- **The MSI no longer fights the running app to install itself** (the "it tried to close things 3-4
  times before it could install" report). Two of our own features were sabotaging Windows Installer:
  the UI **resurrects a missing agent** (crashed-agent recovery, ~2 s backoff), so when the Restart
  Manager killed the agent the UI revived it and the file was in use again — a retry loop; and the
  elevated, windowless helper cannot be closed politely by RM at all, only force-killed mid-write,
  taking its kernel ETW session down dirty. The MSI now stops the stack **itself**, before Windows
  even looks for locked files: it ends the helper's task (so its session triggers can't relaunch it
  mid-install), asks the helper to **shut down cooperatively** (a new stop-request file it polls each
  window — the only way an unelevated installer phase can close an elevated process), then kills the
  UI *before* the agent so nothing can resurrect it. A SYSTEM backstop force-kills any survivor —
  including **other logged-in users'** UI/agent, which also hold the binaries open on a multi-user
  machine. The Restart Manager is switched off: there is nothing left for it to find.
  `install.ps1` uses the same ordered, cooperative stop.

## [1.4.5] — 2026-07-14

Install/startup fixes on top of 1.4.4's multi-user rework, plus the first desktop PM_Table map.

### Fixed

- **"You must install or update .NET" after installing the MSI on a machine without the .NET 10
  runtime.** The MSI's own apps are self-contained and were never the problem: a *pre-1.4.4
  per-user* install (`install.ps1` published framework-dependent into `%LOCALAPPDATA%` and pointed
  **HKCU** Run at it) kept autostarting alongside the new per-machine one and died on the missing
  runtime. The MSI now purges that stale copy (HKCU Run value + `%LOCALAPPDATA%\SidebarMonitor\app`)
  on install, and **`install.ps1` publishes self-contained by default** — a per-machine install must
  never depend on a runtime being present (`-FrameworkDependent` opts back in for dev iteration).
  `tools/fix-legacy-peruser-install.ps1` cleans an already-broken machine without reinstalling.

- **A stale `ui.json.bak` can no longer wipe the machine's sensor opt-ins.** `UiConfig.Load` falls
  back to the backup when the main config is briefly unreadable; the UI's startup marker sync then
  saw stale `false` toggles and DELETED the machine-wide consent markers (helper closed PawnIO /
  SDK on the next window). The startup sync is now create-only — revoking a consent is exclusively
  the explicit Settings toggle's job.

### Added

- **Live global frequency limit + real per-core clocks on Raphael (desktop Zen 4)** via PawnIO:
  PM_Table version **0x540104** mapped empirically on a 7800X3D (idle/all-core/1-thread diffing).
  The boost line now shows the SMU's dynamic ceiling — `boost 4.62 / 4.65 GHz (límite SMU)` under
  all-core load, `/ 5.05` at idle — instead of approximating it with the session peak, and "GHz
  máx" can draw from the table's per-core clocks ([317..324]). Power fields (PPT [2]/[3],
  TDC [8]/[9], THM [10] = the X3D's real 89 °C Tjmax) are mapped too, for SKUs where the Ryzen
  Master SDK is absent (Dragon Range laptops report the same family). Other Raphael table
  versions stay unguessed — the community dump flow covers them.

## [1.4.4] — 2026-07-14


Multi-user rework: one machine-wide helper serving every Windows session, plus install/startup
hardening. See `docs/multi-user.md` for the full design.

### Fixed

- **Multiple Windows users at once no longer break the stack.** The elevated helper's shared map
  moved `Local\` → **`Global\`** (with an explicit Users-read DACL), so ONE helper serves every
  session — before, each user's UI could only see a helper started in its own session ("sin
  helper" for the second user), and a second helper would silently hijack the first one's
  NT Kernel Logger session (Windows allows exactly one machine-wide). The helper task now
  triggers on any user's logon **and** on session connect/unlock, so when the user who started it
  logs off, it respawns in whichever session becomes active; `IgnoreNew` keeps it single.
- **Helper no longer dies silently when launched by its scheduled task.** Two independent causes:
  the task now launches the helper via `conhost --headless` instead of `wscript`+VBS (script hosts
  spawned by the Task Scheduler trip some AV heuristics), and `install.ps1` now installs
  per-machine into **Program Files** — an unsigned exe in a user-writable folder, started elevated
  by a scheduled task, matches a malware-persistence pattern that Bitdefender kills on sight
  (verified live). On startup the helper also retries its map for up to 30 s instead of exiting
  instantly when a reader still holds a dead predecessor's map (the silent `LastTaskResult=1`).
- Consent markers (AMD EULA, PawnIO opt-ins, FPS) moved to **ProgramData** (machine-wide, matching
  the single machine-wide helper), with a Users-modify ACL and automatic migration from the old
  per-user location on the helper's first elevated run.

### Added

- **Contract plumbing for the SMU's live frequency limit and effective clocks** (PawnIO PM_Table):
  `EtwSnapshot` v15 adds `CpuLimitMhz` + a clocks bit; the UI shows the real SMU boost ceiling as
  the boost line's denominator (`límite SMU`) when it flows. Inert until a PM_Table version maps
  the offsets — the Raphael (desktop Zen 4) map lands with the ongoing capture work.

## [1.4.3] — 2026-07-14

### Added

- **Core voltage (VID) on Intel** — the "Mostrar VID (voltaje)" line now works on Intel too, read from
  `IA32_PERF_STATUS` (0x198) bits [47:32] / 8192. Reliable on Sandy Bridge → Broadwell (verified
  ~1.04 V on an i7-4700HQ); Skylake onward moved the voltage regulator and no longer exposes a clean
  Vcore there, so the value is range-guarded (0.2–1.6 V) and shows "—" when implausible. Best-effort,
  like the other per-generation Intel bits.

### Fixed

- **Agent no longer crashes when a network adapter disappears mid-sample.** If an interface went away
  between the periodic rescan and a per-tick read (VPN torn down, dock unplugged, USB NIC pulled),
  `GetIPStatistics` threw an uncaught `NetworkInformationException` (from `GetIfEntry2`) and took the
  whole agent process down. `Stats` now swallows that exception, `FillNics` skips the vanished
  adapter and forces an interface rescan on the next tick.
- **UI now relaunches a crashed agent instead of showing "agente parado" forever.** When the agent
  died it left its shared-memory map behind with a frozen timestamp; the reader stayed bound to the
  dead map and the status line counted up indefinitely. On a stale snapshot the UI now drops the
  stale reader and relaunches the agent, throttled by an exponential backoff (2→30 s) so a
  crash-on-startup can't spawn a process every tick.

## [1.4.2] — 2026-07-14

### Changed

- **Per-core row colouring is now its own setting, decoupled from the main graph.** Choosing the main
  CPU graph (Total / overlaid / grid) used to *silently* recolour the per-core rows (by process on
  Total, by core otherwise), which made the "Barra por núcleo" mode (C0 / combined / usage+tick) look
  like it only worked on some graphs. New **"Colorear barras"** option under CPU settings — **Auto**
  (old behaviour: follow the graph), **Por proceso**, or **Por núcleo** — so each control does exactly
  one thing and every bar mode works identically under any graph.

## [1.4.1] — 2026-07-14

Polish and fixes on top of the 1.4.0 work. No MSI was cut for 1.4.0, so 1.4.1 is the first release and
includes all of it.

### Changed

- **CPU and GPU model names now show in their section titles by default** (`CpuNameMode` /
  `GpuNameMode` default to 1) — the first thing people look for, at no vertical cost.
- `install.ps1` is Intel-friendly: it resolves `dotnet` even when it's not on PATH, skips the AMD
  RyzenShim/SDK steps when the C++ toolchain/SDK is absent (Intel-only machines install fine), and
  falls back to a non-AOT agent publish when the NativeAOT C++ linker isn't available.

### Fixed

- **Core colour palettes now use their full range regardless of core count.** Every preset was
  hardwired to a 16-core spread, so an 8-core rainbow only reached red→green (half the wheel) while a
  32-core one wrapped and repeated. The hue is now spread across the machine's actual core count, so
  8-, 16- and 32-core systems all get the complete rainbow (and the full cool/warm/pastel bands),
  evenly divided. The high-contrast (golden-angle) preset was already count-independent.
- **Dead right-click + a painted edge strip after resizing/re-placing the panel.** The borderless
  AppBar only re-asserted its client area (`WM_NCCALCSIZE` via `SWP_FRAMECHANGED`) at creation, so a
  later placement — e.g. dragging the panel wider — could let the `WS_THICKFRAME` sizing border
  return, re-inset the client, and offset every hit-test (right-clicks landed on the wrong element and
  the context menu never opened). `SWP_FRAMECHANGED` is now applied on every re-placement and right
  after a drag-resize, so no path can leave the frame in that state.

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
  - **Per-core C0 (active) residency on Intel**, so the combined core bars and the "sleep" marking of
    parked cores — previously AMD-SDK-only, and silent no-ops on Intel — now work there. C0% is
    `ΔMPERF / (baseHz × Δt)` (MPERF advances at the base frequency only in C0; the module's TSC read is
    unusable, so wall-clock Δt stands in — verified 98% on a pinned core vs ~10% idle). No best-core
    "star" on Intel, by design: a favored core is a Turbo-Boost-Max-3.0 feature (Skylake/Broadwell-E
    onward, some SKUs only); on older/mainstream parts all cores share the same turbo bins, so a star
    would mark an arbitrary core. AMD keeps it (CPPC preferred cores are real on every modern Ryzen).
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

[Unreleased]: https://github.com/WRCX/SidebarMonitor/compare/v1.4.7...HEAD
[1.4.7]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.4.7
[1.4.6]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.4.6
[1.4.5]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.4.5
[1.4.4]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.4.4
[1.4.3]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.4.3
[1.3.0]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.3.0
[1.2.4]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.4
[1.2.3]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.3
[1.2.2]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.2
[1.2.1]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.1
[1.2.0]: https://github.com/WRCX/SidebarMonitor/releases/tag/v1.2.0
