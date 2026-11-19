# Runs the model-based tests: the speech pipeline and the full end-to-end run (TTS narration with retakes, cleanup, edit, 1080p60 export checked with ffprobe, loudness and re-transcription). Needs the speech models in %LOCALAPPDATA%\Cutroom\models.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
& dotnet test (Join-Path $projectRoot 'tests\Cutroom.Tests\Cutroom.Tests.csproj') -c Debug --no-build -nologo --filter 'Category=Models'
if ($LASTEXITCODE -ne 0) { throw 'Speech tests failed' }
