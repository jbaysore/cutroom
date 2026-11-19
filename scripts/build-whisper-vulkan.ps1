# Builds whisper.cpp with the Vulkan GPU backend into .deps\whisper-vulkan. Nothing is installed system-wide:
# headers, glslc (conda-forge shaderc) and the whisper.cpp source are pinned downloads, and vulkan-1.lib is
# generated from the graphics driver's own vulkan-1.dll. Cutroom falls back to the CPU build if this is missing.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'pinned.ps1')

$tag = 'b5454'   # same whisper.cpp release as the CPU build in bootstrap.ps1
$sdk = '1.4.363.0'
$inputs = @(
    @{ Name = "whisper.cpp-$tag-src.zip"; Url = "https://github.com/ggml-org/whisper.cpp/archive/refs/tags/$tag.zip"; Sha = '39089bb3fee9a7ed9474fa0949c6f98ae989073cca1db4f4794294e7015a099e' },
    @{ Name = "vulkan-headers-$sdk.zip"; Url = "https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/vulkan-sdk-$sdk.zip"; Sha = 'f4be95220ff0ee0b1c620301ff4fe9ae48592d71f4c30f2d5c571351a153ae81' },
    @{ Name = "spirv-headers-$sdk.zip"; Url = "https://github.com/KhronosGroup/SPIRV-Headers/archive/refs/tags/vulkan-sdk-$sdk.zip"; Sha = '9dd4de860f9b19c707aac51143eddfba7a20d4578fca2f064937098a1cc7004f' },
    @{ Name = 'shaderc-2026.4-hef10606_1.conda'; Url = 'https://conda.anaconda.org/conda-forge/win-64/shaderc-2026.4-hef10606_1.conda'; Sha = 'd73088bb4b4fc388252d53c1aba4522891c131b7020815198776668e47616caa'; Conda = $true },
    @{ Name = 'glslang-16.6.0-h8fa7867_0.conda'; Url = 'https://conda.anaconda.org/conda-forge/win-64/glslang-16.6.0-h8fa7867_0.conda'; Sha = '048f538b07594cc717a14517191a81cb1d3825fdde9c573d132c158b26d15806'; Conda = $true },
    @{ Name = 'spirv-tools-2026.4-h49e36cd_2.conda'; Url = 'https://conda.anaconda.org/conda-forge/win-64/spirv-tools-2026.4-h49e36cd_2.conda'; Sha = '933e40fe9ca887a63ef742260f0ed801cda317354806504d864764a4ed9749e1'; Conda = $true }
)
$recipe = 'v1'   # bump when the build flags below change
$stamp = (($inputs | ForEach-Object { $_.Sha }) + $recipe) -join '-'
$stamp = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($stamp))).Replace('-', '').ToLowerInvariant()
$out = Join-Path $deps 'whisper-vulkan'
if (!$Force -and (Test-Marker $out $stamp)) { Write-Output 'GPU whisper is up to date.'; return }

$driver = Join-Path $env:WINDIR 'System32\vulkan-1.dll'
if (!(Test-Path -LiteralPath $driver)) { throw 'No Vulkan driver (System32\vulkan-1.dll); the CPU build will be used' }

$work = Join-Path $deps 'vk'
foreach ($stale in @($work, $out)) { if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force } }
$src = Join-Path $work 'src'
$glslc = Join-Path $work 'glslc'
$lib = Join-Path $work 'lib'
New-Item -ItemType Directory -Path $src, $glslc, $lib -Force | Out-Null
foreach ($item in $inputs) {
    $file = Get-Pinned $item.Name $item.Url $item.Sha
    if ($item.Conda) {
        # A .conda package is a zip holding a zstd tarball; Windows' bsdtar reads zstd.
        $unpack = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($item.Name))
        New-Item -ItemType Directory -Path $unpack -Force | Out-Null
        $zip = Join-Path $unpack 'package.zip'
        Copy-Item -LiteralPath $file -Destination $zip
        Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
        $tarball = Get-ChildItem -LiteralPath $unpack -Filter 'pkg-*.tar.zst' | Select-Object -First 1
        & (Join-Path $env:WINDIR 'System32\tar.exe') -xf $tarball.FullName -C $unpack
        if ($LASTEXITCODE -ne 0) { throw "Could not unpack $($item.Name)" }
        Copy-Item -Path (Join-Path $unpack 'Library\bin\*') -Include '*.exe', '*.dll' -Destination $glslc -Force
    } else {
        Expand-Archive -LiteralPath $file -DestinationPath $src -Force
    }
}
$whisperSrc = Join-Path $src "whisper.cpp-$tag"
$vulkanInclude = Join-Path $src "Vulkan-Headers-vulkan-sdk-$sdk\include"
$spirvSrc = Join-Path $src "SPIRV-Headers-vulkan-sdk-$sdk"
$prefix = Join-Path $work 'prefix'
# The Vulkan SDK ships SPIR-V headers in its include folder and ggml relies on that (it finds SPIRV-Headers
# but never adds its include path), so mirror the SDK layout.
Copy-Item -LiteralPath (Join-Path $spirvSrc 'include\spirv') -Destination $vulkanInclude -Recurse -Force
# ggml's shader generator nests deep build folders; a short path keeps it under Windows' 260-character limit.
$build = Join-Path ([IO.Path]::GetTempPath()) 'cutroom-vk'
if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ Build Tools are required' }
$dev = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
$prologue = "@echo off`r`nset `"PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%`"`r`ncall `"$dev`" -arch=x64 -host_arch=x64 >nul`r`nif errorlevel 1 exit /b 1`r`ncd /d `"$lib`"`r`n"

