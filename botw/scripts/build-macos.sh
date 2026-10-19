#!/usr/bin/env bash
# Usage: botw/scripts/build-macos.sh [output-dir]      (default: ../dist-macos)
# Self-contained osx-arm64 build of BOTW-PC as <output-dir>/BOTW-PC.app, ad-hoc signed with the JIT/hypervisor
# entitlements, plus a zip. Contains no game files, keys or firmware. Finder launch = Ryujinx --botw.
set -euo pipefail
REPO=$(cd "$(dirname "$0")/../.." && pwd)
TOOLS=${BOTWPC_TOOLS:-$(cd "$REPO/.." && pwd)/.tools}
DIST=${1:-$(cd "$REPO/.." && pwd)/dist-macos}
RID=${BOTWPC_RID:-osx-arm64}
APP=$DIST/BOTW-PC.app
PUB=$DIST/publish-$RID

for d in "${DOTNET_ROOT:-}" "$TOOLS/dotnet" "$HOME/.dotnet"; do
  [ -n "$d" ] && [ -x "$d/dotnet" ] && { export DOTNET_ROOT=$d PATH=$d:$PATH; break; }
done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Ryujinx.LibHac 0.21.0-alpha.116 only exists on git.ryujinx.app; use build.sh's local feed when it was made,
# otherwise nuget.org's 0.20.0 (same API; the alpha only adds a mod-loading crash fix).
if [ -f "$TOOLS/nuget-local/ryujinx.libhac.0.21.0-alpha.116.nupkg" ]; then
  NUGET=(--configfile "$REPO/botw/nuget.offline.config"); LIBHAC=0.21.0-alpha.116
else
  NUGET=(--configfile "$REPO/botw/nuget.macos.config"); LIBHAC=0.20.0
fi
VERSION=${BOTWPC_VERSION:-1.0.0}
HASH=$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo unknown)

mkdir -p "$DIST"; DIST=$(cd "$DIST" && pwd); APP=$DIST/BOTW-PC.app; PUB=$DIST/publish-$RID
rm -rf "$PUB" "$APP"
dotnet publish "$REPO/src/Ryujinx/Ryujinx.csproj" -c Release -r "$RID" --self-contained "${NUGET[@]}" \
  -p:RyujinxLibHacVersion=$LIBHAC -p:DebugType=embedded -p:Version="$VERSION" -p:SourceRevisionId="$HASH" -o "$PUB"
# libsoundio ships x86_64 only; SDL2 is the macOS audio backend.
[ "$RID" = osx-arm64 ] && rm -f "$PUB/libsoundio.dylib"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources"
cp "$PUB/Ryujinx" "$APP/Contents/MacOS/BOTW-PC"
cp "$PUB"/*.dylib "$APP/Contents/Frameworks/"
sed -e "s/%%VERSION%%/$VERSION/g" -e "s/%%GIT_HASH%%/$HASH/g" "$REPO/botw/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
cp "$REPO/distribution/macos/Ryujinx.icns" "$APP/Contents/Resources/"
cp "$REPO/LICENSE.txt" "$PUB/THIRDPARTY.md" "$APP/Contents/Resources/" 2>/dev/null || true
cp "$REPO/botw/README.md" "$APP/Contents/Resources/README-BOTW-PC.md"
printf 'APPL????' > "$APP/Contents/PkgInfo"

(cd "$REPO/distribution/macos" && python3 bundle_fix_up.py "$APP" MacOS/BOTW-PC)
find "$APP/Contents/Frameworks" -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --sign - --entitlements "$REPO/distribution/macos/entitlements.xml" "$APP/Contents/MacOS/BOTW-PC"
codesign --force --sign - --entitlements "$REPO/distribution/macos/entitlements.xml" "$APP"
codesign --verify --deep --strict "$APP"

(cd "$DIST" && rm -f BOTW-PC-macos-arm64.zip && ditto -c -k --keepParent BOTW-PC.app BOTW-PC-macos-arm64.zip)
echo "packaged: $APP (LibHac $LIBHAC) and $DIST/BOTW-PC-macos-arm64.zip"
