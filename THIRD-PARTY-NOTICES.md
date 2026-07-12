# Third-party notices

SidebarMonitor (the source code in this repository) is licensed under the MIT License
(see `LICENSE`). It builds on, and its installer redistributes, the third-party components
below, each under its own terms. Their inclusion does **not** place them under the MIT License.

---

## AMD Ryzen Master Monitoring SDK

- **Used for:** CPU temperature, package power (PPT), per-core clock/temperature, C0 residency and
  limit telemetry on AMD Ryzen processors, via the elevated helper (`SidebarMonitor.Etw`) and the
  native bridge `RyzenShim.dll`.
- **Files redistributed by the installer** (object code only): `Platform.dll`, `Device.dll`,
  `AMDRyzenMasterDriver.sys` / `.inf` / `.cat`.
- **License:** AMD *Software Evaluation License Agreement (Object Code Only)* — shipped as
  `License.rtf` and shown to the user on first run. Redistribution is permitted only when each
  recipient accepts that EULA before use, which SidebarMonitor enforces with its first-run consent
  dialog. The SDK is **not** modified, reverse-engineered, or committed to this public repository;
  its binaries are pulled from an AMD SDK installation at packaging time (`native/RyzenSdk/fetch.ps1`)
  and travel only in the installer.
- **Source:** https://www.amd.com/en/developer/ryzen-master-monitoring-sdk.html
- © Advanced Micro Devices, Inc. All rights reserved.

## AMD Device Library eXtra (ADLX) SDK

- **Used for:** AMD GPU utilisation, temperature, power, fan, clocks and VRAM (Radeon dGPU / Ryzen
  iGPU), via the unelevated agent and the native bridge `AdlxShim.dll`.
- **Runtime:** `amdadlx64.dll` ships with the AMD Adrenalin driver and is loaded at runtime;
  **not** redistributed by SidebarMonitor.
- **`AdlxShim.dll`** is our own object code, incorporating AMD's ADLX *sample code* (the ADLXHelper
  and interface headers). The ADLX SDK license permits redistributing sample code in **object-code
  form only**, for use on AMD systems, under an end-user license — which SidebarMonitor's first-run
  AMD consent covers. The SDK headers/source are **not** modified or committed to this public
  repository; they are pulled on demand (`native/AdlxSdk/fetch.ps1`) and only the compiled shim ships.
- **License:** AMD *Software Development Kit License Agreement (ADLX SDK)*.
- **Source:** https://github.com/GPUOpen-LibrariesAndSDKs/ADLX
- © Advanced Micro Devices, Inc. All rights reserved.

## NVIDIA Management Library (NVML)

- **Used for:** NVIDIA GPU utilisation, temperature, power, clocks, fan and VRAM.
- **Distribution:** `nvml.dll` ships with the NVIDIA display driver and is loaded at runtime;
  **not** redistributed by SidebarMonitor.
- **Source:** https://developer.nvidia.com/management-library-nvml
- © NVIDIA Corporation.

## Microsoft.Diagnostics.Tracing.TraceEvent

- **Used for:** the kernel ETW session in the elevated helper (per-core process attribution and
  per-process network bytes).
- **License:** MIT.
- **Source:** https://github.com/microsoft/perfview

## Microsoft Visual C++ Runtime (redistributable)

- **Used for:** required by AMD's `Device.dll`.
- **Files redistributed by the installer:** `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, `MSVCP140.dll`.
- **License:** Microsoft Visual C++ Redistributable license (redistribution permitted).
- © Microsoft Corporation.

## .NET runtime and libraries

- **License:** MIT.
- **Source:** https://github.com/dotnet/runtime
- © .NET Foundation and Contributors.

---

*If you believe an attribution is missing or incorrect, please open an issue.*
