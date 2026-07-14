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

        /// <summary>The clock fields below are live: this version's layout maps the global boost
        /// ceiling and/or the per-core effective clocks.</summary>
        public bool HasClocks;
        public float LimitMhz;    // SMU's dynamic global frequency limit; 0 = not in this layout
        public float BestEffMhz;  // highest per-core effective clock; 0 = not in this layout
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
    // Sized in TryOpen once the version is known: desktop layouts keep their clock fields hundreds
    // of floats past the power header, so the per-window read spans exactly what the map needs.
    private ulong[] _pmQwords = new ulong[PmFloats / 2];
    private float[] _pmFloats = new float[PmFloats];
    private ulong _pmVersion;    // resolved PM_Table version, 0 = unresolved (no PM_Table access)
    private bool _pmEnabled;     // we have an offset map for _pmVersion → power fields flow

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

            // Resolve the PM_Table once (its address is stable per boot). The version is kept even
            // when we have no offset map for it — the diagnostics dump needs it — but only a mapped
            // version enables the power fields; anything else degrades to Tctl-only.
            var out2 = new ulong[2];
            bool got = p.AcquirePci();
            try
            {
                if (got && pawnio_execute(p._handle, "ioctl_resolve_pm_table", [], 0, out2, 2, out _) == 0)
                {
                    p._pmVersion = out2[0];
                    p._pmEnabled = KnownPmTableVersion(out2[0]);
                    // Size the per-window read to this layout's furthest mapped offset (desktop
                    // tables keep clocks well past the power header).
                    if (TryGetMap(out2[0], out var map))
                    {
                        int floats = Math.Max(PmFloats, map.FloatsNeeded);
                        p._pmQwords = new ulong[(floats + 1) / 2];
                        p._pmFloats = new float[p._pmQwords.Length * 2];
                    }
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

    /// <summary>PM_Table version this CPU reports, 0 if unresolved; for the log, the debug overlay
    /// and the "add my CPU" diagnostics dump.</summary>
    public ulong PmTableVersion => _pmVersion;

    /// <summary>True when the resolved version has an offset map (power fields will flow).</summary>
    public bool PmTableSupported => _pmEnabled;

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
            if (_pmEnabled
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
    /// Maps a known PM_Table layout onto <see cref="Data"/>'s power fields. Offsets are float
    /// indexes. The header is interleaved limit/value pairs and its first six floats — STAPM [0]/[1]
    /// and fast/slow PPT [2]/[3]/[4]/[5] — sit at the same place in every known APU table; only the
    /// VDD TDC pair and the THM limit move by version. Sources: our empirical Phoenix map (0x4C0007,
    /// live idle/load diffing on a 7840HS — docs/amd-advanced-pawnio.md), which agrees exactly with
    /// the per-version tables RyzenAdj maintains (FlyGoat/RyzenAdj lib/api.c, ported here as data);
    /// the fast PPT value equalled the socket-power field in every capture. Guards keep a garbled
    /// read (zero limits, absurd watts) from publishing.
    /// </summary>
    /// <summary>Per-version offset map. Power offsets follow the APU header convention (PPT pair at
    /// [2]/[3]); -1 = the layout has no (trustworthy) slot for that field. Clock offsets are -1
    /// until a version's dump identifies them (values in GHz in every layout seen so far).</summary>
    internal readonly record struct PmMap(int TdcL, int TdcV, int ThmL, int FreqLim = -1, int EffFirst = -1, int EffCount = 0)
    {
        /// <summary>Floats the per-window read must span to cover every mapped offset.</summary>
        public int FloatsNeeded => Math.Max(Math.Max(PmFloats, FreqLim + 1), EffFirst >= 0 ? EffFirst + EffCount : 0);
    }

    internal static bool TryGetMap(ulong version, out PmMap map)
    {
        switch (version)
        {
            // Raven Ridge / Picasso / Dali (Zen1 APUs). Their table has no trustworthy THM-limit
            // slot (that offset holds a per-core temperature), so TjMaxC stays 0 and the UI keeps
            // its generic thresholds.
            case 0x1E0001 or 0x1E0002 or 0x1E0003 or 0x1E0004 or 0x1E0005 or 0x1E000A or 0x1E0101:
                map = new PmMap(6, 7, -1); return true;

            // The classic APU header: Renoir/Lucienne (0x37xxxx), Cezanne (0x40xxxx),
            // Rembrandt (0x45xxxx), Phoenix/Hawk Point (0x4C0006-9; 0x4C0007 verified live).
            case >= 0x370000 and <= 0x370005:
            case >= 0x400001 and <= 0x400005:
            case 0x450004 or 0x450005:
            case >= 0x4C0006 and <= 0x4C0009:
                map = new PmMap(8, 9, 16); return true;

            // Strix Point / Krackan Point: the TDC block moved down.
            case 0x5D0008 or 0x5D0009 or 0x5D000B or 0x650005:
                map = new PmMap(12, 13, 16); return true;

            default: map = default; return false;   // unknown layout: never guess offsets
        }
    }

    public static bool TryMapPmTable(ulong version, ReadOnlySpan<float> t, ref Data d)
    {
        if (!TryGetMap(version, out var m)) return false;

        if (t.Length < PmFloats) return false;
        float pptLimit = t[2], pptValue = t[3];
        if (!(pptLimit > 0) || !(pptValue >= 0) || pptValue > 1000) return false;
        d.HasPower = true;
        d.PackageW = pptValue;
        d.PptPct = Math.Clamp(100f * pptValue / pptLimit, 0f, 200f);
        d.TdcPct = m.TdcL >= 0 && t[m.TdcL] > 0 ? Math.Clamp(100f * t[m.TdcV] / t[m.TdcL], 0f, 200f) : 0f;
        d.TjMaxC = m.ThmL >= 0 && t[m.ThmL] is > 0 and < 150 ? t[m.ThmL] : 0f;

        // Clocks, where this layout maps them. Tables carry GHz; plausibility-gate each field so a
        // garbled read (or a parked-cores idle where every effective clock is ~0) never publishes
        // nonsense. BestEffMhz==0 with HasClocks set just means "nothing boosting this window".
        if (m.FreqLim >= 0 && m.FreqLim < t.Length)
        {
            float lim = t[m.FreqLim];
            if (lim is > 0.4f and < 7f)   // GHz
            {
                d.HasClocks = true;
                d.LimitMhz = lim * 1000f;
            }
        }
        if (m.EffFirst >= 0 && m.EffFirst + m.EffCount <= t.Length)
        {
            float best = 0;
            for (int i = m.EffFirst; i < m.EffFirst + m.EffCount; i++)
                if (t[i] is > 0.2f and < 7f && t[i] > best) best = t[i];   // GHz; parked cores ~0
            if (best > 0)
            {
                d.HasClocks = true;
                d.BestEffMhz = best * 1000f;
            }
        }
        return true;
    }

    /// <summary>True when we have an offset map for this PM_Table version (see TryMapPmTable).</summary>
    public static bool KnownPmTableVersion(ulong version)
    {
        var d = default(Data);
        Span<float> probe = stackalloc float[PmFloats];
        probe[2] = 1f;   // minimal plausible header so only the version switch decides
        return TryMapPmTable(version, probe, ref d);
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

    /// <summary>
    /// One-shot PM_Table dump for the community "support my CPU" flow (the GitHub issue template):
    /// version + the first page of floats, invariant culture. Works on UNKNOWN versions on purpose —
    /// that's the whole point. Null-safe text on any failure, never throws.
    /// </summary>
    public string DumpDiagnostics()
    {
        var sb = new System.Text.StringBuilder(8192);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        sb.AppendLine(ci, $"pm_table_version=0x{_pmVersion:X}");
        sb.AppendLine(ci, $"pm_table_mapped={_pmEnabled}");
        if (_pmVersion == 0) { sb.AppendLine("pm_table: no resuelto (el SMU no devolvio la tabla)"); return sb.ToString(); }

        if (!AcquirePci()) { sb.AppendLine("pm_table: mutex PCI ocupado, reintenta"); return sb.ToString(); }
        try
        {
            // One page (512 qwords = 1024 floats) is the module's per-call cap and covers every
            // known header; enough to map a new version.
            var q = new ulong[512];
            if (pawnio_execute(_handle, "ioctl_update_pm_table", [], 0, [], 0, out _) != 0)
                sb.AppendLine("pm_table: update rebotado (SMU ocupado); volcando la ultima copia");
            if (pawnio_execute(_handle, "ioctl_read_pm_table", [], 0, q, (nuint)q.Length, out _) != 0)
            {
                sb.AppendLine("pm_table: lectura fallida");
                return sb.ToString();
            }
            var f = new float[q.Length * 2];
            Buffer.BlockCopy(q, 0, f, 0, q.Length * 8);
            for (int i = 0; i < f.Length; i++)
                if (f[i] != 0 && float.IsFinite(f[i]) && MathF.Abs(f[i]) < 1e9f)
                    sb.AppendLine(ci, $"[{i}]={f[i]:F4}");
        }
        catch (Exception ex) { sb.AppendLine("pm_table: excepcion " + ex.Message); }
        finally { _pciMutex.ReleaseMutex(); }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (IsOpen) { try { pawnio_close(_handle); } catch { } IsOpen = false; }
        _pciMutex.Dispose();
    }
}
