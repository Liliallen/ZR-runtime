#!/usr/bin/env python3
"""Summarise a BOTWPC_TIMELINE trace: per-thread wait/busy shares, the main thread's job-wait stalls and which
guest threads are working during them (the frame's critical path).
usage: timeline_report.py <trace.json> [main-thread-name]   (default: the guest thread with most GuestWaitAddress)"""
import collections, json, sys

t = json.load(open(sys.argv[1]))['traceEvents']
names = {e['tid']: e['args']['name'] for e in t if e['ph'] == 'M'}
ev = [e for e in t if e['ph'] == 'X']
frames = sorted(e['ts'] for e in ev if e['name'] == 'GuestFrameQueued')
span = frames[-1] - frames[0]
print(f"frames {len(frames)}  {len(frames) / (span / 1e6):.1f} FPS  ({span / 1e3:.0f} ms)")
waits = {'GuestWaitCondVar', 'GuestWaitAddress', 'GuestWaitSync', 'GuestWaitLock', 'RenderIdle', 'SyncWait'}
agg = collections.defaultdict(float)
for e in ev:
    if frames[0] <= e['ts'] <= frames[-1]:
        agg[(names.get(e['tid'], e['tid']), e['name'])] += e['dur']
print("\nlargest regions (% of wall time):")
for (th, n), d in sorted(agg.items(), key=lambda x: -x[1])[:15]:
    print(f"  {th[:26]:26s} {n:20s} {d / span * 100:5.1f}%")
by_thread = collections.Counter({th: d for (th, n), d in agg.items() if n == 'GuestWaitAddress'})
main = sys.argv[2] if len(sys.argv) > 2 else by_thread.most_common(1)[0][0]
stalls = []
for e in sorted((e for e in ev if names.get(e['tid']) == main and e['name'] == 'GuestWaitAddress'), key=lambda e: e['ts']):
    s, en = e['ts'], e['ts'] + e['dur']
    if stalls and s - stalls[-1][1] < 200:
        stalls[-1][1] = en
    else:
        stalls.append([s, en])
stalls = [w for w in stalls if w[1] - w[0] > 2000]
total = sum(b - a for a, b in stalls) or 1
print(f"\n{main}: {len(stalls)} stalls > 2 ms, {total / len(frames) / 1e3 if stalls else 0:.2f} ms per frame")
iv = collections.defaultdict(list)
for e in ev:
    if e['name'] in waits:
        iv[e['tid']].append((e['ts'], e['ts'] + e['dur']))
print("threads working during those stalls (not in a wait region):")
rows = []
for tid, ws in iv.items():
    covered = sum(min(en, b) - max(s, a) for a, b in stalls for s, en in ws if en > a and s < b)
    rows.append((1 - covered / total, names.get(tid, tid)))
for share, th in sorted(rows, reverse=True)[:8]:
    print(f"  {th[:30]:30s} {share * 100:5.1f}%")
