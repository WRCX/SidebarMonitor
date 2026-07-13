# Test plan — SidebarMonitor

Guiding idea: this is a native, hardware-touching monitor, so a lot of it (PDH, ETW, NVML, ADLX, the
AMD SDK, D3DKMT) **can't** be unit-tested without the hardware. So the strategy is a **fat base of fast
unit tests on the pure logic that's easy to get subtly wrong** (updater, config, seqlock, contract
layout, parsing, thresholds), a **thin layer of integration tests** on the process seams, and a **small
e2e set driven by the debug hooks that already exist** (`Client --freshness`, `UI --shot/--frames`,
`msiexec`). Don't chase coverage of the sensor plumbing — cover its *graceful degradation* instead.

Status: **bootstrapped.** `tests/SidebarMonitor.Tests` (xUnit) exists with **39 passing unit tests**
covering `Updater` (IsNewer / TryParseTag / IsTrustedGitHubUrl / Format — incl. the host-confusion
cases), `ArgParse.Int`, `NameField` (UTF-8 roundtrip / truncation / NUL bound), and `SeqLock`
(roundtrip / version + signature rejection / single-writer). CI runs `--filter Category=Unit` on every
push. The rest of this plan is the roadmap for the remaining coverage, in priority order.

---

## 0. Infrastructure

- New project **`tests/SidebarMonitor.Tests`** — xUnit, `net10.0-windows` (so it can reference the WPF
  UI project). References `SidebarMonitor.Shared` + `SidebarMonitor.UI`; references `.Agent`/`.Etw` only
  for integration tests.
- Add `[assembly: InternalsVisibleTo("SidebarMonitor.Tests")]` to **Shared, UI, Agent, Etw** (their key
  types — `Updater`, `UiConfig`, `FrameStats`, `PdhQuery` — are `internal`).
- **Categories via `[Trait("Category", …)]`:** `Unit` (pure, always run), `Integration` (needs the OS /
  maybe admin), `E2E` (needs the built exes or a VM). CI runs `dotnet test --filter Category=Unit`.
- Wire `Category=Unit` into `.github/workflows/ci.yml` (no hardware needed → runs on every push).
- Golden PNGs for render tests under `tests/goldens/`, compared with a perceptual tolerance.

---

## 1. Unit tests (fast, no OS/hardware) — the priority

### Shared
| Target | Cases |
|---|---|
| **`SeqLock`** | writer→reader roundtrip preserves the struct; reader `TryOpen` rejects wrong signature / version / payload-size; **torn read**: a concurrent writer mid-`Publish` makes the reader retry and eventually succeed (or return false after 128 tries); a second `SeqLockWriter` on the same name throws; `AbandonedMutexException` → new writer takes over. |
| **`NameField`** | Set/Get UTF-8 roundtrip; truncation at 32/64/160; non-ASCII (`é ° ★ ↓`); empty string; exactly-full field with **no NUL** → `Get` stays in-bounds. |
| **`ArgParse.Int`** | present / absent / non-numeric / empty value; ordinal prefix; negative; first-wins with duplicate flags. |
| **`ServiceMap`** | `Label` formatting (`svc` vs `svc+N`); the x64 struct constants (elem 56, `dwProcessId@44`) as a golden assertion. |
| **`ConsentMarker`** | Set → marker present → cleared, against a temp `%LOCALAPPDATA%` (inject the base path). |
| **`CpuVendor`** | brand-string assembly + Amd/Intel/Unknown mapping — **needs** splitting the string parse from the `CpuId` call first (see §4). |

