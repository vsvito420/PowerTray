<#
.SYNOPSIS
  Prüft ein FanControl-Profil unter CPU-Last: aktiviert es, erzeugt Last und protokolliert Temperaturen und Lüfter-%.
  Ergebnis wird an %ProgramData%\PowerTray\messung.json angehängt (Feld "profiltests"). Abbruch ab -AbortC °C.
#>
param([string]$Profile = 'Ausgewogen.json', [int]$Seconds = 240, [int]$AbortC = 90, [int]$Threads = [Math]::Max(1, [Environment]::ProcessorCount / 2))
$ErrorActionPreference = 'Stop'
$fcDir = 'C:\Program Files (x86)\FanControl'
$out = Join-Path $env:ProgramData 'PowerTray'
$live = Join-Path $out 'live.json'
function Read-Live { try { $h = @{}; foreach ($hw in (Get-Content $live -Raw | ConvertFrom-Json).hw) { foreach ($s in $hw.s) { $h[$s.id] = [double]$s.v } }; $h } catch { @{} } }

$pt = Get-Process PowerTray -ErrorAction SilentlyContinue
$pt | Stop-Process -Force
Start-Process (Join-Path $fcDir 'FanControl.exe') -ArgumentList "-c `"$Profile`"" -WorkingDirectory $fcDir
$jobs = 1..$Threads | % { Start-Job { $x = 1.0; while ($true) { for ($k = 0; $k -lt 200000; $k++) { $x = [Math]::Sqrt($x + $k) } } } }
$rows = @(); $t0 = Get-Date; $hot = $false
try {
    while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
        Start-Sleep 2
        $h = Read-Live
        $pkg = $h['/intelcpu/0/temperature/10']
        if ($pkg -ge $AbortC) { $hot = $true; Write-Host "!! $pkg °C - Abbruch"; break }
        if (((Get-Date) - $t0).TotalSeconds -ge $Seconds - 60) {
            $rows += [pscustomobject]@{ c = $pkg; w = $h['/intelcpu/0/power/0']; air = $h['/lpc/nct6797d/0/temperature/2']
                cpu = $h['/lpc/nct6797d/0/control/1']; back = $h['/lpc/nct6797d/0/control/2']; unten = $h['/lpc/nct6797d/0/control/3']
                side = $h['/lpc/nct6797d/0/control/5']; top = $h['/lpc/nct6797d/0/control/6'] }
        }
    }
} finally {
    $jobs | Stop-Job -PassThru | Remove-Job -Force
    if ($pt) { Start-Process (Join-Path $env:LOCALAPPDATA 'Programs\PowerTray\PowerTray.exe') }
}
$avg = { param($k) [Math]::Round((($rows | % { $_.$k }) | Measure-Object -Average).Average, 1) }
$r = [ordered]@{ profil = $Profile; zuHeiss = $hot; cpuPkgC = (& $avg 'c'); cpuW = (& $avg 'w'); luftC = (& $avg 'air')
    cpuFan = (& $avg 'cpu'); hinten = (& $avg 'back'); unten = (& $avg 'unten'); seite = (& $avg 'side'); oben = (& $avg 'top') }
[pscustomobject]$r | Format-List
$mf = Join-Path $out 'messung.json'
$m = if (Test-Path $mf) { Get-Content $mf -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{ schritte = @() } }
$tests = @($m.profiltests | ? { $_ -and $_.profil -ne $Profile }) + [pscustomobject]$r
$m | Add-Member -NotePropertyName profiltests -NotePropertyValue $tests -Force
[IO.File]::WriteAllText($mf, ($m | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
