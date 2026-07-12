// AdlxShim.dll — a flat-C bridge to AMD's ADLX (AMD Device Library eXtra), mirroring RyzenShim.
//
// ADLX is a COM-style C++ API; NativeAOT C# cannot call its vtables comfortably, so this shim wraps
// the parts we need (per-GPU telemetry: usage, temperature, power, fan, clocks, VRAM) behind three
// plain C entry points. The agent P/Invokes it unelevated — ADLX needs no driver of ours: it talks
// to amdadlx64.dll, which ships with the AMD Adrenalin driver.
//
// Legal: this .cpp incorporates AMD's ADLXHelper *sample code*; the ADLX SDK licence permits
// redistributing sample code only in Object Code form (i.e. this compiled DLL), for use on AMD
// systems, under an end-user licence. So we ship AdlxShim.dll but never the SDK headers/source, and
// the app gates the AMD SDKs behind the first-run EULA. See build.cmd / fetch.ps1.

#include "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.h"
#include "SDK/Include/IPerformanceMonitoring3.h"

#include <vector>
#include <cmath>
#include <cstring>

using namespace adlx;

#define ADLX_API extern "C" __declspec(dllexport)

namespace
{
    // ADLX must outlive every interface we hand back, so everything lives in file-scope globals.
    ADLXHelper g_help;
    IADLXPerformanceMonitoringServicesPtr g_perf;
    std::vector<IADLXGPUPtr> g_amdGpus;   // only AMD GPUs (PCI vendor 0x1002), in list order
    bool g_open = false;

    // AMD's PCI vendor id, as ADLX reports it from IADLXGPU::VendorId.
    bool IsAmd(const IADLXGPUPtr& gpu)
    {
        const char* vendor = nullptr;
        if (ADLX_FAILED(gpu->VendorId(&vendor)) || vendor == nullptr) return false;
        // Accept "0x1002" / "1002" in any case.
        return std::strstr(vendor, "1002") != nullptr;
    }
}

// The flat telemetry record the agent reads. Layout must match AdlxSensors.cs exactly.
// Doubles are NaN and ints are -1 when the metric is unsupported on this GPU.
#pragma pack(push, 1)
struct AdlxGpu
{
    char   name[128];
    double usagePct;
    double tempC;
    double hotspotC;
    double powerW;        // GPU-only power draw
    double totalBoardW;   // whole-board power (preferred when present)
    double vramMB;        // VRAM in use
    int    clockMhz;
    int    memClockMhz;
    int    fanRpm;
    int    fanPct;        // fan duty %
    int    voltageMV;
};
#pragma pack(pop)

// Initialise ADLX and cache the AMD GPUs. Returns the number of AMD GPUs (>= 0), or a negative
// value if ADLX is unavailable (no Adrenalin driver, non-AMD box, init failure) — the caller then
// simply skips ADLX and keeps its vendor-neutral behaviour.
ADLX_API int AdlxOpen()
{
    if (g_open) return (int)g_amdGpus.size();

    if (ADLX_FAILED(g_help.Initialize())) return -1;

    IADLXSystem* sys = g_help.GetSystemServices();
    if (sys == nullptr) { g_help.Terminate(); return -2; }

    if (ADLX_FAILED(sys->GetPerformanceMonitoringServices(&g_perf)) || g_perf == nullptr)
    {
        g_help.Terminate();
        return -3;
    }

    IADLXGPUListPtr gpus;
    if (ADLX_FAILED(sys->GetGPUs(&gpus)) || gpus == nullptr)
    {
        g_perf = nullptr;
        g_help.Terminate();
        return -4;
    }

    for (adlx_uint i = gpus->Begin(); i != gpus->End(); ++i)
    {
        IADLXGPUPtr gpu;
        if (ADLX_FAILED(gpus->At(i, &gpu)) || gpu == nullptr) continue;
        if (IsAmd(gpu)) g_amdGpus.push_back(gpu);
    }

    g_open = true;
    return (int)g_amdGpus.size();
}

// Read the current metrics for AMD GPU #index into *out. Returns 0 on success, negative on error.
// Unsupported metrics stay NaN / -1, so the caller can tell "0" from "not measured".
ADLX_API int AdlxRead(int index, AdlxGpu* out)
{
    if (!g_open || out == nullptr) return -1;
    if (index < 0 || index >= (int)g_amdGpus.size()) return -2;

    std::memset(out, 0, sizeof(*out));
    out->usagePct = out->tempC = out->hotspotC = NAN;
    out->powerW = out->totalBoardW = out->vramMB = NAN;
    out->clockMhz = out->memClockMhz = out->fanRpm = out->fanPct = out->voltageMV = -1;

    IADLXGPUPtr gpu = g_amdGpus[index];

    const char* name = nullptr;
    if (ADLX_SUCCEEDED(gpu->Name(&name)) && name != nullptr)
    {
        std::strncpy(out->name, name, sizeof(out->name) - 1);
        out->name[sizeof(out->name) - 1] = '\0';
    }

    IADLXGPUMetricsSupportPtr support;
    if (ADLX_FAILED(g_perf->GetSupportedGPUMetrics(gpu, &support)) || support == nullptr) return -3;

    IADLXGPUMetricsPtr metrics;
    if (ADLX_FAILED(g_perf->GetCurrentGPUMetrics(gpu, &metrics)) || metrics == nullptr) return -4;

    adlx_bool supported = false;
    adlx_double d = 0;
    adlx_int    n = 0;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUUsage(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUUsage(&d))) out->usagePct = d;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUTemperature(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUTemperature(&d))) out->tempC = d;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUHotspotTemperature(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUHotspotTemperature(&d))) out->hotspotC = d;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUPower(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUPower(&d))) out->powerW = d;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUTotalBoardPower(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUTotalBoardPower(&d))) out->totalBoardW = d;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUVRAM(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUVRAM(&n))) out->vramMB = n;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUClockSpeed(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUClockSpeed(&n))) out->clockMhz = n;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUVRAMClockSpeed(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUVRAMClockSpeed(&n))) out->memClockMhz = n;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUFanSpeed(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUFanSpeed(&n))) out->fanRpm = n;

    if (ADLX_SUCCEEDED(support->IsSupportedGPUVoltage(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics->GPUVoltage(&n))) out->voltageMV = n;

    // Fan duty (%) lives on the support3 / metrics3 revisions; query them if the driver exposes them.
    IADLXGPUMetricsSupport3Ptr support3(support);
    IADLXGPUMetrics3Ptr metrics3(metrics);
    if (support3 && metrics3 &&
        ADLX_SUCCEEDED(support3->IsSupportedGPUFanDuty(&supported)) && supported &&
        ADLX_SUCCEEDED(metrics3->GPUFanDuty(&n))) out->fanPct = n;

    return 0;
}

// Release everything and shut ADLX down. Safe to call when never opened.
ADLX_API void AdlxClose()
{
    if (!g_open) return;
    g_amdGpus.clear();
    g_perf = nullptr;
    g_help.Terminate();
    g_open = false;
}