### UI
| Target | Cases |
|---|---|
| **`Updater.IsNewer`** | 1.2.0<1.2.1; equal→false; 1.3.0>1.2.9; revision ignored; build clamp. |
| **`Updater.TryParseTag`** | `v1.2.3`, `1.2.3`, **`v1.2.3-rc1` → strips suffix**, `v1.2`, `vX.Y`, empty → false. |
| **`Updater.IsTrustedGitHubUrl`** | `https://github.com/…` ✓, `https://objects.githubusercontent.com/…` ✓, `http://github.com` ✗, `https://evil.com` ✗, `null` ✗, **`https://github.com.evil.com` ✗**, **`https://notgithub.com` ✗** (verifies the `.github.com` suffix check isn't fooled). |
| **`Updater.Format`** | `v{M}.{m}.{max(0,build)}`. |
| **`UiConfig`** | full JSON roundtrip; **`Save` is atomic** (temp then `File.Replace`, `.bak` kept); **`Load` falls back to `.bak`** when the main file is corrupt; corrupt main **and** bak → defaults; old bare-`{"cpu":2}` map → defaults; **`Ephemeral` → `Save` is a no-op**. |
| **`ShortCpuName` / `ShortGpuName`** | the documented transforms (`…7800X3D 8-Core Processor`→`Ryzen 7 7800X3D`, etc.). |
| **`TempLevel`** (Theme thresholds) | boundaries at `Tjmax-4`→2, `Tjmax-12`→1, between→0; `Tjmax=0`→fallback 90/80; `NaN`→0. (Guards the just-centralized `Theme.TempHotMarginC` etc.) |
| **`FrameStats.ParseRow`** | header-driven mapping; missing/reordered columns; `<10 fps` filtered; `AnimationError`/latency `NaN`; foreground PID `-1`. **Needs** making `ParseRow` a pure static (see §4). |
| **`GuestCollectors`** | `ParsePct("12.3%")`, `ParseBytes("1.5GiB"/"512MiB")`, comma-decimal `"1,5"`, `"N/A"`. |
| **`DiskInventory.Short`** | decimal TB/GB (`12 TB → 12.0T`, not 10.9T). |

### Agent (the pure bits)
- NIC-wrap clamp (`now<prev → 0`); PDH `% Processor Utility` clamp to `[0,100]`; the best/second-core
  argmax from a `corePeak[]`; the freshness percentile math (extract it, §4).

---

## 2. Integration tests (real OS, one process; some need admin) — `[Trait Integration]`

- **Agent**: spawn it, open `SnapshotChannel`, assert CPU/mem/disk/nic populated, version matches, and
  **0 torn reads over ~200 reads**. Kill it, assert the reader degrades cleanly.
- **Etw helper** (admin): spawn, assert it publishes `EtwSnapshot`; generate traffic → per-process net
  attribution appears; assert the AMD SDK stays closed until the consent marker exists, then opens hot.
- **PDH/WMI**: assert every counter path in `PdhQuery` resolves via `PdhAddEnglishCounterW` (guards the
  non-English-Windows regression), and the WMI queries in `DiskTempsWmi`/`ProcessDetails` run.
- **Config on a real disk**: Save→crash-simulate (leave a `.tmp`)→Load recovers from `.bak`.

## 3. E2E tests (whole product, via the debug hooks) — `[Trait E2E]`

- **Pipeline under load** *(formalize the existing manual test)*: agent+helper up, run
  `Client --freshness=20` at idle **and** under CPU stress (PowerShell runspaces) → assert
  `torn==0`, `stale==0`, `p95 < ~1200 ms`. This is the seqlock/timer integrity gate.
- **UI render golden**: `UI --floating --no-tray --seconds=3 --shot=out.png --width=W --expand=… --lang=es|en`
  → perceptual-diff against a baseline. Run for a few section sets + both languages. **This is the test
  that would have caught a layout regression from the MainWindow split.**
- **First-run dialogs**: `--firstrun-preview` and `--firstrun-preview=intel` render without crash.
- **Graceful degradation**: with the helper stopped / consent absent → assert CPU temp/power show `—`,
  throttle/best-core hidden, no crash (drive via killing the helper + `--verbose` overlay assertions).
- **Updater cycle (VM-gated, manual)**: install `vN` MSI → publish `vN+1` → enable `CheckUpdates` →
  assert detection; drive `ApplyUpdate` → assert in-place upgrade, relaunch, **config preserved** (ui.json
  intact), autostart intact. Repeat for the silent `AutoInstallUpdates` path (`/qn`).
- **Installer (VM-gated)**: `msiexec /i` then `/x` → assert `Program Files`, `HKLM\Software\SidebarMonitor`,
  the logon scheduled task, Start-Menu shortcut, the **HKLM Run** autostart, and a clean uninstall.

## 4. Small refactors to unlock testing (do alongside)

- Make **`FrameStats.ParseRow`** a pure `static` (`(colMap, line) → FrameInfo?`) — currently reads
  instance state; a tiny extract makes the CSV parsing unit-testable.
- Extract the **freshness/percentile** math (in `Client`) into a pure helper in Shared.
- Split **`CpuVendor`** string parsing from the `X86Base.CpuId` call.
- Give `ConsentMarker`/`UiConfig` an injectable base directory (or an env override) so file tests don't
  touch the real `%LOCALAPPDATA%`.

## 5. Priority order (write in this sequence)

1. **`Updater`** (`IsNewer`, `TryParseTag`, `IsTrustedGitHubUrl`, `Format`) — security-relevant, trivial, high value.
2. **`UiConfig`** atomic write + `.bak` fallback + `Ephemeral` — prevents settings loss.
3. **`SeqLock`** roundtrip + torn detection — the data-channel integrity.
4. **`NameField`** + **`ArgParse`** — contract strings + CLI.
5. **Pipeline-freshness e2e** — formalize the existing manual check into a repeatable test.
6. **UI render golden** — regression net for future refactors.
7. Everything else in §1–§3.

Steps 1–4 are pure and could all land in a first `SidebarMonitor.Tests` project in one sitting, wired
into CI as `Category=Unit`. 5–6 need the built exes and run locally / on a self-hosted runner.
