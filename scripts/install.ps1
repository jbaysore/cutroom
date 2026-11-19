# Publishes Cutroom self-contained and installs it for this user with a Start menu shortcut.
# Settings, projects, the library index and models live elsewhere and are never touched.
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Cutroom'), [switch]$NoShortcut)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$publish = $Destination + '.manual-' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
$deploymentLock = [IO.File]::Open($Destination + '.update.lock', 'OpenOrCreate', 'ReadWrite', 'None')

try {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $publish
    $exe = Join-Path $Destination 'Cutroom.exe'
    # Only a Cutroom running from the install folder is closed, and it saves on close.
    $running = @(Get-Process -Name Cutroom -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    foreach ($p in $running) {
        [void]$p.CloseMainWindow()
        if (!$p.WaitForExit(20000)) { throw 'Cutroom is still open; close it and run the installer again' }
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $marker = Join-Path $Destination 'installed-commit.txt'
    if (Test-Path -LiteralPath $marker -PathType Leaf) { Remove-Item -LiteralPath $marker }
    Copy-Item -Path (Join-Path $publish '*') -Destination $Destination -Recurse -Force
}
finally {
    try {
        if (Test-Path -LiteralPath $publish) {
            $stage = Get-Item -LiteralPath $publish -Force
            if (!$stage.PSIsContainer -or $stage.Parent.FullName -ne (Split-Path $Destination -Parent) -or
                ($stage.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Unexpected staging directory: $publish" }
            Remove-Item -LiteralPath $stage.FullName -Recurse -Force
        }
    }
    finally { $deploymentLock.Dispose() }
}

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
