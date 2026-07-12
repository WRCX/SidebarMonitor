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
    float  VidV;           // average core voltage (Vcore / VID)
    float  TjMaxC;         // thermal limit (cHTC), i.e. the throttle temperature
    float  PptPct;         // PPT (package power) usage as % of its limit
    float  TdcPct;         // TDC (sustained current) usage as % of its limit
    float  EdcPct;         // EDC (peak current) usage as % of its limit
    double CoreC0Pct[16];  // per-PHYSICAL-core C0 (active) state residency %; ~0 = parked/asleep
};
#pragma pack(pop)

static HMODULE g_dll = nullptr;
static IPlatform* g_platform = nullptr;
static ICPUEx* g_cpu = nullptr;

typedef IPlatform& (*GetPlatformFunc)();

// The directory this shim DLL itself lives in — where the installer drops the bundled SDK DLLs.
static std::wstring SelfDir()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       (LPCWSTR)&SelfDir, &self);
    wchar_t path[MAX_PATH] = {0};
    GetModuleFileNameW(self, path, MAX_PATH);
    std::wstring p = path;
    size_t slash = p.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring(L".") : p.substr(0, slash);
}

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

static bool FileExists(const std::wstring& p)
{
    return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// 0 on success, negative on failure.
__declspec(dllexport) int RmOpen()
{
    if (g_cpu) return 0;

    // Prefer the DLLs the installer bundled next to us (fully standalone, no SDK install needed);
    // fall back to the installed SDK's bin only if they are not here.
    std::wstring self = SelfDir();
    std::wstring dir = FileExists(self + L"\\Platform.dll") ? self : SdkBin();

    SetDllDirectoryW(dir.c_str());   // so Platform.dll's own dependencies (Device.dll, ...) resolve
    std::wstring platformPath = dir + L"\\Platform.dll";
    g_dll = LoadLibraryW(platformPath.c_str());
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
    out->VidV = (float)p.dAvgCoreVoltage;
    out->TjMaxC = p.fcHTCLimit;

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

    // C0 residency by PHYSICAL core index (not compacted): a parked core keeps its slot and reports
    // ~0, which is exactly the "Sleep" state Ryzen Master shows. dState is the SDK's C0 State
    // Residency % — how much of the sample the core was actually awake.
    for (unsigned i = 0; i < len && i < 16; i++)
        out->CoreC0Pct[i] = p.stFreqData.dState ? p.stFreqData.dState[i] : 0.0;

    // Limit utilisation, exactly what HWiNFO's "Limits" group shows: how close each cap is to
    // being hit. FmaxMhz above is the global frequency limit; these are power and current.
    out->PptPct = p.fPPTLimit     > 0.0f ? p.fPPTValue     / p.fPPTLimit     * 100.0f : 0.0f;
    out->TdcPct = p.fTDCLimit_VDD > 0.0f ? p.fTDCValue_VDD / p.fTDCLimit_VDD * 100.0f : 0.0f;
    out->EdcPct = p.fEDCLimit_VDD > 0.0f ? p.fEDCValue_VDD / p.fEDCLimit_VDD * 100.0f : 0.0f;

    return 0;
}

__declspec(dllexport) void RmClose()
{
    if (g_platform) { g_platform->UnInit(); g_platform = nullptr; }
    g_cpu = nullptr;
    if (g_dll) { FreeLibrary(g_dll); g_dll = nullptr; }
}

}   // extern "C"
