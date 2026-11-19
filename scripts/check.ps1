# Build plus all fast tests. Needs no models or recordings; this is the project's check command.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
# The GPU whisper build is skipped to keep the check fast; it is used here if already built.
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug -SkipWhisperGpu
& dotnet test (Join-Path $projectRoot 'tests\Cutroom.Tests\Cutroom.Tests.csproj') -c Debug --no-build -nologo --filter 'Category!=Models&Category!=Live'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

# UI smoke: open the real app off-screen with throwaway data, render every stage at 1440p and 1080p sizes.
$smokeOut = Join-Path $projectRoot 'artifacts\smoke'
$smokeData = Join-Path $projectRoot 'artifacts\smoke-data'
Remove-Item -Recurse -Force $smokeOut, $smokeData -ErrorAction SilentlyContinue
$env:CUTROOM_DATA = $smokeData
try {
    $exe = Join-Path $projectRoot 'src/Cutroom.App/bin/Debug/net10.0-windows/win-x64/Cutroom.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--smoke', $smokeOut) -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(120000)) { $p.Kill(); throw 'UI smoke timed out' }
    if ($p.ExitCode -ne 0) { throw "UI smoke failed: $(Get-Content (Join-Path $smokeOut 'smoke-error.txt') -Raw -ErrorAction SilentlyContinue)" }
    $shots = @(Get-ChildItem $smokeOut -Filter *.png)
    if ($shots.Count -ne 8) { throw "UI smoke produced $($shots.Count) of 8 screenshots" }
} finally { Remove-Item Env:CUTROOM_DATA }
Write-Output 'Cutroom check passed.'
