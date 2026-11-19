<#
.SYNOPSIS
  Erzeugt FanControl-Profile (Leise/Ausgewogen/Leistung/Turbo) aus dem Gehäuse-Layout in PowerTray.ini.
  Funktioniert für beliebige Layouts: Front-/Top-/Boden-/Seitenlüfter, AIO-Radiatoren, beliebige Richtungen.

  Jede Lüftergruppe bekommt eine Mix-Kurve (Maximum) aus den Quellen, die für ihre Rolle zählen:
    CPU-Kühler / AIO-Radiator : CPU-Watt + CPU-Notfall
    GPU-Lüfter                : GPU-Watt + GPU-Hotspot
    Zuluft unten              : GPU-Watt + Gehäuseluft + GPU-Notfall          (bläst auf die Grafikkarte)
    Zuluft vorne/seite/oben   : CPU-Watt + GPU-Watt + Gehäuseluft + Notfälle
    Abluft hinten             : CPU-Watt + Gehäuseluft + CPU-Notfall          (direkt hinter dem CPU-Kühler)
    Abluft oben/sonst         : CPU-Watt + Gesamt-Watt + Gehäuseluft + Notfälle
  Überdruck: Abluftkurven liegen -Pressure Prozentpunkte unter den Zuluftkurven.
  Watt-Achsen werden auf CpuMaxWatt/GpuMaxWatt aus FanControl.WattSensors.ini skaliert (Referenz 250 W / 400 W).

  Eingaben (PowerTray.ini):
    [Gehaeuse]   LuefterN=Position|FanControl-NickName|Anzahl|rein/raus|Radiator
    [Sensoren]   CpuTemp=, GpuHotspot=, Gehaeuseluft=   (FanControl-Sensor-IDs; fehlende Quellen werden weggelassen)
  Vorher wird Configurations\ nach Backup-<Zeit> gesichert. Kalibrierung/Namen kommen aus -Base (userConfig.json).
.EXAMPLE
  .\Make-FanProfiles.ps1                 # alle Profile neu schreiben
  .\Make-FanProfiles.ps1 -WhatIf         # nur anzeigen, welche Kurven welcher Lüfter bekäme
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Ini = (Join-Path $env:LOCALAPPDATA 'Programs\PowerTray\PowerTray.ini'),
    [string]$FanControlDir,
    [string]$Base = 'userConfig.json',
    [int]$Pressure = 6
)
$ErrorActionPreference = 'Stop'

function Read-Ini($path) {
    $s = @{}; $cur = $null
    foreach ($l in Get-Content $path -Encoding UTF8) {
        $l = $l.Trim()
        if ($l -eq '' -or $l.StartsWith(';')) { continue }
        if ($l -match '^\[(.+)\]$') { $cur = @{}; $s[$Matches[1]] = $cur; continue }
        if ($cur -ne $null -and $l -match '^([^=]+)=(.*)$') { $cur[$Matches[1].Trim()] = ($Matches[2] -replace '\s;.*$', '').Trim() }
    }
    $s
}
function Find-FanControl {
    foreach ($d in $FanControlDir, "${env:ProgramFiles(x86)}\FanControl", "$env:ProgramFiles\FanControl", "$env:LOCALAPPDATA\Programs\FanControl") { if ($d -and (Test-Path "$d\FanControl.exe")) { return $d } }
    throw 'FanControl nicht gefunden (-FanControlDir angeben).'
}

$fc = Find-FanControl
$cfgDir = Join-Path $fc 'Configurations'
$conf = Read-Ini $Ini
$plug = Join-Path $fc 'Plugins\FanControl.WattSensors.ini'
$cpuMax = 250; $gpuMax = 400
if (Test-Path $plug) { $pi = Read-Ini $plug; foreach ($l in Get-Content $plug) { if ($l -match '^\s*CpuMaxWatt\s*=\s*([\d.]+)') { $cpuMax = [double]$Matches[1] }; if ($l -match '^\s*GpuMaxWatt\s*=\s*([\d.]+)') { $gpuMax = [double]$Matches[1] } } }
$kc = $cpuMax / 250; $kg = $gpuMax / 400; $kt = ($cpuMax + $gpuMax) / 650

