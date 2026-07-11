using System.Management;
using SidebarMonitor.Shared;

namespace SidebarMonitor.Etw;

/// <summary>
/// Drive temperatures via the Storage WMI reliability counter — the same source as
/// Get-StorageReliabilityCounter. It reaches SATA (and NVMe) temps that need admin, which the
/// agent's unelevated NVMe IOCTL can't cover, so this closes the last HWiNFO dependency.
///
/// WMI is comparatively heavy (each poll spins up a query with per-disk associations), and drive
/// temperature barely moves, so it runs on a slow background timer and the helper just publishes
/// the cache. Keyed by physical disk number (MSFT_PhysicalDisk.DeviceId), which is the index the
/// agent uses too.
/// </summary>
internal sealed class DiskTempsWmi : IDisposable
{
    private readonly float[] _temps = new float[SnapshotLayout.MaxDisks];
    private readonly Timer _timer;
    private volatile bool _busy;

    public DiskTempsWmi()
    {
        for (int i = 0; i < _temps.Length; i++) _temps[i] = float.NaN;
        _timer = new Timer(_ => Poll(), null, 0, 10_000);   // every 10 s
    }

    /// <summary>Copies the cache into the snapshot's per-disk array.</summary>
    public void Fill(ref EtwSnapshot s)
    {
        for (int i = 0; i < _temps.Length; i++) s.DiskTempsC[i] = _temps[i];
    }

    private void Poll()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var scope = new ManagementScope(@"\\.\root\microsoft\windows\storage");
            scope.Connect();
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));

            foreach (ManagementObject disk in search.Get())
            {
                try
                {
                    if (!int.TryParse(disk["DeviceId"]?.ToString(), out int id) || (uint)id >= (uint)_temps.Length) continue;
                    float t = float.NaN;
                    foreach (ManagementObject rc in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        if (rc["Temperature"] is { } o && Convert.ToInt32(o) > 0) t = Convert.ToInt32(o);
                        rc.Dispose();
                    }
                    _temps[id] = t;
                }
                catch { /* this disk has no reliability counter */ }
                finally { disk.Dispose(); }
            }
        }
        catch { /* WMI unavailable this round; keep the last cache */ }
        finally { _busy = false; }
    }

    public void Dispose() => _timer.Dispose();
}
