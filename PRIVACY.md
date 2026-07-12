# Privacy

**SidebarMonitor collects no telemetry. Nothing you do in it leaves your machine.**

Concretely:

- **No analytics, no usage tracking, no crash reporting, no "phone home".** There is no code that
  sends metrics, identifiers, or diagnostics anywhere.
- **No network activity at all** during normal operation. The app samples local hardware and Windows
  APIs and renders them; it opens no sockets to any server of ours or anyone else's.
- **Your configuration and logs stay local.** Settings live in `%LOCALAPPDATA%\SidebarMonitor\ui.json`
  and optional CSV logs in `%LOCALAPPDATA%\SidebarMonitor\logs` — on your disk, never uploaded.
- **No account, no sign-in, no license server.**

The only time bytes cross the network on your behalf are things *you* initiate outside the running
app: downloading it (from GitHub Releases, `winget`, or the Microsoft Store if it's ever listed), or
an optional "check for a newer version" feature *if* it is added later — which would query only the
public GitHub Releases API, would be clearly disclosed, and would send nothing about you.

If any future version changes this, it will be called out explicitly in the release notes and here.