# ---- Sensoren ----
$sens = if ($conf.ContainsKey('Sensoren')) { $conf['Sensoren'] } else { @{} }
$S = @{
    CpuW    = 'Watt-Sensoren (CPU/GPU)/WattSensors/cpu/package'
    GpuW    = 'Watt-Sensoren (CPU/GPU)/WattSensors/gpu/board'
    TotalW  = 'Watt-Sensoren (CPU/GPU)/WattSensors/total'
    CpuTemp = $sens['CpuTemp']
    GpuHot  = $sens['GpuHotspot']
    CaseAir = $sens['Gehaeuseluft']
}

# ---- Layout ----
$groups = @()
$g = if ($conf.ContainsKey('Gehaeuse')) { $conf['Gehaeuse'] } else { @{} }
$posMap = @{ cpu = 'CPU'; gpu = 'GPU'; unten = 'Unten'; bottom = 'Unten'; seite = 'Seite'; side = 'Seite'; vorne = 'Vorne'; front = 'Vorne'; hinten = 'Hinten'; rear = 'Hinten'; back = 'Hinten'; oben = 'Oben'; top = 'Oben' }
$defaultIn = @{ Unten = $true; Seite = $true; Vorne = $true; Hinten = $false; Oben = $false }
foreach ($k in ($g.Keys | Where-Object { $_ -match '^(Luefter|Fan)\d+$' } | Sort-Object { [int]($_ -replace '\D') })) {
    $p = $g[$k] -split '\|' | ForEach-Object { $_.Trim() }
    $pos = $posMap[$p[0].ToLower()]; if (-not $pos) { Write-Warning "Unbekannte Position in $k=$($g[$k])"; continue }
    $dir = if ($p.Count -gt 3 -and $p[3] -match '^(rein|in)$') { $true } elseif ($p.Count -gt 3 -and $p[3] -match '^(raus|out)$') { $false } else { $defaultIn[$pos] }
    $groups += [pscustomobject]@{ Pos = $pos; Nick = $p[1]; Intake = $dir; Radiator = ($p -contains 'Radiator' -or $p -contains 'AIO') }
}
if (-not $groups) {   # Kurzform: Position=NickName|Anzahl
    foreach ($pos in 'CPU', 'GPU', 'Unten', 'Seite', 'Vorne', 'Hinten', 'Oben') {
        if ($g[$pos]) { $groups += [pscustomobject]@{ Pos = $pos; Nick = ($g[$pos] -split '\|')[0].Trim(); Intake = $defaultIn[$pos]; Radiator = $false } }
    }
}
if (-not $groups) { throw "Kein Layout in [Gehaeuse] von $Ini." }

