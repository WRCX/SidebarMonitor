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
        // browser. (Truly hands-off only where elevation is silent; elsewhere msiexec still elevates.)
        //
        // Except when someone ELSE is logged in: this per-machine update would close their sidebar
        // mid-session, and "silent" must never mean "silent for the person it happens to". Fall back
        // to the normal prompt, which names them and asks. Auto-update is a convenience for a machine
        // you have to yourself.
        if (_cfg.AutoInstallUpdates && _install.FromMsi && rel.AssetUrl is not null && !_updateBusy
            && Updater.OtherLoggedInUsers().Count == 0)
        {
            _updateBusy = true;
            Report(Loc.T("Instalando {0} automáticamente…", ver));
            Dispatcher.Invoke(() => _tray?.Notify(Loc.T("Actualizando a {0}…", ver)));
            try { await Updater.ApplyAsync(rel, _install, () => Dispatcher.Invoke(Close), silent: true); }
            catch { _updateBusy = false; Report(Loc.T("No se pudo actualizar automáticamente.")); }
            return;
        }

        Report(Loc.T("Disponible {0} — pulsa «Actualizar».", ver));
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

            if (!_install.FromMsi || latest.AssetUrl is null)
            {
                // Not an MSI install (or no matching asset): send them to the release page.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(latest.HtmlUrl) { UseShellExecute = true }); } catch { }
                return;
            }

            // Default flow: always confirm first, and reassure that nothing is lost. Config, layout and
            // logs live in %LOCALAPPDATA% and are never touched by the MSI (it only replaces Program Files).
            string msg = Loc.T("Se descargará e instalará {0}.\n\nSe conserva toda tu configuración (panel, ajustes, colocación, historial) — no se pierde nada. El panel se cerrará y se volverá a abrir solo, ya en la versión nueva.\n\n¿Actualizar ahora?", ver);

            // This is a per-machine install: updating updates it for EVERYONE on this PC, and the
            // installer has to close the sidebar of any other user who is logged in (their running
            // copy holds the files open). Say so, by name — that is someone else's screen.
            var others = Updater.OtherLoggedInUsers();
            if (others.Count > 0)
                msg = Loc.T("Hay otros usuarios con la sesión iniciada en este PC: {0}.\n\nSidebarMonitor se instala por equipo, así que la actualización afecta a todos: su barra se cerrará y volverá al reconectar o desbloquear su sesión (o al iniciar sesión de nuevo).\n\n", string.Join(", ", others)) + msg;

            var ok = MessageBox.Show(msg, "SidebarMonitor", MessageBoxButton.OKCancel,
                others.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;

            // Visual feedback through every phase: Downloading %… → Installing… (msiexec shows its own
            // progress bar too) → the panel closes and relaunches into the new version.
            void Report(string s) { onProgress?.Invoke(s); _tray?.Notify(s); }
            var progress = new Progress<string>(Report);
            Report(Loc.T("Descargando {0}…", ver));
            await Updater.ApplyAsync(latest, _install, Close, silent: false, progress: progress);
        }
        catch
        {
            string m = Loc.T("No se pudo actualizar. Prueba a descargarlo manualmente.");
            onProgress?.Invoke(m); _tray?.Notify(m);
        }
        finally { _updateBusy = false; }
    }
}
