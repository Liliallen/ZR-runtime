#!/usr/bin/env bash
# iOS Simulator build and run (Apple Silicon): validates the iOS code paths without a device. The simulator's GPU is not
# the iPad's; performance numbers from it mean nothing.  botw/ipados/sim.sh [seconds]
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd); ROOT=$(cd "$REPO/.." && pwd)
OUT=$ROOT/dist-ipados-sim; DEPS=${BOTWPC_IOS_SIM_DEPS:-$ROOT/.tools/ios/out-sim}; mkdir -p "$OUT"
SIM=${BOTWPC_SIM:-$(xcrun simctl list devices available | grep 'iPad Pro' | grep -oE '[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}' | head -1)}
for d in "${DOTNET_ROOT:-}" "$HOME/.dotnet"; do [ -n "$d" ] && [ -x "$d/dotnet" ] && { export DOTNET_ROOT=$d PATH=$d:$PATH; break; }; done
dotnet publish "$REPO/src/Ryujinx/Ryujinx.csproj" -c Release -r iossimulator-arm64 --configfile "$REPO/botw/nuget.macos.config" \
  -p:RyujinxLibHacVersion=0.20.0 -p:BotwPcNative=true -p:BotwPcIosDeps="$DEPS" -o "$OUT/runtime" > "$OUT/runtime.log" 2>&1
cmake -S "$HERE" -B "$OUT/xcode" -G Xcode -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_SYSROOT=iphonesimulator -DBOTWPC_RUNTIME_DIR="$OUT/runtime" \
  -DBOTWPC_IOS_DEPS="$DEPS" -DBOTWPC_INCREASED_MEMORY=OFF > "$OUT/cmake.log"
xcodebuild -project "$OUT/xcode/botw_ipad.xcodeproj" -scheme BOTWiPad -configuration Release -sdk iphonesimulator \
  -destination "generic/platform=iOS Simulator" ARCHS=arm64 CODE_SIGN_IDENTITY=- CODE_SIGN_STYLE=Manual DEVELOPMENT_TEAM= \
  CONFIGURATION_BUILD_DIR="$OUT/build" build > "$OUT/xcodebuild.log" 2>&1
xcrun simctl boot "$SIM" 2>/dev/null || true
xcrun simctl install "$SIM" "$OUT/build/BOTWiPad.app"
DOCS="$(xcrun simctl get_app_container "$SIM" local.botwpc.ipad data)/Documents"; echo "app data: $DOCS"
# Test data (BOTWPC_SIM_DATA, default the S1 benchmark template; never the user's own folder) and links to the game files.
if [ ! -d "$DOCS/BOTW-PC" ]; then cp -Rc "${BOTWPC_SIM_DATA:-$ROOT/mac-bench/template-shrine}" "$DOCS/BOTW-PC"; fi
mkdir -p "$DOCS/Games"; for f in "$ROOT"/*.nsp "$ROOT"/update/*.nsp "$ROOT"/dlc/*.nsp; do [ -f "$f" ] && ln -sf "$f" "$DOCS/Games/"; done
SIMCTL_CHILD_BOTWPC_MEMLOG=1 xcrun simctl launch --console-pty --terminate-running-process "$SIM" local.botwpc.ipad > "$OUT/run.log" 2>&1 &
sleep "${1:-90}"; xcrun simctl io "$SIM" screenshot "$OUT/screen.png" >/dev/null 2>&1; echo "log $OUT/run.log, screenshot $OUT/screen.png"
