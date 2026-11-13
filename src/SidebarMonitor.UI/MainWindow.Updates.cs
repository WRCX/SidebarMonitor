using System.Globalization;
using System.Windows;

namespace SidebarMonitor.UI;

// The update-checking / applying half of MainWindow, split into its own file for readability. Same
// class (partial) — it shares the window's fields (_cfg, _tray, _install, …) with MainWindow.cs.
internal sealed partial class MainWindow
{
    private readonly Updater.Install _install = Updater.CurrentInstall();
    private Updater.Release? _latest;
    private bool _updateBusy;   // one apply / auto-install at a time (UI-thread only)
    private bool _checking;     // a check is in flight (UI-thread only)

    /// <summary>Current build version as "vMajor.Minor.Patch", for the Settings page.</summary>
    public string CurrentVersionText => Updater.Format(Updater.CurrentVersion());

    /// <summary>How often the automatic check re-runs. The sidebar is left running for weeks at a
    /// time, so "on startup" alone can mean never — which made the Settings copy ("on startup, then
    /// daily") a lie for anyone who does not reboot.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

    // ── The one update state ───────────────────────────────────────────────────────────────────
    //
    // Every path — the startup and daily checks, the Settings buttons, the tray item — reports here,
    // and the Settings page listens. Before, each button carried its own status callback: a check run
    // by the timer, or an install started by it, never reached the page, which opened blank; and
    // «Actualizar ahora» acted only on a release some earlier check had happened to remember, so after
    // a version came out while the panel was running it returned without a word.

    /// <summary>What the update machinery is doing or last found, as one line for the Settings page.</summary>
    public string UpdateStatus { get; private set; } = "";

    /// <summary>A newer release this session knows about, or null.</summary>
    public Updater.Release? AvailableUpdate => _latest;

    /// <summary>Checking or installing right now: the page greys its buttons out.</summary>
    public bool UpdateBusy => _updateBusy || _checking;

    /// <summary>Raised on the UI thread whenever any of the above changes.</summary>
    public event Action? UpdateStateChanged;

