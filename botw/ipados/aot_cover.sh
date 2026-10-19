#!/usr/bin/env bash
# Grows AOT coverage on a Mac: repeated hybrid runs (image + JIT for misses) record the misses, the image is rebuilt
# after each, then a no-JIT run checks it. Usage: aot_cover.sh <rounds> <runtime dir with Ryujinx.dylib + machost>
#   <data template> <game> <aot dir> [input script]
set -u
ROUNDS=$1 RT=$2 TEMPLATE=$3 GAME=$4 AOT=$5 INPUT=${6:-}
HERE=$(cd "$(dirname "$0")" && pwd)
build() { python3 "$HERE/aot_build.py" "$AOT/img" "$AOT"/rec-*.bin | tail -1; (cd "$AOT/img" && clang -arch arm64 -dynamiclib -mmacos-version-min=13.0 botw_aot.s -o botw_aot.dylib 2>/dev/null); }
run() { # $1: label, rest: env
  local label=$1; shift; local W="$AOT/work"; rm -rf "$W"; cp -Rc "$TEMPLATE" "$W"
  env BOTWPC_AUTO_CONTROLLER=0 BOTWPC_FRAMELOG="$W/frames.csv" ${INPUT:+BOTWPC_INPUT_SCRIPT=$INPUT} "$@" "$RT/machost" "$RT/Ryujinx.dylib" - "$W" "$GAME" 150 > "$AOT/$label.log" 2>&1
  echo "$label: $(sed 's/\x1b\[[0-9;]*m//g' "$AOT/$label.log" | grep -a -E 'AOT:|AOT miss' | tail -1 | cut -c1-160)"
}
build
for i in $(seq 1 "$ROUNDS"); do
  run "hybrid-$i" BOTWPC_AOT_DYLIB="$AOT/img/botw_aot.dylib" BOTWPC_AOT_RECORD="$AOT/rec-cover-$(date +%s).bin"
  build
done
run nojit BOTWPC_AOT_DYLIB="$AOT/img/botw_aot.dylib" BOTWPC_NO_JIT=1
