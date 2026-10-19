#!/usr/bin/env bash
# Usage: run.sh <seconds> <label> [extra Ryujinx GUI args...]
# Runs BotW in the isolated data dir (never the Flatpak's), stops after <seconds>,
# and leaves framelog.csv, stdout.log, screenshots and summary.json in bench/<label>/.
# Env: BOTWPC_TRACE=<s> records a dotnet-trace sampling profile (trace.speedscope.json) after warm-up,
#      BOTWPC_PERF=<s> records a Linux perf profile (perf.txt, per-thread self time incl. JIT code) after warm-up,
#      BOTWPC_SET="key=json,key=json" temporarily overrides Config.json keys (restored afterwards),
#      BOTWPC_BUILD (default ../build-botwpc), BOTWPC_CAPTURE_INTERVAL (s), BOTWPC_SKIP (warm-up s, default 30)
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
BUILD=${BOTWPC_BUILD:-$ROOT/build-botwpc}
DATA=${BOTWPC_DATA:-$ROOT/devdata}
GAME=${BOTWPC_GAME:-$ROOT/TLOZ BOTW[01007EF00011E000][US][v0].nsp}
SECS=$1 OUT=$ROOT/bench/$2; shift 2
export BOTWPC_CAPTURE_INTERVAL=${BOTWPC_CAPTURE_INTERVAL:-15}
mkdir -p "$OUT"; rm -rf "$DATA/screenshots"
cp "$DATA/Config.json" "$OUT/Config.orig.json"
trap 'cp "$OUT/Config.orig.json" "$DATA/Config.json"' EXIT
if [ -n "${BOTWPC_SET:-}" ]; then
  python3 - "$DATA/Config.json" "$BOTWPC_SET" <<'PY'
import json, sys
path, sets = sys.argv[1], sys.argv[2]
c = json.load(open(path))
for kv in sets.split(","):
    k, v = kv.split("=", 1)
    if k not in c: sys.exit(f"unknown config key {k}")
    c[k] = json.loads(v)
json.dump(c, open(path, "w"), indent=2)
PY
fi
cp "$DATA/Config.json" "$OUT/Config.used.json"
env | grep '^BOTWPC_' | sort > "$OUT/env.txt"
export DOTNET_ROOT=$ROOT/.tools/dotnet BOTWPC_FRAMELOG=$OUT/framelog.csv
PERF="env LD_LIBRARY_PATH=$ROOT/.tools/perf-pkg/usr/lib $ROOT/.tools/perf-pkg/usr/bin/perf"
# JIT symbol map in /tmp/perf-<pid>.map; W^X double mapping hides JIT code from perf, so off while profiling.
[ -n "${BOTWPC_PERF:-}" ] && export DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 BOTWPC_GUEST_PERFMAP=$OUT/guest.map
[ -n "${BOTWPC_PERF:-}" ] && rm -f "$OUT/guest.map"
timeout -s INT -k 15 "$SECS" "$BUILD/Ryujinx" -r "$DATA" "$@" "$GAME" > "$OUT/stdout.log" 2>&1 &
RUN=$!
SKIP=${BOTWPC_SKIP:-30}
sleep "$SKIP"
nvidia-smi --query-gpu=timestamp,utilization.gpu,clocks.gr,memory.used,power.draw --format=csv,noheader,nounits -lms 500 > "$OUT/gpu.csv" 2>/dev/null &
SMI=$!
PID=$(pgrep -n -f "$BUILD/Ryujinx -r $DATA")
if [ -n "$PID" ] && [ -n "${BOTWPC_TRACE:-}" ]; then
  # Sampling trace instead of thread stats (tracing overhead would skew them).
  "$ROOT/.tools/bin/dotnet-trace" collect -p "$PID" --profile dotnet-sampled-thread-time --duration "00:00:$(printf %02d "$BOTWPC_TRACE")" -o "$OUT/trace.nettrace" > "$OUT/trace.log" 2>&1
  "$ROOT/.tools/bin/dotnet-trace" convert --format Speedscope "$OUT/trace.nettrace" -o "$OUT/trace" >> "$OUT/trace.log" 2>&1
elif [ -n "$PID" ] && [ -n "${BOTWPC_PERF:-}" ]; then
  $PERF record -F 997 -g -p "$PID" -o "$OUT/perf.data" -- sleep "$BOTWPC_PERF" > "$OUT/perf.log" 2>&1
  # Reported after the game exits (below) so the JIT map /tmp/perf-<pid>.map is complete.
elif [ -n "$PID" ]; then
  python3 "$(dirname "$0")/threadstat.py" "$PID" $(( SECS - SKIP - 5 )) > "$OUT/threads.json"
fi
kill $SMI 2>/dev/null
wait $RUN
if [ -f "$OUT/perf.data" ]; then
  # Guest functions translated by ARMeilleure (guest_<address>) next to the .NET JIT symbols.
  [ -f "$OUT/guest.map" ] && [ -f "/tmp/perf-$PID.map" ] && cat "$OUT/guest.map" >> "/tmp/perf-$PID.map"
  $PERF report -i "$OUT/perf.data" --no-children --sort comm,dso,sym --stdio -g none --percent-limit 0.2 > "$OUT/perf.txt" 2>> "$OUT/perf.log"
  $PERF report -i "$OUT/perf.data" --children --sort comm,sym --stdio -g caller --percent-limit 0.5 > "$OUT/perf-children.txt" 2>> "$OUT/perf.log"
  [ -n "${BOTWPC_PERF_KEEP:-}" ] || rm -f "$OUT/perf.data" "/tmp/perf-$PID.map" "/tmp/perfinfo-$PID.map"
fi
[ -d "$DATA/screenshots" ] && rm -rf "$OUT/screenshots" && mv "$DATA/screenshots" "$OUT/" && "$(dirname "$0")/contact_sheet.sh" "$OUT/screenshots" "$OUT/sheet.png"
python3 "$(dirname "$0")/frametime_stats.py" "$OUT/framelog.csv" --skip "$SKIP" --label "$(basename "$OUT")" --gpu "$OUT/gpu.csv" --counters "$OUT/counters.csv" | tee "$OUT/summary.json"
