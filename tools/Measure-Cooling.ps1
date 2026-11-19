<#
.SYNOPSIS
  Optimier-Session: misst, wie stark jede Lüftergruppe die CPU-Temperatur unter Last beeinflusst.
  Setzt Lüfter über temporäre FanControl-Konfigurationen auf feste Stufen, erzeugt CPU-Volllast und
  wertet %ProgramData%\PowerTray\live.json (vom WattSensors-Plugin) aus.
  Ergebnis: %ProgramData%\PowerTray\messung.json (+ messung.csv mit Verlauf). Das Dashboard zeigt es an.
  Sicherheit: ab -AbortC °C CPU Package sofort Abbruch; am Ende wird immer das Profil -Restore aktiviert.
.EXAMPLE
  .\Measure-Cooling.ps1                         # Standard-Ablauf (~22 min)
  .\Measure-Cooling.ps1 -SettleSec 60 -MeasureSec 20   # Kurztest
#>
param(
    [string]$Base = 'Ausgewogen.json',
    [string]$Restore = 'Ausgewogen.json',
    [string]$CpuFan = 'Fan #2 CPU',
    [string[]]$CaseFans = @('Fan #3 BACK', 'Fan #4 UNTEN', 'Fan #6 Side', 'Fan #7 TOP'),
    [int]$SettleSec = 180,
    [int]$MeasureSec = 60,
    [int]$AbortC = 90,
    [int]$Threads = [Math]::Max(1, [Environment]::ProcessorCount / 2)   # ~Spielelast; volle Last ist auf Luftkühlern oft kühlerbegrenzt
)
$ErrorActionPreference = 'Stop'
$fcDir = 'C:\Program Files (x86)\FanControl'
$fcExe = Join-Path $fcDir 'FanControl.exe'
$cfgDir = Join-Path $fcDir 'Configurations'
$out = Join-Path $env:ProgramData 'PowerTray'
$live = Join-Path $out 'live.json'
$log = Join-Path $out 'messung.log'
$csv = Join-Path $out 'messung.csv'

# Ablauf: Name, Last an?, CPU-Lüfter %, Gehäuse %
$Steps = @(
    @{ Name = 'Aufwärmen';            Load = $true; Cpu = 60;  Case = 20; Warm = $true }
    @{ Name = 'CPU 60 % · Gehäuse 20 %';  Load = $true; Cpu = 60;  Case = 20 }
    @{ Name = 'CPU 30 % · Gehäuse 20 %';  Load = $true; Cpu = 30;  Case = 20 }
    @{ Name = 'CPU 100 % · Gehäuse 20 %'; Load = $true; Cpu = 100; Case = 20 }
    @{ Name = 'CPU 100 % · Gehäuse 70 %'; Load = $true; Cpu = 100; Case = 70 }
    @{ Name = 'CPU 60 % · Gehäuse 70 %';  Load = $true; Cpu = 60;  Case = 70 }
)

function Write-Log($t) { $l = "{0:HH:mm:ss} {1}" -f (Get-Date), $t; Write-Host $l; Add-Content $log $l -Encoding UTF8 }

function Read-Live {
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $j = Get-Content $live -Raw -ErrorAction Stop | ConvertFrom-Json
            $h = @{}
            foreach ($hw in $j.hw) { foreach ($s in $hw.s) { $h[$s.id] = [double]$s.v } }
            return $h
        } catch { Start-Sleep -Milliseconds 300 }
    }
    @{}
}

