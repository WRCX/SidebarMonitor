using System.Runtime.InteropServices;

namespace SidebarMonitor.Agent;

/// <summary>
/// Every graphics adapter the OS knows, via D3DKMT (the kernel-mode-graphics thunk in gdi32). This
/// is vendor-neutral — it lists the NVIDIA card, the Ryzen iGPU and the Microsoft Basic Render
/// software adapter alike — and, unlike NVML, needs neither a vendor SDK nor elevation. It gives us
/// the adapter's name and, crucially, its LUID, which is how the "GPU Engine" performance counter
/// tags each engine instance: that LUID is the join key between "what the GPU is doing" and "which
/// GPU it is". DedicatedVideoMemory tells an integrated GPU (little/no VRAM) from a discrete one.
/// </summary>
internal static class GpuAdapters
{
    /// <summary>A physical adapter. <see cref="LuidLow"/>/<see cref="LuidHigh"/> match the
    /// "luid_0x{High}_0x{Low}" fragment in a GPU Engine counter instance name.</summary>
    public readonly record struct Adapter(uint LuidLow, int LuidHigh, string Name, bool Integrated, ulong DedicatedVram);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint hAdapter;
        public Luid AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SegmentSizeInfo
    {
        public ulong DedicatedVideoMemorySize;
        public ulong DedicatedSystemMemorySize;
        public ulong SharedSystemMemorySize;
    }

    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    private const int MaxPath = 260;

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter p);

    /// <summary>
    /// Enumerates the real GPUs, discrete first then integrated, capped at <paramref name="max"/>.
    /// The Microsoft Basic Render software adapter is dropped. Returns empty on any failure, so the
    /// caller can fall back to NVML-only behaviour.
    /// </summary>
    public static List<Adapter> Enumerate(int max)
    {
        var result = new List<Adapter>();
        try
        {
            // Two-call: first ask how many, then fetch into a buffer of that size.
            var query = new EnumAdapters2 { NumAdapters = 0, pAdapters = IntPtr.Zero };
            if (D3DKMTEnumAdapters2(ref query) != 0 || query.NumAdapters == 0) return result;

            int count = (int)query.NumAdapters;
            int stride = Marshal.SizeOf<AdapterInfo>();
            IntPtr buf = Marshal.AllocHGlobal(stride * count);
            try
            {
                query.pAdapters = buf;
                if (D3DKMTEnumAdapters2(ref query) != 0) return result;

                for (int i = 0; i < (int)query.NumAdapters; i++)
                {
                    var ai = Marshal.PtrToStructure<AdapterInfo>(buf + i * stride);
                    string name = AdapterName(ai.hAdapter);
                    ulong vram = SegmentDedicatedVram(ai.hAdapter);

                    var close = new CloseAdapter { hAdapter = ai.hAdapter };
                    D3DKMTCloseAdapter(ref close);

                    if (name.Length == 0 || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // An iGPU carves its "VRAM" out of system RAM, so it reports little or no
                    // dedicated video memory. 1 GiB is a comfortable discrete/integrated divider.
                    bool integrated = vram < (1UL << 30);
                    result.Add(new Adapter(ai.AdapterLuid.LowPart, ai.AdapterLuid.HighPart, name, integrated, vram));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return []; }

        // Discrete first (by VRAM, largest first), integrated after.
        result.Sort((a, b) => a.Integrated != b.Integrated
            ? a.Integrated.CompareTo(b.Integrated)
            : b.DedicatedVram.CompareTo(a.DedicatedVram));
        if (result.Count > max) result.RemoveRange(max, result.Count - max);
        return result;
    }

    private static string AdapterName(uint hAdapter)
    {
        // D3DKMT_ADAPTERREGISTRYINFO is four MAX_PATH WCHAR strings; the first is the adapter name.
        int bytes = MaxPath * 2 * 4;
        IntPtr data = Marshal.AllocHGlobal(bytes);
        try
        {
            for (int b = 0; b < bytes; b++) Marshal.WriteByte(data, b, 0);
            var q = new QueryAdapterInfo
            {
                hAdapter = hAdapter,
                Type = KMTQAITYPE_ADAPTERREGISTRYINFO,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)bytes,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return "";
            return Marshal.PtrToStringUni(data, MaxPath)?.TrimEnd('\0').Trim() ?? "";
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static ulong SegmentDedicatedVram(uint hAdapter)
    {
        int bytes = Marshal.SizeOf<SegmentSizeInfo>();
        IntPtr data = Marshal.AllocHGlobal(bytes);
        try
        {
            for (int b = 0; b < bytes; b++) Marshal.WriteByte(data, b, 0);
            var q = new QueryAdapterInfo
            {
                hAdapter = hAdapter,
                Type = KMTQAITYPE_GETSEGMENTSIZE,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)bytes,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return 0;
            return Marshal.PtrToStructure<SegmentSizeInfo>(data).DedicatedVideoMemorySize;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
}
