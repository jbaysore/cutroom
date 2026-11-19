# Publishes Cutroom self-contained and installs it for this user with a Start menu shortcut.
# Settings, projects, the library index and models live elsewhere and are never touched.
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Cutroom'), [switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1') -SkipManaged
$publish = Join-Path $projectRoot 'artifacts\publish'
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue
& dotnet publish (Join-Path $projectRoot 'src\Cutroom.App\Cutroom.App.csproj') -c Release -r win-x64 --self-contained true -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
foreach ($required in @('Cutroom.exe', 'Cutroom.Engine.Native.dll', 'ffmpeg.exe', 'ffprobe.exe', 'whisper\whisper-cli.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Publish is missing $required" }
}
if (!(Test-Path -LiteralPath (Join-Path $publish 'whisper-vulkan\whisper-cli.exe'))) { Write-Warning 'GPU transcription is not included; cleanup will transcribe on the CPU.' }

$exe = Join-Path $Destination 'Cutroom.exe'
# Only a Cutroom running from the install folder is closed, and it saves on close.
$running = @(Get-Process -Name Cutroom -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
foreach ($p in $running) {
    [void]$p.CloseMainWindow()
    if (!$p.WaitForExit(20000)) { throw 'Cutroom is still open; close it and run the installer again' }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $Destination -Recurse -Force

if (!$NoShortcut) {
    $link = Join-Path ([Environment]::GetFolderPath('Programs')) 'Cutroom.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.Description = 'Cutroom video editor'
    $shortcut.Save()
}
& (Join-Path $PSScriptRoot 'install-hub-operator.ps1') -Destination $Destination
Write-Output "Installed Cutroom to $Destination."
