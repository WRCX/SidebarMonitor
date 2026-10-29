using System.Globalization;
using System.IO;
using SidebarMonitor.Shared;

namespace SidebarMonitor.UI;

/// <summary>
/// When one reading deserves a notification. Pure state with the clock passed in, so the timing is
/// testable. Three guards, because an alert that cries wolf gets switched off and is then worth nothing:
/// <list type="bullet">
/// <item><b>Sustain</b> — the condition must hold this long. A CPU touching its limit for one second
/// while a game loads is normal; staying there is not.</item>
/// <item><b>Hysteresis</b> — once fired, the rule only re-arms when the reading is clearly back (the
/// caller's <c>rearm</c>, a margin below the threshold), so a value hovering on the line fires once.</item>
/// <item><b>Cooldown</b> — never twice within this long, whatever the reading does.</item>
/// </list>
/// </summary>
internal sealed class AlertRule(TimeSpan sustain, TimeSpan cooldown)
{
    private bool _armed = true;
    private DateTime? _overSince;
    private DateTime _lastFired = DateTime.MinValue;

    /// <param name="over">The reading is past the threshold now.</param>
    /// <param name="rearm">The reading is back past the threshold minus its margin.</param>
    /// <returns>True exactly when a notification should go out.</returns>
    public bool Update(bool over, bool rearm, DateTime now)
    {
        if (!_armed)
        {
            if (rearm) { _armed = true; _overSince = null; }
            return false;
        }
        if (!over) { _overSince = null; return false; }

        _overSince ??= now;
        if (now - _overSince.Value < sustain || now - _lastFired < cooldown) return false;

        _armed = false;
        _lastFired = now;
        return true;
    }

    /// <summary>The alert was switched off: forget any half-elapsed sustain, keep the cooldown.</summary>
    public void Reset() { _armed = true; _overSince = null; }
}

