#!/usr/bin/env python3
"""Symbolizes botw/tools/GuestSampler output (Windows) with the BOTWPC_GUEST_PERFMAP file.

usage: guest_profile.py <samples.csv> <perfmap> [--threads PREFIX] [--top N]

Per thread: share of samples in JIT-compiled guest code (per guest function), JIT stubs (dispatch), native modules
(ntdll = mostly waits/syscalls, driver, CoreCLR, ...) and other memory (managed code JIT-compiled by .NET, which the
perf map does not cover; dotnet-trace attributes it). A sample is one instruction pointer of a suspended thread, so
"ntdll" includes threads blocked in a wait: running time is everything else."""
import bisect
import collections
import csv
import os
import sys

args = sys.argv[1:]
samples_path, perfmap_path = args[0], args[1]
prefix = args[args.index("--threads") + 1] if "--threads" in args else ""
top = int(args[args.index("--top") + 1]) if "--top" in args else 25

# Perf map: "host size name"; later lines win (re-translation can reuse freed cache space).
entries = {}
for line in open(perfmap_path, encoding="utf-8", errors="replace"):
    parts = line.split()
    if len(parts) >= 3:
        entries[int(parts[0], 16)] = (int(parts[1], 16), parts[2])
starts = sorted(entries)
jit_lo = starts[0] if starts else 0
jit_hi = max((s + entries[s][0] for s in starts), default=0)

modules = []
for row in csv.DictReader(open(samples_path + ".modules", encoding="utf-8", errors="replace")):
    modules.append((int(row["base"], 16), int(row["size"], 16), os.path.basename(row["path"]).lower()))
modules.sort()
module_starts = [m[0] for m in modules]


def classify(rip):
    i = bisect.bisect_right(starts, rip) - 1
    if i >= 0:
        size, name = entries[starts[i]]
        if rip < starts[i] + size:
            return ("guest" if name.startswith("guest_") else "stub"), name
    if jit_lo <= rip < jit_hi + (1 << 20):
        return "jit-other", "jit cache (unmapped)"
    j = bisect.bisect_right(module_starts, rip) - 1
    if j >= 0 and rip < modules[j][0] + modules[j][1]:
        return "native", modules[j][2]
    return "other", "private memory (managed JIT code)"


threads = collections.defaultdict(lambda: collections.Counter())
cats = collections.defaultdict(lambda: collections.Counter())
for row in csv.DictReader(open(samples_path, encoding="utf-8", errors="replace")):
    if not row["thread"].startswith(prefix):
        continue
    rip, n = int(row["rip"], 16), int(row["samples"])
    cat, name = classify(rip)
    key = f'{row["thread"]} ({row["tid"]})'
    threads[key][(cat, name)] += n
    cats[key][cat] += n

for key in sorted(threads, key=lambda k: -sum(cats[k].values())):
    total = sum(cats[key].values())
    running = total - sum(n for (c, name), n in threads[key].items() if c == "native" and name == "ntdll.dll")
    shares = "  ".join(f"{c} {100 * n / total:.1f}%" for c, n in cats[key].most_common())
    print(f"\n== {key}: {total} samples, not in ntdll {100 * running / total:.1f}%  [{shares}]")
    for (cat, name), n in threads[key].most_common(top):
        print(f"  {100 * n / total:6.2f}%  {cat:9s} {name}")
