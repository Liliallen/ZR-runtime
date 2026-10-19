#!/usr/bin/env bash
# Usage: botw/scripts/build.sh [output-dir]   (Release build of the GUI+headless app)
set -euo pipefail
REPO=$(cd "$(dirname "$0")/../.." && pwd)
TOOLS=${BOTWPC_TOOLS:-$(cd "$REPO/.." && pwd)/.tools}
OUT=${1:-$(cd "$REPO/.." && pwd)/build-botwpc}
export DOTNET_ROOT=$TOOLS/dotnet PATH=$TOOLS/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[ -f "$TOOLS/nuget-local/ryujinx.libhac.0.21.0-alpha.116.nupkg" ] || "$REPO/botw/scripts/make-offline-feed.sh"
dotnet restore "$REPO/src/Ryujinx/Ryujinx.csproj" --configfile "$REPO/botw/nuget.offline.config"
dotnet build "$REPO/src/Ryujinx/Ryujinx.csproj" -c Release --no-restore -o "$OUT"
