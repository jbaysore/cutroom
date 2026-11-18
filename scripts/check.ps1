# Build plus all fast tests. Needs no models or recordings; this is the project's check command.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
& dotnet test (Join-Path $projectRoot 'tests\Cutroom.Tests\Cutroom.Tests.csproj') -c Debug --no-build -nologo --filter 'Category!=Models&Category!=Live'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
Write-Output 'Cutroom check passed.'
