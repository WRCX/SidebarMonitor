# FAQ

## Does it really work with Memory Integrity / Core Isolation (HVCI) on?

Yes — that's the whole reason SidebarMonitor exists. It ships **no kernel driver of its own** and
never uses WinRing0, the driver Windows added to its vulnerable-driver blocklist in 2025 (which broke
Sidebar Diagnostics and most WinRing0-based monitors on HVCI machines). Sensors come from paths that
are signed and HVCI-safe: the AMD Ryzen Master Monitoring SDK, NVML, ADLX, D3DKMT and native Windows
APIs.

## Why does it need the AMD SDK?

To read CPU temperature and package power **without** a ring0 driver of our own. The AMD Ryzen Master
Monitoring SDK provides a signed, HVCI-safe driver for exactly this. You accept its EULA in-app on
first run (only on AMD systems); if you decline, the app still runs in "basic mode" — per-core usage
and frequency via Windows performance counters, plus network, disks and GPU.

The AMD SDK binaries are never committed to this repository. They are pulled at packaging time and
travel only inside the installer, which is what its license permits (object-code redistribution for
use on AMD systems).

## Why is there an elevated helper process?

Two data sources need administrator rights: a kernel **ETW** session (which process is using each CPU
core, and per-process network bytes) and the **AMD SDK**. Isolating both in a small helper
(`SidebarMonitor.Etw`, started by a scheduled task at Highest run level) lets the main sampling agent
stay **unelevated and NativeAOT**. If the helper isn't running, the app degrades gracefully: bars
still show usage, but without per-process attribution or CPU temperature/power.

## Do you collect any telemetry?

**No.** Nothing leaves your machine. See [PRIVACY.md](../PRIVACY.md).

## Does it need an internet connection?

No. It runs fully offline. The only optional network access is the **update check** (Settings →
Updates), which is **off by default** and, when enabled, only asks GitHub's public Releases API whether
a newer version exists — see [PRIVACY.md](../PRIVACY.md). Downloading the installer is the other obvious
time bytes move, and that's you initiating it.

## What works on my hardware?

| Feature | Needs |
|---|---|
| CPU per-core usage & frequency, memory, network, disks, processes | Any Windows 11 x64 machine |
| CPU temperature, package power, per-core temp, C0/sleep, throttle, best-core | **AMD Ryzen** + the Ryzen Master SDK (accepted on first run) |
| NVIDIA GPU temp/power/fan/clocks/VRAM | NVIDIA driver (NVML) |
| AMD GPU (Radeon dGPU / Ryzen iGPU) temp/power/fan/clocks/VRAM | AMD Adrenalin driver (ADLX) |
| Per-GPU per-engine activity (3D/compute/decode/encode) | Any GPU (D3DKMT — no vendor SDK needed) |
| Per-core process colours, per-process network | The elevated helper |

Best experience: a **Ryzen CPU with an NVIDIA or AMD GPU**.

## What about Intel CPUs?

Intel CPU temperature and power live in model-specific registers (MSRs) reachable only through a
ring0 driver (e.g. PawnIO), which SidebarMonitor doesn't bundle yet. On Intel the app detects this,
tells you why those fields are blank, and everything else — per-core usage/frequency, GPU, memory,
network, disks, processes — works normally. Intel sensor support via a signed ring0 path is on the
roadmap.

## Where is my configuration stored?

`%LOCALAPPDATA%\SidebarMonitor\ui.json`. CSV logs (if you enable them) go to
`%LOCALAPPDATA%\SidebarMonitor\logs`. Uninstalling with the WiX MSI removes the program files, the
scheduled task and the autostart entry; your `ui.json`/logs are left in place unless you delete them.

## Can I change the language?

Yes. It follows your Windows language by default (Spanish on Spanish systems, English otherwise), and
you can force Spanish or English in **Settings → Appearance → Language**. Changing it restarts the
panel.

## How do I install / uninstall?

Via the WiX MSI (`msiexec /i SidebarMonitor.msi`) or the developer `install.ps1`. To remove:
Add/Remove Programs, `msiexec /x`, or `uninstall.ps1 -Purge`. See
[installer/README.md](../installer/README.md). *(A `winget` package will come with the first release.)*

## Why three separate processes instead of one?

They have incompatible build models: the agent is **NativeAOT** (native, unelevated, tiny), the UI is
**WPF** (which can't be AOT-compiled), and the helper needs a **different elevation manifest**.
Merging them would mean giving up the agent's AOT footprint. The UI launches the agent as its child,
so they nest under "SidebarMonitor" in Task Manager; the helper runs from its scheduled task. Full
rationale and measurements are in [ARCHITECTURE.md](ARCHITECTURE.md).
