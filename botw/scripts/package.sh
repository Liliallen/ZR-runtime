#!/usr/bin/env bash
# Usage: botw/scripts/package.sh [output-dir]
# Self-contained linux-x64 build of BOTW-PC in <output-dir>/botw-pc (default ../dist), with the botw-pc launcher,
# a .desktop file and a tarball. Contains no game files, keys or firmware. If appimagetool is on PATH, also an AppImage.
set -euo pipefail
REPO=$(cd "$(dirname "$0")/../.." && pwd)
TOOLS=${BOTWPC_TOOLS:-$(cd "$REPO/.." && pwd)/.tools}
DIST=${1:-$(cd "$REPO/.." && pwd)/dist}
OUT=$DIST/botw-pc
export DOTNET_ROOT=$TOOLS/dotnet PATH=$TOOLS/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
rm -rf "$OUT"; mkdir -p "$OUT"
dotnet publish "$REPO/src/Ryujinx/Ryujinx.csproj" -c Release -r linux-x64 --self-contained \
  --configfile "$REPO/botw/nuget.offline.config" -p:DebugType=none -o "$OUT"
install -m755 "$REPO/botw/scripts/botw-pc" "$OUT/botw-pc"
install -m644 "$REPO/botw/packaging/botw-pc.desktop" "$OUT/botw-pc.desktop"
cp "$REPO/distribution/misc/Logo.svg" "$OUT/botw-pc.svg" 2>/dev/null || true
cp "$REPO/LICENSE.txt" "$OUT/LICENSE.txt"
cp "$REPO/botw/README.md" "$OUT/README-BOTW-PC.md"
# DLSS: our bridge (links NVIDIA's NGX static library) + the DLSS runtime from the user's local DLSS SDK, with NVIDIA's
# license. Local package only; check the DLSS SDK license before publishing it (DLSS.md). Skipped if not built.
NGX=$TOOLS/ngx-bridge; SDK=$TOOLS/dlss-sdk
if [ -f "$NGX/libbotw_ngx_bridge.so" ] && ls "$SDK"/libnvidia-ngx-dlss.so.* >/dev/null 2>&1; then
  mkdir -p "$OUT/ngx"; install -m755 "$NGX/libbotw_ngx_bridge.so" "$OUT/ngx/"
  cp -L "$SDK"/libnvidia-ngx-dlss.so.* "$OUT/ngx/"; cp "$SDK/LICENSE.txt" "$OUT/ngx/NVIDIA-DLSS-SDK-LICENSE.txt"
else
  echo "DLSS bridge or SDK runtime not found: package without DLSS (the game's own upscale is used)"
fi
tar -C "$DIST" -czf "$DIST/botw-pc-linux-x64.tar.gz" botw-pc
echo "packaged: $OUT and $DIST/botw-pc-linux-x64.tar.gz"
if command -v appimagetool >/dev/null; then
  APPDIR=$DIST/BOTW-PC.AppDir; rm -rf "$APPDIR"; mkdir -p "$APPDIR/usr"; cp -a "$OUT" "$APPDIR/usr/bin"
  ln -s usr/bin/botw-pc "$APPDIR/AppRun"; cp "$OUT/botw-pc.desktop" "$APPDIR/"; cp "$OUT/botw-pc.svg" "$APPDIR/botw-pc.svg" 2>/dev/null || true
  appimagetool "$APPDIR" "$DIST/BOTW-PC-x86_64.AppImage"
else
  echo "appimagetool not found: AppImage skipped (the tarball is a portable installation)"
fi
