# Runs the model-based speech tests (needs the ~2 GB of models in %LOCALAPPDATA%\Cutroom\models; downloads them if missing via the app).
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
& dotnet test (Join-Path $projectRoot 'tests\Cutroom.Tests\Cutroom.Tests.csproj') -c Debug --no-build -nologo --filter 'Category=Models'
if ($LASTEXITCODE -ne 0) { throw 'Speech tests failed' }