# ---- Profile (Referenz: CPU 250 W, GPU 400 W; x-Werte werden skaliert) ----
$Profiles = [ordered]@{
    Leise      = @{ CpuFan = '40,25', '80,38', '110,52', '130,62', '160,78', '200,92'; CpuCase = '40,18', '80,24', '110,36', '130,46', '160,58', '200,70'
                    GpuCase = '100,18', '200,22', '300,32', '380,42', '450,55'; Total = '150,18', '300,24', '450,36', '650,55'; Air = '33,0', '36,30', '40,55', '45,85', '50,100'
                    CpuHot = '88,0', '93,90', '97,100'; GpuHot = '90,0', '98,70', '105,100'; GpuFan = '140,0', '160,20', '250,26', '330,34', '400,44', '460,56'; GpuFanHot = '85,0', '92,45', '98,75', '104,100'
                    Up = 2; Down = 8; StepUp = 6; StepDown = 2 }
    Ausgewogen = @{ CpuFan = '40,30', '80,45', '110,60', '130,70', '160,85', '200,100'; CpuCase = '40,20', '80,30', '110,45', '130,58', '160,70', '200,82'
                    GpuCase = '100,20', '200,26', '300,38', '380,50', '450,65'; Total = '150,20', '300,30', '450,45', '650,65'; Air = '32,0', '35,30', '38,55', '42,85', '46,100'
                    CpuHot = '86,0', '91,90', '95,100'; GpuHot = '88,0', '96,70', '103,100'; GpuFan = '140,0', '160,22', '250,28', '330,38', '400,48', '460,60'; GpuFanHot = '82,0', '90,45', '96,75', '102,100'
                    Up = 2; Down = 7; StepUp = 8; StepDown = 2 }
    Leistung   = @{ CpuFan = '40,35', '80,55', '110,72', '130,82', '160,95', '190,100'; CpuCase = '40,25', '80,38', '110,55', '130,68', '160,80', '190,90'
                    GpuCase = '100,25', '200,32', '300,46', '380,60', '450,75'; Total = '150,25', '300,36', '450,55', '650,78'; Air = '32,0', '34,35', '37,60', '40,85', '44,100'
                    CpuHot = '84,0', '89,90', '93,100'; GpuHot = '88,0', '95,75', '100,100'; GpuFan = '130,0', '150,25', '250,32', '330,42', '400,52', '450,62'; GpuFanHot = '80,0', '88,50', '94,75', '100,100'
                    Up = 1; Down = 7; StepUp = 10; StepDown = 2 }
    Turbo      = @{ CpuFan = '40,45', '80,70', '110,88', '130,100'; CpuCase = '40,35', '80,55', '110,75', '130,88', '160,100'
                    GpuCase = '100,30', '200,48', '300,70', '380,90', '420,100'; Total = '150,30', '300,50', '450,75', '600,100'; Air = '30,0', '33,45', '36,75', '40,100'
                    CpuHot = '80,0', '86,90', '90,100'; GpuHot = '80,0', '90,80', '96,100'; GpuFan = '100,30', '200,45', '300,62', '380,82', '430,100'; GpuFanHot = '60,0', '70,60', '80,90', '88,100'
                    Up = 1; Down = 4; StepUp = 15; StepDown = 4 }
}

function Graph($name, $src, $points, $min, $max, $up, $down, $hu, $hd) {
    [ordered]@{ Name = $name; IsHidden = $false; CommandMode = 0; SelectedTempSource = [ordered]@{ Identifier = $src }; Points = @($points)
        MaximumTemperature = $max; MinimumTemperature = $min; MaximumCommand = 100
        HysteresisConfig = [ordered]@{ ResponseTimeUp = $up; ResponseTimeDown = $down; HysteresisValueUp = $hu; HysteresisValueDown = $hd; IgnoreHysteresisAtLimits = $true } }
}
function Mix($name, $curves) { [ordered]@{ Name = $name; IsHidden = $false; CommandMode = 0; SelectedFanCurves = @($curves | ForEach-Object { [ordered]@{ Name = $_ } }); SelectedMixFunction = 0 } }
function Scale($points, $k) { $points | ForEach-Object { $x, $y = $_ -split ','; "$([Math]::Round([double]$x * $k)),$y" } }
function Lower($points, $by) { $points | ForEach-Object { $x, $y = $_ -split ','; "$x,$([Math]::Max(15, [int]$y - $by))" } }

# Welche Quellen eine Gruppe bekommt
function Sources($grp) {
    $h = @(); if ($S.CpuTemp) { $h += 'CPU Notfall' }; $gh = @(); if ($S.GpuHot) { $gh += 'GPU Notfall' }
    $air = if ($S.CaseAir) { @(if ($grp.Intake) { 'Gehaeuseluft (Zuluft)' } else { 'Gehaeuseluft (Abluft)' }) } else { @() }
    if ($grp.Pos -eq 'CPU' -or $grp.Radiator) { return @('CPU Watt') + $h }
    if ($grp.Pos -eq 'GPU') { return @('GPU-Luefter Watt') + $(if ($S.GpuHot) { 'GPU-Luefter Hotspot' }) }
    if ($grp.Intake) {
        if ($grp.Pos -eq 'Unten') { return @('GPU Watt (Zuluft)') + $air + $gh }
        return @('CPU Watt (Zuluft)', 'GPU Watt (Zuluft)') + $air + $h + $gh
    }
    if ($grp.Pos -eq 'Hinten') { return @('CPU Watt (Abluft)') + $air + $h }
    return @('CPU Watt (Abluft)', 'Gesamt Watt (Abluft)') + $air + $h + $gh
}
function MixName($grp) {
    $n = @{ CPU = 'CPU-Kuehler'; GPU = 'GPU-Luefter' }[$grp.Pos]
    if ($n) { return $n }
    "$($grp.Pos)$(if ($grp.Radiator) { ' AIO' }) ($(if ($grp.Intake) { 'rein' } else { 'raus' }))"
}

