<#
.SYNOPSIS
  Erzeugt FanControl-Profile (Leise/Ausgewogen/Leistung/Turbo) nach Lüfter-ROLLEN statt pro Lüfter.
  Jede Rolle ist eine Mix-Kurve (Maximum) aus mehreren Quellen - so reagiert jeder Lüfter auf alles, was für
  seine Position relevant ist:
    CPU-Kühler  : CPU-Watt              + CPU-Notfall (°C)
    Unten (rein): GPU-Watt              + Gehäuseluft (°C) + GPU-Notfall
    Seite (rein): CPU-Watt, GPU-Watt    + Gehäuseluft     + Notfälle
    Hinten (raus): CPU-Watt             + Gehäuseluft     + CPU-Notfall
    Oben (raus) : CPU-Watt, Gesamt-Watt + Gehäuseluft     + Notfälle
  Überdruck: Abluftkurven liegen immer unter den Zuluftkurven (-Pressure Prozentpunkte).
  Kalibrierung, Namen und Sensor-Einstellungen kommen aus -Base (userConfig.json) und bleiben erhalten.
  Vorher werden die bestehenden Profile nach Configurations\Backup-<Zeit> gesichert.
.NOTES
  Werte für Vitos PC (i9-9900K, RX 7900 XTX, 6x rein / 4x raus), abgeleitet aus tools\Measure-Cooling.ps1:
  bei 132 W CPU: CPU 60 % + Gehäuse 20 % -> >90 °C, CPU 60 % + Gehäuse 70 % -> 83,8 °C, CPU 100 % + Gehäuse 70 % -> 81 °C.
  => Gehäuseluftstrom ist bei CPU-Last mindestens so wichtig wie der CPU-Lüfter.
#>
param(
    [string]$ConfigDir = 'C:\Program Files (x86)\FanControl\Configurations',
    [string]$Base = 'userConfig.json',
    [int]$Pressure = 6,
    [switch]$NoBackup
)
$ErrorActionPreference = 'Stop'

# ---- Sensoren (FanControl-IDs) ----
$S = @{
    CpuW    = 'Watt-Sensoren (CPU/GPU)/WattSensors/cpu/package'
    GpuW    = 'Watt-Sensoren (CPU/GPU)/WattSensors/gpu/board'
    TotalW  = 'Watt-Sensoren (CPU/GPU)/WattSensors/total'
    CpuTemp = '/intelcpu/0/temperature/10'                      # CPU Package
    GpuHot  = 'ADLX/AMD Radeon RX 7900 XTX/768/temp/Hotspot'    # GPU Hotspot
    CaseAir = '/lpc/nct6797d/temperature/2'                     # Board "Temperature #2" = Gehäuseluft (gemessen)
}

# ---- Lüfter (NickName) -> Rolle ----
$Assign = [ordered]@{
    'Fan #2 CPU'   = 'CPU-Kuehler'
    'Fan #4 UNTEN' = 'Unten (rein)'
    'Fan #6 Side'  = 'Seite (rein)'
    'Fan #3 BACK'  = 'Hinten (raus)'
    'Fan #7 TOP'   = 'Oben (raus)'
    'Control 1 - AMD Radeon RX 7900 XTX' = 'GPU-Luefter'
}

