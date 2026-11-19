<#
.SYNOPSIS
  Findet heraus, welche Lüfteranschlüsse belegt sind und welcher Anschluss welcher Lüfter ist.
  Dreht jeden Anschluss nacheinander auf 100 % (alle anderen auf -BaseLevel %) und misst die Drehzahl über
  %ProgramData%\PowerTray\live.json (WattSensors-Plugin). Ergebnis: %ProgramData%\PowerTray\luefter.json
.DESCRIPTION
  Für KI-Agenten: Mit -Only "<NickName>" läuft nur ein Anschluss hoch - den Nutzer dabei fragen, welcher Lüfter
  im Gehäuse laut wurde (vorne/oben/hinten/unten/seite, rein/raus, wie viele). Daraus [Gehaeuse] in PowerTray.ini füllen.
  Am Ende wird immer das Profil -Restore wieder aktiviert. GPU-Lüfter (ADLX/NVIDIA) werden nicht angefasst.
.EXAMPLE
  .\Detect-Fans.ps1                          # alle Anschlüsse nacheinander (je ~12 s)
  .\Detect-Fans.ps1 -Only "Fan #3"           # nur einen Anschluss 15 s hochdrehen
#>
param(
    [string]$Only,
    [int]$Seconds = 12,
    [int]$BaseLevel = 30,
    [string]$Base = 'userConfig.json',
    [string]$Restore = 'Ausgewogen.json'
)
$ErrorActionPreference = 'Stop'
$fc = @("${env:ProgramFiles(x86)}\FanControl", "$env:ProgramFiles\FanControl", "$env:LOCALAPPDATA\Programs\FanControl") | Where-Object { Test-Path "$_\FanControl.exe" } | Select-Object -First 1
if (-not $fc) { throw 'FanControl nicht gefunden.' }
$cfgDir = Join-Path $fc 'Configurations'
$out = Join-Path $env:ProgramData 'PowerTray'
$live = Join-Path $out 'live.json'
if (-not (Test-Path $live)) { throw 'live.json fehlt - WattSensors-Plugin mit LiveDaten=1 muss in FanControl laufen.' }

function Read-Live { try { $h = @{}; foreach ($hw in (Get-Content $live -Raw | ConvertFrom-Json).hw) { foreach ($s in $hw.s) { $h[$s.id] = [double]$s.v } }; $h } catch { @{} } }
function LiveId($fcId) { $fcId -replace '^(/lpc/[^/]+)/', '$1/0/' }

$baseCfg = Get-Content (Join-Path $cfgDir $Base) -Raw -Encoding UTF8
$controls = @(($baseCfg | ConvertFrom-Json).FanControl.Controls | Where-Object { $_.Identifier -like '/lpc/*' -or $_.Identifier -like '/ec/*' })
if ($Only) { $targets = @($controls | Where-Object { $_.NickName -like "*$Only*" }); if (-not $targets) { throw "Kein Anschluss passt zu '$Only'." }; $Seconds = [Math]::Max($Seconds, 15) } else { $targets = $controls }

function Apply($targetId) {
    $cfg = $baseCfg | ConvertFrom-Json
    $src = 'Watt-Sensoren (CPU/GPU)/WattSensors/cpu/package'
    $mk = { param($n, $v) [pscustomobject][ordered]@{ Name = $n; IsHidden = $false; CommandMode = 0; SelectedTempSource = [pscustomobject]@{ Identifier = $src }
            Points = @("0,$v", "1000,$v"); MaximumTemperature = 1000; MinimumTemperature = 0; MaximumCommand = 100
            HysteresisConfig = [pscustomobject]@{ ResponseTimeUp = 1; ResponseTimeDown = 1; HysteresisValueUp = 0; HysteresisValueDown = 0; IgnoreHysteresisAtLimits = $true } } }
    $cfg.FanControl.FanCurves = @(@($cfg.FanControl.FanCurves) + (& $mk 'DETECT Basis' $BaseLevel) + (& $mk 'DETECT Ziel' 100))
    foreach ($c in $cfg.FanControl.Controls) {
        if ($controls.Identifier -contains $c.Identifier) {
            $c.Enable = $true; $c.ManualControl = $false; $c.SelectedCommandStepUp = 100; $c.SelectedCommandStepDown = 100
            $c.SelectedFanCurve = [pscustomobject]@{ Name = $(if ($c.Identifier -eq $targetId) { 'DETECT Ziel' } else { 'DETECT Basis' }) }
        }
    }
    $name = 'PT-Detect.json'
    [IO.File]::WriteAllText((Join-Path $cfgDir $name), ($cfg | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding $false))
    Start-Process (Join-Path $fc 'FanControl.exe') -ArgumentList "-c `"$name`"" -WorkingDirectory $fc
}

$results = @()
try {
    Apply '-'; Start-Sleep 8
    $h0 = Read-Live
    foreach ($t in $targets) {
        $fanId = LiveId ($t.Identifier -replace '/control/', '/fan/')
        Write-Host ("→ {0,-20} läuft jetzt {1} s auf 100 % …" -f $t.NickName, $Seconds)
        Apply $t.Identifier
        Start-Sleep $Seconds
        $h = Read-Live
        $r = [ordered]@{ nickName = $t.NickName; control = $t.Identifier; rpmBasis = $h0[$fanId]; rpmMax = $h[$fanId]
            belegt = ($h[$fanId] -gt 200) }
        $results += [pscustomobject]$r
        Write-Host ("   {0,6} rpm bei {1} %  →  {2,6} rpm bei 100 %   {3}" -f $r.rpmBasis, $BaseLevel, $r.rpmMax, $(if ($r.belegt) { 'belegt' } else { 'leer / kein Tacho' }))
        Apply '-'; Start-Sleep 4
    }
} finally {
    Start-Process (Join-Path $fc 'FanControl.exe') -ArgumentList "-c `"$Restore`"" -WorkingDirectory $fc
    Start-Sleep 2
    Remove-Item (Join-Path $cfgDir 'PT-Detect.json') -ErrorAction SilentlyContinue
}
if (-not $Only) { [IO.File]::WriteAllText((Join-Path $out 'luefter.json'), ($results | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false)); "Gespeichert: $out\luefter.json" }
