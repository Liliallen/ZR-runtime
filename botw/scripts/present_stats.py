#!/usr/bin/env python3
"""FPS statistics from a present log (one CLOCK_MONOTONIC seconds value per vkQueuePresentKHR, native/present_layer).
usage: present_stats.py <presents.txt> <skip_s> [window_s]   (time 0 = first present)"""
import json, sys
t = [float(l) for l in open(sys.argv[1]) if l.strip()]
skip = float(sys.argv[2]); win = float(sys.argv[3]) if len(sys.argv) > 3 else 1e9
t = [x for x in t if t[0] + skip <= x <= t[0] + skip + win]
d = sorted((b - a) * 1000 for a, b in zip(t, t[1:]))
if len(d) < 10:
    sys.exit("not enough frames in the window")
pct = lambda p: d[min(len(d) - 1, int(p / 100 * len(d)))]
low = d[int(len(d) * 0.99):]
print(json.dumps({"frames": len(d), "seconds": round(t[-1] - t[0], 2), "avg_fps": round(len(d) / (t[-1] - t[0]), 2),
                  "median_fps": round(1000 / pct(50), 2), "one_pct_low_fps": round(1000 / (sum(low) / len(low)), 2),
                  "p95_ms": round(pct(95), 2), "p99_ms": round(pct(99), 2), "max_ms": round(d[-1], 2)}, indent=1))