# ---- Profile: Punkte "Wert,Prozent". Skala: CPU W 40-200, GPU W 100-450, Gesamt 150-650, Luft °C ----
$Profiles = [ordered]@{
    Leise = @{
        CpuFan  = '40,25', '80,38', '110,52', '130,62', '160,78', '200,92'
        CpuCase = '40,18', '80,24', '110,36', '130,46', '160,58', '200,70'     # CPU-Wärme -> Seite/Hinten/Oben
        GpuCase = '100,18', '200,22', '300,32', '380,42', '450,55'            # GPU-Wärme -> Unten/Seite
        Total   = '150,18', '300,24', '450,36', '650,55'
        Air     = '33,0', '36,30', '40,55', '45,85', '50,100'
        CpuHot  = '88,0', '93,90', '97,100'
        GpuHot  = '90,0', '98,70', '105,100'
        GpuFan  = '140,0', '160,20', '250,26', '330,34', '400,44', '460,56'
        GpuFanHot = '85,0', '92,45', '98,75', '104,100'
        Up = 2; Down = 8; StepUp = 6; StepDown = 2
    }
    Ausgewogen = @{
        CpuFan  = '40,30', '80,45', '110,60', '130,70', '160,85', '200,100'
        CpuCase = '40,20', '80,30', '110,45', '130,58', '160,70', '200,82'
        GpuCase = '100,20', '200,26', '300,38', '380,50', '450,65'
        Total   = '150,20', '300,30', '450,45', '650,65'
        Air     = '32,0', '35,30', '38,55', '42,85', '46,100'
        CpuHot  = '86,0', '91,90', '95,100'
        GpuHot  = '88,0', '96,70', '103,100'
        GpuFan  = '140,0', '160,22', '250,28', '330,38', '400,48', '460,60'
        GpuFanHot = '82,0', '90,45', '96,75', '102,100'
        Up = 2; Down = 7; StepUp = 8; StepDown = 2
    }
    Leistung = @{
        CpuFan  = '40,35', '80,55', '110,72', '130,82', '160,95', '190,100'
        CpuCase = '40,25', '80,38', '110,55', '130,68', '160,80', '190,90'
        GpuCase = '100,25', '200,32', '300,46', '380,60', '450,75'
        Total   = '150,25', '300,36', '450,55', '650,78'
        Air     = '32,0', '34,35', '37,60', '40,85', '44,100'
        CpuHot  = '84,0', '89,90', '93,100'
        GpuHot  = '88,0', '95,75', '100,100'
        GpuFan  = '130,0', '150,25', '250,32', '330,42', '400,52', '450,62'
        GpuFanHot = '80,0', '88,50', '94,75', '100,100'
        Up = 1; Down = 7; StepUp = 10; StepDown = 2
    }
    Turbo = @{
        CpuFan  = '40,45', '80,70', '110,88', '130,100'
        CpuCase = '40,35', '80,55', '110,75', '130,88', '160,100'
        GpuCase = '100,30', '200,48', '300,70', '380,90', '420,100'
        Total   = '150,30', '300,50', '450,75', '600,100'
        Air     = '30,0', '33,45', '36,75', '40,100'
        CpuHot  = '80,0', '86,90', '90,100'
        GpuHot  = '80,0', '90,80', '96,100'
        GpuFan  = '100,30', '200,45', '300,62', '380,82', '430,100'
        GpuFanHot = '60,0', '70,60', '80,90', '88,100'
        Up = 1; Down = 4; StepUp = 15; StepDown = 4
    }
}

function Graph($name, $src, $points, $min, $max, $up, $down, $hystUp, $hystDown) {
    [ordered]@{
        Name = $name; IsHidden = $false; CommandMode = 0
        SelectedTempSource = [ordered]@{ Identifier = $src }
        Points = @($points); MaximumTemperature = $max; MinimumTemperature = $min; MaximumCommand = 100
        HysteresisConfig = [ordered]@{ ResponseTimeUp = $up; ResponseTimeDown = $down; HysteresisValueUp = $hystUp; HysteresisValueDown = $hystDown; IgnoreHysteresisAtLimits = $true }
    }
}
function Mix($name, $curves) {
    [ordered]@{ Name = $name; IsHidden = $false; CommandMode = 0; SelectedFanCurves = @($curves | ForEach-Object { [ordered]@{ Name = $_ } }); SelectedMixFunction = 0 }
}
# Abluft = Zuluft minus Druck-Abstand (nie unter 15 %)
function Lower($points, $by) { $points | ForEach-Object { $x, $y = $_ -split ','; "$x,$([Math]::Max(15, [int]$y - $by))" } }

