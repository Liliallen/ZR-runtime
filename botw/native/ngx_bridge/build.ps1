# Windows counterpart of build.sh: builds botw_ngx_bridge.dll with MSVC (Visual Studio C++ tools).
# The NVIDIA DLSS SDK is not in this repository: it is fetched into $Tools\dlss-sdk-win from
# https://github.com/NVIDIA/DLSS (NVIDIA RTX SDK license; do not commit it). Vulkan headers likewise.
# Usage: build.ps1 [-Out <dir>]    (default <tools>\ngx-bridge-win; copy it to <app>\ngx or set BOTWPC_NGX_DIR)
param(
    [string]$Out,
    [string]$Tools = $(if ($env:BOTWPC_TOOLS) { $env:BOTWPC_TOOLS } else { Join-Path $PSScriptRoot '..\..\..\..\.tools' })
)
$ErrorActionPreference = 'Stop'
$Tools = [IO.Path]::GetFullPath($Tools)
if (-not $Out) { $Out = Join-Path $Tools 'ngx-bridge-win' }
$Sdk = Join-Path $Tools 'dlss-sdk-win'
$Vkh = Join-Path $Tools 'vulkan-headers\include'
$DlssTag = 'v310.9.1'
$Raw = "https://raw.githubusercontent.com/NVIDIA/DLSS/$DlssTag"

if (-not (Test-Path (Join-Path $Sdk 'nvsdk_ngx_d.lib'))) {
    New-Item -ItemType Directory -Force $Sdk | Out-Null
    foreach ($f in 'nvsdk_ngx.h', 'nvsdk_ngx_defs.h', 'nvsdk_ngx_defs_vk.h', 'nvsdk_ngx_params.h', 'nvsdk_ngx_vk.h',
                   'nvsdk_ngx_helpers.h', 'nvsdk_ngx_helpers_vk.h', 'nvsdk_ngx_helpers_d3d.h', 'nvsdk_ngx_helpers_cuda.h') {
        Invoke-WebRequest "$Raw/include/$f" -OutFile (Join-Path $Sdk $f) -UseBasicParsing
    }
    Invoke-WebRequest "$Raw/lib/Windows_x86_64/x64/nvsdk_ngx_d.lib" -OutFile (Join-Path $Sdk 'nvsdk_ngx_d.lib') -UseBasicParsing
    Invoke-WebRequest "$Raw/lib/Windows_x86_64/rel/nvngx_dlss.dll" -OutFile (Join-Path $Sdk 'nvngx_dlss.dll') -UseBasicParsing
    Invoke-WebRequest "$Raw/LICENSE.txt" -OutFile (Join-Path $Sdk 'LICENSE.txt') -UseBasicParsing
}
if (-not (Test-Path (Join-Path $Vkh 'vulkan'))) {
    $tgz = Join-Path $Tools 'vulkan-headers.tar.gz'
    Invoke-WebRequest https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/v1.4.309.tar.gz -OutFile $tgz -UseBasicParsing
    tar -xzf $tgz -C $Tools
    Move-Item (Join-Path $Tools 'Vulkan-Headers-1.4.309') (Join-Path $Tools 'vulkan-headers')
    Remove-Item $tgz
}

# MSVC environment (x64) from the newest Visual Studio with the C++ tools.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "MSVC C++ tools not found (need Visual Studio 'Desktop development with C++')" }

New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path $Out 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
# /MD matches nvsdk_ngx_d.lib (dynamic CRT). Only the BOTW_NGX_API (dllexport) functions are exported.
$cl = "cl /nologo /std:c++17 /O2 /EHsc /MD /W3 /DNOMINMAX /D_CRT_SECURE_NO_WARNINGS /I`"$Sdk`" /I`"$Vkh`" /Fo`"$obj\\`" " +
      "/LD `"$PSScriptRoot\ngx_bridge.cpp`" /Fe`"$Out\botw_ngx_bridge.dll`" /link `"$Sdk\nvsdk_ngx_d.lib`" advapi32.lib user32.lib"
cmd /c "`"$vcvars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $Sdk 'nvngx_dlss.dll') $Out -Force
Copy-Item (Join-Path $Sdk 'LICENSE.txt') (Join-Path $Out 'NVIDIA-DLSS-LICENSE.txt') -Force
Remove-Item -Recurse -Force $obj
Get-ChildItem $Out -Filter *.lib | Remove-Item
Get-ChildItem $Out -Filter *.exp | Remove-Item
"built: $Out"
