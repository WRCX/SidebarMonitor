using System.Runtime.InteropServices;

namespace SidebarMonitor.Shared;

/// <summary>
/// Maps a process id to the Windows services it hosts — the answer to "which svchost is this?".
/// Reads the Service Control Manager, which an unelevated caller may enumerate, so both the agent
/// and the helper can use it. AOT-safe: DllImport + manual buffer reads, no reflection.
/// </summary>
public sealed class ServiceMap
{
    private readonly Dictionary<int, List<string>> _byPid = new(256);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(
        IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr buffer, uint bufSize, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_WIN32 = 0x30;   // OWN_PROCESS | SHARE_PROCESS
    private const uint SERVICE_ACTIVE = 0x1;
    private const int SC_ENUM_PROCESS_INFO = 0;

    // ENUM_SERVICE_STATUS_PROCESS on x64: 2 pointers (16) + SERVICE_STATUS_PROCESS (9 uints, 36),
    // 8-aligned = 56 bytes. dwProcessId is the 8th uint of the nested struct → offset 16 + 28 = 44.
    private const int ElemSize = 56;
    private const int PidOffset = 44;

    public void Refresh()
    {
        _byPid.Clear();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            if (needed == 0) return;

            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                        buf, needed, out _, out uint count, ref resume, null))
                    return;

                for (int i = 0; i < count; i++)
                {
                    IntPtr rec = buf + i * ElemSize;
                    IntPtr namePtr = Marshal.ReadIntPtr(rec);          // lpServiceName
                    int pid = Marshal.ReadInt32(rec + PidOffset);      // ServiceStatusProcess.dwProcessId
                    if (pid <= 0 || namePtr == IntPtr.Zero) continue;
                    string? name = Marshal.PtrToStringUni(namePtr);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!_byPid.TryGetValue(pid, out var list)) _byPid[pid] = list = new List<string>(1);
                    list.Add(name);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>A compact label of the services a PID hosts ("Dhcp" or "Dhcp+2"), or null if it
    /// hosts none (i.e. it isn't a service host).</summary>
    public string? Label(int pid)
    {
        if (!_byPid.TryGetValue(pid, out var list) || list.Count == 0) return null;
        return list.Count == 1 ? list[0] : $"{list[0]}+{list.Count - 1}";
    }
}
