using System.Runtime.InteropServices;
using System.Text;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Agent;

/// <summary>
/// NVIDIA GPU telemetry through NVML, which ships with the driver and is documented and stable.
/// NVAPI is only needed for things NVML does not expose; nothing here needs it.
/// </summary>
internal sealed class Nvml : IDisposable
{
    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr dev, byte[] name, uint len);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out MemoryInfoNative mem);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint percent);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr dev, out uint width);

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfoNative { public ulong Total, Free, Used; }

    private readonly IntPtr[] _devices;
    private readonly string[] _names;

    public int Count => _devices.Length;

    private Nvml(IntPtr[] devices, string[] names)
    {
        _devices = devices;
        _names = names;
    }

    public static Nvml? TryOpen(out string? error)
    {
        error = null;
        try
        {
            int rc = nvmlInit_v2();
            if (rc != 0) { error = $"nvmlInit devolvio {rc}"; return null; }
        }
        catch (DllNotFoundException)
        {
            error = "nvml.dll no encontrada (driver NVIDIA ausente)";
            return null;
        }

        nvmlDeviceGetCount_v2(out uint n);
        n = Math.Min(n, SnapshotLayout.MaxGpus);

        var devices = new List<IntPtr>();
        var names = new List<string>();
        var buf = new byte[96];

        for (uint i = 0; i < n; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2(i, out IntPtr dev) != 0) continue;
            System.Array.Clear(buf);
            nvmlDeviceGetName(dev, buf, (uint)buf.Length);
            devices.Add(dev);
            names.Add(Encoding.ASCII.GetString(buf).TrimEnd('\0'));
        }

        if (devices.Count == 0) { nvmlShutdown(); error = "sin GPUs NVIDIA"; return null; }
        return new Nvml([.. devices], [.. names]);
    }

    public void Fill(int i, ref GpuInfo info)
    {
        IntPtr dev = _devices[i];
        NameField.Set(ref info.Name, _names[i]);

        info.LoadPct = nvmlDeviceGetUtilizationRates(dev, out var util) == 0 ? util.Gpu : float.NaN;
        info.MemControllerPct = util.Memory;

        if (nvmlDeviceGetMemoryInfo(dev, out var mem) == 0) { info.VramUsed = mem.Used; info.VramTotal = mem.Total; }
        info.TempC = nvmlDeviceGetTemperature(dev, 0, out uint t) == 0 ? t : float.NaN;
        info.PowerW = nvmlDeviceGetPowerUsage(dev, out uint mw) == 0 ? mw / 1000f : float.NaN;
        info.CoreClockMhz = nvmlDeviceGetClockInfo(dev, 0, out uint core) == 0 ? core : 0;
        info.MemClockMhz = nvmlDeviceGetClockInfo(dev, 2, out uint mclk) == 0 ? mclk : 0;
        info.FanPct = nvmlDeviceGetFanSpeed(dev, out uint fan) == 0 ? fan : 0;
        info.PcieWidth = nvmlDeviceGetCurrPcieLinkWidth(dev, out uint w) == 0 ? w : 0;
    }

    public void Dispose() => nvmlShutdown();
}
