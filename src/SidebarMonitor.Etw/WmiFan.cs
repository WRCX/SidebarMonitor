using System.Globalization;
using System.Management;

namespace SidebarMonitor.Etw;

/// <summary>
/// Laptop fan RPM via the HP WMI BIOS interface (<c>root\wmi : hpqBIntM.hpqBIOSInt128</c>), the
/// "Victus S / OMEN" gaming fan-speed queries. This is the fan source for HP gaming laptops
/// (Victus/OMEN) whose fan speed is <b>not</b> mirrored to a plain EC register — so the per-model
/// NBFC map in <see cref="EcFan"/> finds nothing for them. Verified live on a Victus 16-s0xxx (board
/// 8BD4): under sustained load the CPU fan ramps (2300→2700 rpm) and the GPU fan wakes with the dGPU.
///
/// It reports both a raw RPM (from the live GET) and a duty % (RPM over the fan curve's top speed,
/// read once from the fan table), so the UI can show "2700 rpm (47%)". The classic max-speed query
/// (0x26) returns 0 on this board, so the denominator comes from the fan table (0x2F) instead.
///
/// <b>Read-only:</b> only GET queries are ever issued — the live speed GET (<c>0x2D</c>) and the fan
/// table GET (<c>0x2F</c>) — never any SET (<c>0x2E</c>/<c>0x27</c>), so fan state is never changed.
/// Needs admin (the helper is elevated). Protocol from the Linux <c>hp-wmi</c> Victus S support:
/// Command = <c>HPWMI_GM</c> (<c>0x20008</c>), Sign = "SECU"; live reply <c>Data[0]*100</c> = CPU rpm,
/// <c>Data[1]*100</c> = GPU rpm; the table is <c>Data[1]</c> entries of <c>[cpu, gpu, unknown]</c>
/// (each *100 = rpm) after a 2-byte header.
/// </summary>
internal sealed class WmiFan : IDisposable
{
    private const uint HpwmiGm = 0x20008;      // HPWMI_GM (gamers' mode) command
    private const uint FanSpeedGet = 0x2D;     // HPWMI_VICTUS_S_FAN_SPEED_GET_QUERY (read-only)
    private const uint FanTableGet = 0x2F;     // HPWMI_VICTUS_S_GET_FAN_TABLE_QUERY (read-only)
    private static readonly byte[] Secu = [0x53, 0x45, 0x43, 0x55];   // "SECU" signature

    /// <summary>Fallback max rpm when the fan table can't be decoded to a plausible top speed. HP
    /// gaming-laptop fans top out around 5500-6500 rpm; 6000 keeps the % sane on an unknown table.</summary>
    private const int FallbackMaxRpm = 6000;

    private readonly ManagementObject _inst;
    private readonly ManagementClass _inClass;

    private WmiFan(ManagementObject inst, ManagementClass inClass) { _inst = inst; _inClass = inClass; }

    /// <summary>Top CPU fan rpm from the fan curve table — the 100% denominator for the duty %.</summary>
    public int MaxCpuRpm { get; private set; } = FallbackMaxRpm;
    /// <summary>Top GPU fan rpm from the fan curve table.</summary>
    public int MaxGpuRpm { get; private set; } = FallbackMaxRpm;

