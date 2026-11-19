# Shared helpers for pinned, sha256-checked downloads into .deps (dot-sourced by bootstrap and build scripts).
$projectRoot = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $projectRoot '.deps'
$cache = Join-Path $deps 'dl'
New-Item -ItemType Directory -Path $cache -Force | Out-Null

function Get-Pinned([string]$Name, [string]$Url, [string]$Sha256) {
    $file = Join-Path $cache $Name
    if (!(Test-Path -LiteralPath $file)) {
        Write-Host "Downloading $Name"
        Invoke-WebRequest -Uri $Url -OutFile "$file.download" -UseBasicParsing
        Move-Item -LiteralPath "$file.download" -Destination $file -Force
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) {
        Remove-Item -LiteralPath $file
        throw "$Name did not match its pinned checksum"
    }
    return $file
}

function Test-Marker([string]$Folder, [string]$Sha256) {
    $marker = Join-Path $Folder 'pinned-sha256.txt'
    return (Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -eq $Sha256
}

function Expand-Pinned([string]$Archive, [string]$Folder, [string]$Sha256) {
    if (Test-Marker $Folder $Sha256) { return }
    $unpack = Join-Path $deps 'unpacked'
    foreach ($stale in @($unpack, $Folder)) { if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force } }
    Expand-Archive -LiteralPath $Archive -DestinationPath $unpack -Force
    $source = Get-ChildItem -LiteralPath $unpack -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Path $Folder -Force | Out-Null
    Copy-Item -Path (Join-Path $source.FullName '*') -Destination $Folder -Recurse -Force
    Remove-Item -LiteralPath $unpack -Recurse -Force
    Set-Content -LiteralPath (Join-Path $Folder 'pinned-sha256.txt') -Value $Sha256 -Encoding ascii
}
