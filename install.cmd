@echo off
rem Doppelklick-Installer: startet install.ps1 (installiert PowerTray + FanControl-Plugin)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