function Set-FixedFans([int]$cpu, [int]$case, [string]$name) {
    $cfg = Get-Content (Join-Path $cfgDir $Base) -Raw -Encoding UTF8 | ConvertFrom-Json
    $src = '/intelcpu/0/temperature/10'
    $curves = @($cfg.FanControl.FanCurves)
    foreach ($p in @(@{ N = 'TEST CPU'; V = $cpu }, @{ N = 'TEST Gehaeuse'; V = $case })) {
        $curves += [pscustomobject][ordered]@{
            Name = $p.N; IsHidden = $false; CommandMode = 0
            SelectedTempSource = [pscustomobject]@{ Identifier = $src }
            Points = @("0,$($p.V)", "150,$($p.V)"); MaximumTemperature = 150; MinimumTemperature = 0; MaximumCommand = 100
            HysteresisConfig = [pscustomobject]@{ ResponseTimeUp = 1; ResponseTimeDown = 1; HysteresisValueUp = 0; HysteresisValueDown = 0; IgnoreHysteresisAtLimits = $true }
        }
    }
    $cfg.FanControl.FanCurves = $curves
    foreach ($c in $cfg.FanControl.Controls) {
        $target = if ($c.NickName -eq $CpuFan) { 'TEST CPU' } elseif ($CaseFans -contains $c.NickName) { 'TEST Gehaeuse' } else { $null }
        if ($target) {
            $c.Enable = $true; $c.ManualControl = $false
            $c.SelectedFanCurve = [pscustomobject]@{ Name = $target }
            $c.SelectedCommandStepUp = 100; $c.SelectedCommandStepDown = 100
        }
    }
    $file = "PT-Test-$name.json"
    [IO.File]::WriteAllText((Join-Path $cfgDir $file), ($cfg | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding $false))
    Start-Process $fcExe -ArgumentList "-c `"$file`"" -WorkingDirectory $fcDir
}

$jobs = @()
function Start-Load { if ($script:jobs.Count) { return }; Write-Log "CPU-Last an ($Threads Threads)"; for ($i = 0; $i -lt $Threads; $i++) { $script:jobs += Start-Job { $x = 1.0; while ($true) { for ($k = 0; $k -lt 200000; $k++) { $x = [Math]::Sqrt($x + $k) } } } } }
function Stop-Load { if ($script:jobs.Count) { $script:jobs | Stop-Job -PassThru | Remove-Job -Force; $script:jobs = @(); Write-Log 'CPU-Last aus' } }

if (-not (Test-Path $live)) { throw "live.json fehlt - WattSensors-Plugin mit LiveDaten=1 muss in FanControl laufen." }
New-Item -ItemType Directory -Force $out | Out-Null
Remove-Item $log, $csv -ErrorAction SilentlyContinue
"zeit,schritt,cpu_pkg_c,cpu_max_c,cpu_w,sys1_c,sys2_c,sys5_c,cpu_rpm,back_rpm,unten_rpm,side_rpm,top_rpm" | Set-Content $csv -Encoding UTF8

$ptWasRunning = [bool](Get-Process PowerTray -ErrorAction SilentlyContinue)
Get-Process PowerTray -ErrorAction SilentlyContinue | Stop-Process -Force    # Automatik/Wächter während der Messung aus
$results = @()
$aborted = $null
try {
    $n = 0
    foreach ($st in $Steps) {
        $n++
        Write-Log "Schritt $n/$($Steps.Count): $($st.Name)"
        Set-FixedFans $st.Cpu $st.Case $n
        if ($st.Load) { Start-Load } else { Stop-Load }
        $samples = @()
        $hot = $false
        $dur = if ($st.Warm) { 120 } else { $SettleSec + $MeasureSec }
        $t0 = Get-Date
        while (((Get-Date) - $t0).TotalSeconds -lt $dur) {
            Start-Sleep 2
            $h = Read-Live
            $pkg = $h['/intelcpu/0/temperature/10']
            if ($pkg -ge $AbortC) {
                # Schritt zu heiß: Last kurz aus, Lüfter hoch, Schritt als "über Grenze" werten und weitermachen
                Write-Log "  !! $pkg °C >= $AbortC °C - Schritt übersprungen"
                Stop-Load; Set-FixedFans 100 70 "cool$n"; Start-Sleep 45
                $hot = $true; break
            }
            $row = [ordered]@{
                pkg = $pkg; max = $h['/intelcpu/0/temperature/0']; w = $h['/intelcpu/0/power/0']
                s1 = $h['/lpc/nct6797d/0/temperature/1']; s2 = $h['/lpc/nct6797d/0/temperature/2']; s5 = $h['/lpc/nct6797d/0/temperature/5']
                cpuRpm = $h['/lpc/nct6797d/0/fan/1']; backRpm = $h['/lpc/nct6797d/0/fan/2']; untenRpm = $h['/lpc/nct6797d/0/fan/3']
                sideRpm = $h['/lpc/nct6797d/0/fan/5']; topRpm = $h['/lpc/nct6797d/0/fan/6']
            }
            ("{0:o},{1}," -f (Get-Date), $n) + (($row.Values | % { if ($_ -ne $null) { ([double]$_).ToString([Globalization.CultureInfo]::InvariantCulture) } else { '' } }) -join ',') | Add-Content $csv -Encoding UTF8
            if (-not $st.Warm -and ((Get-Date) - $t0).TotalSeconds -ge $SettleSec) { $samples += [pscustomobject]$row }
        }
        if ($hot -and -not $st.Warm) {
            $results += [pscustomobject][ordered]@{ schritt = $st.Name; cpuFan = $st.Cpu; caseFan = $st.Case; zuHeiss = $true; cpuPkgC = $AbortC }
        }
        elseif (-not $st.Warm) {
            $avg = { param($k) [Math]::Round((($samples | % { $_.$k } | ? { $_ -ne $null }) | Measure-Object -Average).Average, 1) }
            $r = [ordered]@{
                schritt = $st.Name; cpuFan = $st.Cpu; caseFan = $st.Case
                cpuPkgC = (& $avg 'pkg'); cpuMaxC = (& $avg 'max'); cpuW = (& $avg 'w')
                sys1C = (& $avg 's1'); sys2C = (& $avg 's2'); sys5C = (& $avg 's5')
                cpuRpm = (& $avg 'cpuRpm'); caseRpm = [Math]::Round(((& $avg 'backRpm') + (& $avg 'untenRpm') + (& $avg 'sideRpm') + (& $avg 'topRpm')) / 4)
            }
            $results += [pscustomobject]$r
            Write-Log ("  -> CPU {0} °C (max Kern {1}) bei {2} W · CPU-Lüfter {3} RPM · Gehäuse {4} RPM · Board {5}/{6}/{7} °C" -f $r.cpuPkgC, $r.cpuMaxC, $r.cpuW, $r.cpuRpm, $r.caseRpm, $r.sys1C, $r.sys2C, $r.sys5C)
        }
    }
} catch {
    if (-not $aborted) { $aborted = $_.Exception.Message }
    Write-Log "ABBRUCH: $aborted"
} finally {
    Stop-Load
    Start-Process $fcExe -ArgumentList "-c `"$Restore`"" -WorkingDirectory $fcDir
    Write-Log "Profil $Restore wiederhergestellt"
    Start-Sleep 2
    Get-ChildItem $cfgDir -Filter 'PT-Test-*.json' | Remove-Item -Force
    $res = [ordered]@{ zeit = (Get-Date).ToString('o'); abgebrochen = $aborted; schritte = $results }
    [IO.File]::WriteAllText((Join-Path $out 'messung.json'), ($res | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    $pt = Join-Path $env:LOCALAPPDATA 'Programs\PowerTray\PowerTray.exe'
    if ($ptWasRunning -and (Test-Path $pt)) { Start-Process $pt }
    Write-Log 'Fertig.'
}
