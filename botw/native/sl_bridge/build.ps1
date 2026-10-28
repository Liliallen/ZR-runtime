# Builds botw_sl_bridge.dll (Streamline DLSS Frame Generation bridge) with MSVC, like ngx_bridge/build.ps1.
# The NVIDIA Streamline SDK is not in this repository: it is fetched into $Tools\streamline from
# https://github.com/NVIDIA-RTX/Streamline (MIT for the SDK source; NVIDIA's binaries keep their own licenses;
# do not commit them). The output directory gets the bridge plus the Streamline runtime (sl.interposer.dll, sl.common,
# sl.dlss_g, sl.reflex, sl.pcl, nvngx_dlssg.dll, NvLowLatencyVk.dll); copy it to <app>\streamline.
# Usage: build.ps1 [-Out <dir>] [-Development]   (default <tools>\sl-bridge-win; -Development ships the development
# Streamline plugins, which log more and can show an on-screen debug overlay)
param(
    [string]$Out,
    [switch]$Development,
    [string]$Tools = $(if ($env:BOTWPC_TOOLS) { $env:BOTWPC_TOOLS } else { Join-Path $PSScriptRoot '..\..\..\..\.tools' })
)
$ErrorActionPreference = 'Stop'
$Tools = [IO.Path]::GetFullPath($Tools)
if (-not $Out) { $Out = Join-Path $Tools 'sl-bridge-win' }
$Sdk = Join-Path $Tools 'streamline'
$Vkh = Join-Path $Tools 'vulkan-headers\include'
$SlTag = 'v2.14.16'

if (-not (Test-Path (Join-Path $Sdk 'lib\x64\sl.interposer.lib'))) {
    $zip = Join-Path $Tools 'streamline-sdk.zip'
    Invoke-WebRequest "https://github.com/NVIDIA-RTX/Streamline/releases/download/$SlTag/streamline-sdk-$($SlTag.TrimStart('v')).zip" -OutFile $zip -UseBasicParsing
    New-Item -ItemType Directory -Force $Sdk | Out-Null
    Expand-Archive $zip -DestinationPath $Sdk -Force
    Remove-Item $zip
}
if (-not (Test-Path (Join-Path $Vkh 'vulkan'))) { throw "Vulkan headers missing: run ..\ngx_bridge\build.ps1 first" }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "MSVC C++ tools not found (need Visual Studio 'Desktop development with C++')" }

New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path $Out 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$cl = "cl /nologo /std:c++17 /O2 /EHsc /MD /W3 /DNOMINMAX /DWIN32_LEAN_AND_MEAN /D_CRT_SECURE_NO_WARNINGS /I`"$Sdk\include`" /I`"$Vkh`" /Fo`"$obj\\`" " +
      "/LD `"$PSScriptRoot\sl_bridge.cpp`" /Fe`"$Out\botw_sl_bridge.dll`" /link `"$Sdk\lib\x64\sl.interposer.lib`""
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
cmd /c "`"$vcvars`" >nul 2>&1 && $cl 2>&1"
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }

$bin = Join-Path $Sdk ($(if ($Development) { 'bin\x64\development' } else { 'bin\x64' }))
# nvngx_dlss.dll too: once Streamline has initialised NGX, the DLSS Super Resolution bridge (..\ngx_bridge) finds its
# DLSS runtime only in Streamline's plugin directory.
foreach ($f in 'sl.interposer.dll', 'sl.common.dll', 'sl.dlss_g.dll', 'sl.reflex.dll', 'sl.pcl.dll', 'nvngx_dlssg.dll', 'nvngx_dlss.dll', 'NvLowLatencyVk.dll') {
    Copy-Item (Join-Path $bin $f) $Out -Force
}
Copy-Item (Join-Path $Sdk 'license.txt') (Join-Path $Out 'STREAMLINE-LICENSE.txt') -Force
Remove-Item -Recurse -Force $obj
Get-ChildItem $Out -Filter *.lib | Remove-Item
Get-ChildItem $Out -Filter *.exp | Remove-Item
"built: $Out"