    /// <summary>
    /// Null (with a reason) when this isn't an HP machine exposing the WMI BIOS interface, or the
    /// fan-speed query doesn't answer a probe read. Intended for HP gaming laptops (Victus/OMEN).
    /// </summary>
    public static WmiFan? TryOpen(out string? error)
    {
        error = null;
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Options.EnablePrivileges = true;
            scope.Options.Impersonation = ImpersonationLevel.Impersonate;
            scope.Connect();

            ManagementObject? inst = null;
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM hpqBIntM")))
                foreach (ManagementObject m in s.Get()) { inst = m; break; }
            if (inst is null) { error = "clase HP WMI 'hpqBIntM' ausente (no es un portatil HP compatible)"; return null; }

            var inClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);
            var fan = new WmiFan(inst, inClass);
            // Probe once so an unsupported query fails here (→ fall back to EcFan) rather than silently
            // returning nothing every window.
            if (!fan.TryReadRpm(out _, out _))
            {
                fan.Dispose();
                error = "la query de ventilador (0x2D) no respondio en este modelo";
                return null;
            }
            fan.ReadMaxFromTable();
            return fan;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>CPU/GPU fan rpm (<c>Data[fan]*100</c>). False when the query failed this window (e.g.
    /// transient WMI/BIOS contention) — the caller keeps the last good value.</summary>
    public bool TryReadRpm(out int cpuRpm, out int gpuRpm)
    {
        cpuRpm = gpuRpm = 0;
        var d = RawCall(FanSpeedGet, size: 0, new byte[128]);
        if (d is null || d.Length < 2) return false;
        cpuRpm = d[0] * 100;
        gpuRpm = d[1] * 100;
        return true;
    }

    /// <summary>CPU fan duty % over the table's top speed (0..100), or NaN when max is unknown.</summary>
    public float ToPct(int cpuRpm) =>
        MaxCpuRpm > 0 ? Math.Clamp(cpuRpm * 100f / MaxCpuRpm, 0f, 100f) : float.NaN;

    /// <summary>Reads the fan curve table (0x2F) once and derives the top CPU/GPU rpm. The table is a
    /// 2-byte header (<c>[0]</c> unknown, <c>[1]</c> entry count) then entries of <c>[cpu, gpu,
    /// unknown]</c>, each value *100 = rpm. Leaves the fallback max if the table is implausible.</summary>
    private void ReadMaxFromTable()
    {
        var d = RawCall(FanTableGet, size: 0, new byte[128]);
        if (d is null) return;
        var (maxCpu, maxGpu) = DecodeTableMax(d);
        // Only trust a physically sensible top speed; otherwise keep the fallback.
        if (maxCpu is >= 2000 and <= 12000) MaxCpuRpm = maxCpu;
        if (maxGpu is >= 2000 and <= 12000) MaxGpuRpm = maxGpu;
    }

    /// <summary>Pure decode of the fan-table reply into (maxCpuRpm, maxGpuRpm). The table is a 2-byte
    /// header (<c>[0]</c> unknown, <c>[1]</c> entry count) then <c>[cpu, gpu, unknown]</c> triples,
    /// each byte *100 = rpm. Returns (0, 0) on a too-short buffer. Split out so the decode is unit
    /// tested against a captured table without hitting WMI.</summary>
    internal static (int cpuRpm, int gpuRpm) DecodeTableMax(byte[] d)
    {
        if (d.Length < 5) return (0, 0);
        int count = Math.Min(d[1], (d.Length - 2) / 3);
        int maxCpu = 0, maxGpu = 0;
        for (int i = 0, off = 2; i < count; i++, off += 3)
        {
            if (d[off] > maxCpu) maxCpu = d[off];
            if (d[off + 1] > maxGpu) maxGpu = d[off + 1];
        }
        return (maxCpu * 100, maxGpu * 100);
    }

    /// <summary>One hpqBIOSInt128 GET call; returns the 128-byte reply Data, or null on rc!=0/failure.
    /// The provider requires <c>hpqBData.Length == Size</c> when Size &gt; 0; Size = 0 is used for our
    /// GETs (all input sizes return the same reply).</summary>
    private byte[]? RawCall(uint commandType, uint size, byte[] data)
    {
        try
        {
            ManagementObject inData = _inClass.CreateInstance();
            inData["Sign"] = Secu;
            inData["Command"] = HpwmiGm;
            inData["CommandType"] = commandType;
            inData["Size"] = size;
            inData["hpqBData"] = data;

            using var mp = _inst.GetMethodParameters("hpqBIOSInt128");
            mp["InData"] = inData;
            using var outp = _inst.InvokeMethod("hpqBIOSInt128", mp, null);
            var od = (ManagementBaseObject)outp["OutData"];
            if (Convert.ToUInt32(od["rwReturnCode"], CultureInfo.InvariantCulture) != 0) return null;
            return (byte[])od["Data"];
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _inst.Dispose();
        _inClass.Dispose();
    }
}
