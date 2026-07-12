# FPS & frame-timing in the sidebar via ETW (PresentMon) — design & plan

> **Status: backend implemented and verified; UI section pending.** The data pipeline (PresentMon →
> helper → agent → snapshot) works end-to-end — FPS, frametime, 1%/0.1% lows, GPU busy, latency,
> stutter ("animation error") and the displayed-vs-presented frame-generation split — measured **without
> injecting into games**. What remains is the on-panel GAME section that renders it.

## The line we do NOT cross: no in-game overlay

RivaTuner Statistics Server (RTSS / MSI Afterburner's overlay) draws *inside* the game by **injecting
a DLL and hooking the graphics API** (D3D/Vulkan present). That's a different product and a different
risk class: **anti-cheat bans, per-game fragility, injection**. SidebarMonitor stays HVCI-safe and
un-invasive, so an in-game overlay is **out of scope**. We measure and display in **our own panel**.

## The clean engine: PresentMon 2 (ETW, no injection)

Intel's open-source **[PresentMon](https://github.com/GameTechDev/PresentMon)** (v2.x, the same tech
GamersNexus/JayzTwoCents use) measures graphics performance purely from **ETW** — it consumes the
`Microsoft-Windows-DxgKrnl` present/flip events and the graphics providers, the *same class of ETW
session our elevated helper already runs*. No hooks, no injection, no per-game code. MIT-licensed, so
we can ship/consume it cleanly, and it's HVCI-safe (ETW, not a driver).

Two integration options:

1. **Use PresentMon's service + SDK (recommended).** PresentMon 2 ships a **service** (does the ETW
   capture) and a **client SDK** (a small library that queries live, smoothed metrics over a named
   pipe / shared memory — pick the target process, poll metrics). We run/consume the service from our
   elevated helper and read the metrics for the foreground game. Least code, Intel maintains the hard
   part (ETW parsing across driver versions), and we inherit new metrics for free.
2. **Consume the ETW providers ourselves** in `SidebarMonitor.Etw` (we already have a kernel ETW
   session). More work and more maintenance (present-event parsing changes with Windows/driver
   updates — exactly what PresentMon abstracts), but zero external dependency. Only worth it if
   bundling PresentMon is undesirable.

> Recommendation: **option 1**. Detect the foreground fullscreen/borderless app, ask PresentMon for
> its metrics, publish them into `EtwSnapshot` (new fields), the agent merges to `Snapshot`, a new
> **GAME/FPS** sidebar section renders them. Only active when a game is in the foreground.

## Metrics to surface (what PresentMon 2 gives us today)

| Metric | What it means | Why show it |
|---|---|---|
| **FPS-Displayed** | Rate frames actually hit the screen (**includes AI-generated frames**) | The number marketing/DLSS-FG shows |
| **FPS-Presented / Application** | Rate the *game engine* produces frames (real work) | The honest FPS; drives responsiveness |
| **Frametime + 1% / 0.1% lows** | Per-frame ms and worst-case percentiles | Smoothness, the number that matters more than avg FPS |
| **GPU Busy / GPU Wait** | How much of each frame the GPU actually worked vs waited on the CPU | CPU-bound vs GPU-bound at a glance |
| **Animation Error** | Mismatch between a frame's *simulation time* and when it's displayed | **Directly measures stutter** even when avg FPS looks fine — the GamersNexus topic |
| **Click-to-Photon / Instrumented Latency** | Mouse click → pixels on screen (2.2 added real-time, ~30 ms reporting; driver markers for instrumented) | The real "feel"/responsiveness number |
| **FrameType** | Tags each frame: Application, Repeated, or **generated** (DLSS-FG / FSR-FG / XeSS-FG / AFMF) | Lets us flag and separate AI frames |

## The frame-generation / "AI frames" nuance (what you asked about)

Frame generation (NVIDIA DLSS-FG, AMD FSR-FG/AFMF, Intel XeSS-FG) **inserts interpolated frames**
between real ones. PresentMon 2's `FrameType` + the split of **Displayed vs Application FPS** is
exactly what makes this measurable and honest:

- **Displayed FPS goes up** (e.g. 120), but **Application FPS is the truth** (e.g. 60) — the engine
  still simulates at 60, so **input latency tracks the ~60 fps path**, not the 120.
- **FG does not reduce latency; it usually adds a little** (it holds a frame to interpolate). What
  reduces latency is **Reflex / Anti-Lag**, a separate axis. So a big "Displayed FPS" with mediocre
  latency is the tell-tale of FG — and our panel would show **both FPS numbers + the latency**, so the
  user sees past the inflated headline.
- Design intent for the sidebar: show **"120 fps (60 real)"** style, plus **latency** and an **FG
  badge** when generated frames are detected. That's a genuinely useful, honest readout that most
  overlays *don't* separate — a differentiator.
- "Effective latency": we can present the **instrumented/click-to-photon latency** as the responsiveness
  metric and correlate it with Application FPS, making clear that FG buys smoothness-of-motion, not
  responsiveness.

## Architecture fit

- **`SidebarMonitor.Etw`** (elevated helper) already runs a kernel ETW session → natural home for the
  PresentMon service/consumer. It publishes to `EtwSnapshot`.
- **New `EtwSnapshot` fields** (contract bump): `FpsDisplayed`, `FpsApplication`, `FrametimeMs`,
  `FrametimeP99Ms`, `GpuBusyPct`, `AnimationErrorMs`, `LatencyMs`, `FrameGenActive`, plus the
  foreground app name.
- **Agent** merges them into `Snapshot` (as it does CPU/GPU sensors); **UI** adds a **GAME** section
  (auto-shown only when a game is in the foreground), with sparklines for frametime + the stat row.
- **No AOT/HVCI impact**: ETW, no driver of ours, no injection.

## Caveats

- Needs the game to present through the normal path (most do; some UWP/protected content is limited).
- Foreground-app detection (which process is the game) — via the shell foreground window + PID, and/or
  PresentMon's per-process streams; pick the one presenting fullscreen.
- PresentMon service must run elevated (our helper already is). Bundling it means one more redistributed
  component (MIT — fine; add to THIRD-PARTY-NOTICES).
- Metrics availability varies by GPU vendor/driver for the newest ones (instrumented latency needs
  driver markers; animation error and the FPS/GPU-busy set are broadly available).

## Effort / roadmap

- **Phase 1 (core):** consume PresentMon SDK in the helper, publish FPS-Displayed/Application +
  frametime + 1% low + GPU busy for the foreground app; a basic **GAME** section with a frametime
  sparkline. Medium effort, high wow-factor.
- **Phase 2:** animation error (stutter), latency (click-to-photon / instrumented), FrameType/FG badge
  and the "120 (60 real)" + latency presentation.
- **Phase 3 (optional):** logging these to the existing CSV; per-game history.

This is the strongest candidate for the next "star" feature: it reuses the ETW helper, stays HVCI-safe
and injection-free, and — with the Displayed-vs-Application + latency + animation-error framing — gives
an **honest** frame-generation readout that most overlays don't.

## Sources

- PresentMon (GameTechDev, MIT): https://github.com/GameTechDev/PresentMon
- Animation Error (GamersNexus): https://gamersnexus.net/gpus-cpus-deep-dive/fps-benchmarks-are-flawed-introducing-animation-error-engineering-discussion
- PresentMon 2.2 (real-time latency, click-to-photon): https://videocardz.com/newz/intel-presentmon-2-2-0-offers-significantly-lowered-event-latency
