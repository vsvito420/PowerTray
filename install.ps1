<#
.SYNOPSIS
  Installiert PowerTray (Tray-App) und optional das FanControl-Plugin "WattSensors".
.EXAMPLE
  .\install.ps1                      # alles, mit Rückfragen
  .\install.ps1 -Yes                 # alles, ohne Rückfragen (für Skripte / KI-Agenten)
  .\install.ps1 -Component App       # nur PowerTray
  .\install.ps1 -Component Plugin    # nur das FanControl-Plugin
#>
param(
    [ValidateSet('All', 'App', 'Plugin')] [string]$Component = 'All',
    [switch]$Yes,                 # keine Rückfragen
    [switch]$NoFanControl,        # FanControl nicht per winget installieren
    [switch]$NoAutostart,
    [switch]$NoStart,
    [string]$ImportIni            # vorhandene PowerTray.ini übernehmen
)
$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\PowerTray'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Ask($q) { if ($Yes) { return $true }; (Read-Host "$q [J/n]") -notmatch '^(n|no|nein)$' }

function Find-FanControl {
    $p = Get-Process FanControl -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.Path) { return Split-Path $p.Path }
    foreach ($d in "${env:ProgramFiles(x86)}\FanControl", "$env:ProgramFiles\FanControl", "$env:LOCALAPPDATA\Programs\FanControl") {
        if (Test-Path "$d\FanControl.exe") { return $d }
    }
    $null
}

function Get-Bin($name) {
    foreach ($p in "$here\bin\$name", "$here\$name") { if (Test-Path $p) { return $p } }
    if (Test-Path "$here\build.ps1") { & "$here\build.ps1" | Out-Host; if (Test-Path "$here\bin\$name") { return "$here\bin\$name" } }
    throw "$name nicht gefunden (weder vorgebaut noch baubar)."
}

Say "== PowerTray Installer ==" Cyan

# ---------------- App ----------------
if ($Component -in 'All', 'App') {
    $exe = Get-Bin 'PowerTray.exe'
    Get-Process PowerTray -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force $appDir | Out-Null
    Copy-Item $exe "$appDir\PowerTray.exe" -Force
    if ($ImportIni -and (Test-Path $ImportIni)) { Copy-Item $ImportIni "$appDir\PowerTray.ini" -Force; Say "Einstellungen übernommen: $ImportIni" }

    # Standard-Energiepläne wiederherstellen, falls sie fehlen (gleiche GUID, damit die Profile passen)
    $plans = (powercfg /list) -join "`n"
    foreach ($g in 'a1841308-3541-4fab-bc81-f71556f20b4a', '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c') {
        if ($plans -notmatch $g) { powercfg -duplicatescheme $g $g 2>$null | Out-Null; if ($LASTEXITCODE -eq 0) { Say "Energieplan $g wiederhergestellt" } }
    }

    # Startmenü-Verknüpfung
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'PowerTray.lnk'
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $s.TargetPath = "$appDir\PowerTray.exe"; $s.WorkingDirectory = $appDir; $s.Description = 'Energieplan + Lüfterkurve umschalten'; $s.Save()
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'PowerTray Dashboard.lnk'))
    $s.TargetPath = "$appDir\PowerTray.exe"; $s.Arguments = '--dashboard'; $s.WorkingDirectory = $appDir; $s.Description = 'PowerTray Dashboard'; $s.Save()

    if (-not $NoAutostart) { Set-ItemProperty $runKey PowerTray "`"$appDir\PowerTray.exe`"" }
    # Uninstall-Eintrag (Apps & Features)
    $u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PowerTray'
    New-Item $u -Force | Out-Null
    Copy-Item "$here\uninstall.ps1" "$appDir\uninstall.ps1" -Force -ErrorAction SilentlyContinue
    Set-ItemProperty $u DisplayName 'PowerTray'
    Set-ItemProperty $u DisplayVersion '1.4.0'
    Set-ItemProperty $u Publisher 'PowerTray'
    Set-ItemProperty $u DisplayIcon "$appDir\PowerTray.exe"
    Set-ItemProperty $u InstallLocation $appDir
    Set-ItemProperty $u UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$appDir\uninstall.ps1`""
    Set-ItemProperty $u NoModify 1; Set-ItemProperty $u NoRepair 1
    Say "PowerTray installiert: $appDir" Green
}

# ---------------- FanControl + Plugin ----------------
if ($Component -in 'All', 'Plugin') {
    $fc = Find-FanControl
    if (-not $fc -and -not $NoFanControl -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        if (Ask 'FanControl (Lüftersteuerung, kostenlos) ist nicht installiert. Jetzt per winget installieren?') {
            winget install --id Rem0o.FanControl -e --accept-source-agreements --accept-package-agreements | Out-Host
            $fc = Find-FanControl
        }
    }
    if (-not $fc) {
        Say 'FanControl nicht gefunden - Plugin übersprungen. PowerTray schaltet dann nur Energiepläne.' Yellow
    } else {
        $dll = Get-Bin 'FanControl.WattSensors.dll'
        $plug = Join-Path $fc 'Plugins'
        $fcExe = Join-Path $fc 'FanControl.exe'
        $wasRunning = [bool](Get-Process FanControl -ErrorAction SilentlyContinue)
        Get-Process FanControl -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 1
        try {
            New-Item -ItemType Directory -Force $plug | Out-Null
            Copy-Item $dll $plug -Force
        } catch [System.UnauthorizedAccessException] {
            Say 'Adminrechte nötig für den FanControl-Plugin-Ordner - bestätige die UAC-Abfrage.' Yellow
            $cmd = "New-Item -ItemType Directory -Force '$plug' | Out-Null; Copy-Item '$dll' '$plug' -Force"
            Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd
        }
        if (Test-Path (Join-Path $plug 'FanControl.WattSensors.dll')) { Say "Plugin installiert: $plug" Green } else { Say 'Plugin-Kopie fehlgeschlagen.' Red }
        if ($wasRunning -or -not $NoStart) { Start-Process $fcExe -ArgumentList '-m' -WorkingDirectory $fc }
    }
}

if ($Component -in 'All', 'App' -and -not $NoStart) { Start-Process "$appDir\PowerTray.exe" -WorkingDirectory $appDir }

Say ''
Say 'Fertig. Nächste Schritte:' Cyan
Say ' 1. Tray-Icon (3-4 Balken) suchen, ggf. über ^ in der Taskleiste anheften.'
Say ' 2. In FanControl Lüfterkurven anlegen und als Leise.json / Ausgewogen.json / Leistung.json speichern.'
Say '    Tipp: Claude oder ChatGPT können das mit AGENTS.md aus dem Repo für dich einrichten.'
