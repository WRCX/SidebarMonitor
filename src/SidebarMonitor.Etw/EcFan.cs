using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SidebarMonitor.Etw;

/// <summary>
/// Laptop fan duty (%), read from the ACPI Embedded Controller through PawnIO's signed
/// <c>LpcACPIEC</c> module. That module only exposes raw port I/O to the EC ports (0x62 data / 0x66
/// status-command), so the standard ACPI EC read handshake runs here, serialized on
/// <c>Global\Access_EC</c> — the same lock HWiNFO/NBFC use — held across the whole 4-step transaction
/// so the OS EC driver can't interleave. <b>Read-only: we never write the EC.</b>
///
/// Which register holds the fan level, and its value range, is per laptop model. That mapping is a
/// fact table derived from <b>NoteBook FanControl</b> (nbfc-linux, GPL-3.0) — see <c>FanDb.tsv</c>,
/// an embedded resource. We match the machine's DMI model string against it; an unlisted model = no
/// reading (the UI shows "—"). Because a community map can point at the wrong register on an
/// unverified model, this is opt-in and surfaced to the user as best-effort. Verified live on an
/// ASUS N56JR (register 0x97, level 0..8: 12.5% idle → 37.5% under load).
/// </summary>
internal sealed class EcFan : IDisposable
{
    private const int EC_DATA = 0x62;
    private const int EC_SC   = 0x66;
    private const byte IBF = 0x02;   // status bit1: input buffer full (write not yet consumed)
    private const byte OBF = 0x01;   // status bit0: output buffer full (a byte is waiting)
    private const byte RD_EC = 0x80; // "read EC" command

    public readonly record struct FanReg(int Register, int MinRead, int MaxRead, bool Word);

