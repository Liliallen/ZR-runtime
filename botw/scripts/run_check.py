#!/usr/bin/env python3
"""Validity check of a benchmark run from its framelog.csv (bench_win.ps1 / bench_s1.sh output).

usage: run_check.py <framelog.csv> [--bucket 10]

Prints the frame rate per time bucket and flags what makes a run unusable for comparison:
  - title screen (first bucket) far below the uncapped ~119 FPS of the 120 mode: the host presentation was limited
    for the whole run (seen once on Windows with the display in another state; the run must be repeated),
  - gaps without frames longer than 2 s after loading (60 s): process suspended (GuestSampler's ClrMD attach) or stalled,
  - a bucket 15 % above the early gameplay rate (40..70 s): usually a Game Over screen (a night Keese on the W2 save),
    whose frames must not be averaged with gameplay (ab_cut.py cuts the A/B analysis there).
Exit code 1 when a problem was found."""
import csv
import sys


def main(argv):
    path = argv[1]
    bucket = float(argv[argv.index("--bucket") + 1]) if "--bucket" in argv else 10.0
    with open(path, encoding="utf-8") as f:
        rows = [r for r in csv.reader(f)][1:]
    times = [float(r[0]) for r in rows if r]
    if len(times) < 100:
        print("too few frames")
        return 1
    t0 = times[0]
    counts = {}
    gaps = []
    for prev, cur in zip(times, times[1:]):
        if cur - prev > 2.0:
            gaps.append((prev - t0, cur - prev))
    for t in times:
        k = int((t - t0) // bucket)
        counts[k] = counts.get(k, 0) + 1
    rates = [counts.get(k, 0) / bucket for k in range(max(counts) + 1)]
    print(" ".join(f"{k * bucket:.0f}s:{r:.0f}" for k, r in enumerate(rates)))
    problems = []
    if rates[0] < 85:
        problems.append(f"title screen at {rates[0]:.0f} FPS (expected ~119 in the 120 mode): presentation limited")
    for start, length in gaps:
        if start >= 60:
            problems.append(f"no frames for {length:.1f} s at {start:.0f} s")
    # Early gameplay rate (40..70 s); a later bucket 15 % above it looks like a Game Over screen.
    early = sorted(rates[int(40 / bucket):int(70 / bucket)])
    if early:
        reference = early[len(early) // 2]
        for k in range(int(80 / bucket), len(rates) - 1):
            if rates[k] > reference * 1.15:
                problems.append(f"rate {rates[k]:.0f} FPS at {k * bucket:.0f} s, early gameplay {reference:.0f}: Game Over or scene change?")
                break
    for p in problems:
        print("PROBLEM:", p)
    if not problems:
        print("ok")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
