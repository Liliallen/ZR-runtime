#!/usr/bin/env python3
"""In-run A/B (BOTWPC_AB_PERIOD) for the per-frame counters in counters.csv, e.g. GPU-thread busy time.

usage: ab_counters.py <counters.csv> <period s> [--skip 90] [--until <s>] [--columns GpuFifoMs,SyncWaitMs]

counters.csv has one row per ~750 ms window ("time_s" = window end on the run's Stopwatch clock, "frames" = frames in
the window, the other columns = totals in the window). Windows that do not lie entirely inside one period are dropped.
Per period, a column is turned into a per-frame value (total / frames); each B period is paired with the mean of its two
neighbouring A periods, as in ab_phases.py, and the paired difference is reported with a 95 % t interval."""
import csv
import math
import os
import sys as _sys
import statistics
import sys

T95 = {1: 12.71, 2: 4.30, 3: 3.18, 4: 2.78, 5: 2.57, 6: 2.45, 7: 2.36, 8: 2.31, 9: 2.26, 10: 2.23, 12: 2.18, 15: 2.13,
       20: 2.09, 30: 2.04}


def t95(df):
    keys = sorted(k for k in T95 if k <= df)
    return T95[keys[-1]] if keys else 12.71


def main(argv):
    path, period = argv[1], float(argv[2])
    skip = float(argv[argv.index("--skip") + 1]) if "--skip" in argv else 90.0
    until = float(argv[argv.index("--until") + 1]) if "--until" in argv else float("inf")
    if "--until" not in argv:
        _sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from ab_cut import detect_cut
        cut = detect_cut(path, period, skip)
        if cut is not None:
            until = cut
            print(f"scene change (GPU-thread time below 60 %, e.g. Game Over) at {cut:.0f} s: periods from there left out")
    columns = (argv[argv.index("--columns") + 1] if "--columns" in argv else "GpuFifoMs,SyncWaitMs").split(",")

    rows = list(csv.DictReader(open(path, newline="")))
    t0 = float(rows[0]["time_s"])
    per = {}
    prev_end = None
    for r in rows:
        end = float(r["time_s"])
        start = prev_end if prev_end is not None else end - 0.75
        prev_end = end
        if end - t0 < skip or end - t0 > until:
            continue
        k = int(start // period)
        if int(end // period) != k or float(r["frames"]) <= 0:
            continue
        acc = per.setdefault(k, {"frames": 0.0, **{c: 0.0 for c in columns}})
        frames = float(r["frames"])
        acc["frames"] += frames
        for c in columns:
            acc[c] += float(r[c])

    for c in columns:
        diffs, a_all, b_all = [], [], []
        for k, v in per.items():
            val = v[c] / v["frames"]
            (b_all if k % 2 else a_all).append(val)
            if k % 2 == 1 and k - 1 in per and k + 1 in per:
                a = (per[k - 1][c] / per[k - 1]["frames"] + per[k + 1][c] / per[k + 1]["frames"]) / 2
                diffs.append(val - a)
        if len(diffs) < 2:
            print(f"{c}: not enough pairs")
            continue
        m = statistics.mean(diffs)
        h = t95(len(diffs) - 1) * statistics.stdev(diffs) / math.sqrt(len(diffs))
        print(f"{c:12s} A {statistics.mean(a_all):8.3f}  B {statistics.mean(b_all):8.3f}  B - A {m:+.3f} "
              f"(95 % CI {m - h:+.3f} .. {m + h:+.3f}), {len(diffs)} pairs")


if __name__ == "__main__":
    main(sys.argv)
