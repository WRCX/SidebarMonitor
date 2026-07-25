using System.Windows;

namespace SidebarMonitor.UI;

// The update-checking / applying half of MainWindow, split into its own file for readability. Same
// class (partial) — it shares the window's fields (_cfg, _tray, _install, …) with MainWindow.cs.
internal sealed partial class MainWindow
{
    private readonly Updater.Install _install = Updater.CurrentInstall();
    private Updater.Release? _latest;
    private bool _updateBusy;   // one apply / auto-install at a time (UI-thread only)

    /// <summary>Current build version as "vMajor.Minor.Patch", for the Settings page.</summary>
    public string CurrentVersionText => Updater.Format(Updater.CurrentVersion());

    /// <summary>How often the automatic check re-runs. The sidebar is left running for weeks at a
    /// time, so "on startup" alone can mean never — which made the Settings copy ("on startup, then
    /// daily") a lie for anyone who does not reboot.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

    /// <summary>Checks now, then once a day for as long as the window lives. Each tick re-reads
    /// <c>_cfg.CheckUpdates</c> (RunUpdateCheck returns immediately when it is off), so toggling the
    /// setting takes effect without a restart. The timer is deliberately not stopped once an update is
    /// found: an ignored notification should come back tomorrow.</summary>
    public void StartUpdateWatch()
    {
        ReportFailedAttempt();
        RunUpdateCheck(false);
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = UpdateInterval };
        _updateTimer.Tick += (_, _) => RunUpdateCheck(false);
        _updateTimer.Start();
    }

    // Held so OnClosing can stop it, like every other timer here.
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    /// <summary>What the last update attempt came to, if it failed — the Settings page opens on this
    /// instead of a blank status line, since that page is where somebody goes after "it didn't work".
    /// Null when there is nothing to report.</summary>
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
        _tray?.Notify(msg);
    }

    /// <summary>Checks GitHub for a newer release. Auto (fromUser=false) only runs when the user has
    /// opted in. A newer version surfaces in the tray; <paramref name="onResult"/> reports status text
    /// to the Settings page. Runs off the UI thread, marshals results back.</summary>
    public async void RunUpdateCheck(bool fromUser, Action<string>? onResult = null)
    {
        if (!fromUser && !_cfg.CheckUpdates) return;
        onResult?.Invoke(Loc.T("Buscando…"));
        var rel = await Updater.CheckAsync(_install.Flavor);
        void Report(string s) => Dispatcher.Invoke(() => onResult?.Invoke(s));

        if (rel is null) { Report(Loc.T("No se pudo comprobar (sin conexión o sin releases).")); return; }
        if (!Updater.IsNewer(rel.Version, Updater.CurrentVersion()))
        {
            _latest = null;
            Report(Loc.T("Estás en la última versión."));
            if (fromUser) _tray?.Notify(Loc.T("Estás en la última versión."));
            return;
        }

        _latest = rel;
        string ver = Updater.Format(rel.Version);
        Dispatcher.Invoke(() => _tray?.SetUpdateAvailable(ver));

        // Zero-friction path: if the user opted into automatic install and this is an MSI install with a
        // matching asset, download + install it silently right now — no prompt, no progress window, no
        // browser.
        //
        // Only where elevation is already granted, though, and NOT in the way this comment used to
        // claim ("elsewhere msiexec still elevates"). /qn tells Windows Installer to show no UI at
        // all, which includes the UAC consent prompt a per-machine package needs — so from an
        // unelevated UI it does not prompt, it fails with 1925. The script left behind by the 1.4.9 →
        // 1.5.0 loop on 2026-07-25 was the /qn one, which is the best explanation of five silent,
        // instant, invisible failures in a row. It is left to try exactly once and report what it got:
        // the exit code now lands in UpdateAttempt, so the next occurrence is a fact rather than this
        // paragraph, and either way it never retries itself.
        //
        // Except when someone ELSE is logged in: this per-machine update would close their sidebar
        // mid-session, and "silent" must never mean "silent for the person it happens to". Fall back
        // to the normal prompt, which names them and asks. Auto-update is a convenience for a machine
        // you have to yourself.
        //
        // And except when we already tried this exact version and it did not end with us running it.
        // That automatic retry — old UI comes back, checks, installs, fails, comes back — IS the loop,
        // and there was no backoff and no limit to stop it. One attempt is enough: the tray item and
        // «Actualizar ahora» still work, so a deliberate press retries, which is the one retry worth
        // having.
        string? triedBefore = UpdateAttempt.WhyNotAgain(rel.Version);
        if (_cfg.AutoInstallUpdates && _install.FromMsi && rel.AssetUrl is not null && !_updateBusy
            && triedBefore is null && Updater.OtherLoggedInUsers().Count == 0)
        {
            _updateBusy = true;
            Report(Loc.T("Instalando {0} automáticamente…", ver));
            Dispatcher.Invoke(() => _tray?.Notify(Loc.T("Actualizando a {0}…", ver)));
            try
            {
                var outcome = await Updater.ApplyAsync(rel, _install, () => Dispatcher.Invoke(Close), silent: true);
                if (outcome == Updater.ApplyOutcome.Started) return;   // on our way out; the script takes over
                _updateBusy = false;                                   // nothing started — fall through to the prompt
            }
            catch { _updateBusy = false; Report(Loc.T("No se pudo actualizar automáticamente.")); return; }
        }

        Report(triedBefore is not null
            ? Loc.T("No se pudo actualizar a {0}: {1}. Pulsa «Actualizar» para reintentar.", ver, triedBefore)
            : Loc.T("Disponible {0} — pulsa «Actualizar».", ver));
    }

    /// <summary>Applies the pending update: MSI installs update in place (one UAC prompt, then the UI
    /// relaunches); dev/non-MSI installs just open the release page in the browser.</summary>
    public async void ApplyUpdate(Action<string>? onProgress = null)
    {
        // Snapshot _latest up front: a reentrant "Check now" (while the confirm dialog pumps the message
        // loop) could otherwise null it mid-apply. _updateBusy blocks a second concurrent apply.
        var latest = _latest;
        if (latest is null || _updateBusy) return;
        _updateBusy = true;
        try
        {
            string ver = Updater.Format(latest.Version);

            // Visual feedback through every phase: Downloading %… → Installing… (msiexec shows its own
            // progress bar too) → the panel closes and relaunches into the new version.
            void Report(string s) { onProgress?.Invoke(s); _tray?.Notify(s); }

            if (!_install.FromMsi || latest.AssetUrl is null)
            {
                // Not an MSI install (or no matching asset): send them to the release page.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(latest.HtmlUrl) { UseShellExecute = true }); } catch { }
                return;
            }

            // A hand-off from a moment ago is still running (the tray item and the Settings button are
            // two ways to press the same thing, and msiexec takes a while). Starting another one only
            // earns a 1618; the machine is seconds from restarting into the new version anyway.
            if (UpdateAttempt.InFlight()) { Report(Loc.T("Ya hay una actualización en curso.")); return; }

            // Pressing "Update" IS the confirmation — from here it runs straight through to the new
            // version: download, install, relaunch, no further clicks. (msiexec still raises one UAC
            // prompt; a per-machine MSI cannot install without it.) Nothing is lost on the way: config,
            // layout and logs live in %LOCALAPPDATA% and the MSI only replaces Program Files.
            //
            // The one thing still worth stopping for is OTHER people. This is a per-machine install, so
            // updating updates it for EVERYONE on this PC, and the installer has to close the sidebar of
            // any other logged-in user (their running copy holds the files open). Say so, by name —
            // that is someone else's screen, and they did not press anything.
            var others = Updater.OtherLoggedInUsers();
            if (others.Count > 0)
            {
                string msg = Loc.T("Hay otros usuarios con la sesión iniciada en este PC: {0}.\n\nSidebarMonitor se instala por equipo, así que la actualización afecta a todos: su barra se cerrará y volverá al reconectar o desbloquear su sesión (o al iniciar sesión de nuevo).\n\n¿Actualizar a {1} de todas formas?", string.Join(", ", others), ver);
                if (MessageBox.Show(msg, "SidebarMonitor", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    != MessageBoxResult.OK) return;
            }

            var progress = new Progress<string>(Report);
            Report(Loc.T("Descargando {0}…", ver));
            // Pressing the button IS the user asking again, so it retries a version that failed before:
            // ApplyAsync overwrites the marker on the way past. "Do not auto-retry" must never harden
            // into "never again" — the fix for a dismissed UAC prompt is pressing it once more.
            LastUpdateError = null;
            if (await Updater.ApplyAsync(latest, _install, Close, silent: false, progress: progress)
                == Updater.ApplyOutcome.AlreadyRunning)
                Report(Loc.T("Ya hay una actualización en curso."));
        }
        catch
        {
            string m = Loc.T("No se pudo actualizar. Prueba a descargarlo manualmente.");
            onProgress?.Invoke(m); _tray?.Notify(m);
        }
        finally { _updateBusy = false; }
    }
}
