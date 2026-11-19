$ErrorActionPreference = 'Stop'
# Fetches pinned third-party tools into .deps (gitignored). Every download is checked against a sha256;
# archives are cached in .deps\dl so a dead upstream URL never blocks a rebuild that already has them.
. (Join-Path $PSScriptRoot 'pinned.ps1')

# FFmpeg 8.1 LGPL shared. Month-end BtbN autobuilds are kept long term; daily ones are pruned.
$ffmpegSha = '5cef7bc48e513f3cee7c881e8b2999d835541edb1846c3e89443a67f8f8556ab'
$ffmpegZip = Get-Pinned 'ffmpeg-n8.1.3-27-g8dca1586f4-win64-lgpl-shared-8.1.zip' `
    'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n8.1.3-27-g8dca1586f4-win64-lgpl-shared-8.1.zip' $ffmpegSha
Expand-Pinned $ffmpegZip (Join-Path $deps 'ffmpeg') $ffmpegSha

# whisper.cpp CPU build (the guaranteed baseline; a Vulkan build is optional, see scripts\build-whisper-vulkan.ps1).
$whisperSha = '6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb'
$whisperZip = Get-Pinned 'whisper-bin-x64-b5454.zip' 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip' $whisperSha
Expand-Pinned $whisperZip (Join-Path $deps 'whisper') $whisperSha

# Single-file tools and assets.
$tools = Join-Path $deps 'tools'
$fonts = Join-Path $deps 'fonts'
New-Item -ItemType Directory -Path $tools, $fonts -Force | Out-Null
$single = @(
    @{ Name = 'deep-filter-0.5.6.exe'; Target = Join-Path $tools 'deep-filter.exe'; Sha = '75e11fa16445f560cb6b021521ddb89e89270d13b83089705d98776f58fd7915'
       Url = 'https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep-filter-0.5.6-x86_64-pc-windows-msvc.exe' },
    @{ Name = 'rnnoise-sh.rnnn'; Target = Join-Path $tools 'sh.rnnn'; Sha = '70bb6685eb0c2a1d18e2918dca3fbfbd39317010b1802eb1b6ea73a92f3fdec0'
       Url = 'https://raw.githubusercontent.com/GregorR/rnnoise-models/3eee541a283fd3b8f81b85b1748e3b9ccbefa04d/somnolent-hogwash-2018-09-01/sh.rnnn' },
    @{ Name = 'Montserrat-ExtraBold.ttf'; Target = Join-Path $fonts 'Montserrat-ExtraBold.ttf'; Sha = 'd3ac6a843d3ba6d5cafd44cf39e437055c8aed7e261010f595f57d3c7b3e2c1b'
       Url = 'https://raw.githubusercontent.com/JulietaUla/Montserrat/555facfb2a18c72c3c0380f0d9c0f060453a9058/fonts/ttf/Montserrat-ExtraBold.ttf' },
    @{ Name = 'Montserrat-SemiBold.ttf'; Target = Join-Path $fonts 'Montserrat-SemiBold.ttf'; Sha = 'b4e1563393d73fdff491a869441245aef31add2ec03d9c97a6dae4de07c52fd0'
       Url = 'https://raw.githubusercontent.com/JulietaUla/Montserrat/555facfb2a18c72c3c0380f0d9c0f060453a9058/fonts/ttf/Montserrat-SemiBold.ttf' },
    @{ Name = 'Montserrat-OFL.txt'; Target = Join-Path $fonts 'OFL.txt'; Sha = '8b7141c03fa4f8d44e6345d5d4931709290f0f67875e452e95ac1fd3a027802e'
       Url = 'https://raw.githubusercontent.com/JulietaUla/Montserrat/555facfb2a18c72c3c0380f0d9c0f060453a9058/OFL.txt' }
)
foreach ($item in $single) {
    $file = Get-Pinned $item.Name $item.Url $item.Sha
    if (!(Test-Path -LiteralPath $item.Target) -or (Get-FileHash -LiteralPath $item.Target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Sha) {
        Copy-Item -LiteralPath $file -Destination $item.Target -Force
    }
}
Write-Output 'Pinned dependencies are ready.'
