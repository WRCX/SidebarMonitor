using System.Runtime.InteropServices;

namespace SidebarMonitor.Agent;

/// <summary>
/// Reads NVMe drive temperature ourselves, no HWiNFO. The SMART/Health log (page 0x02) carries
/// the composite temperature in Kelvin, and IOCTL_STORAGE_QUERY_PROPERTY with the NVMe
/// protocol-specific query reads it through the SAME unelevated, access-0 handle we already use
/// for the disk inventory — verified matching HWiNFO's reading. SATA temperature needs an ATA
/// pass-through (admin), so those still fall back to HWiNFO; the NVMe (the fast system disk) does
/// not. Handles are opened once and the log re-read each refresh.
/// </summary>
internal sealed class DiskTemps : IDisposable
{
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;
    private const uint OpenExisting = 3;
    private const uint FileShareReadWrite = 0x1 | 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProtocolSpecificData
    {
        public int ProtocolType;
        public uint DataType;
        public uint RequestValue, RequestSubValue;
        public uint DataOffset, DataLength;
        public uint FixedReturnData;
        public uint Sub2, Sub3, Sub4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyQueryHeader { public int PropertyId; public int QueryType; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern DiskInventory.SafeFileHandleWrapper CreateFileW(
        string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        DiskInventory.SafeFileHandleWrapper h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    private readonly Dictionary<int, DiskInventory.SafeFileHandleWrapper> _handles = [];
    private readonly int _headerSize = Marshal.SizeOf<PropertyQueryHeader>();
    private readonly int _protoSize = Marshal.SizeOf<ProtocolSpecificData>();
    private readonly IntPtr _buf;
    private readonly int _bufSize;

    public DiskTemps(IEnumerable<int> nvmeDiskIndices)
    {
        _bufSize = _headerSize + _protoSize + 512;
        _buf = Marshal.AllocHGlobal(_bufSize);

        foreach (int index in nvmeDiskIndices)
        {
            // Desired access 0: enough for the property query, and works without elevation.
            var h = CreateFileW($@"\\.\PhysicalDrive{index}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!h.IsInvalid) _handles[index] = h;
            else h.Dispose();
        }
    }

    /// <summary>Composite temperature in °C, or NaN if this disk has no NVMe handle.</summary>
    public double TempC(int diskIndex)
    {
        if (!_handles.TryGetValue(diskIndex, out var h)) return double.NaN;

        for (int i = 0; i < _bufSize; i++) Marshal.WriteByte(_buf, i, 0);
        Marshal.StructureToPtr(new PropertyQueryHeader { PropertyId = StorageDeviceProtocolSpecificProperty }, _buf, false);
        Marshal.StructureToPtr(new ProtocolSpecificData
        {
            ProtocolType = ProtocolTypeNvme,
            DataType = NVMeDataTypeLogPage,
            RequestValue = 0x02,             // SMART/Health log
            DataOffset = (uint)_protoSize,
            DataLength = 512,
        }, _buf + _headerSize, false);

        if (!DeviceIoControl(h, IoctlStorageQueryProperty, _buf, _bufSize, _buf, _bufSize, out _, IntPtr.Zero))
            return double.NaN;

        int logOffset = _headerSize + _protoSize;
        int kelvin = Marshal.ReadByte(_buf, logOffset + 1) | (Marshal.ReadByte(_buf, logOffset + 2) << 8);
        return kelvin == 0 ? double.NaN : kelvin - 273.0;
    }

    public void Dispose()
    {
        foreach (var h in _handles.Values) h.Dispose();
        _handles.Clear();
        if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
    }
}
