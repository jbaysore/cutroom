# Publish a complete self-contained install into an empty staging directory.
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (Test-Path -LiteralPath $Output) { throw "Publish output already exists: $Output" }
& (Join-Path $PSScriptRoot 'build.ps1') -SkipManaged
& dotnet publish (Join-Path $projectRoot 'src\Cutroom.App\Cutroom.App.csproj') -c Release -r win-x64 --self-contained true -o $Output -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($required in @('Cutroom.exe', 'Cutroom.Engine.Native.dll', 'ffmpeg.exe', 'ffprobe.exe', 'whisper\whisper-cli.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $Output $required) -PathType Leaf)) { throw "Publish is missing $required" }
}
if (!(Test-Path -LiteralPath (Join-Path $Output 'whisper-vulkan\whisper-cli.exe'))) {
    Write-Warning 'GPU transcription is not included; cleanup will transcribe on the CPU.'
}