/// <summary>
/// Watches the snapshot for the configured alerts and raises a tray notification for each one that
/// fires. Runs every tick whether the panel is open, folded, minimised or hidden — the moment nobody
/// is looking at the numbers is when an alert is useful.
/// </summary>
internal sealed class AlertMonitor
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private readonly UiConfig _cfg;
    private readonly Action<string, string> _notify;   // (title, text)
    private readonly Func<IEnumerable<(string Name, long Free, long Total)>> _drives;

    // Temperatures and RAM must persist before they mean anything; a battery or disk crossing its line
    // is already the news.
    private readonly AlertRule _cpu = new(TimeSpan.FromSeconds(10), Cooldown);
    private readonly AlertRule _gpu = new(TimeSpan.FromSeconds(10), Cooldown);
    private readonly AlertRule _ram = new(TimeSpan.FromSeconds(30), Cooldown);
    private readonly AlertRule _battery = new(TimeSpan.Zero, Cooldown);
    private readonly Dictionary<string, AlertRule> _disks = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextDiskCheck = DateTime.MinValue;
    private readonly bool _drivesInBackground;
    private Task<List<(string Name, long Free, long Total)>>? _driveQuery;

    /// <param name="drives">Replaces the real fixed-drive query (tests); it then runs inline.</param>
    public AlertMonitor(UiConfig cfg, Action<string, string> notify,
                        Func<IEnumerable<(string Name, long Free, long Total)>>? drives = null)
    {
        _cfg = cfg;
        _notify = notify;
        _drives = drives ?? FixedDrives;
        _drivesInBackground = drives is null;
    }

    // What fired during the current Evaluate; sent as one notification at the end of it.
    private readonly List<(string Title, string Text)> _fired = [];

    public void Evaluate(ref Snapshot s, DateTime now)
    {
        _fired.Clear();
        EvaluateRules(ref s, now);
        // Several at once (four drives crossing a threshold together) used to be four balloons in a
        // row, each replacing the last before it could be read. One notification lists them all.
        if (_fired.Count == 1) _notify(_fired[0].Title, _fired[0].Text);
        else if (_fired.Count > 1)
            _notify(Loc.T("{0} alertas", _fired.Count), string.Join("\n", _fired.Select(f => $"{f.Title}: {f.Text}")));
    }

    private void EvaluateRules(ref Snapshot s, DateTime now)
    {
        // CPU: the panel's own red zone, so the alert and the blinking number always agree.
        float cpuT = s.Cpu.TempC, tj = s.Cpu.TjMaxC;
        float hot = tj > 0 ? tj - Theme.TempHotMarginC : Theme.TempHotC;
        Check(_cfg.AlertCpuTemp, _cpu, !float.IsNaN(cpuT) && cpuT >= hot,
              float.IsNaN(cpuT) || cpuT < hot - 5, now,
              () => (Loc.T("CPU muy caliente"), tj > 0
                  ? Loc.T("{0:F0} °C, a {1:F0} °C de su límite térmico", cpuT, tj - cpuT)
                  : Loc.T("{0:F0} °C", cpuT)));

        // GPU: the hottest one with real telemetry.
        float gpuT = float.NaN; string gpuName = "";
        for (int i = 0; i < s.GpuCount; i++)
            if (s.Gpus[i].HasDetail != 0 && !float.IsNaN(s.Gpus[i].TempC) && !(s.Gpus[i].TempC <= gpuT))
            { gpuT = s.Gpus[i].TempC; gpuName = NameField.Get(ref s.Gpus[i].Name); }
        float gpuThr = _cfg.AlertGpuTempC;
        Check(_cfg.AlertGpuTemp, _gpu, !float.IsNaN(gpuT) && gpuT >= gpuThr,
              float.IsNaN(gpuT) || gpuT < gpuThr - 5, now,
              () => (Loc.T("GPU muy caliente"), string.Create(Ci, $"{gpuName}: {gpuT:F0} °C")));

        double ramPct = s.Mem.PhysTotal > 0 ? 100.0 * s.Mem.PhysUsed / s.Mem.PhysTotal : double.NaN;
        Check(_cfg.AlertRam, _ram, ramPct >= _cfg.AlertRamPct, !(ramPct >= _cfg.AlertRamPct - 5), now,
              () => (Loc.T("Memoria casi llena"), Loc.T("{0:F0} % en uso", ramPct)));

        var b = s.Battery;
        bool draining = b.Present != 0 && b.Discharging != 0 && !float.IsNaN(b.Pct);
        float pct = b.Pct;
        int secs = b.SecondsLeft;
        Check(_cfg.AlertBattery, _battery, draining && pct <= _cfg.AlertBatteryPct,
              !draining || pct > _cfg.AlertBatteryPct + 5, now,
              () => (Loc.T("Batería baja"), secs >= 0
                  ? Loc.T("{0:F0} % · quedan {1}", pct, BatterySection.Duration(secs))
                  : Loc.T("{0:F0} %", pct)));

        DiskTick(now);
    }

    /// <summary>
    /// Once a minute: free space moves slowly. The query itself runs off the UI thread — it is disk
    /// I/O, and a spun-down HDD or a volume that stops answering would otherwise freeze the panel
    /// for as long as it takes. Its answer is judged on the first tick after it arrives.
    /// </summary>
    private void DiskTick(DateTime now)
    {
        if (_driveQuery is { IsCompleted: true } done)
        {
            _driveQuery = null;
            if (done.IsCompletedSuccessfully) CheckDisks(done.Result, now);
        }
        if (now < _nextDiskCheck || _driveQuery is not null) return;
        _nextDiskCheck = now.AddMinutes(1);
        if (!_cfg.AlertDisk) { foreach (var r in _disks.Values) r.Reset(); return; }

        if (_drivesInBackground) _driveQuery = Task.Run(() => _drives().ToList());
        else
        {
            List<(string, long, long)> list;
            try { list = _drives().ToList(); } catch { return; }
            CheckDisks(list, now);
        }
    }

    private void Check(bool enabled, AlertRule rule, bool over, bool rearm, DateTime now, Func<(string, string)> message)
    {
        if (!enabled) { rule.Reset(); return; }
        if (rule.Update(over, rearm, now)) _fired.Add(message());
    }

    private void CheckDisks(List<(string Name, long Free, long Total)> drives, DateTime now)
    {
        foreach (var (name, free, total) in drives)
        {
            if (total <= 0) continue;
            if (!_disks.TryGetValue(name, out var rule)) _disks[name] = rule = new AlertRule(TimeSpan.Zero, Cooldown);
            double freePct = 100.0 * free / total;
            Check(_cfg.AlertDisk, rule, freePct < _cfg.AlertDiskFreePct, freePct >= _cfg.AlertDiskFreePct + 2, now,
                  () => (Loc.T("Poco espacio en disco"),
                         Loc.T("{0} — quedan {1} ({2:F0} % libre)", name, Size(free), freePct)));
        }
    }

    private static string Size(long bytes) => bytes >= 1L << 30
        ? string.Create(Ci, $"{bytes / (double)(1L << 30):F1} GiB")
        : string.Create(Ci, $"{bytes / (double)(1L << 20):F0} MiB");

    private static IEnumerable<(string, long, long)> FixedDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;
            long free, total;
            try { if (!d.IsReady) continue; free = d.TotalFreeSpace; total = d.TotalSize; }
            catch { continue; }   // a volume vanishing mid-enumeration is not an alert
            yield return (d.Name.TrimEnd('\\'), free, total);
        }
    }
}
