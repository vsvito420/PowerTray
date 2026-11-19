# Entfernt PowerTray und das FanControl-Plugin. Einstellungen (PowerTray.ini) bleiben nur mit -KeepSettings erhalten.
param([switch]$KeepSettings)
$ErrorActionPreference = 'SilentlyContinue'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\PowerTray'

Get-Process PowerTray | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' PowerTray
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'PowerTray.lnk') -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'PowerTray Dashboard.lnk') -Force
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PowerTray' -Recurse -Force
Start-Sleep -Milliseconds 500
if ($KeepSettings) { Get-ChildItem $appDir -Exclude PowerTray.ini | Remove-Item -Force } else { Remove-Item $appDir -Recurse -Force }

foreach ($d in "${env:ProgramFiles(x86)}\FanControl", "$env:ProgramFiles\FanControl", "$env:LOCALAPPDATA\Programs\FanControl") {
    $dll = Join-Path $d 'Plugins\FanControl.WattSensors.dll'
    if (Test-Path $dll) {
        $running = Get-Process FanControl
        $running | Stop-Process -Force; Start-Sleep 1
        Remove-Item $dll -Force
        if (Test-Path $dll) { Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', "Remove-Item '$dll' -Force" }
        if ($running) { Start-Process (Join-Path $d 'FanControl.exe') -ArgumentList '-m' -WorkingDirectory $d }
    }
}
Write-Host 'PowerTray wurde entfernt.' -ForegroundColor Green
