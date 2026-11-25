# Measures the performance budgets in IMPLEMENTATION_PLAN.md section 6 and writes artifacts\bench.md. Slow (several
# minutes): it builds the GPU whisper, cleans a 15-minute TTS narration and exports a 12-minute video.
# Timeline frame rate and A/V drift need the live editor and are not measured here.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release | Out-Null

$artifacts = Join-Path $projectRoot 'artifacts'
$report = Join-Path $artifacts 'bench.md'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
Set-Content -LiteralPath $report -Encoding utf8 -Value @(
    "# Cutroom benchmarks ($(Get-Date -Format 'yyyy-MM-dd HH:mm'), $env:COMPUTERNAME)", '',
    '| Measure | Time | Budget | Result | Notes |', '|---|---:|---:|---|---|')
& dotnet test (Join-Path $projectRoot 'tests\Cutroom.Tests\Cutroom.Tests.csproj') -c Release --no-build -nologo --filter 'Category=Bench'
$testsFailed = $LASTEXITCODE -ne 0

# The editor itself opening the 90-minute project the tests generated, through to the Edit stage drawn.
$project = Join-Path $artifacts 'bench\bench-90min.cutroom.json'
$data = Join-Path $artifacts 'bench\data'
$out = Join-Path $artifacts 'bench\open.txt'
Remove-Item -Recurse -Force $data, $out -ErrorAction SilentlyContinue
$env:CUTROOM_DATA = $data
try {
    $exe = Join-Path $projectRoot 'src/Cutroom.App/bin/Release/net10.0-windows/win-x64/Cutroom.exe'
    $p = Start-Process -FilePath $exe -ArgumentList @('--bench-open', "`"$project`"", "`"$out`"") -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(120000)) { $p.Kill(); throw 'Editor open benchmark timed out' }
} finally { Remove-Item Env:CUTROOM_DATA }
if ($p.ExitCode -ne 0) { throw "Editor open benchmark failed: $(Get-Content -LiteralPath $out -Raw)" }
$seconds = [double]::Parse((Get-Content -LiteralPath $out -Raw).Trim(), [Globalization.CultureInfo]::InvariantCulture)
$result = if ($seconds -le 2) { 'ok' } else { 'OVER' }
Add-Content -LiteralPath $report -Encoding utf8 -Value "| Open a 90-minute project (editor, to Edit drawn) | $($seconds.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture)) s | 2 s | $result | Release build, 2560x1400 window |"

Get-Content -LiteralPath $report -Raw -Encoding UTF8
if ($testsFailed -or $result -ne 'ok') { throw 'At least one benchmark is over budget (see above)' }
