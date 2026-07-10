using System.Runtime.InteropServices;

namespace ShellProbe;

/// <summary>
/// Two very different tiers of API.
///
/// <see cref="IVirtualDesktopManager"/> is documented and stable, but it can only *ask*
/// which desktop a window is on and *move* it to another. It cannot pin.
///
/// Pinning ("show on all desktops") only exists through undocumented COM served by the
/// Immersive Shell. The IIDs are not contracts; Microsoft rotates them between Windows
/// builds. Treat every call here as best-effort and never let a failure be fatal.
/// </summary>
internal static class VirtualDesktop
{
    // Documented.
    private static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    // Undocumented. Build-specific.
    private static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid SID_VirtualDesktopPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    private static readonly Guid IID_IVirtualDesktopPinnedApps = new("4CE81583-1E4C-4632-A621-07A53543148F");

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    // Method order IS the vtable. Getting it wrong does not throw; it calls the wrong
    // function pointer. Hence the isolated --pin-probe process.
    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId, out int pinned);
        [PreserveSig] int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        // IsViewPinned / PinView / UnpinView follow; they need IApplicationView, which we do not touch.
    }

    public static string DescribeWindow(IntPtr hwnd)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager)
                ?? throw new InvalidOperationException("CLSID no registrado");
            var mgr = (IVirtualDesktopManager)Activator.CreateInstance(type)!;

            int hrOn = mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrent);
            int hrId = mgr.GetWindowDesktopId(hwnd, out Guid id);
            Marshal.ReleaseComObject(mgr);

            string onStr = hrOn == 0 ? (onCurrent != 0 ? "si" : "no") : $"hr=0x{hrOn:X8}";
            string idStr = hrId == 0
                ? (id == Guid.Empty ? "GUID_NULL (= visible en todos)" : id.ToString())
                : $"hr=0x{hrId:X8}";
            return $"en el escritorio actual: {onStr};  desktopId: {idStr}";
        }
        catch (Exception ex)
        {
            return $"IVirtualDesktopManager no disponible: {ex.Message}";
        }
    }

    /// <summary>
    /// Attempts the undocumented pin path. Returns a human-readable verdict.
    /// Runs in its own process because a wrong vtable guess is an access violation,
    /// not a catchable exception.
    /// </summary>
    public static string TryPinAppId(string appId)
    {
        IntPtr punk = IntPtr.Zero;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_ImmersiveShell);
            if (type is null) return "CLSID_ImmersiveShell no resuelve";

            var provider = Activator.CreateInstance(type) as IServiceProvider;
            if (provider is null) return "ImmersiveShell no expone IServiceProvider";

            Guid sid = SID_VirtualDesktopPinnedApps, iid = IID_IVirtualDesktopPinnedApps;
            int hr = provider.QueryService(ref sid, ref iid, out punk);
            Marshal.ReleaseComObject(provider);

            if (hr != 0 || punk == IntPtr.Zero)
                return $"QueryService(IVirtualDesktopPinnedApps) fallo: hr=0x{hr:X8}  -> el IID cambio en esta build";

            var pinned = (IVirtualDesktopPinnedApps)Marshal.GetObjectForIUnknown(punk);

            int hr1 = pinned.IsAppIdPinned(appId, out int before);
            if (hr1 != 0) return $"IsAppIdPinned fallo: hr=0x{hr1:X8}";

            int hr2 = pinned.PinAppID(appId);
            int hr3 = pinned.IsAppIdPinned(appId, out int after);

            // Leave the machine as we found it.
            if (before == 0 && after != 0) pinned.UnpinAppID(appId);

            Marshal.ReleaseComObject(pinned);

            return hr2 == 0 && after != 0
                ? $"FUNCIONA. PinAppID ok, IsAppIdPinned paso de {before} a {after}, luego lo despinamos."
                : $"PinAppID hr=0x{hr2:X8}, IsAppIdPinned despues={after} (hr=0x{hr3:X8}) -> no pin";
        }
        catch (Exception ex)
        {
            return $"excepcion: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (punk != IntPtr.Zero) Marshal.Release(punk);
        }
    }
}
