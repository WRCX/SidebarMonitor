using System.IO;
using System.Runtime.InteropServices;

namespace SidebarMonitor.Etw;

/// <summary>
/// P/Invoke over PawnIOLib.dll: loads PawnIO's signed RyzenSMU module and reads Tctl from the SMU's
/// thermal register (SMN 0x00059800) through the northbridge indirect pair. This is the CPU
/// temperature on machines the Ryzen Master SDK can't read — every mobile APU (Phoenix/7040 etc.);
/// on desktop it refines the SDK's die-average into the hotspot HWiNFO shows. Opt-in (the user
/// installs PawnIO and flips the toggle) and fails softly: without PawnIO, the module, or a
/// supported CPU, TryOpen returns null and the helper behaves exactly as before.
///
/// The PCI 0x60/0x64 config window is shared with anything else poking the SMU (HWiNFO, Ryzen
/// Master), so every read serializes on the conventional <c>Global\Access_PCI</c> mutex.
/// </summary>
internal sealed class PawnIoCpu : IDisposable
{
    /// <summary>THM_TCON_CUR_TMP: current Tctl, SMN address on all Zen families (17h/19h/1Ah).</summary>
    private const uint ThmTconCurTmp = 0x00059800;

    public struct Data
    {
        public double TctlC;
    }

    // PawnIOLib.dll lives in PawnIO's install dir (not on PATH); a resolver maps the import to the
    // absolute path. All entry points return HRESULTs (0 = S_OK).
    private const string Dll = "PawnIOLib";

    [DllImport(Dll)] private static extern int pawnio_version(out uint version);
    [DllImport(Dll)] private static extern int pawnio_open(out nint handle);
    [DllImport(Dll)] private static extern int pawnio_load(nint handle, byte[] blob, nuint size);
    [DllImport(Dll)] private static extern int pawnio_execute(
        nint handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);
    [DllImport(Dll)] private static extern int pawnio_close(nint handle);

    static PawnIoCpu()
    {
        NativeLibrary.SetDllImportResolver(typeof(PawnIoCpu).Assembly, (name, _, _) =>
        {
            if (name != Dll) return nint.Zero;
            return NativeLibrary.TryLoad(InstalledLibPath, out nint lib) ? lib : nint.Zero;
        });
    }

    private static string InstalledLibPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

    private nint _handle;
    private readonly Mutex _pciMutex = new(false, @"Global\Access_PCI");
    private readonly ulong[] _in = new ulong[1];
    private readonly ulong[] _out = new ulong[1];

    public bool IsOpen { get; private set; }

    private PawnIoCpu() { }

    /// <summary>
    /// Null (with a human-readable reason) when PawnIO isn't installed, the module blob isn't next
    /// to the helper, or the CPU isn't a supported Zen part (the signed module checks family/model
    /// itself and refuses to load elsewhere).
    /// </summary>
    public static PawnIoCpu? TryOpen(out string? error)
    {
        error = null;
        string binPath = Path.Combine(AppContext.BaseDirectory, "RyzenSMU.bin");
        if (!File.Exists(InstalledLibPath)) { error = "PawnIO no instalado (falta PawnIOLib.dll)"; return null; }
        if (!File.Exists(binPath)) { error = "RyzenSMU.bin no encontrado junto al helper"; return null; }

        var p = new PawnIoCpu();
        try
        {
            int hr = pawnio_open(out p._handle);
            if (hr != 0) { error = $"pawnio_open devolvio 0x{hr:X8}"; return null; }

            byte[] blob = File.ReadAllBytes(binPath);
            hr = pawnio_load(p._handle, blob, (nuint)blob.Length);
            if (hr != 0)
            {
                // The module's own main() rejects non-AMD / unknown families with STATUS_NOT_SUPPORTED.
                pawnio_close(p._handle);
                error = $"pawnio_load devolvio 0x{hr:X8} (CPU no soportada por RyzenSMU?)";
                return null;
            }

            p.IsOpen = true;
            return p;
        }
        catch (DllNotFoundException) { error = "PawnIOLib.dll no cargable"; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public bool TryRead(out Data data)
    {
        data = default;
        if (!IsOpen) return false;

        bool got = false;
        try { got = _pciMutex.WaitOne(50); }
        catch (AbandonedMutexException) { got = true; }   // previous holder died; the window is ours
        if (!got) return false;                            // contended: skip this window, no stale data
        try
        {
            _in[0] = ThmTconCurTmp;
            if (pawnio_execute(_handle, "ioctl_read_smu_register", _in, 1, _out, 1, out _) != 0)
                return false;
            double tctl = DecodeTctl(_out[0]);
            if (tctl <= 0 || tctl >= 150) return false;   // implausible: treat as a failed read
            data.TctlC = tctl;
            return true;
        }
        catch { return false; }
        finally { _pciMutex.ReleaseMutex(); }
    }

    /// <summary>
    /// THM_TCON_CUR_TMP → °C: bits [31:21] are the current temperature in 0.125 °C steps; bit 19
    /// (CUR_TEMP_RANGE_SEL) selects the -49..206 °C range, i.e. subtract the 49 °C offset. Same
    /// decode LibreHardwareMonitor and HWiNFO apply. Verified on a 7840HS (range bit set, ~56 °C).
    /// </summary>
    public static double DecodeTctl(ulong raw)
    {
        double t = ((raw >> 21) & 0x7FF) * 0.125;
        if ((raw & (1u << 19)) != 0) t -= 49;
        return t;
    }

    public void Dispose()
    {
        if (IsOpen) { try { pawnio_close(_handle); } catch { } IsOpen = false; }
        _pciMutex.Dispose();
    }
}
