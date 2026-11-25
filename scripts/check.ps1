# Build plus all fast tests. Needs no models or recordings; this is the project's check command.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
# The GPU whisper build is skipped to keep the check fast; it is used here if already built.
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug -SkipWhisperGpu
& dotnet test (Join-Path $projectRoot 'tests\Cutroom.Tests\Cutroom.Tests.csproj') -c Debug --no-build -nologo --filter 'Category!=Models&Category!=Live&Category!=Bench'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

# Render empty and populated stages off-screen at 1440p and 1080p, at 100% and 150% scaling.
function Invoke-Smoke([string]$name, [int]$expectedShots, [string]$project) {
    $smokeOut = Join-Path $projectRoot "artifacts\$name"
    $smokeData = "$smokeOut-data"
    foreach ($path in @($smokeOut, $smokeData)) {
        if (Test-Path -LiteralPath $path) {
            $target = Get-Item -LiteralPath $path -Force
            if (-not $target.FullName.StartsWith("$projectRoot\", [StringComparison]::OrdinalIgnoreCase) -or
                ($target.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                ($target.Parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unexpected smoke directory: $path"
            }
            Remove-Item -LiteralPath $target.FullName -Recurse -Force
        }
    }
    $exe = Join-Path $projectRoot 'src/Cutroom.App/bin/Debug/net10.0-windows/win-x64/Cutroom.exe'
    $arguments = @('--smoke', ('"{0}"' -f $smokeOut))
    if ($project) { $arguments += '"{0}"' -f $project }
    $previousData = $env:CUTROOM_DATA
    $env:CUTROOM_DATA = $smokeData
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(120000)) { $p.Kill(); throw "UI smoke timed out ($name)" }
        if ($p.ExitCode -ne 0) { throw "UI smoke failed ($name): $(Get-Content (Join-Path $smokeOut 'smoke-error.txt') -Raw -ErrorAction SilentlyContinue)" }
    } finally { $env:CUTROOM_DATA = $previousData }
    $shots = @(Get-ChildItem $smokeOut -Filter *.png)
    if ($shots.Count -ne $expectedShots) { throw "UI smoke produced $($shots.Count) of $expectedShots screenshots ($name)" }
}

Invoke-Smoke 'smoke' 18
Invoke-Smoke 'smoke-populated' 19 (Join-Path $projectRoot 'artifacts\smoke-project\sample.cutroom.json')
Write-Output 'Cutroom check passed.'
