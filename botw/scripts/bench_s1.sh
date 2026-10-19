#!/usr/bin/env bash
# Usage: bench_s1.sh <mode> <label> [seconds]
# Fixed scenario S1: load the dev save (Shrine of Resurrection) by scripted input, idle, 1080p, res scale 1x,
# statistics after a 90 s warm-up. mode: 30 | 60 | 90 | 120 | unb. Extra BOTWPC_* env vars pass through.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
case "$1" in
  30)  SET='vsync_mode=0,enable_custom_vsync_interval=false' ;;
  60)  SET='vsync_mode=2,enable_custom_vsync_interval=true,custom_vsync_interval=120' ;;
  90)  SET='vsync_mode=2,enable_custom_vsync_interval=true,custom_vsync_interval=180' ;;
  120) SET='vsync_mode=2,enable_custom_vsync_interval=true,custom_vsync_interval=240' ;;
  unb) SET='vsync_mode=1,enable_custom_vsync_interval=false' ;;
  *) echo "mode must be 30|60|90|120|unb" >&2; exit 2 ;;
esac
BOTWPC_SET="$SET,res_scale=1,backend_threading=\"Auto\"${BOTWPC_SET:+,$BOTWPC_SET}" \
BOTWPC_INPUT_SCRIPT=${BOTWPC_INPUT_SCRIPT:-$HERE/../bench/continue.input} BOTWPC_SKIP=${BOTWPC_SKIP:-90} \
BOTWPC_AUTO_CONTROLLER=${BOTWPC_AUTO_CONTROLLER:-0} \
  exec "$HERE/run.sh" "${3:-150}" "$2"
