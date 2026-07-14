using System.IO;
using System.Security.AccessControl;

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
    /// <summary>
    /// Machine-wide marker directory (ProgramData): the elevated helper is ONE per machine serving
    /// every user's session, so the consents that gate it are machine-level decisions. The helper
    /// calls <see cref="EnsureMachineDir"/> at startup (elevated) to create it with a Users-writable
    /// ACL and migrate any per-user markers from the pre-multi-user location; UIs read/write here
    /// directly. <see cref="LegacyDir"/> (per-user LOCALAPPDATA) is still read as a fallback so a
    /// not-yet-migrated setup keeps its consents until the helper's first elevated run.
    /// </summary>
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SidebarMonitor");

    /// <summary>Pre-multi-user per-user marker dir; read-fallback + migration source only.</summary>
    public static string LegacyDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidebarMonitor");

    private static bool Present(string name) =>
        File.Exists(Path.Combine(Dir, name)) || File.Exists(Path.Combine(LegacyDir, name));

    private static void Set(string name, string content, bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name);
            if (enabled) { if (!File.Exists(path)) File.WriteAllText(path, content); }
            else
            {
                if (File.Exists(path)) File.Delete(path);
                // Clear the legacy copy too, or Present() would keep reporting consent.
                string legacy = Path.Combine(LegacyDir, name);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
        }
        catch { /* non-fatal: absence just means degrade */ }
    }

    /// <summary>Elevated helper, at startup: create the machine dir with a Users-modify ACL (a
    /// plain unelevated CreateDirectory leaves it owner-writable only, so OTHER users' UIs could
    /// not toggle), then migrate this user's legacy markers once. Safe to call repeatedly.</summary>
    public static void EnsureMachineDir()
    {
        try
        {
            var di = Directory.CreateDirectory(Dir);
            var sec = di.GetAccessControl();
            var users = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
            sec.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            di.SetAccessControl(sec);

            if (Directory.Exists(LegacyDir))
                foreach (var f in Directory.GetFiles(LegacyDir))
                {
                    string name = Path.GetFileName(f);
                    // Only the marker files; ui.json/logs/etc. stay per-user where they belong.
                    if (name is "amd-sdk-consent" or "amd-advanced-pawnio" or "intel-sensors-pawnio"
                             or "fan-pawnio" or "fps-enabled"
                        && !File.Exists(Path.Combine(Dir, name)))
                        File.Copy(f, Path.Combine(Dir, name));
                }
        }
        catch { /* non-fatal: the legacy fallback keeps single-user setups working */ }
    }

    public static bool AmdSdkAccepted => Present("amd-sdk-consent");

    public static bool FpsEnabled => Present("fps-enabled");

    /// <summary>Idempotently create/remove the FPS marker to match the UI's stored setting.</summary>
    public static void SetFps(bool enabled) =>
        Set("fps-enabled", "game FPS monitoring enabled by the user.\n", enabled);

    /// <summary>Marker for "the user enabled the advanced CPU sensors via PawnIO" — gates the helper
    /// loading the signed RyzenSMU module (Tctl on machines the Ryzen Master SDK can't read, i.e.
    /// every mobile APU). Needs PawnIO installed; absence = the SDK-or-nothing behaviour.</summary>
    public static bool AmdAdvancedEnabled => Present("amd-advanced-pawnio");

    /// <summary>Idempotently create/remove the PawnIO marker to match the UI's stored setting.</summary>
    public static void SetAmdAdvanced(bool enabled) =>
        Set("amd-advanced-pawnio", "advanced CPU sensors via PawnIO enabled by the user.\n", enabled);

    /// <summary>Marker for "the user enabled Intel CPU sensors via PawnIO" — gates the helper loading
    /// the signed IntelMSR module (per-core temp via IA32_THERM_STATUS + RAPL package power). Intel
    /// ships no monitoring SDK, so on Intel this is the ONLY route to CPU temp/watts. Needs PawnIO
    /// installed; absence = temp/power stay "—" (PDH-only behaviour).</summary>
    public static bool IntelSensorsEnabled => Present("intel-sensors-pawnio");

    /// <summary>Idempotently create/remove the Intel-sensors marker to match the UI's stored setting.</summary>
    public static void SetIntelSensors(bool enabled) =>
        Set("intel-sensors-pawnio", "Intel CPU sensors via PawnIO enabled by the user.\n", enabled);

    /// <summary>Marker for "the user enabled laptop fan monitoring via PawnIO" — gates the helper
    /// loading the signed LpcACPIEC module and reading the embedded controller. Vendor-agnostic (works
    /// on AMD and Intel laptops); community-sourced per-model register map, so explicitly opt-in and
    /// flagged as best-effort. Needs PawnIO installed.</summary>
    public static bool FanPawnIoEnabled => Present("fan-pawnio");

    /// <summary>Idempotently create/remove the fan marker to match the UI's stored setting.</summary>
    public static void SetFanPawnIo(bool enabled) =>
        Set("fan-pawnio", "laptop fan monitoring via PawnIO enabled by the user.\n", enabled);

    /// <summary>Idempotently create/remove the marker to match the UI's stored consent.</summary>
    public static void SetAmdSdk(bool accepted) =>
        Set("amd-sdk-consent", "AMD Ryzen Master Monitoring SDK EULA accepted by the user.\n", accepted);
}
