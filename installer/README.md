# SidebarMonitor installer (WiX MSI)

A per-machine Windows Installer (`.msi`, built with [WiX](https://wixtoolset.org/) v5) that installs
the three apps, wires up autostart, and cleans up on uninstall. It is the release-grade alternative
to the developer `install.ps1` in the repo root.

## What the MSI does

- Installs the three self-contained apps to `C:\Program Files\SidebarMonitor` (**no .NET runtime
  prerequisite** — the runtime is bundled; the agent is NativeAOT).
- Registers the elevated helper (`SidebarMonitor.Etw`) as a **logon scheduled task**
  (`SidebarMonitor Helper`, Highest run level = no UAC prompt), launched hidden via
  `run-helper-hidden.vbs` so no console window ever flashes.
- Sets the **UI to autostart** under the per-user `Run` key (the UI launches the agent itself).
- Adds a **Start-menu shortcut** and an Add/Remove Programs entry with the app icon.
- Starts the helper immediately after install (`schtasks /Run`); the UI can be started from the
  shortcut right away, and everything comes up automatically at the next sign-in.
- On uninstall, removes the scheduled task, the Run key and all files.

The AMD SDK EULA is **not** shown by the installer — it is accepted in-app on first run (only on AMD
systems), which is where the AMD sensors are actually gated.

## Building

Prerequisites: .NET 10 SDK, the MSVC C++ toolchain with `vswhere` on `PATH` (for the AOT agent), the
AMD SDK DLLs fetched (`native/RyzenSdk/fetch.ps1`) and the shims built
(`native/RyzenShim/build.cmd`, `native/AdlxShim/build.cmd`), plus the WiX v5 CLI:

```powershell
dotnet tool install --global wix --version 5.*
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
```

> WiX **v5**, not v6/v7: v6+ require accepting the Open Source Maintenance Fee (OSMF) EULA. v5 is the
> last version without it and is fully sufficient here.

Then:

```powershell
./installer/build.ps1 -Version 1.4.9                    # full -> installer/out/SidebarMonitor-1.4.9.msi
./installer/build.ps1 -Version 1.4.9 -Lite -SkipPublish # lite -> installer/out/SidebarMonitor-lite-1.4.9.msi
```

The version is baked into the filename (`SidebarMonitor-<version>.msi`) so a downloaded MSI says which
build it is on its own, and CI and a hand-cut release produce the identical name. `-Version` defaults to
a placeholder, so pass the real one when cutting a release.

Two flavours are shipped so users pick how AMD's proprietary SDK reaches their machine:

- **full** (`SidebarMonitor-<version>.msi`) — bundles AMD's `Platform.dll`, `Device.dll` and driver
  (redistributed under AMD's EULA, accepted in-app on first run). Works offline.
- **lite** (`SidebarMonitor-lite-<version>.msi`, `-Lite`) — ships **none** of AMD's binaries. `RyzenShim.dll`
  then loads `Platform.dll` from the AMD Ryzen Master / Monitoring SDK the user installs; without it,
  CPU sensors fall back to basic mode. Our own shims (`RyzenShim.dll`, `AdlxShim.dll`) and the Microsoft
  VC runtime stay. Build full first, then lite with `-SkipPublish` (it reuses the stage and prunes the
  AMD files). Both share an UpgradeCode with `AllowSameVersionUpgrades`, so users can switch either way.

`installer/stage/` and `installer/out/` are gitignored: the staged publish and the `.msi` bundle the
AMD SDK binaries + driver (proprietary — never committed) and the .NET runtime.

## Testing

The MSI changes machine state (Program Files, a scheduled task, a Run key). **Test it in a VM or on a
machine where SidebarMonitor isn't already running from a manual/`install.ps1` deployment**, otherwise
the Program Files instance and the existing one will fight over the single-instance lock.

```powershell
msiexec /i installer\out\SidebarMonitor-1.4.9.msi        # install (interactive)
msiexec /x installer\out\SidebarMonitor-1.4.9.msi        # uninstall
msiexec /i installer\out\SidebarMonitor-1.4.9.msi /qn /l*v install.log   # silent + verbose log
```

The custom actions run through `WixQuietExec`, so they show no console window and their output lands in
the MSI log instead — `/l*v` is how you read what `schtasks` and `taskkill` actually said:

```powershell
msiexec /i installer\out\SidebarMonitor-1.4.9.msi /l*v install.log
Select-String install.log -Pattern 'WixQuietExec'
```

Upgrading *from* an older build still shows that build's console windows: Windows Installer removes the
previous product with its own cached MSI, which carries the old, noisy actions. Nothing in a new package
can change that; it goes quiet once the version being replaced is one that had this fix.

## Naming the release asset

The in-app updater finds its download by **name**, so what you attach to the GitHub Release is part of
the contract, not cosmetic. `build.ps1` always emits the versioned name, which is what you should ship;
the bare name is still accepted so older releases keep updating (see `Updater.IsFlavorAsset`):

| Flavour | Produced by build.ps1        | Also accepted (legacy)    |
|---------|------------------------------|---------------------------|
| full    | `SidebarMonitor-1.4.9.msi`   | `SidebarMonitor.msi`      |
| lite    | `SidebarMonitor-lite-1.4.9.msi` | `SidebarMonitor-lite.msi` |

Only a parseable version may follow the name. Anything else (`SidebarMonitor-final.msi`,
`SidebarMonitor_1.4.8.msi`) matches nothing, and the failure is **silent**: the updater finds no asset
and quietly opens the release page in the browser instead of installing. That is exactly how every
release from 1.4.4 to 1.4.8 shipped with a dead auto-update without anyone noticing — the release
carried the versioned name while the updater looked only for the bare one. Now both the build and the
updater agree on the versioned name, so they cannot drift.

`tests/SidebarMonitor.Tests/UpdaterIntegrationTests.cs` checks the real latest release for a matching
asset. After publishing, run it:

```powershell
dotnet test --filter Category=Integration
```

## winget

`installer/winget/` holds the manifest (schema 1.6). Before submitting to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), fill in, from the **published**
MSI:

- `InstallerUrl` — the GitHub Release asset URL.
- `InstallerSha256` — `(Get-FileHash SidebarMonitor.msi -Algorithm SHA256).Hash`.
- `ProductCode` — read it back from the released MSI (WiX regenerates it each build).

Then `winget validate installer/winget` and `winget install --manifest installer/winget` to test
locally before submitting.
