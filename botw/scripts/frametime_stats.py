#!/usr/bin/env python3
"""Summarize a BOTWPC_FRAMELOG csv (time_s,frame_ms,fifo_pct) into JSON.

usage: frametime_stats.py <framelog.csv> [--skip SECONDS] [--label NAME] [--gpu nvidia-smi.csv] [--counters counters.csv]
       frametime_stats.py --selftest
"""
import csv
import json
import statistics
import sys


def summarize(frame_ms, fifo=None):
    if not frame_ms:
        return {"frames": 0}
    s = sorted(frame_ms)
    n = len(s)
    worst = s[-max(1, n // 100):]  # worst 1% of frame times
    median = statistics.median(s)
    total_s = sum(s) / 1000
    out = {
        "frames": n,
        "duration_s": round(total_s, 3),
        "avg_fps": round(n / total_s, 2),
        "median_fps": round(1000 / median, 2),
        "one_pct_low_fps": round(1000 / statistics.mean(worst), 2),
        "p50_ms": round(median, 3),
        "p95_ms": round(s[min(n - 1, int(n * 0.95))], 3),
        "p99_ms": round(s[min(n - 1, int(n * 0.99))], 3),
        "max_ms": round(s[-1], 3),
        # A stutter is a frame taking more than twice the median frame time.
        "stutters_2x_median": sum(1 for x in s if x > 2 * median),
    }
    if fifo:
        out["fifo_pct_mean"] = round(statistics.mean(fifo), 2)
    return out


def load(path, skip):
    frame_ms, fifo = [], []
    with open(path) as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return frame_ms, fifo
    # First row's delta is measured from process start, not a real frame.
    start = float(rows[0]["time_s"])
    for r in rows[1:]:
        if float(r["time_s"]) - start < skip:
            continue
        frame_ms.append(float(r["frame_ms"]))
        fifo.append(float(r["fifo_pct"]))
    return frame_ms, fifo


def gpu_summary(path):
    """Mean GPU utilization/clock/power and peak VRAM from nvidia-smi csv (ts, util%, MHz, MiB, W)."""
    rows = []
    try:
        with open(path) as f:
            for line in f:
                p = [x.strip() for x in line.split(",")]
                if len(p) == 5:
                    rows.append([float(x) for x in p[1:]])
    except (OSError, ValueError):
        return {}
    if not rows:
        return {}
    return {
        "gpu_util_pct_mean": round(statistics.mean(r[0] for r in rows), 1),
        "gpu_clock_mhz_mean": round(statistics.mean(r[1] for r in rows)),
        "vram_mib_peak": max(r[2] for r in rows),
        "gpu_power_w_mean": round(statistics.mean(r[3] for r in rows), 1),
    }


def counters_summary(path, skip):
    """Per-frame averages of the Vulkan backend counters (counters.csv) after the warm-up window."""
    try:
        with open(path) as f:
            rows = list(csv.DictReader(f))
    except OSError:
        return {}
    if not rows:
        return {}
    start = float(rows[0]["time_s"])
    rows = [r for r in rows if float(r["time_s"]) - start >= skip]
    frames = sum(int(r["frames"]) for r in rows)
    if not frames:
        return {}
    keys = [k for k in rows[0] if k not in ("time_s", "frames", "game_s")]
    out = {"per_frame": {k: round(sum(float(r[k]) for r in rows) / frames, 3) for k in keys}}
    if "game_s" in rows[0] and len(rows) > 1:
        # BotW game seconds advanced per wall second over the window (1.0 = real time).
        wall = float(rows[-1]["time_s"]) - float(rows[0]["time_s"])
        game = sum(float(r["game_s"]) for r in rows[1:])
        if wall > 0 and game > 0:
            out["game_speed"] = round(game / wall, 3)
    return out


def selftest():
    r = summarize([33.333] * 99 + [100.0])
    assert r["frames"] == 100 and r["median_fps"] == 30.0 and r["p95_ms"] == 33.333, r
    assert r["one_pct_low_fps"] == 10.0 and r["stutters_2x_median"] == 1, r
    assert r["avg_fps"] == 29.41, r  # 100 frames / 3.39997 s
    assert summarize([]) == {"frames": 0}
    import os, tempfile
    with tempfile.NamedTemporaryFile("w", suffix=".csv", delete=False) as f:
        f.write("time_s,frames,game_s,Submits\n0,30,1.0,10\n1,30,1.0,10\n2,30,0.5,10\n")
    c = counters_summary(f.name, 0)
    os.unlink(f.name)
    assert c["game_speed"] == 0.75 and c["per_frame"]["Submits"] == 0.333, c
    print("selftest ok")


def main(argv):
    if argv[1:] == ["--selftest"]:
        return selftest()
    path, skip, label = argv[1], 0.0, None
    if "--skip" in argv:
        skip = float(argv[argv.index("--skip") + 1])
    if "--label" in argv:
        label = argv[argv.index("--label") + 1]
    out = summarize(*load(path, skip))
    if "--counters" in argv:
        out.update(counters_summary(argv[argv.index("--counters") + 1], skip))
    if "--gpu" in argv:
        out.update(gpu_summary(argv[argv.index("--gpu") + 1]))
    if label:
        out = {"label": label, **out}
    print(json.dumps(out, indent=2))


if __name__ == "__main__":
    main(sys.argv)
