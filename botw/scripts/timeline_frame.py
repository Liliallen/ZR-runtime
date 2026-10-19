#!/usr/bin/env python3
"""Typical-frame view of a BOTWPC_TIMELINE trace: for every guest frame (GuestFrameQueued to the next one), where
selected regions start and end relative to the frame start, averaged over frames (median). Shows the dependency chain
(GPU command processing, backend submit/present, guest readback waits, main-thread stalls) without adding overlapping
thread durations.
usage: timeline_frame.py <trace.json> [main-thread-name]"""
import bisect
import collections
import json
import statistics
import sys

t = json.load(open(sys.argv[1]))["traceEvents"]
names = {e["tid"]: e["args"]["name"] for e in t if e["ph"] == "M"}
ev = [e for e in t if e["ph"] == "X"]
frames = sorted(e["ts"] for e in ev if e["name"] == "GuestFrameQueued")
by = collections.defaultdict(list)
for e in ev:
    by[(names.get(e["tid"], str(e["tid"])), e["name"])].append((e["ts"], e["ts"] + e["dur"]))

main = sys.argv[2] if len(sys.argv) > 2 else max(
    (k for k in by if k[1] == "GuestWaitAddress"), key=lambda k: sum(b - a for a, b in by[k]))[0]

# Regions to place in the frame: (thread, region, merge gap in us, min length in us).
wanted = [("GPU.MainThread", "GpuFifo", 0, 0)]
wanted += [(th, "GpfifoSubmit", 0, 0) for th, n in by if n == "GpfifoSubmit"]
wanted += [(th, "SyncWait", 0, 0) for th, n in by if n == "SyncWait"]
wanted += [("GUI.RenderThread", "QueueSubmit", 0, 0), ("GUI.RenderThread", "Present", 0, 0)]
wanted += [(main, "GuestWaitAddress", 200, 1000)]

print(f"{len(frames) - 1} frames, median frame {statistics.median(b - a for a, b in zip(frames, frames[1:])) / 1000:.2f} ms; main thread {main}")
print("region (thread)                                  n/frame   start ms   end ms   total ms/frame")
for th, name, gap, minimum in wanted:
    spans = sorted(by.get((th, name), []))
    merged = []
    for a, b in spans:
        if merged and a - merged[-1][1] < gap:
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b])
    merged = [s for s in merged if s[1] - s[0] >= minimum]
    rel = collections.defaultdict(list)
    for a, b in merged:
        i = bisect.bisect_right(frames, a) - 1
        if 0 <= i < len(frames) - 1:
            rel[i].append((a - frames[i], b - frames[i]))
    if not rel:
        continue
    count = statistics.mean(len(v) for v in rel.values())
    start = statistics.median(min(a for a, _ in v) for v in rel.values()) / 1000
    end = statistics.median(max(b for _, b in v) for v in rel.values()) / 1000
    total = sum(b - a for v in rel.values() for a, b in v) / (len(frames) - 1) / 1000
    print(f"{name + ' (' + th + ')':48s} {count:7.1f} {start:9.2f} {end:9.2f} {total:10.2f}")
