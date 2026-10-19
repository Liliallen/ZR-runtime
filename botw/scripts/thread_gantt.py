#!/usr/bin/env python3
"""Average per-frame occupancy of the threads that matter, from a BOTWPC_TIMELINE trace.

usage: thread_gantt.py <trace.json> [bin ms] [thread ...]

Frames run from one GuestFrameQueued to the next. Guest threads count as busy outside their wait regions (svc waits,
GPU sync waits); GPU.MainThread counts as busy inside GpuFifo. Each cell is the share of frames (in %) in which the
thread was busy during that bin, so a thread's row shows when in the frame it works and when it waits. With
BOTWPC_WAKE_LOG=1 the trace also has SchedWake regions (host wake-up latency of a guest thread); their total per
frame and thread is printed below the chart."""
import bisect
import collections
import json
import sys

trace = json.load(open(sys.argv[1]))["traceEvents"]
bin_us = float(sys.argv[2]) * 1000 if len(sys.argv) > 2 else 500.0
names = {e["tid"]: e["args"]["name"] for e in trace if e["ph"] == "M"}
events = [e for e in trace if e["ph"] == "X"]
frames = sorted(e["ts"] for e in events if e["name"] == "GuestFrameQueued")
nf = len(frames) - 1
waits = {"GuestWaitAddress", "GuestWaitSync", "GuestWaitCondVar", "GuestWaitLock", "SyncWait"}

# Guest threads with the most running time (outside waits), unless named on the command line.
wait_total = collections.Counter()
for e in events:
    if e["name"] in waits:
        wait_total[names.get(e["tid"])] += e["dur"]
span = frames[-1] - frames[0]
guest = [t for t in set(names.values()) if t.startswith("HLE.GuestThread")]
threads = sys.argv[3:] or sorted(guest, key=lambda t: wait_total[t])[:3] + ["GPU.MainThread"]

nbins = int(max(b - a for a, b in zip(frames, frames[1:])) // bin_us) + 1
occ = {t: [0.0] * nbins for t in threads}


def add(thread, a, b, sign):
    i = bisect.bisect_right(frames, a) - 1
    while a < b and 0 <= i < nf:
        f0, f1 = frames[i], frames[i + 1]
        end = min(b, f1)
        x = a - f0
        while x < end - f0:
            k = int(x // bin_us)
            nxt = min((k + 1) * bin_us, end - f0)
            occ[thread][k] += sign * (nxt - x) / bin_us / nf
            x = nxt
        a, i = end, i + 1


for t in threads:
    if t != "GPU.MainThread":
        for i in range(nf):  # busy unless waiting, within each frame's own length
            add(t, frames[i], frames[i + 1], +1)
for e in events:
    t = names.get(e["tid"])
    if t in occ:
        if t == "GPU.MainThread" and e["name"] == "GpuFifo":
            add(t, e["ts"], e["ts"] + e["dur"], +1)
        elif t != "GPU.MainThread" and e["name"] in waits:
            add(t, e["ts"], e["ts"] + e["dur"], -1)

print(f"{nf} frames; busy % per {bin_us / 1000:g} ms bin after the guest queued the previous frame")
print(f"{'ms':>20s} " + " ".join(f"{k * bin_us / 1000:4.1f}" for k in range(nbins)))
for t in threads:
    print(f"{t:>20s} " + " ".join(f"{max(0.0, v) * 100:4.0f}" for v in occ[t]))

wake = collections.Counter()
wake_n = collections.Counter()
for e in events:
    if e["name"] == "SchedWake":
        wake[names.get(e["tid"])] += e["dur"]
        wake_n[names.get(e["tid"])] += 1
for t, total in wake.most_common(8):
    print(f"SchedWake {t}: {wake_n[t] / nf:.1f} per frame, {total / nf / 1000:.3f} ms per frame, {total / wake_n[t]:.1f} us each")
