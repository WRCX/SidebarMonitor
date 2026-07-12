@echo off
rem Doble clic para desinstalar SidebarMonitor. Se auto-eleva para quitar la tarea del helper.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
