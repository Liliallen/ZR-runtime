#!/usr/bin/env bash
# Builds BOTW-iPad: the runtime as an ios-arm64 NativeAOT library, the Xcode project, the signed app and BOTW-iPad.ipa.
# Usage: botw/ipados/build.sh [out dir]   (default ../dist-ipados)
#   BOTWPC_IOS_DEPS  SDL2 + MoltenVK built from source for iOS (default ../.tools/ios/out)
#   BOTWPC_TEAM      Apple development team ID (required to sign for a device)
#   BOTWPC_DEVICE    device UDID to build for (default: first connected iPad); signing registers it if needed
#   BOTWPC_AOT_DIR   ahead-of-time image (aot_build.py output) to link; without it the app needs JIT
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd)
OUT=${1:-$(cd "$REPO/.." && pwd)/dist-ipados}; mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
DEPS=${BOTWPC_IOS_DEPS:-$(cd "$REPO/.." && pwd)/.tools/ios/out}
for d in "${DOTNET_ROOT:-}" "$HOME/.dotnet"; do [ -n "$d" ] && [ -x "$d/dotnet" ] && { export DOTNET_ROOT=$d PATH=$d:$PATH; break; }; done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -f "$DEPS/SDL2/lib/libSDL2.a" ] && [ -f "$DEPS/MoltenVK/libMoltenVK.dylib" ] || { echo "missing iOS deps in $DEPS (see botw/docs/IPADOS.md)"; exit 1; }

echo "== runtime (NativeAOT, ios-arm64)"
dotnet publish "$REPO/src/Ryujinx/Ryujinx.csproj" -c Release -r ios-arm64 --configfile "$REPO/botw/nuget.macos.config" \
  -p:RyujinxLibHacVersion=0.20.0 -p:BotwPcNative=true -p:BotwPcIosDeps="$DEPS" -o "$OUT/runtime" > "$OUT/runtime.log" 2>&1 \
  || { grep -E " error " "$OUT/runtime.log" | sort -u | head; exit 1; }

echo "== app"
DEV=${BOTWPC_DEVICE:-$(xcrun devicectl list devices 2>/dev/null | awk '/iPad/ && /(available|connected)/ && !/simulated/ {for (i=1;i<=NF;i++) if ($i ~ /^[0-9A-F]{8}-[0-9A-F]{16}$/) print $i; exit}')}
cmake -S "$HERE" -B "$OUT/xcode" -G Xcode -DCMAKE_SYSTEM_NAME=iOS -DBOTWPC_RUNTIME_DIR="$OUT/runtime" -DBOTWPC_IOS_DEPS="$DEPS" \
  ${BOTWPC_TEAM:+-DBOTWPC_TEAM=$BOTWPC_TEAM} ${BOTWPC_INCREASED_MEMORY:+-DBOTWPC_INCREASED_MEMORY=$BOTWPC_INCREASED_MEMORY} \
  ${BOTWPC_AOT_DIR:+-DBOTWPC_AOT_DIR=$BOTWPC_AOT_DIR} > "$OUT/cmake.log"
if [ -n "$DEV" ]; then DEST="id=$DEV"; else DEST="generic/platform=iOS"; fi
xcodebuild -project "$OUT/xcode/botw_ipad.xcodeproj" -scheme BOTWiPad -configuration Release -destination "$DEST" -allowProvisioningUpdates \
  build > "$OUT/xcodebuild.log" 2>&1 || { grep -E "error:|^ld:" "$OUT/xcodebuild.log" | head; exit 1; }
rm -rf "$OUT/build"; mkdir -p "$OUT/build"; cp -R "$OUT/xcode/Release-iphoneos/BOTWiPad.app" "$OUT/build/"

echo "== ipa"
rm -rf "$OUT/Payload" "$OUT/BOTW-iPad.ipa"; mkdir -p "$OUT/Payload"
cp -R "$OUT/build/BOTWiPad.app" "$OUT/Payload/"
(cd "$OUT" && zip -qry BOTW-iPad.ipa Payload) && rm -rf "$OUT/Payload"
echo "built: $OUT/build/BOTWiPad.app and $OUT/BOTW-iPad.ipa"
