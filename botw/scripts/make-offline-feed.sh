#!/usr/bin/env bash
# Builds a local NuGet feed for the packages that only exist on git.ryujinx.app
# (unreachable from this network) by repacking the identical DLLs from the
# official Ryubing Flatpak single-file binary. Versions are verified below.
set -euo pipefail
TOOLS=${BOTWPC_TOOLS:-$(cd "$(dirname "$0")/../../.." && pwd)/.tools}
FEED=$TOOLS/nuget-local
EXTRACT=$TOOLS/flatpak-extract
APP=$(flatpak info --show-location io.github.ryubing.Ryujinx)/files/bin/Ryujinx
export DOTNET_ROOT=$TOOLS/dotnet PATH=$TOOLS/dotnet:$PATH DOTNET_ROLL_FORWARD=Major

[ -x "$TOOLS/bin/sfextract" ] || dotnet tool install sfextract --tool-path "$TOOLS/bin"
[ -f "$EXTRACT/LibHac.dll" ] || "$TOOLS/bin/sfextract" "$APP" -o "$EXTRACT"

check() { strings -el "$EXTRACT/$1" | grep -q "$2" || { echo "version mismatch: $1 is not $2" >&2; exit 1; }; }
check LibHac.dll '0.21.0.0-alpha.116+e39169ab'
check Ryujinx.Systems.Update.Client.dll '1.0.44+21f5dcd2'
check Ryujinx.Systems.Update.Common.dll '1.0.44+21f5dcd2'

# Single-file publish rewrote references to System.Private.CoreLib; retarget to System.Runtime.
RETARGET=$TOOLS/bin/RetargetCoreLib/RetargetCoreLib
[ -x "$RETARGET" ] || dotnet build "$(dirname "$0")/../tools/RetargetCoreLib" -c Release -o "$TOOLS/bin/RetargetCoreLib" --configfile "$(dirname "$0")/../nuget.offline.config"
REFS=$TOOLS/flatpak-refs; mkdir -p "$REFS"
for d in LibHac.dll Ryujinx.Systems.Update.Client.dll Ryujinx.Systems.Update.Common.dll; do "$RETARGET" "$EXTRACT/$d" "$REFS/$d"; done

pack() { # id version tfm dll...
  local id=$1 ver=$2 tfm=$3; shift 3
  local w; w=$(mktemp -d)
  # ref/ = retargeted copy for the compiler; lib/ = original bytes used at runtime.
  mkdir -p "$w/lib/$tfm" "$w/ref/$tfm"
  for d in "$@"; do cp "$REFS/$d" "$w/ref/$tfm/"; cp "$EXTRACT/$d" "$w/lib/$tfm/"; done
  cat > "$w/$id.nuspec" <<NUSPEC
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata><id>$id</id><version>$ver</version><authors>Ryubing (repacked)</authors>
  <description>Repacked from official Ryubing 1.3.3 Flatpak for offline builds.</description></metadata>
</package>
NUSPEC
  mkdir -p "$FEED"
  python3 -c 'import sys,os,zipfile
src,dst=sys.argv[1:]
with zipfile.ZipFile(dst,"w",zipfile.ZIP_DEFLATED) as z:
    for r,_,fs in os.walk(src):
        for f in fs: z.write(os.path.join(r,f),os.path.relpath(os.path.join(r,f),src))' "$w" "$FEED/${id,,}.$ver.nupkg"
  rm -rf "$w"
}
pack Ryujinx.LibHac 0.21.0-alpha.116 net8.0 LibHac.dll
pack Ryujinx.Systems.Update.Common 1.0.44 net9.0 Ryujinx.Systems.Update.Common.dll
pack Ryujinx.UpdateClient 1.0.44 net9.0 Ryujinx.Systems.Update.Client.dll
echo "feed: $FEED"; ls "$FEED"
