@echo off
rem Doble clic para instalar SidebarMonitor. Se auto-eleva cuando llega a crear la tarea del helper.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
