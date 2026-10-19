#!/usr/bin/env python3
"""A/B inside one run (BOTWPC_AB_PERIOD=<s>, see Ryujinx.Common/BotwPc/AbPhase.cs): compares adjacent periods.

usage: ab_phases.py <framelog.csv> <period s> [--skip 90] [--until <s>] [--settle 1.0] [--selftest]

Period k is "B" (switch on) when floor(time_s / period) is odd, "A" otherwise, on the same Stopwatch clock the
runtime uses. The first --settle seconds of each period are dropped (frames queued under the previous setting). Each
B period is paired with the mean of its two neighbouring A periods, so slow drift (weather, time of day, camera,
background load) cancels. Reports A and B frame rate, 1 % low and p99, and the paired difference with a 95 %
interval (t distribution)."""
import csv
import math
import statistics
import sys

T95 = {1: 12.71, 2: 4.30, 3: 3.18, 4: 2.78, 5: 2.57, 6: 2.45, 7: 2.36, 8: 2.31, 9: 2.26, 10: 2.23, 12: 2.18, 15: 2.13,
       20: 2.09, 30: 2.04}


def t95(df):
    keys = sorted(k for k in T95 if k <= df)
    return T95[keys[-1]] if keys else 12.71


def periods(rows, period, skip, settle, until=float("inf")):
    t0 = rows[0][0]
    out = {}
    for t, ms in rows:
        if t - t0 < skip or t - t0 > until:
            continue
        k = int(t // period)
        if t - k * period < settle:
            continue
        out.setdefault(k, []).append(ms)
    return out


def stats(frames):
    s = sorted(frames)
    fps = 1000 * len(s) / sum(s)
    worst = s[-max(1, len(s) // 100):]
    return fps, 1000 / statistics.mean(worst), s[min(len(s) - 1, int(len(s) * 0.99))]


def analyze(rows, period, skip=90.0, settle=1.0, until=float("inf")):
    per = periods(rows, period, skip, settle, until)
    ks = sorted(per)
    pairs = []
    for k in ks:
        if k % 2 == 1 and k - 1 in per and k + 1 in per:
            b = stats(per[k])
            a1, a2 = stats(per[k - 1]), stats(per[k + 1])
            pairs.append((b, tuple((x + y) / 2 for x, y in zip(a1, a2))))
    a_all = [ms for k in ks if k % 2 == 0 for ms in per[k]]
    b_all = [ms for k in ks if k % 2 == 1 for ms in per[k]]
    return pairs, stats(a_all), stats(b_all)


def main(argv):
    if "--selftest" in argv:
        rows = [(i * 0.01, 10.0 if int(i * 0.01 // 5) % 2 == 0 else 12.5) for i in range(100000)]
        pairs, a, b = analyze(rows, 5.0, skip=0, settle=0.5)
        assert abs(a[0] - 100) < 0.01 and abs(b[0] - 80) < 0.01 and len(pairs) > 50, (a, b, len(pairs))
        print("selftest ok")
        return
    rows = []
    with open(argv[0]) as f:
        for r in csv.DictReader(f):
            rows.append((float(r["time_s"]), float(r["frame_ms"])))
    period = float(argv[1])
    skip = float(argv[argv.index("--skip") + 1]) if "--skip" in argv else 90.0
    until = float(argv[argv.index("--until") + 1]) if "--until" in argv else float("inf")
    if "--until" not in argv and "--selftest" not in argv:
        import os
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from ab_cut import detect_cut
        cut = detect_cut(os.path.join(os.path.dirname(os.path.abspath(argv[1])), "counters.csv"), period, skip)
        if cut is not None:
            until = cut
            print(f"scene change (GPU-thread time below 60 %, e.g. Game Over) at {cut:.0f} s: periods from there left out")
    settle = float(argv[argv.index("--settle") + 1]) if "--settle" in argv else 1.0
    pairs, a, b = analyze(rows, period, skip, settle, until)
    print(f"A (off): {a[0]:.2f} FPS, 1% low {a[1]:.1f}, p99 {a[2]:.2f} ms")
    print(f"B (on):  {b[0]:.2f} FPS, 1% low {b[1]:.1f}, p99 {b[2]:.2f} ms")
    for name, i in (("FPS", 0), ("1% low", 1), ("p99 ms", 2)):
        d = [p[0][i] - p[1][i] for p in pairs]
        if len(d) < 2:
            continue
        m, sd = statistics.mean(d), statistics.stdev(d)
        half = t95(len(d) - 1) * sd / math.sqrt(len(d))
        print(f"B - A {name:7s}: {m:+.2f} (95 % CI {m - half:+.2f} .. {m + half:+.2f}), {len(d)} pairs")
    fa, fb = 1000 / a[0], 1000 / b[0]
    print(f"frame time A {fa:.3f} ms, B {fb:.3f} ms, B - A {fb - fa:+.3f} ms")


if __name__ == "__main__":
    main(sys.argv[1:])