if (-not $NoBackup) {
    $bk = Join-Path $ConfigDir ("Backup-" + (Get-Date -Format 'yyyyMMdd-HHmm'))
    New-Item -ItemType Directory -Force $bk | Out-Null
    Get-ChildItem $ConfigDir -Filter *.json | Copy-Item -Destination $bk
    "Backup: $bk"
}

$baseJson = Get-Content (Join-Path $ConfigDir $Base) -Raw -Encoding UTF8
foreach ($pn in $Profiles.Keys) {
    $p = $Profiles[$pn]
    $cfg = $baseJson | ConvertFrom-Json
    $u = $p.Up; $d = $p.Down
    $cfg.FanControl.FanCurves = @(
        (Graph 'CPU Watt'               $S.CpuW    $p.CpuFan  0 300 $u $d 5 15)
        (Graph 'CPU Watt (Zuluft)'      $S.CpuW    $p.CpuCase 0 300 $u $d 5 15)
        (Graph 'CPU Watt (Abluft)'      $S.CpuW    (Lower $p.CpuCase $Pressure) 0 300 $u $d 5 15)
        (Graph 'GPU Watt (Zuluft)'      $S.GpuW    $p.GpuCase 0 500 $u $d 5 25)
        (Graph 'Gesamt Watt (Abluft)'   $S.TotalW  (Lower $p.Total $Pressure) 0 800 $u $d 8 20)
        (Graph 'Gehaeuseluft (Zuluft)'  $S.CaseAir $p.Air     20 60 3 15 1 2)
        (Graph 'Gehaeuseluft (Abluft)'  $S.CaseAir (Lower $p.Air $Pressure) 20 60 3 15 1 2)
        (Graph 'CPU Notfall'            $S.CpuTemp $p.CpuHot  20 120 1 $d 2 4)
        (Graph 'GPU Notfall'            $S.GpuHot  $p.GpuHot  20 120 1 $d 2 4)
        (Graph 'GPU-Luefter Watt'       $S.GpuW    $p.GpuFan    0 500 $u $d 5 30)
        (Graph 'GPU-Luefter Hotspot'    $S.GpuHot  $p.GpuFanHot 20 120 1 $d 2 4)
        (Mix 'CPU-Kuehler'   @('CPU Watt', 'CPU Notfall'))
        (Mix 'Unten (rein)'  @('GPU Watt (Zuluft)', 'Gehaeuseluft (Zuluft)', 'GPU Notfall'))
        (Mix 'Seite (rein)'  @('CPU Watt (Zuluft)', 'GPU Watt (Zuluft)', 'Gehaeuseluft (Zuluft)', 'CPU Notfall', 'GPU Notfall'))
        (Mix 'Hinten (raus)' @('CPU Watt (Abluft)', 'Gehaeuseluft (Abluft)', 'CPU Notfall'))
        (Mix 'Oben (raus)'   @('CPU Watt (Abluft)', 'Gesamt Watt (Abluft)', 'Gehaeuseluft (Abluft)', 'CPU Notfall', 'GPU Notfall'))
        (Mix 'GPU-Luefter'   @('GPU-Luefter Watt', 'GPU-Luefter Hotspot'))
    )
    foreach ($c in $cfg.FanControl.Controls) {
        if ($Assign.Contains($c.NickName)) {
            $c.Enable = $true; $c.ManualControl = $false
            $c.SelectedFanCurve = [ordered]@{ Name = $Assign[$c.NickName] }
            $c.SelectedCommandStepUp = $p.StepUp; $c.SelectedCommandStepDown = $p.StepDown
        }
    }
    $out = Join-Path $ConfigDir "$pn.json"
    [IO.File]::WriteAllText($out, ($cfg | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
    "geschrieben: $out"
}
$names = ($baseJson | ConvertFrom-Json).FanControl.Controls.NickName
foreach ($k in $Assign.Keys) { if ($names -notcontains $k) { Write-Warning "Lüfter '$k' nicht in $Base gefunden" } }
