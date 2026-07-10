using System.Runtime.InteropServices;

namespace SidebarMonitor.Etw;

/// <summary>
/// P/Invoke over RyzenShim.dll (the flat-C bridge to the AMD Ryzen Master Monitoring SDK). Gives
/// CPU temperature, package power (PPT) and best-core clock straight from AMD's driver — no
/// HWiNFO, works with HVCI. Needs admin, which the helper already has. Fails softly: if the SDK,
/// driver or shim is absent, IsOpen stays false and the agent keeps using HWiNFO.
/// </summary>
internal sealed class RyzenSdk : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RmCpu
    {
        public double TempC;
        public float PackageW;
        public float PackageLimitW;
        public float FmaxMhz;
        public double PeakSpeedMhz;
        public int CoreCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] CoreFreqMhz;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public double[] CoreTempC;
    }

    [DllImport("RyzenShim.dll")] private static extern int RmOpen();
    [DllImport("RyzenShim.dll")] private static extern int RmRead(out RmCpu data);
    [DllImport("RyzenShim.dll")] private static extern void RmClose();

    public bool IsOpen { get; private set; }

    public static RyzenSdk? TryOpen(out string? error)
    {
        error = null;
        var sdk = new RyzenSdk();
        try
        {
            int rc = RmOpen();
            if (rc != 0) { error = $"RmOpen devolvio {rc}"; return null; }
            sdk.IsOpen = true;
            return sdk;
        }
        catch (DllNotFoundException) { error = "RyzenShim.dll no encontrada"; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public bool TryRead(out RmCpu data)
    {
        data = default;
        if (!IsOpen) return false;
        try { return RmRead(out data) == 0; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (IsOpen) { try { RmClose(); } catch { } IsOpen = false; }
    }
}