    private void SetUpdateStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetUpdateStatus(text)); return; }
        UpdateStatus = text;
        UpdateStateChanged?.Invoke();
    }

    private static string At(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Checks now, then once a day for as long as the window lives. Each tick re-reads
    /// <c>_cfg.CheckUpdates</c> (the automatic check returns at once when it is off), so toggling the
    /// setting takes effect without a restart. The timer is deliberately not stopped once an update is
    /// found: an ignored notification should come back tomorrow.</summary>
    public void StartUpdateWatch()
    {
        ReportFailedAttempt();
        _ = CheckForUpdate(automatic: true);
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateInterval };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdate(automatic: true);
        _updateTimer.Start();
    }

    // Held so OnClosing can stop it, like every other timer here.
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    /// <summary>What the last update attempt came to, if it failed. Also the page's opening line.</summary>
    public string? LastUpdateError { get; private set; }

    /// <summary>
    /// A relaunch after a FAILED install used to be indistinguishable from a relaunch after a
    /// successful one: the same old version came back without a word, which reads as "the button did
    /// nothing" and invites another press — and another download, msiexec and relaunch. Say what
    /// happened, once. The marker itself is what keeps the automatic path from retrying.
    /// </summary>
    private void ReportFailedAttempt()
    {
        var last = UpdateAttempt.Read();
        if (last.Version is null || last.Notified) return;

        // Here we are running that version or better, so it landed — this run, or later by hand. Forget
        // the attempt, or the marker would go on suppressing a version with nothing wrong with it.
        if (!Updater.IsNewer(last.Version, Updater.CurrentVersion())) { UpdateAttempt.Clear(); return; }

        string? why = UpdateAttempt.WhyNotAgain(last.Version);
        if (why is null) return;   // still in flight — nothing to report yet

        UpdateAttempt.MarkNotified();
        string msg = Loc.T("No se pudo actualizar a {0}: {1}.", Updater.Format(last.Version), why);
        LastUpdateError = msg + " " + Loc.T("Detalles en {0}", UpdateAttempt.MsiLogPath);
        SetUpdateStatus(LastUpdateError);
        _tray?.Notify(msg);
    }

    /// <summary>
    /// «Buscar ahora»: looks, reports, never installs — finding a version is not agreeing to it.
    /// That is what «Actualizar» (or the automatic setting) is for. It used to install straight away
    /// when automatic install was on, which made the search button the one that updated.
    /// </summary>
    public void CheckForUpdateNow() => _ = CheckForUpdate(automatic: false);

    /// <summary>
    /// The automatic setting was just switched on: act on it now rather than at tomorrow's tick. A
    /// release already known is installed at once; otherwise a check runs, and installs if it finds one.
    /// </summary>
    public void UpdateSettingsChanged()
    {
        if (!_cfg.CheckUpdates) { SetUpdateStatus(UpdateStatus); return; }   // nothing to do; refresh the page
        if (_cfg.AutoInstallUpdates && _latest is not null) _ = TryAutoInstall(_latest);
        else _ = CheckForUpdate(automatic: true);
    }

    /// <summary>
    /// One check of GitHub. <paramref name="automatic"/> runs only when the user opted in, and is the
    /// only kind that may go on to install by itself. Returns the newer release, or null when there is
    /// none or the check failed (the status line says which).
    /// </summary>
    private async Task<Updater.Release?> CheckForUpdate(bool automatic)
    {
        if (automatic && !_cfg.CheckUpdates) return null;
        if (_checking) return _latest;   // one at a time; the one in flight reports for both
        _checking = true;
        SetUpdateStatus(Loc.T("Buscando…"));
        Updater.Release? rel;
        try { rel = await Updater.CheckAsync(_install.Flavor); }
        finally { _checking = false; }

        if (rel is null)
        {
            SetUpdateStatus(Loc.T("No se pudo comprobar a las {0} (sin conexión o sin releases).", At(DateTime.Now)));
            return null;
        }
        if (!Updater.IsNewer(rel.Version, Updater.CurrentVersion()))
        {
            _latest = null;
            // No notification: a manual check comes from the Settings page, which says so right there.
            SetUpdateStatus(Loc.T("Estás en la última versión (comprobado a las {0}).", At(DateTime.Now)));
            return null;
        }

        _latest = rel;
        string ver = Updater.Format(rel.Version);
        _tray?.SetUpdateAvailable(ver);

        if (automatic && _cfg.AutoInstallUpdates && await TryAutoInstall(rel)) return rel;

        string? triedBefore = UpdateAttempt.WhyNotAgain(rel.Version);
        SetUpdateStatus(triedBefore is not null
            ? Loc.T("No se pudo actualizar a {0}: {1}. Pulsa «Actualizar» para reintentar.", ver, triedBefore)
            : Loc.T("Disponible {0} — pulsa «Actualizar».", ver));
        return rel;
    }

    /// <summary>
    /// The automatic install, when it may run. True when it is under way (the panel is about to close).
    /// <para>
    /// "Silent" means no dialogs of ours and no browser — it cannot mean no UAC prompt, because a
    /// per-machine MSI has to run elevated (one prompt, or none where UAC is off). Until 1.5.4 it passed
    /// /qn to an unelevated msiexec and died at InstallFinalize every time; the relauncher now elevates.
    /// </para>
    /// <para>
    /// It does not run when someone ELSE is logged in — this per-machine update would close their
    /// sidebar mid-session, and "silent" must never mean silent for the person it happens to — nor for
    /// a version already tried that did not end with us running it: that automatic retry was the
    /// update loop. A deliberate «Actualizar» still retries; it is the one retry worth having.
    /// </para>
    /// </summary>
    private async Task<bool> TryAutoInstall(Updater.Release rel)
    {
        if (!_install.FromMsi || rel.AssetUrl is null || _updateBusy) return false;
        if (UpdateAttempt.WhyNotAgain(rel.Version) is not null) return false;
        if (Updater.OtherLoggedInUsers().Count > 0) return false;

        string ver = Updater.Format(rel.Version);
        _updateBusy = true;
        SetUpdateStatus(Loc.T("Instalando {0} automáticamente…", ver));
        _tray?.Notify(Loc.T("Actualizando a {0}…", ver));
        try
        {
            var outcome = await Updater.ApplyAsync(rel, _install, () => Dispatcher.Invoke(Close), silent: true);
            if (outcome == Updater.ApplyOutcome.Started) return true;   // on our way out; the script takes over
            if (outcome == Updater.ApplyOutcome.AlreadyRunning) { SetUpdateStatus(Loc.T("Ya hay una actualización en curso.")); return true; }
            return false;
        }
        catch { SetUpdateStatus(Loc.T("No se pudo actualizar automáticamente.")); return true; }
        finally { _updateBusy = false; UpdateStateChanged?.Invoke(); }
    }

    /// <summary>
    /// «Actualizar ahora» and the tray item: always does something. With no release known yet it checks
    /// first — a version that came out while the panel was running used to leave this button inert until
    /// «Buscar ahora» had been pressed — then installs: MSI installs update in place (one UAC prompt,
    /// then the UI relaunches); other installs open the release page in the browser.
    /// </summary>
    public async void ApplyUpdate()
    {
        if (_updateBusy) { SetUpdateStatus(Loc.T("Ya hay una actualización en curso.")); return; }
        if (_checking) return;   // the check in flight will say what it found; press again then

        // async void: anything thrown and not caught here takes the whole panel down, so the check
        // that may come first is inside the try as well.
        Updater.Release? latest;
        try { latest = _latest ?? await CheckForUpdate(automatic: false); }
        catch { SetUpdateStatus(Loc.T("No se pudo actualizar. Prueba a descargarlo manualmente.")); return; }
        if (latest is null) return;   // up to date, or the check failed — the status line says which
        if (_updateBusy) return;      // an automatic install started while we were checking

        _updateBusy = true;
        UpdateStateChanged?.Invoke();
        try
        {
            string ver = Updater.Format(latest.Version);
            // Progress goes to the Settings page and the tray icon's hover text — never a notification:
            // the download reports every percent, and each one was a Windows toast with its sound, ten
            // a second. One notification says it started; the panel closing says it is installing.
            void Report(string s) { SetUpdateStatus(s); _tray?.SetStatus(s); }

            if (!_install.FromMsi || latest.AssetUrl is null)
            {
                // Not an MSI install (or no matching asset): the release page is the way to update.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(latest.HtmlUrl) { UseShellExecute = true }); } catch { }
                SetUpdateStatus(Loc.T("Esta instalación no se actualiza sola: abierta la página de {0} en el navegador.", ver));
                return;
            }

            // A hand-off from a moment ago is still running (the tray item and the Settings button are
            // two ways to press the same thing, and msiexec takes a while). Starting another one only
            // earns a 1618; the machine is seconds from restarting into the new version anyway.
            if (UpdateAttempt.InFlight()) { Report(Loc.T("Ya hay una actualización en curso.")); return; }

            // Pressing "Update" IS the confirmation — from here it runs straight through to the new
            // version: download, install, relaunch. The one thing still worth stopping for is OTHER people:
            // this is a per-machine install, and the installer has to close the sidebar of any other
            // logged-in user. Say so, by name — that is someone else's screen.
            var others = Updater.OtherLoggedInUsers();
            if (others.Count > 0)
            {
                string msg = Loc.T("Hay otros usuarios con la sesión iniciada en este PC: {0}.\n\nSidebarMonitor se instala por equipo, así que la actualización afecta a todos: su barra se cerrará y volverá al reconectar o desbloquear su sesión (o al iniciar sesión de nuevo).\n\n¿Actualizar a {1} de todas formas?", string.Join(", ", others), ver);
                if (MessageBox.Show(msg, "SidebarMonitor", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    != MessageBoxResult.OK) { SetUpdateStatus(Loc.T("Disponible {0} — pulsa «Actualizar».", ver)); return; }
            }

            var progress = new Progress<string>(Report);
            Report(Loc.T("Descargando {0}…", ver));
            _tray?.Notify(Loc.T("Descargando {0}…", ver));
            // Pressing the button IS the user asking again, so it retries a version that failed before:
            // ApplyAsync overwrites the marker on the way past.
            LastUpdateError = null;
            if (await Updater.ApplyAsync(latest, _install, Close, silent: false, progress: progress)
                == Updater.ApplyOutcome.AlreadyRunning)
                Report(Loc.T("Ya hay una actualización en curso."));
        }
        catch
        {
            string m = Loc.T("No se pudo actualizar. Prueba a descargarlo manualmente.");
            SetUpdateStatus(m); _tray?.Notify(m);
        }
        finally { _updateBusy = false; _tray?.SetStatus(null); UpdateStateChanged?.Invoke(); }
    }
}
