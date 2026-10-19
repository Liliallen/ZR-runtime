#!/usr/bin/env python3
"""Per-frame cross-thread critical path from a BOTWPC_TIMELINE trace (no profiling switches needed).

usage: critical_path.py <trace.json>

The chain that limits W1 (PERFORMANCE.md): the guest submits frame k's GPU work (GpfifoSubmit) -> GPU.MainThread
processes it up to the last read-back producer (ReadbackSync marker) -> the host GPU runs it and the guest's readback
wait (SyncWait on guest threads) ends -> the guest finishes frame k+1 and queues it (GuestFrameQueued) -> next submit.
For every frame the script splits submit-to-submit time into these consecutive segments, so they add up to the frame
time instead of adding overlapping thread durations, and reports how much GPU-thread work lies after the last
read-back producer (overlapping the guest tail, i.e. off the critical path)."""
import bisect
import collections
import json
import statistics
import sys

t = json.load(open(sys.argv[1]))["traceEvents"]
names = {e["tid"]: e["args"]["name"] for e in t if e["ph"] == "M"}
ev = [e for e in t if e["ph"] == "X"]
by = collections.defaultdict(list)
for e in ev:
    by[e["name"]].append((e["ts"], e["ts"] + e["dur"], names.get(e["tid"], str(e["tid"]))))
for v in by.values():
    v.sort()

queued = [a for a, _, _ in by["GuestFrameQueued"]]
submits = [a for a, _, _ in by["GpfifoSubmit"]]
fifo = [(a, b) for a, b, th in by["GpuFifo"] if th == "GPU.MainThread"]
readback = [a for a, _, th in by["ReadbackSync"] if th == "GPU.MainThread"]
waits = [(a, b) for a, b, th in by["SyncWait"] if th.startswith("HLE.")]
render_submits = [(a, b) for a, b, th in by["QueueSubmit"] if th == "GUI.RenderThread"]
render_submit_starts = [a for a, _ in render_submits]
fifo_starts = [a for a, _ in fifo]

# Frame k starts at the first submission after the guest queued frame k-1 (the guest submits a frame's work in a
# burst right before queueing it).
starts = []
for q in queued:
    i = bisect.bisect_left(submits, q - 4000)
    if i < len(submits) and submits[i] < q + 1000:
        if not starts or submits[i] > starts[-1]:
            starts.append(submits[i])

rows = []
for s0, s1 in zip(starts, starts[1:]):
    rb = [r for r in readback[bisect.bisect_left(readback, s0):bisect.bisect_left(readback, s1)]]
    if not rb:
        continue
    r = rb[-1]
    w_end = max((b for a, b in waits if s0 <= a < s1), default=None)
    q = next((x for x in queued if s0 < x <= s1 + 1000), None)
    i0, i1 = bisect.bisect_left(fifo_starts, s0 - 100), bisect.bisect_left(fifo_starts, s1)
    gpu_busy = sum(min(b, s1) - max(a, s0) for a, b in fifo[i0:i1] if b > s0)
    g_end = max((b for a, b in fifo[i0:i1]), default=r)
    if w_end is None or q is None or w_end < r:
        w_end = r  # the guest did not wait for the readback in this frame
    # The render (backend) thread submits the command buffer holding the producer after it has replayed the queued
    # commands up to the sync: the first vkQueueSubmit that starts after the marker.
    j = bisect.bisect_left(render_submit_starts, r)
    submitted = min(render_submits[j][1], w_end) if j < len(render_submits) else w_end
    submitted = max(submitted, r)
    rows.append({
        "frame": (s1 - s0) / 1000,
        "gpu_thread_to_last_readback": (r - s0) / 1000,
        "readback_gpu_and_wait": (w_end - r) / 1000,
        "  of which render thread to submit": (submitted - r) / 1000,
        "  of which GPU execution + wake": (w_end - submitted) / 1000,
        "guest_tail": (min(q, s1) - w_end) / 1000,
        "queue_to_next_submit": (s1 - min(q, s1)) / 1000,
        "gpu_thread_busy": gpu_busy / 1000,
        "gpu_thread_after_last_readback": max(0, g_end - r) / 1000,
        "readback_markers": len(rb),
        "waited": 1.0 if w_end > r else 0.0,
    })

print(f"{len(rows)} frames")
print(f"{'segment':34s} {'median':>8s} {'mean':>8s}  (ms; frame = the next four lines without indentation)")
for k in rows[0]:
    vals = [x[k] for x in rows]
    print(f"{k:34s} {statistics.median(vals):8.2f} {statistics.mean(vals):8.2f}")
