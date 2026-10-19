#!/usr/bin/env python3
"""Per-thread CPU utilization of a process over a window, from /proc (no perf needed).

usage: threadstat.py <pid> <seconds>     -> JSON {merged: [...by name], threads: [...top individual]} of {thread, cpu_pct} (100 = one core)
       threadstat.py --selftest
"""
import json
import os
import sys
import time

CLK_TCK = os.sysconf("SC_CLK_TCK")


def sample(pid):
    out = {}
    base = f"/proc/{pid}/task"
    for tid in os.listdir(base):
        try:
            with open(f"{base}/{tid}/stat") as f:
                stat = f.read()
        except OSError:
            continue  # thread exited
        # comm is in parentheses and may contain spaces; fields after it are space separated.
        comm = stat[stat.index("(") + 1:stat.rindex(")")]
        fields = stat[stat.rindex(")") + 2:].split()
        out[tid] = (comm, int(fields[11]) + int(fields[12]))  # utime + stime
    return out


def utilization(a, b, seconds, merge=True):
    rows = {}
    for tid, (comm, ticks) in b.items():
        before = a.get(tid, (comm, ticks))[1]
        pct = (ticks - before) / CLK_TCK / seconds * 100
        key = comm if merge else f"{comm}#{tid}"
        rows[key] = rows.get(key, 0) + pct  # merge same-named threads (e.g. worker pools)
    return sorted(({"thread": k, "cpu_pct": round(v, 1)} for k, v in rows.items() if v >= 1), key=lambda r: -r["cpu_pct"])


def selftest():
    a = {"1": ("GPU", 100), "2": ("cpu0", 0), "3": ("cpu0", 0)}
    b = {"1": ("GPU", 100 + CLK_TCK), "2": ("cpu0", CLK_TCK // 2), "3": ("cpu0", CLK_TCK // 2), "4": ("new", 5)}
    r = utilization(a, b, 1.0)
    assert r[0] == {"thread": "GPU", "cpu_pct": 100.0} and r[1] == {"thread": "cpu0", "cpu_pct": 100.0}, r
    assert all(x["thread"] != "new" for x in r), r  # threads born mid-window only count their delta
    print("selftest ok")


if __name__ == "__main__":
    if sys.argv[1:] == ["--selftest"]:
        selftest()
    else:
        pid, secs = sys.argv[1], float(sys.argv[2])
        a = sample(pid)
        time.sleep(secs)
        b = sample(pid)
        print(json.dumps({"merged": utilization(a, b, secs), "threads": utilization(a, b, secs, merge=False)[:12]}, indent=1))