"Layout ($($groups.Count) Gruppen), Skalierung CPU x$($kc), GPU x$($kg):"
foreach ($grp in $groups) { "  {0,-22} {1,-24} <- {2}" -f (MixName $grp), $grp.Nick, ((Sources $grp) -join ', ') }
if (-not $PSCmdlet.ShouldProcess($cfgDir, 'Profile schreiben')) { return }

$bk = Join-Path $cfgDir ("Backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $bk | Out-Null
Get-ChildItem $cfgDir -Filter *.json | Copy-Item -Destination $bk
"Backup: $bk"

$baseJson = Get-Content (Join-Path $cfgDir $Base) -Raw -Encoding UTF8
$nicks = ($baseJson | ConvertFrom-Json).FanControl.Controls.NickName
foreach ($grp in $groups) { if ($nicks -notcontains $grp.Nick) { Write-Warning "Lüfter '$($grp.Nick)' nicht in $Base gefunden" } }

foreach ($pn in $Profiles.Keys) {
    $p = $Profiles[$pn]; $u = $p.Up; $d = $p.Down
    $cfg = $baseJson | ConvertFrom-Json
    $curves = @(
        (Graph 'CPU Watt'              $S.CpuW   (Scale $p.CpuFan $kc) 0 400 $u $d 5 15)
        (Graph 'CPU Watt (Zuluft)'     $S.CpuW   (Scale $p.CpuCase $kc) 0 400 $u $d 5 15)
        (Graph 'CPU Watt (Abluft)'     $S.CpuW   (Lower (Scale $p.CpuCase $kc) $Pressure) 0 400 $u $d 5 15)
        (Graph 'GPU Watt (Zuluft)'     $S.GpuW   (Scale $p.GpuCase $kg) 0 700 $u $d 5 25)
        (Graph 'Gesamt Watt (Abluft)'  $S.TotalW (Lower (Scale $p.Total $kt) $Pressure) 0 1000 $u $d 8 20)
        (Graph 'GPU-Luefter Watt'      $S.GpuW   (Scale $p.GpuFan $kg) 0 700 $u $d 5 30)
    )
    if ($S.CaseAir) { $curves += (Graph 'Gehaeuseluft (Zuluft)' $S.CaseAir $p.Air 20 60 3 15 1 2); $curves += (Graph 'Gehaeuseluft (Abluft)' $S.CaseAir (Lower $p.Air $Pressure) 20 60 3 15 1 2) }
    if ($S.CpuTemp) { $curves += (Graph 'CPU Notfall' $S.CpuTemp $p.CpuHot 20 120 1 $d 2 4) }
    if ($S.GpuHot) { $curves += (Graph 'GPU Notfall' $S.GpuHot $p.GpuHot 20 120 1 $d 2 4); $curves += (Graph 'GPU-Luefter Hotspot' $S.GpuHot $p.GpuFanHot 20 120 1 $d 2 4) }
    $mixes = @{}
    foreach ($grp in $groups) { $mixes[(MixName $grp)] = @(Sources $grp | Where-Object { $_ }) }
    foreach ($m in $mixes.Keys) { $curves += (Mix $m $mixes[$m]) }
    $cfg.FanControl.FanCurves = $curves
    foreach ($c in $cfg.FanControl.Controls) {
        $grp = $groups | Where-Object { $_.Nick -eq $c.NickName } | Select-Object -First 1
        if ($grp) {
            $c.Enable = $true; $c.ManualControl = $false
            $c.SelectedFanCurve = [ordered]@{ Name = (MixName $grp) }
            $c.SelectedCommandStepUp = $p.StepUp; $c.SelectedCommandStepDown = $p.StepDown
        }
    }
    $out = Join-Path $cfgDir "$pn.json"
    [IO.File]::WriteAllText($out, ($cfg | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
    "geschrieben: $out"
}
