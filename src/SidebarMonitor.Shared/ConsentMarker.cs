using System.IO;

namespace SidebarMonitor.Shared;

/// <summary>
/// The one-bit bridge between the unelevated UI (which shows AMD's EULA and records the user's
/// answer in ui.json) and the elevated helper (which loads AMD's SDK but has no business reading the
/// UI config). The UI mirrors its <c>AmdEulaAccepted</c> flag to this marker file; the helper only
/// opens the Ryzen Master SDK when the marker is present. Same LocalApplicationData profile, so both
/// resolve to the same path. Absence = not consented = SDK stays closed (degrade to PDH/HWiNFO).
/// </summary>
public static class ConsentMarker
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor");

    /// <summary>Marker for "the user accepted AMD's Ryzen Master Monitoring SDK EULA".</summary>
    private static string AmdSdkPath => Path.Combine(Dir, "amd-sdk-consent");

    public static bool AmdSdkAccepted => File.Exists(AmdSdkPath);

    /// <summary>Marker for "the user enabled the game FPS overlay" — gates the helper spawning
    /// PresentMon, so a user who doesn't want it pays nothing.</summary>
    private static string FpsPath => Path.Combine(Dir, "fps-enabled");

    public static bool FpsEnabled => File.Exists(FpsPath);

    /// <summary>Idempotently create/remove the FPS marker to match the UI's stored setting.</summary>
    public static void SetFps(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (enabled) { if (!File.Exists(FpsPath)) File.WriteAllText(FpsPath, "game FPS monitoring enabled by the user.\n"); }
            else if (File.Exists(FpsPath)) File.Delete(FpsPath);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Marker for "the user enabled the advanced CPU sensors via PawnIO" — gates the helper
    /// loading the signed RyzenSMU module (Tctl on machines the Ryzen Master SDK can't read, i.e.
    /// every mobile APU). Needs PawnIO installed; absence = the SDK-or-nothing behaviour.</summary>
    private static string AmdAdvancedPath => Path.Combine(Dir, "amd-advanced-pawnio");

    public static bool AmdAdvancedEnabled => File.Exists(AmdAdvancedPath);

    /// <summary>Idempotently create/remove the PawnIO marker to match the UI's stored setting.</summary>
    public static void SetAmdAdvanced(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (enabled) { if (!File.Exists(AmdAdvancedPath)) File.WriteAllText(AmdAdvancedPath, "advanced CPU sensors via PawnIO enabled by the user.\n"); }
            else if (File.Exists(AmdAdvancedPath)) File.Delete(AmdAdvancedPath);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Marker for "the user enabled Intel CPU sensors via PawnIO" — gates the helper loading
    /// the signed IntelMSR module (per-core temp via IA32_THERM_STATUS + RAPL package power). Intel
    /// ships no monitoring SDK, so on Intel this is the ONLY route to CPU temp/watts. Needs PawnIO
    /// installed; absence = temp/power stay "—" (PDH-only behaviour).</summary>
    private static string IntelSensorsPath => Path.Combine(Dir, "intel-sensors-pawnio");

    public static bool IntelSensorsEnabled => File.Exists(IntelSensorsPath);

    /// <summary>Idempotently create/remove the Intel-sensors marker to match the UI's stored setting.</summary>
    public static void SetIntelSensors(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (enabled) { if (!File.Exists(IntelSensorsPath)) File.WriteAllText(IntelSensorsPath, "Intel CPU sensors via PawnIO enabled by the user.\n"); }
            else if (File.Exists(IntelSensorsPath)) File.Delete(IntelSensorsPath);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Marker for "the user enabled laptop fan monitoring via PawnIO" — gates the helper
    /// loading the signed LpcACPIEC module and reading the embedded controller. Vendor-agnostic (works
    /// on AMD and Intel laptops); community-sourced per-model register map, so explicitly opt-in and
    /// flagged as best-effort. Needs PawnIO installed.</summary>
    private static string FanPawnIoPath => Path.Combine(Dir, "fan-pawnio");

    public static bool FanPawnIoEnabled => File.Exists(FanPawnIoPath);

    /// <summary>Idempotently create/remove the fan marker to match the UI's stored setting.</summary>
    public static void SetFanPawnIo(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (enabled) { if (!File.Exists(FanPawnIoPath)) File.WriteAllText(FanPawnIoPath, "laptop fan monitoring via PawnIO enabled by the user.\n"); }
            else if (File.Exists(FanPawnIoPath)) File.Delete(FanPawnIoPath);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Idempotently create/remove the marker to match the UI's stored consent.</summary>
    public static void SetAmdSdk(bool accepted)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (accepted)
            {
                if (!File.Exists(AmdSdkPath))
                    File.WriteAllText(AmdSdkPath, "AMD Ryzen Master Monitoring SDK EULA accepted by the user.\n");
            }
            else if (File.Exists(AmdSdkPath))
            {
                File.Delete(AmdSdkPath);
            }
        }
        catch { /* non-fatal: absence just means degrade */ }
    }
}
