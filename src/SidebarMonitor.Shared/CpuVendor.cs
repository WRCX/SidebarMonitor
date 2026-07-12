using System.Runtime.Intrinsics.X86;

namespace SidebarMonitor.Shared;

public enum CpuMaker
{
    Unknown = 0,
    Amd = 1,
    Intel = 2,
}

/// <summary>
/// CPU vendor + brand string straight from CPUID. AOT-safe, dependency-free, no elevation, works in
/// every process. Drives the platform branching: AMD gets the Ryzen Master SDK (with its first-run
/// EULA), Intel gets the "deep sensors need a ring0 driver" path, everything else degrades to PDH.
/// </summary>
public static class CpuVendor
{
    private static CpuMaker? _maker;
    private static string? _brand;

    /// <summary>AuthenticAMD / GenuineIntel / else Unknown. Cached; CPUID never changes at runtime.</summary>
    public static CpuMaker Maker => _maker ??= DetectMaker();

    public static bool IsAmd => Maker == CpuMaker.Amd;
    public static bool IsIntel => Maker == CpuMaker.Intel;

    /// <summary>The 12-char vendor id, e.g. "AuthenticAMD" / "GenuineIntel", or "" if unavailable.</summary>
    public static string VendorId { get; private set; } = "";

    /// <summary>Marketing brand string, e.g. "AMD Ryzen 7 7800X3D 8-Core Processor". "" if unavailable.</summary>
    public static string Brand => _brand ??= DetectBrand();

    private static CpuMaker DetectMaker()
    {
        try
        {
            if (!X86Base.IsSupported) return CpuMaker.Unknown;
            // Leaf 0: EBX, EDX, ECX spell the 12-char vendor id (in that odd order).
            (int _, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
            Span<char> s = stackalloc char[12];
            WriteReg(s, 0, ebx);
            WriteReg(s, 4, edx);
            WriteReg(s, 8, ecx);
            VendorId = new string(s);
            return VendorId switch
            {
                "AuthenticAMD" => CpuMaker.Amd,
                "GenuineIntel" => CpuMaker.Intel,
                _ => CpuMaker.Unknown,
            };
        }
        catch { return CpuMaker.Unknown; }
    }

    private static string DetectBrand()
    {
        try
        {
            if (!X86Base.IsSupported) return "";
            // Extended leaf 0x80000000 reports the highest extended leaf; the brand lives in 2..4.
            (int maxExt, int _, int _, int _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
            if ((uint)maxExt < 0x80000004u) return "";
            Span<char> s = stackalloc char[48];
            int o = 0;
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                (int eax, int ebx, int ecx, int edx) = X86Base.CpuId(unchecked((int)leaf), 0);
                WriteReg(s, o, eax); WriteReg(s, o + 4, ebx);
                WriteReg(s, o + 8, ecx); WriteReg(s, o + 12, edx);
                o += 16;
            }
            return new string(s).Replace("\0", "").Trim();
        }
        catch { return ""; }
    }

    private static void WriteReg(Span<char> dst, int at, int reg)
    {
        dst[at + 0] = (char)(byte)reg;
        dst[at + 1] = (char)(byte)(reg >> 8);
        dst[at + 2] = (char)(byte)(reg >> 16);
        dst[at + 3] = (char)(byte)(reg >> 24);
    }
}
