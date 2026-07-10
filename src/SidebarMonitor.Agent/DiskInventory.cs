using System.Runtime.InteropServices;
using System.Text;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Agent;

internal sealed record DiskIdentity(int Index, string Model, string Bus, DiskMedia Media, ulong SizeBytes,
                                    string Label, string Volumes, int VolumeCount, bool Removable, bool Virtual, bool System);

/// <summary>
/// Static facts about the physical disks: model, SSD-vs-HDD, bus, size, and the labels of the
/// volumes living on them. Read once at startup — none of it changes while we run.
///
/// Everything goes through IOCTL_STORAGE_QUERY_PROPERTY rather than WMI: WMI needs COM and
/// reflection, which would cost the agent its AOT build. Opening \\.\PhysicalDriveN with a
/// desired access of 0 is enough to query properties, so this needs no elevation either.
/// </summary>
internal static class DiskInventory
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;

    /// <summary>
    /// GET_DRIVE_GEOMETRY_EX, not GET_LENGTH_INFO: the latter needs read access to the raw
    /// device, which an unelevated process does not get on \\.\PhysicalDriveN.
    /// </summary>
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    private const uint OpenExisting = 3;
    private const uint FileShareReadWrite = 0x1 | 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandleWrapper CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandleWrapper handle, uint code, ref StoragePropertyQuery input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandleWrapper handle, uint code, IntPtr input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPath, StringBuilder volumeName, int volumeNameSize,
        out uint serial, out uint maxComponent, out uint flags,
        StringBuilder fileSystem, int fileSystemSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string dir, out ulong freeAvail, out ulong total, out ulong totalFree);

    internal sealed class SafeFileHandleWrapper : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandleWrapper() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
    }

    /// <summary>Keyed by physical disk index, which is the leading number of the PDH instance.</summary>
    public static Dictionary<int, DiskIdentity> Enumerate(IEnumerable<string> pdhInstances)
    {
        var result = new Dictionary<int, DiskIdentity>();

        foreach (string instance in pdhInstances)
        {
            // "2 C: E:" -> index 2, letters C and E.
            int space = instance.IndexOf(' ');
            string head = space < 0 ? instance : instance[..space];
            if (!int.TryParse(head, out int index) || result.ContainsKey(index)) continue;

            string letters = space < 0 ? "" : instance[(space + 1)..];
            var (label, volumes, count) = Volumes(letters);
            bool system = HoldsSystemVolume(letters);
            var id = Query(index, label, volumes, count, system);
            if (id is not null) result[index] = id;
        }

        return result;
    }

    /// <summary>
    /// Returns (joined labels, per-volume summary). The summary lists each drive letter with its
    /// used/total and label, e.g. "C: 210/293G · juegos(E:) 1.2/1.6T" — so a disk with two
    /// partitions no longer reads as two disks.
    /// </summary>
    private static (string Label, string Volumes, int Count) Volumes(string letters)
    {
        var labels = new List<string>();
        var summary = new List<string>();
        int count = 0;

        foreach (string token in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 1) continue;
            count++;
            char letter = token[0];
            string root = $"{letter}:\\";

            var name = new StringBuilder(64);
            var fs = new StringBuilder(16);
            string label = GetVolumeInformationW(root, name, name.Capacity, out _, out _, out _, fs, fs.Capacity) && name.Length > 0
                ? name.ToString()
                : "";
            if (label.Length > 0) labels.Add(label);

            string size = "";
            if (GetDiskFreeSpaceExW(root, out _, out ulong total, out ulong totalFree) && total > 0)
                size = $" {Short(total - totalFree)}/{Short(total)}";

            // "juegos(E:) 1.2/1.6T" when labelled, else "C: 210/293G".
            summary.Add(label.Length > 0 ? $"{label}({letter}:){size}" : $"{letter}:{size}");
        }

        return (string.Join(" / ", labels), string.Join(" · ", summary), count);
    }

    /// <summary>Compact storage size: 293G, 1.6T. Invariant (the agent runs globalization-invariant).</summary>
    private static string Short(ulong bytes)
    {
        const double G = 1024.0 * 1024 * 1024, T = G * 1024;
        return bytes >= T
            ? (bytes / T).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "T"
            : (bytes / G).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "G";
    }

    private static readonly char SystemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0];

    private static bool HoldsSystemVolume(string letters)
    {
        foreach (string token in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (token.Length >= 1 && char.ToUpperInvariant(token[0]) == char.ToUpperInvariant(SystemDrive))
                return true;
        return false;
    }

    private static DiskIdentity? Query(int index, string label, string volumes, int volumeCount, bool system)
    {
        // Desired access 0: enough for property queries, and works without elevation.
        using var h = CreateFileW($@"\\.\PhysicalDrive{index}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        string model = "", bus = "";
        var query = new StoragePropertyQuery { PropertyId = StorageDeviceProperty, QueryType = PropertyStandardQuery };

        const int bufSize = 1024;
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            if (DeviceIoControl(h, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                                buf, bufSize, out _, IntPtr.Zero))
            {
                // STORAGE_DEVICE_DESCRIPTOR: offsets are byte counts from the start of the buffer.
                int vendorOffset = Marshal.ReadInt32(buf, 12);
                int productOffset = Marshal.ReadInt32(buf, 16);
                int busType = Marshal.ReadInt32(buf, 28);

                string vendor = AnsiAt(buf, vendorOffset, bufSize);
                string product = AnsiAt(buf, productOffset, bufSize);
                model = string.IsNullOrWhiteSpace(vendor) ? product : $"{vendor.Trim()} {product}".Trim();
                bus = BusName(busType);
            }

            var media = DiskMedia.Unknown;
            var seek = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
            if (DeviceIoControl(h, IoctlStorageQueryProperty, ref seek, Marshal.SizeOf<StoragePropertyQuery>(),
                                buf, bufSize, out _, IntPtr.Zero))
            {
                // DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size, BOOLEAN IncursSeekPenalty }
                media = Marshal.ReadByte(buf, 8) != 0 ? DiskMedia.Hdd : DiskMedia.Ssd;
            }

            ulong size = 0;
            // DISK_GEOMETRY_EX { DISK_GEOMETRY Geometry (24 B); LARGE_INTEGER DiskSize; ... }
            if (DeviceIoControl(h, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, buf, bufSize, out int got, IntPtr.Zero)
                && got >= 32)
            {
                long len = Marshal.ReadInt64(buf, 24);
                if (len > 0) size = (ulong)len;
            }

            bool removable = bus is "USB" or "SD" or "MMC" or "1394";
            bool virtualDisk = bus is "Virtual" or "vHD" or "Spaces"
                || model.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

            return new DiskIdentity(index, model.Trim(), bus, media, size, label, volumes, volumeCount, removable, virtualDisk, system);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string AnsiAt(IntPtr buf, int offset, int max)
    {
        if (offset <= 0 || offset >= max) return "";
        var sb = new StringBuilder();
        for (int i = offset; i < max; i++)
        {
            byte b = Marshal.ReadByte(buf, i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString().Trim();
    }

    private static string BusName(int busType) => busType switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        14 => "Virtual", 15 => "vHD", 16 => "Spaces", 17 => "NVMe",
        _ => "?",
    };
}
