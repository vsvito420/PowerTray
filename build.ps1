# Baut PowerTray.exe und FanControl.WattSensors.dll nach .\bin und packt ein Release-ZIP nach .\dist.
# Nutzt nur den in Windows enthaltenen .NET-Framework-4-Compiler (csc.exe) - kein Visual Studio nötig.
param([string]$FanControlDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc  = "$fw\csc.exe"
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

# --- PowerTray ---
& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    /win32manifest:"$root\src\PowerTray\app.manifest" `
    /out:"$bin\PowerTray.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll `
    "$root\src\PowerTray\*.cs" "$root\src\Shared\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build PowerTray fehlgeschlagen' }
Write-Host "OK: bin\PowerTray.exe"

# --- Plugin (braucht FanControl.Plugins.dll aus einer FanControl-Installation) ---
$cands = @(@($FanControlDir, "${env:ProgramFiles(x86)}\FanControl", "$env:ProgramFiles\FanControl", "$env:LOCALAPPDATA\Programs\FanControl", "$root\lib") |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'FanControl.Plugins.dll')) })
if ($cands) {
    $ref = Join-Path $cands[0] 'FanControl.Plugins.dll'
    & $csc /nologo /target:library /optimize+ /codepage:65001 `
        /out:"$bin\FanControl.WattSensors.dll" `
        /r:"$ref" /r:"$fw\netstandard.dll" `
        "$root\src\FanControl.WattSensors\*.cs" "$root\src\Shared\PowerSensors.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Build Plugin fehlgeschlagen' }
    Write-Host "OK: bin\FanControl.WattSensors.dll"
} else {
    Write-Warning 'FanControl.Plugins.dll nicht gefunden - Plugin wird übersprungen. FanControl installieren oder -FanControlDir angeben.'
}

# --- Release-ZIP ---
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'PowerTray'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\bin" | Out-Null
Copy-Item "$bin\*" "$stage\bin\"
Copy-Item "$root\install.ps1", "$root\install.cmd", "$root\uninstall.ps1", "$root\README.md", "$root\AGENTS.md", "$root\LICENSE" $stage; Copy-Item "$root\tools" "$stage\tools" -Recurse
Compress-Archive -Path "$stage\*" -DestinationPath "$dist\PowerTray-Setup.zip" -Force
Write-Host "ZIP: dist\PowerTray-Setup.zip"
