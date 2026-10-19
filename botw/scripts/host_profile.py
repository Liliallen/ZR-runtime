#!/usr/bin/env python3
"""Self time per .NET method (and per native module) of emulator threads, from botw/tools/GuestSampler output taken
with --managed (Windows).

usage: host_profile.py <samples.csv> [--thread GPU.MainThread] [--top 40] [--il]

<samples.csv>.methods ("rip,il,method", ClrMD) attributes samples in CoreCLR JIT code; the rest is attributed to the
native module containing the address (<samples.csv>.modules) or to "?" (guest JIT code, stubs, freed code).
--il splits methods by IL offset (which statement of a large method is hot)."""
import csv
import sys
from collections import Counter


def load_modules(path):
    mods = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            mods.append((int(r["base"], 16), int(r["size"], 16), r["path"].replace("\\", "/").split("/")[-1]))
    mods.sort()
    return mods


def module_of(mods, rip):
    for base, size, name in mods:
        if base <= rip < base + size:
            return name
    return None


def main(argv):
    path = argv[1]
    thread = argv[argv.index("--thread") + 1] if "--thread" in argv else "GPU.MainThread"
    top = int(argv[argv.index("--top") + 1]) if "--top" in argv else 40
    split_il = "--il" in argv

    methods = {}
    with open(path + ".methods", newline="") as f:
        for r in csv.DictReader(f):
            methods[int(r["rip"], 16)] = (r["method"], int(r["il"]))
    mods = load_modules(path + ".modules")

    counts = Counter()
    total = 0
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            if not r["thread"].startswith(thread):
                continue
            rip, n = int(r["rip"], 16), int(r["samples"])
            total += n
            if rip in methods:
                name, il = methods[rip]
                key = f"{name} @IL_{il:04x}" if split_il else name
            else:
                key = f"[{module_of(mods, rip) or '?'}]"
            counts[key] += n

    print(f"{thread}: {total} samples")
    acc = 0
    for key, n in counts.most_common(top):
        acc += n
        print(f"{100 * n / total:6.2f} %  {100 * acc / total:6.2f} %  {key}")


if __name__ == "__main__":
    main(sys.argv)
