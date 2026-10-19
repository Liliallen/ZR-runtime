#!/usr/bin/env bash
# Builds BOTW-iPad's ahead-of-time image on a Mac from your own game files: dumps the loaded executables, finds their
# static entry points (aot_entries.py), translates them and all code they reach with the Mac build of the runtime
# (same LightningJit, position-independent mode) and writes the image (aot_build.py). Then:
#   BOTWPC_AOT_DIR=<out>/img botw/ipados/build.sh
# Usage: botw/ipados/aot_image.sh <BOTW-PC data dir> <game.nsp> <out dir> [extra entries] [recordings...]
#   extra entries: hex guest addresses, one per line (misses reported by the iPad, aot_cover.sh)
#   recordings:    BOTWPC_AOT_RECORD files of hybrid runs (aot_cover.sh) to include
# The data dir is copied, never written. The image is translated game code: keep it local, never commit or share it.
set -euo pipefail
DATA=$1 GAME=$2 OUT=$3 EXTRA=${4:-}; shift $(( $# < 4 ? $# : 4 ))
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../.." && pwd); mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd); RT=$OUT/runtime-osx
for d in "${DOTNET_ROOT:-}" "$HOME/.dotnet"; do [ -n "$d" ] && [ -x "$d/dotnet" ] && { export DOTNET_ROOT=$d PATH=$d:$PATH; break; }; done

if [ ! -x "$RT/machost" ]; then
  echo "== Mac runtime"
  dotnet publish "$REPO/src/Ryujinx/Ryujinx.csproj" -c Release -r osx-arm64 --configfile "$REPO/botw/nuget.macos.config" \
    -p:RyujinxLibHacVersion=0.20.0 -p:BotwPcNative=true -o "$RT" > "$OUT/runtime-osx.log" 2>&1
  clang -fobjc-arc -O1 -I "$HERE" -framework Cocoa -framework QuartzCore -framework Metal "$HERE/machost.m" "$HERE/SignalHandler.c" -o "$RT/machost"
fi

W=$OUT/work P=
run() { rm -rf "$W"; cp -Rc "$DATA" "$W"; env BOTWPC_AUTO_CONTROLLER=0 "$@" "$RT/machost" "$RT/Ryujinx.dylib" - "$W" "$GAME" 900 > "$OUT/host.log" 2>&1 & P=$!; }
until_log() { until grep -aq "$1" "$OUT/host.log" || ! kill -0 "$P" 2>/dev/null; do sleep 2; done; kill -9 "$P" 2>/dev/null || true; wait "$P" 2>/dev/null || true; }

echo "== executables"
rm -rf "$OUT/nso"; run BOTWPC_DUMP_IMAGES="$OUT/nso"; until_log "LoadShaders"
echo "== entry points"
python3 "$HERE/aot_entries.py" "$OUT/nso" "$OUT/seeds.txt"
[ -n "$EXTRA" ] && cat "$EXTRA" >> "$OUT/seeds.txt"
echo "== translation"
rm -f "$OUT/rec-closure.bin"
# .text of every module (the dump's base + text size): the interpreter's FP/SIMD stubs cover all instructions there.
RANGES=$(python3 -c 'import glob, sys
mods = [dict(l.split("=", 1) for l in open(t).read().split()) for t in sorted(glob.glob(sys.argv[1] + "/*.txt"))]
print(",".join("%x:%s" % (int(m["base"], 16) + int(m["text"].split("+")[0], 16), m["text"].split("+")[1][2:]) for m in mods))' "$OUT/nso")
run BOTWPC_AOT_CLOSURE=1 BOTWPC_AOT_RECORD="$OUT/rec-closure.bin" BOTWPC_AOT_PRECOMPILE="$OUT/seeds.txt" BOTWPC_AOT_SIMD_RANGES="$RANGES"
until_log "interpreter FP/SIMD stubs"
sed 's/\x1b\[[0-9;]*m//g' "$OUT/host.log" | grep -a "AOT precompile done" | cut -c1-160
echo "== image"
python3 "$HERE/aot_build.py" "$OUT/img" "$OUT/rec-closure.bin" "$@" | tail -1
(cd "$OUT/img" && clang -arch arm64 -dynamiclib -mmacos-version-min=13.0 botw_aot.s -o botw_aot.dylib)
echo "image: $OUT/img (BOTWPC_AOT_DIR for build.sh; botw_aot.dylib for Mac tests with BOTWPC_AOT_DYLIB)"
