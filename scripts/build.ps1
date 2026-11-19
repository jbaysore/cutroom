param([switch]$SkipManaged, [switch]$SkipWhisperGpu, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'bootstrap.ps1') | Out-Null
if (!$SkipWhisperGpu) {
    # Optional: transcription on the graphics card. Built once (a few minutes), then cached; the CPU build is the fallback.
    try { & (Join-Path $PSScriptRoot 'build-whisper-vulkan.ps1') }
    catch { Write-Warning "GPU transcription was not built, so the CPU build will be used: $($_.Exception.Message)" }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ Build Tools are required' }
$dev = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$ffmpeg = Join-Path $projectRoot '.deps\ffmpeg'
$native = Join-Path $projectRoot 'artifacts\native'
New-Item -ItemType Directory -Path $native -Force | Out-Null

$prologue = "@echo off`r`nset `"PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%`"`r`ncall `"$dev`" -arch=x64 -host_arch=x64 >nul`r`nif errorlevel 1 exit /b 1`r`ncd /d `"$native`"`r`n"

# The FFmpeg archive ships GNU import libraries; generate MSVC ones from the DLL exports (once per pinned build).
$libraries = @('avcodec', 'avformat', 'avutil', 'swscale')
$marker = Join-Path $native 'importlibs.sha'
$pinned = (Get-Content -LiteralPath (Join-Path $ffmpeg 'pinned-sha256.txt') -Raw).Trim()
if (!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $pinned) {
    $script = $prologue
    foreach ($library in $libraries) {
        $dll = Get-ChildItem -LiteralPath (Join-Path $ffmpeg 'bin') -Filter ($library + '-*.dll') | Select-Object -First 1
        $script += "dumpbin /nologo /exports `"$($dll.FullName)`" > `"$library.exports`"`r`n"
    }
    [IO.File]::WriteAllText((Join-Path $native 'exports.cmd'), $script, [Text.Encoding]::ASCII)
    & cmd.exe /d /c (Join-Path $native 'exports.cmd')
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect FFmpeg exports' }
    $script = $prologue
    foreach ($library in $libraries) {
        $names = Get-Content -LiteralPath (Join-Path $native ($library + '.exports')) | ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)') { $matches[1] } }
        $dll = Get-ChildItem -LiteralPath (Join-Path $ffmpeg 'bin') -Filter ($library + '-*.dll') | Select-Object -First 1
        [IO.File]::WriteAllLines((Join-Path $native ($library + '.def')), @(('LIBRARY "' + $dll.Name + '"'), 'EXPORTS') + $names, [Text.Encoding]::ASCII)
        $script += "lib /nologo /machine:x64 /def:$library.def /out:$library.lib`r`nif errorlevel 1 exit /b 1`r`n"
    }
    [IO.File]::WriteAllText((Join-Path $native 'importlibs.cmd'), $script, [Text.Encoding]::ASCII)
    & cmd.exe /d /c (Join-Path $native 'importlibs.cmd')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create FFmpeg import libraries' }
    Set-Content -LiteralPath $marker -Value $pinned -Encoding ascii
}

$include = Join-Path $ffmpeg 'include'
$source = Join-Path $projectRoot 'native\engine\engine.cpp'
$output = Join-Path $native 'Cutroom.Engine.Native.dll'
$sourceTime = (Get-Item -LiteralPath $source).LastWriteTimeUtc, (Get-Item -LiteralPath (Join-Path $projectRoot 'native\engine\engine.h')).LastWriteTimeUtc | Sort-Object | Select-Object -Last 1
if (!(Test-Path -LiteralPath $output) -or (Get-Item -LiteralPath $output).LastWriteTimeUtc -lt $sourceTime) {
    $compile = $prologue + "cl /nologo /utf-8 /std:c++20 /EHsc /MD /O2 /W3 /DUNICODE /D_UNICODE /I`"$include`" /LD `"$source`" /Fo:engine.obj /link /OUT:Cutroom.Engine.Native.dll avcodec.lib avformat.lib avutil.lib swscale.lib d3d11.lib dxgi.lib d3dcompiler.lib`r`nif errorlevel 1 exit /b 1`r`n"
    [IO.File]::WriteAllText((Join-Path $native 'compile.cmd'), $compile, [Text.Encoding]::ASCII)
    & cmd.exe /d /c (Join-Path $native 'compile.cmd')
    if ($LASTEXITCODE -ne 0) { throw 'Native engine build failed' }
}

if (!$SkipManaged) {
    & dotnet build (Join-Path $projectRoot 'Cutroom.sln') -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Managed build failed' }
}
Write-Output 'Cutroom build succeeded.'
