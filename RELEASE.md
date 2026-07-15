# SidebarMonitor — Release Readiness & Platform Research

> Prepared over the night of 2026-07-11→12. Actionable checklist + SDK research
> (NVIDIA / Intel CPU / Intel GPU / AMD GPU) to decide on expansion. Tick the boxes as things close out.

---

## 0. TL;DR

- **Viable** as **open-source (MIT) + "buy me a coffee" + CV showcase project.** Niche: **Ryzen (+ NVIDIA)**. Not a business: donations = pennies.
- **A marketing hook handed to us by the market:** *"the Ryzen side monitor that STILL works after the WinRing0 apocalypse"*. The direct competitor, [Sidebar Diagnostics](https://github.com/ArcadeRenegade/SidebarDiagnostics), has been **broken** since March 2025 because it uses WinRing0 (Defender flags it as `VulnerableDriver`, HVCI blocks it — [issue #475](https://github.com/ArcadeRenegade/SidebarDiagnostics/issues/475)). We avoid it by design.
- **Legal settled** (Rubén's decision): **bundle the AMD DLL + accept its EULA on first run**, code stays **MIT**.

---

## 1. Legal — AMD Ryzen Master Monitoring SDK

Read the local EULA (`C:\Program Files\AMD\RyzenMasterMonitoringSDK\License.rtf`). Title: *"Software Evaluation License Agreement (Object Code Only)"*.

**What it allows (Sec. 2):** distributing the Software (DLL/driver, **object code only**) to third parties, provided **each recipient accepts AMD's EULA before using it** and the restrictions are met. Others do it (HWiNFO, OCMaestro).

**Restrictions that affect us (Sec. 3):**
- ❌ No modifying/creating derivatives of the DLL, no reverse-engineering, no removing copyright notices.
- ❌ **(3f) No using it in a way that would require licensing it under a "Free Software License"** (= copyleft: GPL/LGPL/AGPL). → **Our code must be MIT/BSD/Apache, NOT GPL.**
- You indemnify AMD (liability cap $100). US export rules. The "evaluation" framing = ⚠️ yellow flag if it's ever seriously monetised (confirm commercial use with AMD + a lawyer).

**Chosen path (bundle + first-run EULA):**
- [x] Include AMD's `License.rtf` in the installer/app. → `fetch.ps1` copies it from the SDK to `native/RyzenSdk/`.
- [x] **First-run dialog** that shows AMD's EULA and requires "I accept" before starting the helper/SDK (flag saved in config). Without accepting → the app works but without AMD SDK sensors (degrade to PDH). → `FirstRunDialog.cs` (AMD/Intel branch by CPUID); consent stored in `ui.json` + an `amd-sdk-consent` marker the elevated helper reads and applies hot.
- [x] `LICENSE` = **MIT** for our own code.
- [x] `THIRD-PARTY-NOTICES.md` with attribution: AMD Ryzen Master Monitoring SDK, Microsoft VC++ redistributables, NVIDIA NVML, Microsoft.Diagnostics.Tracing (TraceEvent), etc.
- [ ] The current `fetch.ps1` bundles the DLL **from the dev's install** → for release, document/script that the installer takes it from a valid source and NEVER put AMD's binaries in the public git repo (a `git clone` wouldn't meet the "accept the EULA" condition). The binaries ship only in the **installer**, not the repo.

---

## 2. Release checklist

### P0 — blockers (no release without these)
- [x] **AMD EULA on first run** + `License.rtf` included (see §1).
- [x] **`LICENSE` MIT** + **`THIRD-PARTY-NOTICES.md`**.
- [~] **Graceful degradation** — must NOT crash or look broken on: *(code ready and guarded; still needs validating on real hardware)*
  - [x] Intel CPU (no AMD SDK) → `CpuVendor` (CPUID) detects Intel; the helper does NOT open the AMD SDK; the UI shows "—" for temp/watts, and throttle/limits/best-core/star are hidden (gated by `TjMaxC>0`/`BestCore>=0`/`CpuFromAmd`); first run explains that ring0 is needed.
  - [x] No NVIDIA GPU (NVML absent) → GPU via D3DKMT/engines; the temp/W/VRAM row is omitted (`HasDetail=0`).
  - [x] No elevated helper → no ETW/per-core temp/C0, everything else live (`CpuFromAmd=false`, `BestCore=-1`).
  - [ ] Different core counts (4/6/12/16/24…), no SMT, multi-CCD. *(CSV and per-core rows are already dynamic by CoreCount)*
  - [ ] Multi-monitor, turning monitors off/on (already fixed), DPI 100/150/200%.
  - [ ] **Test on ≥1 Intel machine and ≥1 without NVIDIA** → **Rubén has a Ryzen mobile + RTX 3050 laptop and an Intel 7700K machine to test on.**
- [ ] **Code signing (Authenticode)** → avoids the SmartScreen scare. Free for OSS: [SignPath](https://signpath.io/) or [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/).
- [ ] **Name/brand**: verify "SidebarMonitor" doesn't collide (there's "Sidebar Diagnostics", "System Monitor II"…). Final icon + correct `AppUserModelID`.

### P1 — important for adoption
- [x] **English UI** (i18n; keep Spanish). It's a global market. *(done)*
- [x] **A real settings window** — `SettingsWindow.cs`, replaces the menu (trimmed to quick actions).
- [x] Our own **WiX-MSI installer** (`installer/`, WiX v5): 3 self-contained apps to Program Files (no .NET prerequisite), helper as an elevated scheduled task, UI in the Run key, shortcut, clean uninstall. AMD EULA is accepted in-app on first run (not in the MSI). + a **winget manifest** (`installer/winget/`, template with URL/hash to fill at release). Microsoft Store ruled out for now (the MSIX sandbox clashes with the elevated helper + scheduled task). *(pending: test the MSI in a VM; code signing)*
- [ ] **Auto-update** or at least a new-version notice from GitHub Releases (compare against `AppVersion`).
- [x] **README** with the anti-WinRing0 pitch, feature table, requirements and **screenshots** (hero + sections). In English (OSS standard). The technical deep-dive moved to `docs/ARCHITECTURE.md`. *(pending: demo GIF)*
- [x] **Landing** (GitHub Pages) → `docs/index.html` (self-contained, dark, anti-WinRing0 pitch + screenshots + features + requirements + download CTA). Enable at *Settings → Pages → Source: main / docs*. **Sponsor** button (GitHub Sponsors) in the footer. *(pending: optional custom domain; confirm the GitHub user in the links — currently placeholder `WRCX`)*
- [x] **No-telemetry policy**, stated explicitly → `PRIVACY.md` + highlighted in README/FAQ.
- [x] **CI** (GitHub Actions): `.github/workflows/ci.yml` (build on push/PR) + `release.yml` (tag `v*` → AOT + shims + MSI + SignPath signing + GitHub Release draft). The AOT build uses `windows-latest`'s C++ toolchain; the AMD SDK (not committable) is pulled from a private repo in CI. Signing optional (SignPath OSS). *(pending: onboard SignPath + the SDK private repo + test the pipeline on GitHub)*
- [x] **Docs**: how it works → `docs/ARCHITECTURE.md` (deep-dive) + `docs/FAQ.md` (expanded FAQ: AMD SDK, elevated helper, telemetry, Intel, HVCI, per-hardware matrix, config, language, 3 processes). README with "How it works" + FAQ.

### P2 — nice-to-have / future
- [ ] Portable mode (no install).
- [ ] More languages.
- [ ] Platform expansion (see §3).
- [x] ~~Logging / CSV export~~ → **done and in the free core** (menu *Diagnostics → Log to CSV*: 1 row/sample, 85 cols, dynamic per-core, to `%LOCALAPPDATA%\SidebarMonitor\logs`). + **verbose overlay** (*Diagnostics → Debug data*) and `--verbose`/`--csv` flags.
- [ ] Optional "Pro" tab if monetisation is pursued (extras: alerts, thresholds). *(logging/CSV is no longer Pro)*

---

## 3. SDK research — can we expand without the driver problem?

**Golden rule:** an SDK that **ships with the vendor's driver** (NVML, ADLX, IGCL) = **clean** (signed, HVCI-safe, no driver of our own to bundle). Reading MSRs/registers directly = **ring0** = the WinRing0 problem.

### 3.1 GPUs — a CLEAN, tri-vendor expansion ✅

| Vendor | SDK | Gives | Distribution |
|---|---|---|---|
| **NVIDIA** | **NVML** (already used) | adapter **and per-process** usage (`nvmlDeviceGetProcessUtilization`: gpu/mem/enc/dec %), temp (1 sensor), W, clocks, fan, VRAM | `nvml.dll` **ships with the driver** → no redistribution |
| **AMD** | **ADLX** ([GPUOpen](https://gpuopen.com/adlx/)) — *not used yet* | temp, **W**, fan, VRAM, clocks, tuning | the `.dll` **ships with the** Radeon **driver** → no redistribution |
| **Intel** | **IGCL** ([repo](https://github.com/intel/drivers.gpu.control-library), [docs](https://intel.github.io/drivers.gpu.control-library/)) | temp (core/mem/global), W, fan, freq, engines, memory | binaries **ship with the** Intel **driver**; headers on GitHub. 64-bit. On top of Level Zero Sysman |

- **NVAPI** (NVIDIA alternative): adapter only, but up to 3 thermal sensors. Also ships with the driver.
- **GPU conclusion:** full **NVIDIA + AMD + Intel** support is **feasible and clean**, all ships-with-driver. **A strong, differentiating advantage.** Today only NVIDIA is fully wired; AMD/Intel GPUs only have engines (D3DKMT).

### 3.2 Intel CPU — the HARD gap ⚠️

- **Intel has NO CPU-monitoring SDK equivalent to Ryzen Master.** There's no official consumer "signed driver that gives you temp/W".
- **Intel Power Gadget: DEPRECATED** (end of life Dec 2023).
- Intel's suggested replacement: **[Intel PCM](https://github.com/intel/pcm)** (open-source) — but it reads **MSR/PCI ⇒ needs a ring0 driver**. It gives *thermal headroom* (distance to Tjmax), not absolute temperature.
- Intel temperature = **DTS** via MSR `IA32_THERM_STATUS`. Power = **RAPL** MSR. Both = ring0.
- **The modern clean path: [PawnIO](https://poorlydocumented.com/2025/09/replacing-winring0-in-fan-control-with-pawnio/) + MSR modules** — a **signed, HVCI-safe, non-blocklisted** driver. **[CapFrameX](https://github.com/CXWorld/CapFrameX) already uses it** ("PawnIO wrapper for MSR and OC mailbox with updated Intel MSR IDs").
- **Intel CPU conclusion:** possible, **but with a PawnIO dependency** (separate install, more work, young ecosystem). NOT as clean as AMD (whose SDK is self-contained). **Reinforces the "Ryzen-first" positioning.**
- **AMD "advanced" (optional, not implemented): [`docs/amd-advanced-pawnio.md`](docs/amd-advanced-pawnio.md)** — exact Tctl (+~3° over the die average) + the SMU PM_Table's "Frequency Limit Global", via PawnIO (same infra as Intel). Low ROI / high maintenance (the PM_Table is reverse-engineered per CPU family); only if the market asks. The Ryzen Master SDK (already in production) is the right default.
- **Design plan + reference implementation (untested, no Intel HW): [`docs/intel-pawnio.md`](docs/intel-pawnio.md)** — a Pawn module (`ioctl_read_msr`), C# interop (`PawnIo`/`IntelMsr`), MSRs (IA32_THERM_STATUS 0x19C, MSR_TEMPERATURE_TARGET 0x1A2, RAPL 0x606/0x611), fit into the helper (no contract change) and consent flow (Intel branch of first run → install signed PawnIO). **XTU ruled out** (Intel gives no public SDK). Est.: ~1-2 days on a real Intel machine.

### 3.3 Summary matrix

| Component | Clean path (ships-with-driver / HVCI) | Own driver dependency? | Status |
|---|---|---|---|
| NVIDIA GPU | ✅ NVML | No (system dll) | **Done** |
| AMD GPU | ✅ ADLX | No (system dll) | **Done** (AdlxShim: temp/W/fan/clocks/VRAM) |
| Intel GPU | ✅ IGCL | No (system dll) | Pending |
| AMD CPU | ✅ Ryzen Master SDK | Yes, but **signed + HVCI-safe** (bundled) | **Done** |
| Intel CPU | ⚠️ PawnIO + MSR | Yes — PawnIO (signed, HVCI-safe, **separate install**) | No; big lift |

### 3.4 Expansion recommendation (by ROI)

1. ~~**ADLX** (AMD GPU: temp/W/fan)~~ — **DONE** (2026-07-12, `AdlxShim`): the Ryzen iGPU and any Radeon dGPU get temp/W/fan/clocks/VRAM with no new driver. Verified on the 7800X3D iGPU.
2. **IGCL** (Intel Arc) — medium effort, clean, opens the Intel-GPU market.
3. **NVML per-process** — polish "which process is using the GPU" on NVIDIA with `nvmlDeviceGetProcessUtilization` (more precise than the current PDH GPU Engine, which is the neutral path — keep PDH as a fallback).
4. **Intel CPU via PawnIO** — **only if the Intel market asks for it**. Bigger effort + a separate-driver dependency. Keep **AMD as the premium experience** ("built for Ryzen").

---

## 4. Monetisation (honest recap)

- **Pure donations** (GitHub Sponsors / Buy Me a Coffee): realistic **$0-500/month**, median **$0**. Coffees and goodwill, not a salary.
- **PPI / bundleware:** more money but **poisons the trust** of an enthusiast audience. **No.**
- **Least bad if chasing money:** a cheap listing on the **Microsoft Store** (convenience + auto-update) and/or a **"Pro"** with extras. Keep the core free/OSS.
- **Real ROI:** **reputation + portfolio** (NativeAOT, ETW, a SeqLock over shared memory, native interop, HVCI-compatible). Worth more than the donations.

---

## 5. Positioning (the pitch)

> **SidebarMonitor** — a **native, efficient** monitoring sidebar for Windows 11, **built for Ryzen + NVIDIA**, that **keeps working with Memory Integrity (HVCI)** enabled — where Sidebar Diagnostics and WinRing0-based tools broke in 2025. No HWiNFO, no dubious drivers: AMD's official SDK + native Windows APIs + NVML.

Where to announce it: r/AMD, r/overclocking, r/pcmasterrace, the thread of people who lost Sidebar Diagnostics, Ryzen forums.

---

## 6. Sources

- Sidebar Diagnostics (competitor): https://github.com/ArcadeRenegade/SidebarDiagnostics · WinRing0 issue: https://github.com/ArcadeRenegade/SidebarDiagnostics/issues/475
- WinRing0 blocklist: https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools
- AMD ADLX: https://gpuopen.com/adlx/ · repo: https://github.com/GPUOpen-LibrariesAndSDKs/ADLX
- Intel IGCL: https://github.com/intel/drivers.gpu.control-library · docs: https://intel.github.io/drivers.gpu.control-library/
- Intel PCM: https://github.com/intel/pcm · Power Gadget deprecated: https://github.com/mlco2/codecarbon/issues/457
- PawnIO (WinRing0 replacement): https://poorlydocumented.com/2025/09/replacing-winring0-in-fan-control-with-pawnio/ · CapFrameX: https://github.com/CXWorld/CapFrameX
- NVIDIA NVML: https://developer.nvidia.com/management-library-nvml
- OSS code signing: https://signpath.io/ · https://learn.microsoft.com/azure/trusted-signing/
- OSS monetisation: https://dev.to/thestackdeveloper01/50-real-ways-developers-can-earn-money-from-open-source-with-links-practical-tips-4k8o
