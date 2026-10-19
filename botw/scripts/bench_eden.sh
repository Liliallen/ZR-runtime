#!/usr/bin/env bash
# Usage: bench_eden.sh <30|unb> <label> [seconds]
# Same S1 scenario as bench_s1.sh on Eden (alt-runtimes/eden-*/, isolated HOME). Eden has no input scripting, so a
# PERSON loads the save: press A (Continue, then Start) on the controller configured as Eden's Player 1 once the title
# menu appears (~55 s on a cold shader cache), then leave Link idle. Presents are counted by native/present_layer.
# (Synthetic X11 key injection was tried and removed: on KDE Wayland it cannot be confined to the Eden window.)
# Results: bench/<label>/presents.txt, stats.json (statistics after a 90 s warm-up, like bench_s1.sh).
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd); ROOT=$(cd "$HERE/../../.." && pwd)
EDEN=${EDEN_DIR:-$ROOT/alt-runtimes/eden-0.2.1}; OUT=$ROOT/bench/$2; SECS=${3:-150}
mkdir -p "$OUT"; rm -f "$OUT/presents.txt"
CFG=$EDEN/home/.config/eden/qt-config.ini
cp "$CFG" "$OUT/qt-config.orig.ini"; trap 'cp "$OUT/qt-config.orig.ini" "$CFG"' EXIT
case "$1" in
  30)  sed -i 's/^use_speed_limit=.*/use_speed_limit=true/; s/^use_vsync=.*/use_vsync=2/' "$CFG" ;;
  unb) sed -i 's/^use_speed_limit=.*/use_speed_limit=false/; s/^use_vsync=.*/use_vsync=0/' "$CFG" ;;
  *) echo "mode must be 30|unb" >&2; exit 2 ;;
esac
cp "$CFG" "$OUT/qt-config.used.ini"
L=$ROOT/BOTW-PC/botw/native/present_layer; X=$ROOT/BOTW-PC/botw/native/xshot/xshot
echo "Press A on Eden's Player 1 controller when the title menu appears (Continue → Start)."
# Game-window-only captures (the Eden window, nothing else) to check the scene.
( for t in 64 72 110 $((SECS - 15)); do sleep $((t - ${prev:-0})); prev=$t; "$X" Eden "$OUT/shot-$t.ppm" 2>> "$OUT/xshot.log" || true; done ) &
HOME=$EDEN/home VK_LAYER_PATH=$L VK_INSTANCE_LAYERS=VK_LAYER_BOTWPC_present_log PRESENT_LOG=$OUT/presents.txt \
  timeout -s INT -k 10 "$SECS" "$EDEN/squashfs-root/AppRun" -g "${BOTWPC_GAME:-$ROOT/TLOZ BOTW[01007EF00011E000][US][v0].nsp}" > "$OUT/stdout.log" 2>&1 || true
wait
cp "$EDEN/home/.local/share/eden/log/eden_log.txt" "$OUT/eden_log.txt" 2>/dev/null || true
python3 "$HERE/present_stats.py" "$OUT/presents.txt" "${SKIP:-90}" | tee "$OUT/stats.json"