    private const string Dll = "PawnIOLib";
    [DllImport(Dll)] private static extern int pawnio_open(out nint handle);
    [DllImport(Dll)] private static extern int pawnio_load(nint handle, byte[] blob, nuint size);
    [DllImport(Dll)] private static extern int pawnio_execute(
        nint handle, [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);
    [DllImport(Dll)] private static extern int pawnio_close(nint handle);

    static EcFan()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(EcFan).Assembly, (name, _, _) =>
                name == Dll && NativeLibrary.TryLoad(InstalledLibPath, out nint lib) ? lib : nint.Zero);
        }
        catch (InvalidOperationException) { /* PawnIoCpu/IntelMsr already registered an equivalent resolver */ }
    }

    private static string InstalledLibPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

    private nint _handle;
    private readonly Mutex _ecLock = new(false, @"Global\Access_EC");
    private readonly ulong[] _in2 = new ulong[2];
    private readonly ulong[] _out1 = new ulong[1];
    private FanReg _reg;

    public bool IsOpen { get; private set; }
    /// <summary>The DMI model this instance matched in the map (for the log / diagnostics).</summary>
    public string Model { get; private set; } = "";

    private EcFan() { }

    /// <summary>
    /// Null (with a reason) when PawnIO isn't installed, the module blob isn't next to the helper, or
    /// the machine's DMI model isn't in the NBFC-derived map. <paramref name="candidates"/> are the DMI
    /// strings to try (system model, baseboard product) — the first that matches wins.
    /// </summary>
    public static EcFan? TryOpen(IEnumerable<string> candidates, out string? error)
    {
        error = null;
        string binPath = Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin");
        if (!File.Exists(InstalledLibPath)) { error = "PawnIO no instalado (falta PawnIOLib.dll)"; return null; }
        if (!File.Exists(binPath)) { error = "LpcACPIEC.bin no encontrado junto al helper"; return null; }

        var map = FanDb.Value;
        FanReg reg = default;
        string matched = "";
        foreach (var cand in candidates)
        {
            if (!string.IsNullOrWhiteSpace(cand) && map.TryGetValue(cand.Trim(), out reg)) { matched = cand.Trim(); break; }
        }
        if (matched.Length == 0) { error = "modelo no soportado (no esta en el mapa NBFC)"; return null; }

        var p = new EcFan { _reg = reg, Model = matched };
        try
        {
            int hr = pawnio_open(out p._handle);
            if (hr != 0) { error = $"pawnio_open devolvio 0x{hr:X8}"; return null; }
            byte[] blob = File.ReadAllBytes(binPath);
            hr = pawnio_load(p._handle, blob, (nuint)blob.Length);
            if (hr != 0) { pawnio_close(p._handle); error = $"pawnio_load devolvio 0x{hr:X8}"; return null; }
            p.IsOpen = true;
            return p;
        }
        catch (DllNotFoundException) { error = "PawnIOLib.dll no cargable"; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Fan duty %, or NaN when the read failed this window (EC contention). Retries a couple
    /// of times — the OS EC driver occasionally wins the port and a single read times out.</summary>
    public float TryReadPct()
    {
        if (!IsOpen) return float.NaN;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (TryReadRaw(out int raw))
            {
                int span = _reg.MaxRead - _reg.MinRead;
                if (span == 0) return float.NaN;
                float pct = 100f * (raw - _reg.MinRead) / span;   // handles inverted maps (min>max) too
                return Math.Clamp(pct, 0f, 100f);
            }
        }
        return float.NaN;
    }

    private bool TryReadRaw(out int value)
    {
        value = 0;
        bool held;
        try { held = _ecLock.WaitOne(100); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) return false;
        try
        {
            if (!ReadByte(_reg.Register, out int lo)) return false;
            value = lo;
            if (_reg.Word)
            {
                if (!ReadByte(_reg.Register + 1, out int hi)) return false;
                value = lo | (hi << 8);
            }
            return true;
        }
        finally { _ecLock.ReleaseMutex(); }
    }

    // One ACPI EC byte read: RD_EC → address → data, gated on the IBF/OBF status flags.
    private bool ReadByte(int offset, out int value)
    {
        value = 0;
        if (!WaitStatus(IBF, want: false)) return false;
        if (!PioWrite(EC_SC, RD_EC)) return false;
        if (!WaitStatus(IBF, want: false)) return false;
        if (!PioWrite(EC_DATA, (byte)offset)) return false;
        if (!WaitStatus(OBF, want: true)) return false;
        if (!PioRead(EC_DATA, out byte b)) return false;
        value = b;
        return true;
    }

    private bool WaitStatus(byte mask, bool want)
    {
        for (int spin = 0; spin < 2000; spin++)
        {
            if (!PioRead(EC_SC, out byte st)) return false;
            if (((st & mask) != 0) == want) return true;
            if ((spin & 0x3F) == 0x3F) Thread.Sleep(1);
        }
        return false;
    }

    private bool PioRead(int port, out byte value)
    {
        _in2[0] = (ulong)port;
        int rc = pawnio_execute(_handle, "ioctl_pio_read", _in2, 1, _out1, 1, out _);
        value = (byte)_out1[0];
        return rc == 0;
    }

    private bool PioWrite(int port, byte val)
    {
        _in2[0] = (ulong)port; _in2[1] = val;
        return pawnio_execute(_handle, "ioctl_pio_write", _in2, 2, [], 0, out _) == 0;
    }

    public void Dispose()
    {
        if (IsOpen) { try { pawnio_close(_handle); } catch { } IsOpen = false; }
        _ecLock.Dispose();
    }

    // ---- The embedded NBFC-derived model → register map -------------------------------------------
    private static readonly Lazy<Dictionary<string, FanReg>> FanDb = new(LoadDb);

    private static Dictionary<string, FanReg> LoadDb()
    {
        var d = new Dictionary<string, FanReg>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(EcFan).Assembly;
            string? name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("FanDb.tsv", StringComparison.Ordinal));
            if (name is null) return d;
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return d;
            using var r = new StreamReader(s);
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (f.Length < 5) continue;
                if (!TryHex(f[1], out int reg)) continue;
                if (!int.TryParse(f[2], out int min) || !int.TryParse(f[3], out int max)) continue;
                bool word = f[4] == "1";
                d[f[0]] = new FanReg(reg, min, max, word);
            }
        }
        catch { /* map unavailable → every model unsupported */ }
        return d;
    }

    private static bool TryHex(string s, out int value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
