#!/usr/bin/env bash
# Usage: bench_title.sh <botwpc|eden> <label> [seconds]
# Input-free comparison scenario T0: boot BotW 1.6.0 (update + DLC) and idle on the title menu at the stock 30 FPS.
# Presents are counted by native/present_layer; CPU time, RSS, GPU utilisation and VRAM are sampled once per second
# from 60 s to the end. Results in bench/<label>/ (stats.json, samples.csv). Same game files and display for both.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd); ROOT=$(cd "$HERE/../../.." && pwd); OUT=$ROOT/bench/$2; SECS=${3:-130}
mkdir -p "$OUT"; rm -f "$OUT/presents.txt" "$OUT/samples.csv"
L=$ROOT/BOTW-PC/botw/native/present_layer
export VK_LAYER_PATH=$L VK_INSTANCE_LAYERS=VK_LAYER_BOTWPC_present_log PRESENT_LOG=$OUT/presents.txt
GAME="$ROOT/TLOZ BOTW[01007EF00011E000][US][v0].nsp"
case "$1" in
  botwpc) PROC=Ryujinx; ( BOTWPC_AUTO_CONTROLLER=0 BOTWPC_CAPTURE_INTERVAL=60 BOTWPC_SKIP=5 BOTWPC_SET='vsync_mode=0,enable_custom_vsync_interval=false,res_scale=1' \
            "$HERE/run.sh" "$SECS" "$2" ) > /dev/null 2>&1 & ;;
  eden)   PROC=eden; EDEN=$ROOT/alt-runtimes/eden-0.2.1
          ( HOME=$EDEN/home timeout -s INT -k 10 "$SECS" "$EDEN/squashfs-root/AppRun" -g "$GAME" > "$OUT/stdout.log" 2>&1 || true ) & ;;
  *) echo "botwpc|eden" >&2; exit 2 ;;
esac
sleep 60
PID=$(pgrep -x "$PROC" | head -1); HZ=$(getconf CLK_TCK)
echo "t,cpu_s,rss_mb,gpu_util,vram_mb" > "$OUT/samples.csv"
for ((i = 60; i < SECS - 5; i++)); do
  read -r ut st < <(awk '{print $14, $15}' /proc/$PID/stat) || break
  rss=$(awk '/VmRSS/{print int($2/1024)}' /proc/$PID/status)
  gpu=$(nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits | tr -d ' ')
  echo "$i,$(awk -v a=$ut -v b=$st -v h=$HZ 'BEGIN{print (a+b)/h}'),$rss,$gpu" >> "$OUT/samples.csv"; sleep 1
done
wait
python3 - "$OUT" <<'PY'
import csv, json, subprocess, sys
out = sys.argv[1]
rows = list(csv.DictReader(open(f"{out}/samples.csv")))
cpu = (float(rows[-1]["cpu_s"]) - float(rows[0]["cpu_s"])) / (int(rows[-1]["t"]) - int(rows[0]["t"]))
stats = json.loads(subprocess.check_output(["python3", f"{out}/../../BOTW-PC/botw/scripts/present_stats.py", f"{out}/presents.txt", "60"]))
stats.update(cpu_cores_busy=round(cpu, 2), rss_mb_max=max(int(r["rss_mb"]) for r in rows),
             gpu_util_avg=round(sum(int(r["gpu_util"]) for r in rows) / len(rows), 1), vram_mb_max=max(int(r["vram_mb"]) for r in rows))
json.dump(stats, open(f"{out}/stats.json", "w"), indent=1); print(json.dumps(stats))
PY
