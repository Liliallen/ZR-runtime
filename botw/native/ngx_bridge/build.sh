#!/usr/bin/env bash
# Builds libbotw_ngx_bridge.so and the standalone DLSS test with plain g++.
# The NVIDIA DLSS SDK is not in this repository: it is fetched into $TOOLS/dlss-sdk from
# https://github.com/NVIDIA/DLSS (NVIDIA RTX SDK license; do not commit it). Vulkan headers likewise.
# Usage: build.sh [out-dir]    Env: BOTWPC_TOOLS (default <repo>/../.tools)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
TOOLS=${BOTWPC_TOOLS:-$(cd "$HERE/../../../.." && pwd)/.tools}
OUT=${1:-$TOOLS/ngx-bridge}
SDK=$TOOLS/dlss-sdk
VKH=$TOOLS/vulkan-headers/include
DLSS_VER=310.9.1
RAW=https://raw.githubusercontent.com/NVIDIA/DLSS/main

if [ ! -f "$SDK/libnvsdk_ngx.a" ]; then
  mkdir -p "$SDK"
  for f in nvsdk_ngx.h nvsdk_ngx_defs.h nvsdk_ngx_defs_vk.h nvsdk_ngx_params.h nvsdk_ngx_vk.h nvsdk_ngx_helpers.h \
           nvsdk_ngx_helpers_vk.h nvsdk_ngx_helpers_d3d.h nvsdk_ngx_helpers_cuda.h; do
    curl -sfL "$RAW/include/$f" -o "$SDK/$f"
  done
  curl -sfL "$RAW/lib/Linux_x86_64/libnvsdk_ngx.a" -o "$SDK/libnvsdk_ngx.a"
  curl -sfL "$RAW/lib/Linux_x86_64/rel/libnvidia-ngx-dlss.so.$DLSS_VER" -o "$SDK/libnvidia-ngx-dlss.so.$DLSS_VER"
  curl -sfL "$RAW/LICENSE.txt" -o "$SDK/LICENSE.txt"
fi
if [ ! -d "$VKH/vulkan" ]; then
  mkdir -p "$TOOLS/vk-src"
  curl -sfL https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/v1.4.309.tar.gz | tar xz -C "$TOOLS/vk-src"
  mv "$TOOLS/vk-src/Vulkan-Headers-1.4.309" "$TOOLS/vulkan-headers"
fi

mkdir -p "$OUT"
CXX=${CXX:-g++}
FLAGS=(-std=c++17 -O2 -fPIC -Wall -Wno-unused-function -I"$SDK" -I"$VKH")
# Hide the SDK's symbols; only botw_ngx_* is exported.
"$CXX" "${FLAGS[@]}" -shared -fvisibility=hidden -o "$OUT/libbotw_ngx_bridge.so" "$HERE/ngx_bridge.cpp" \
  "$SDK/libnvsdk_ngx.a" -Wl,--exclude-libs,ALL -ldl -lvulkan
"$CXX" "${FLAGS[@]}" -o "$OUT/test_ngx_bridge" "$HERE/test_ngx_bridge.cpp" -L"$OUT" -lbotw_ngx_bridge -lvulkan -Wl,-rpath,'$ORIGIN'
ln -sf "$SDK/libnvidia-ngx-dlss.so.$DLSS_VER" "$OUT/libnvidia-ngx-dlss.so.$DLSS_VER"
echo "built: $OUT"
nm -D --defined-only "$OUT/libbotw_ngx_bridge.so" | grep -c ' T botw_ngx_' | sed 's/^/exported botw_ngx_* functions: /'
