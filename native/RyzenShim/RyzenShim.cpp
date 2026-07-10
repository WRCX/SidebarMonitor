// Flat-C bridge to the AMD Ryzen Master Monitoring SDK. The C++ compiler lays out the SDK's
// virtual interfaces correctly here; the C# helper only ever sees plain exported functions and a
// POD struct, so no fragile hand-written vtable interop on the managed side.
//
// Needs admin and the AMD driver (installed with Ryzen Master / the Monitoring SDK). Reads CPU
// temperature, package power (PPT) and per-core clock/temperature — everything we took from
// HWiNFO for the CPU, plus more.
#include <windows.h>
#include <string>
#include "IPlatform.h"
#include "IDeviceManager.h"
#include "ICPUEx.h"
#include "DeviceType.h"

extern "C" {

#pragma pack(push, 1)
struct RmCpu
{
    double TempC;          // package temperature
    float  PackageW;       // PPT current value
    float  PackageLimitW;
    float  FmaxMhz;        // best-core boost bin (CCLK Fmax)
    double PeakSpeedMhz;
    int    CoreCount;
    double CoreFreqMhz[16];
    double CoreTempC[16];
};
#pragma pack(pop)

static HMODULE g_dll = nullptr;
static IPlatform* g_platform = nullptr;
static ICPUEx* g_cpu = nullptr;

typedef IPlatform& (*GetPlatformFunc)();

// Reads the SDK install dir from the registry, exactly like AMD's sample, so we do not hardcode.
static std::wstring SdkBin()
{
    wchar_t buf[512] = {0};
    DWORD sz = sizeof(buf);
    HKEY k;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\AMD\\RyzenMasterMonitoringSDK", 0, KEY_READ, &k) == ERROR_SUCCESS)
    {
        DWORD type = 0;
        RegQueryValueExW(k, L"InstallationPath", nullptr, &type, (LPBYTE)buf, &sz);
        RegCloseKey(k);
    }
    std::wstring p = buf[0] ? buf : L"C:\\Program Files\\AMD\\RyzenMasterMonitoringSDK\\";
    return p + L"bin";
}

// 0 on success, negative on failure.
__declspec(dllexport) int RmOpen()
{
    if (g_cpu) return 0;

    std::wstring bin = SdkBin();
    SetDllDirectoryW(bin.c_str());   // resolve Platform.dll's dependencies from the SDK bin
    g_dll = LoadLibraryW(L"Platform.dll");
    if (!g_dll) return -1;

    GetPlatformFunc gp = (GetPlatformFunc)GetProcAddress(g_dll, "GetPlatform");
    if (!gp) return -2;

    g_platform = &gp();
    if (!g_platform->Init()) return -3;

    IDeviceManager& dm = g_platform->GetIDeviceManager();
    g_cpu = (ICPUEx*)dm.GetDevice(dtCPU, 0);
    return g_cpu ? 0 : -4;
}

// 0 on success. Fills *out with the latest sample.
__declspec(dllexport) int RmRead(RmCpu* out)
{
    if (!g_cpu || !out) return -1;

    CPUParameters p = {};
    int rc = g_cpu->GetCPUParameters(p);
    if (rc != 0) return rc;

    out->TempC = p.dTemperature;
    out->PackageW = p.fPPTValue;
    out->PackageLimitW = p.fPPTLimit;
    out->FmaxMhz = p.fCCLK_Fmax;
    out->PeakSpeedMhz = p.dPeakSpeed;

    int n = 0;
    unsigned len = p.stFreqData.uLength;
    for (unsigned i = 0; i < len && n < 16 && p.stFreqData.dCurrentTemp; i++)
    {
        double f = p.stFreqData.dCurrentFreq ? p.stFreqData.dCurrentFreq[i] : 0.0;
        if (f == 0.0) continue;   // parked/absent core
        out->CoreFreqMhz[n] = f;
        out->CoreTempC[n] = p.stFreqData.dCurrentTemp[i];
        n++;
    }
    out->CoreCount = n;
    return 0;
}

__declspec(dllexport) void RmClose()
{
    if (g_platform) { g_platform->UnInit(); g_platform = nullptr; }
    g_cpu = nullptr;
    if (g_dll) { FreeLibrary(g_dll); g_dll = nullptr; }
}

}   // extern "C"
