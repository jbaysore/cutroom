# Project Hub runs this before restarting Cutroom. Build only committed local main.
param(
    [string]$Source = (Split-Path $PSScriptRoot -Parent),
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Cutroom'),
    [string]$BuildRoot = (Join-Path $env:LOCALAPPDATA 'CutroomDeployment\source')
)
$ErrorActionPreference = 'Stop'
$sourcePath = (Resolve-Path -LiteralPath $Source).ProviderPath
$destinationPath = [IO.Path]::GetFullPath($Destination)
$buildPath = [IO.Path]::GetFullPath($BuildRoot)
if ($buildPath -eq $sourcePath -or $buildPath.StartsWith($sourcePath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The deployment checkout must be outside the source repository.'
}
$parent = Split-Path $destinationPath -Parent
if (!$parent -or $destinationPath -eq $buildPath -or $buildPath.StartsWith($destinationPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The deployment checkout and the installation must be separate directories.'
}
$backup = $destinationPath + '.previous'
$stage = $destinationPath + '.staged-' + [Guid]::NewGuid().ToString('N')
$markerName = 'installed-commit.txt'
New-Item -ItemType Directory -Path $parent -Force | Out-Null

function Invoke-DeploymentGit {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw 'Git could not prepare the Cutroom deployment checkout.' }
}

function Stop-InstalledCutroom {
    $exe = Join-Path $destinationPath 'Cutroom.exe'
    $running = @(Get-Process -Name Cutroom -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if ($running.Count -eq 0) { return }
    $listener = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 3891 -State Listen -ErrorAction SilentlyContinue
    if ($running.Count -ne 1 -or $listener.OwningProcess -ne $running[0].Id) {
        throw 'Close Cutroom before updating. The installed process does not own the Cutroom operator endpoint.'
    }
    Write-Output 'Waiting for Cutroom to close gracefully.'
    Invoke-WebRequest -Uri 'http://127.0.0.1:3891/stop' -Method Post -UseBasicParsing -TimeoutSec 5 | Out-Null
    if (!$running[0].WaitForExit(30000)) { throw 'Cutroom is still open. Finish the current work or close the app, then retry Restart.' }
}

function Remove-DeploymentDirectory([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $directory = Get-Item -LiteralPath $path -Force
    if (!$directory.PSIsContainer -or $directory.Parent.FullName -ne $parent -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected deployment directory: $path"
    }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}

# Serialize installation and checkout changes across overlapping Hub requests.
$deploymentLock = [IO.File]::Open($destinationPath + '.update.lock', 'OpenOrCreate', 'ReadWrite', 'None')
$checkoutLock = $null
try {
    if (!(Test-Path -LiteralPath $destinationPath) -and (Test-Path -LiteralPath $backup)) {
        [IO.Directory]::Move($backup, $destinationPath)
    }
    $revision = (Invoke-DeploymentGit -C $sourcePath rev-parse --verify refs/heads/main).Trim()
    $marker = Join-Path $destinationPath $markerName
    if ((Test-Path -LiteralPath $marker -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $destinationPath 'Cutroom.exe') -PathType Leaf) -and
        (Get-Content -LiteralPath $marker -Raw).Trim() -eq $revision) {
        Write-Output "Cutroom $revision is already installed. Skipping build."
        return
    }

    Write-Output "Building Cutroom $revision from local main."
    New-Item -ItemType Directory -Path (Split-Path $buildPath -Parent) -Force | Out-Null
    $checkoutLock = [IO.File]::Open($buildPath + '.build.lock', 'OpenOrCreate', 'ReadWrite', 'None')
    $checkoutMarker = Join-Path $buildPath '.git\cutroom-deployment'
    if (!(Test-Path -LiteralPath $checkoutMarker -PathType Leaf)) {
        if (Test-Path -LiteralPath $buildPath) { throw "Deployment checkout is already occupied: $buildPath" }
        Invoke-DeploymentGit init $buildPath
        Set-Content -LiteralPath $checkoutMarker -Value 'Cutroom deployment checkout' -Encoding ascii
    }
    Invoke-DeploymentGit -C $buildPath fetch --no-tags $sourcePath $revision
    Invoke-DeploymentGit -C $buildPath checkout --detach --force $revision
    # Retain dependency and import-library caches while compiling native code from this revision.
    $native = Join-Path $buildPath 'artifacts\native\Cutroom.Engine.Native.dll'
    if (Test-Path -LiteralPath $native -PathType Leaf) { Remove-Item -LiteralPath $native }
    & (Join-Path $buildPath 'scripts\publish.ps1') -Output $stage
    Set-Content -LiteralPath (Join-Path $stage $markerName) -Value $revision -Encoding ascii

    Stop-InstalledCutroom
    Remove-DeploymentDirectory $backup
    if (Test-Path -LiteralPath $destinationPath) { [IO.Directory]::Move($destinationPath, $backup) }
    try { [IO.Directory]::Move($stage, $destinationPath) }
    catch {
        if (Test-Path -LiteralPath $backup) { [IO.Directory]::Move($backup, $destinationPath) }
        throw
    }
    Write-Output "Installed Cutroom $revision."
}
finally {
    try { Remove-DeploymentDirectory $stage }
    finally {
        if ($checkoutLock) { $checkoutLock.Dispose() }
        $deploymentLock.Dispose()
    }
}
