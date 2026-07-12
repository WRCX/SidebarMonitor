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

The only time bytes cross the network on your behalf are things *you* initiate: downloading the app
(from GitHub Releases, `winget`, or the Microsoft Store if it's ever listed), or the **optional update
check**. That check is **off by default** (Settings → Updates → "Check for updates automatically") and,
when you turn it on, contacts **only** GitHub's public Releases API to compare version numbers — it
sends nothing about you, no identifiers, no usage data. A manual "Check now" button does the same on
demand. If you apply an update, the new installer is downloaded from the GitHub Release you're updating
to. That's the whole of it.

If any future version changes this, it will be called out explicitly in the release notes and here.
