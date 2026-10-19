#!/usr/bin/env python3
"""Shared by ab_phases.py and ab_counters.py: find where a benchmark scene stopped being the scene.

On the W2 outdoor save, a night enemy can kill Link during an idle run; the Game Over screen costs a fraction of the
scene's GPU-thread time, so every pair spanning or following it compares different scenes. detect_cut() returns the
start time (s, relative to the first counters row) of the first period whose GPU-thread time per frame drops below
60 % of the median of the first four periods after the skip, or None."""
import csv
import os
import statistics


def detect_cut(counters_path, period, skip=90.0):
    if not os.path.exists(counters_path):
        return None
    rows = list(csv.DictReader(open(counters_path, newline="")))
    if not rows:
        return None
    t0 = float(rows[0]["time_s"])
    per = {}
    for r in rows:
        t = float(r["time_s"])
        if t - t0 < skip or float(r["frames"]) <= 0:
            continue
        acc = per.setdefault(int(t // period), [0.0, 0.0])
        acc[0] += float(r["frames"])
        acc[1] += float(r["GpuFifoMs"])
    ks = sorted(per)
    if len(ks) < 5:
        return None
    ref = statistics.median(per[k][1] / per[k][0] for k in ks[:4])
    for k in ks[4:]:
        if per[k][1] / per[k][0] < 0.6 * ref:
            return k * period - t0
    return None
