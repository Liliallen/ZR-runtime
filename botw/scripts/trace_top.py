#!/usr/bin/env python3
"""Hottest functions per thread from a dotnet-trace speedscope export.

usage: trace_top.py <trace.speedscope.json> [thread-substring] [N]
       trace_top.py --selftest
A thread matches if its name or any of its frames contains thread-substring.
Prints, per matching thread, the top-N leaf (self) and inclusive frames as % of that thread's samples.
"""
import json
import sys
from collections import Counter


def analyze(doc, thread_filter="", top=25):
    frames = [f["name"] for f in doc["shared"]["frames"]]
    report = []
    for prof in doc["profiles"]:
        # Match the thread name, or (since exports often lose names) any frame on that thread.
        names_on_thread = {frames[e["frame"]] for e in prof.get("events", [])} | {frames[i] for st in prof.get("samples", []) for i in st}
        if thread_filter not in prof["name"] and not any(thread_filter in n for n in names_on_thread):
            continue
        if prof["type"] == "sampled":
            stacks, weights = prof["samples"], prof.get("weights") or [1] * len(prof["samples"])
        else:  # evented: convert open/close events into weighted stacks
            stacks, weights, open_stack, last = [], [], [], None
            for ev in prof["events"]:
                if last is not None and open_stack:
                    stacks.append(list(open_stack))
                    weights.append(ev["at"] - last)
                last = ev["at"]
                if ev["type"] == "O":
                    open_stack.append(ev["frame"])
                else:
                    open_stack.pop()
        total = sum(weights)
        if not total:
            continue
        self_c, incl_c = Counter(), Counter()
        for st, w in zip(stacks, weights):
            if not st:
                continue
            self_c[frames[st[-1]]] += w
            for name in {frames[i] for i in st}:
                incl_c[name] += w
        pct = lambda c: [(round(100 * v / total, 1), k) for k, v in c.most_common(top)]
        report.append({"thread": prof["name"], "total": total, "self": pct(self_c), "inclusive": pct(incl_c)})
    return report


def selftest():
    doc = {"shared": {"frames": [{"name": "Main"}, {"name": "Work"}, {"name": "Wait"}]},
           "profiles": [{"name": "T1", "type": "sampled", "samples": [[0, 1], [0, 1], [0, 2], [0]], "weights": [1, 1, 1, 1]},
                        {"name": "T2", "type": "evented", "events": [
                            {"type": "O", "frame": 0, "at": 0}, {"type": "O", "frame": 2, "at": 1},
                            {"type": "C", "frame": 2, "at": 4}, {"type": "C", "frame": 0, "at": 4}]}]}
    r = analyze(doc, "T1")
    assert r[0]["self"][0] == (50.0, "Work") and (100.0, "Main") in r[0]["inclusive"], r
    r2 = analyze(doc, "T2")
    assert r2[0]["self"][0] == (75.0, "Wait") and r2[0]["total"] == 4, r2
    print("selftest ok")


if __name__ == "__main__":
    if sys.argv[1:] == ["--selftest"]:
        selftest()
        sys.exit()
    doc = json.load(open(sys.argv[1]))
    filt = sys.argv[2] if len(sys.argv) > 2 else ""
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 25
    for t in analyze(doc, filt, n):
        print(f"=== {t['thread']} (total {t['total']:.0f})")
        print("  -- self --")
        for p, k in t["self"]:
            print(f"  {p:5.1f}%  {k[:150]}")
        print("  -- inclusive --")
        for p, k in t["inclusive"]:
            print(f"  {p:5.1f}%  {k[:150]}")