# vulkan-1.lib from the installed driver's exports (the loader ABI is stable).
[IO.File]::WriteAllText((Join-Path $lib 'exports.cmd'), $prologue + "dumpbin /nologo /exports `"$driver`" > vulkan-1.exports`r`nif errorlevel 1 exit /b 1`r`n", [Text.Encoding]::ASCII)
& cmd.exe /d /c (Join-Path $lib 'exports.cmd')
if ($LASTEXITCODE -ne 0) { throw 'Could not read vulkan-1.dll exports' }
$names = Get-Content -LiteralPath (Join-Path $lib 'vulkan-1.exports') | ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)') { $matches[1] } }
[IO.File]::WriteAllLines((Join-Path $lib 'vulkan-1.def'), @('LIBRARY "vulkan-1.dll"', 'EXPORTS') + $names, [Text.Encoding]::ASCII)

$steps = @(
    'lib /nologo /machine:x64 /def:vulkan-1.def /out:vulkan-1.lib',
    "cmake -S `"$spirvSrc`" -B `"$build\spirv`" -G Ninja -DSPIRV_HEADERS_ENABLE_TESTS=OFF -DSPIRV_HEADERS_ENABLE_INSTALL=ON -DCMAKE_INSTALL_PREFIX=`"$prefix`"",
    "cmake --install `"$build\spirv`"",
    ("cmake -S `"$whisperSrc`" -B `"$build\w`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DGGML_VULKAN=ON -DGGML_CCACHE=OFF " +
     "-DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_SERVER=OFF -DWHISPER_CURL=OFF -DWHISPER_SDL2=OFF -DCMAKE_PREFIX_PATH=`"$prefix`" " +
     "-DVulkan_INCLUDE_DIR=`"$vulkanInclude`" -DVulkan_LIBRARY=`"$lib\vulkan-1.lib`" -DVulkan_GLSLC_EXECUTABLE=`"$glslc\glslc.exe`""),
    "cmake --build `"$build\w`" --config Release --target whisper-cli"
)
$script = $prologue + (($steps | ForEach-Object { "$_`r`nif errorlevel 1 exit /b 1`r`n" }) -join '')
[IO.File]::WriteAllText((Join-Path $lib 'build.cmd'), $script, [Text.Encoding]::ASCII)
Write-Output 'Building GPU whisper (first time only, several minutes)...'
# Redirect inside cmd: PowerShell 5.1 turns a native tool's stderr warnings into terminating errors.
$log = Join-Path $work 'build.log'
& cmd.exe /d /c "`"$(Join-Path $lib 'build.cmd')`" > `"$log`" 2>&1"
if ($LASTEXITCODE -ne 0) { throw "GPU whisper build failed; see $log" }

New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -Path (Join-Path $build 'w\bin\*') -Include 'whisper-cli.exe', '*.dll' -Destination $out -Force
if (!(Test-Path -LiteralPath (Join-Path $out 'whisper-cli.exe'))) { throw 'GPU whisper build produced no whisper-cli.exe' }
Remove-Item -LiteralPath $build -Recurse -Force
Set-Content -LiteralPath (Join-Path $out 'pinned-sha256.txt') -Value $stamp -Encoding ascii
Write-Output 'GPU whisper is ready.'
