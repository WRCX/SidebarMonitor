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

    /// <summary>The one PM_Table layout we have validated (Phoenix, mapped empirically on a 7840HS
    /// by diffing idle/load dumps — see docs/amd-advanced-pawnio.md). Any other version: Tctl only.</summary>
    private const ulong PmTablePhoenix = 0x4C0007;

    /// <summary>Floats needed from the PM_Table head (the limit/value header); qwords = half.</summary>
    private const int PmFloats = 24;

    public struct Data
    {
        public double TctlC;
        /// <summary>The PM_Table was read and its layout is known — the power fields below are live.
        /// False on unsupported table versions (then only Tctl is valid).</summary>
        public bool HasPower;
        public float PackageW;   // PPT fast value == socket power (identical in every capture)
        public float PptPct;     // fast PPT usage as % of its limit
        public float TdcPct;     // VDD TDC usage as % of its limit
        public float TjMaxC;     // THM limit (100 °C on Phoenix) — the real throttle temperature
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
    private readonly ulong[] _pmQwords = new ulong[PmFloats / 2];
    private readonly float[] _pmFloats = new float[PmFloats];
    private ulong _pmVersion;   // 0 = PM_Table unavailable or layout unknown → Tctl only

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

            // Resolve the PM_Table once (its address is stable per boot). Only a version we have a
            // validated map for enables the power fields; anything else degrades to Tctl-only.
            var out2 = new ulong[2];
            bool got = p.AcquirePci();
            try
            {
                if (got && pawnio_execute(p._handle, "ioctl_resolve_pm_table", [], 0, out2, 2, out _) == 0
                        && out2[0] == PmTablePhoenix)
                {
                    p._pmVersion = out2[0];
                    // Warm-up refresh: the first SMU transfer after resolve can bounce (busy/prereq);
                    // absorbing it here means TryRead's per-window refresh starts clean.
                    pawnio_execute(p._handle, "ioctl_update_pm_table", [], 0, [], 0, out _);
                }
            }
            finally { if (got) p._pciMutex.ReleaseMutex(); }

            return p;
        }
        catch (DllNotFoundException) { error = "PawnIOLib.dll no cargable"; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>PM_Table version this CPU reports, 0 if unresolved; for the startup log.</summary>
    public ulong PmTableVersion => _pmVersion;

    private bool AcquirePci()
    {
        try { return _pciMutex.WaitOne(50); }
        catch (AbandonedMutexException) { return true; }   // previous holder died; the window is ours
    }

    public bool TryRead(out Data data)
    {
        data = default;
        if (!IsOpen) return false;

        if (!AcquirePci()) return false;   // contended: skip this window, no stale data
        try
        {
            _in[0] = ThmTconCurTmp;
            if (pawnio_execute(_handle, "ioctl_read_smu_register", _in, 1, _out, 1, out _) != 0)
                return false;
            double tctl = DecodeTctl(_out[0]);
            if (tctl <= 0 || tctl >= 150) return false;   // implausible: treat as a failed read
            data.TctlC = tctl;

            // Power from the PM_Table (known layout only). A refresh can transiently bounce off a
            // busy SMU — then this window is temp-only, never stale power.
            if (_pmVersion != 0
                && pawnio_execute(_handle, "ioctl_update_pm_table", [], 0, [], 0, out _) == 0
                && pawnio_execute(_handle, "ioctl_read_pm_table", [], 0, _pmQwords, (nuint)_pmQwords.Length, out _) == 0)
            {
                Buffer.BlockCopy(_pmQwords, 0, _pmFloats, 0, _pmQwords.Length * 8);
                TryMapPmTable(_pmVersion, _pmFloats, ref data);
            }
            return true;
        }
        catch { return false; }
        finally { _pciMutex.ReleaseMutex(); }
    }

    /// <summary>
    /// Maps a known PM_Table layout onto <see cref="Data"/>'s power fields. Phoenix (0x4C0007),
    /// mapped empirically on the 7840HS: interleaved limit/value pairs — STAPM [0]/[1],
    /// fast PPT [2]/[3] (the fast value equals the socket power field at [47] in every capture),
    /// slow PPT [4]/[5], VDD TDC [8]/[9], THM [16]/[17]. Guards keep a garbled read (zero limits,
    /// absurd watts) from publishing.
    /// </summary>
    public static bool TryMapPmTable(ulong version, ReadOnlySpan<float> t, ref Data d)
    {
        if (version != PmTablePhoenix || t.Length < 18) return false;
        float pptLimit = t[2], pptValue = t[3], tdcLimit = t[8], tdcValue = t[9], thmLimit = t[16];
        if (!(pptLimit > 0) || !(pptValue >= 0) || pptValue > 1000) return false;
        d.HasPower = true;
        d.PackageW = pptValue;
        d.PptPct = Math.Clamp(100f * pptValue / pptLimit, 0f, 200f);
        d.TdcPct = tdcLimit > 0 ? Math.Clamp(100f * tdcValue / tdcLimit, 0f, 200f) : 0f;
        d.TjMaxC = thmLimit is > 0 and < 150 ? thmLimit : 0f;
        return true;
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
