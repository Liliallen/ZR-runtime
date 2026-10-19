#!/usr/bin/env python3
"""Inclusive and self time per method from collapsed stacks (botw/tools/GuestSampler --stacks).

usage: stack_profile.py <stacks.txt> [--top 40] [--focus <substring>] [--exclude <substring>,...]

Without --focus: inclusive and self share of every method (a method counted once per sample even when recursive).
With --focus: for samples whose stack contains a method matching <substring>, the callers of that method and its
direct callees, by share of all samples. --exclude drops samples containing a matching frame (e.g. the idle wait)."""
import sys
from collections import Counter


def load(path, exclude):
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            stack, _, n = line.rstrip("\n").rpartition(" ")
            frames = stack.split(";")
            if any(e and any(e in fr for fr in frames) for e in exclude):
                continue
            rows.append((frames, int(n)))
    return rows


def main(argv):
    path = argv[1]
    top = int(argv[argv.index("--top") + 1]) if "--top" in argv else 40
    focus = argv[argv.index("--focus") + 1] if "--focus" in argv else None
    exclude = argv[argv.index("--exclude") + 1].split(",") if "--exclude" in argv else []
    rows = load(path, exclude)
    total = sum(n for _, n in rows)
    print(f"{total} samples")

    if focus is None:
        incl, self_ = Counter(), Counter()
        for frames, n in rows:
            for m in set(frames):
                incl[m] += n
            self_[frames[-1]] += n
        print("  incl %   self %  method")
        for m, n in incl.most_common(top):
            print(f"{100 * n / total:7.2f}  {100 * self_[m] / total:7.2f}  {m}")
        return

    callers, callees, hit = Counter(), Counter(), 0
    for frames, n in rows:
        idx = [i for i, fr in enumerate(frames) if focus in fr]
        if not idx:
            continue
        hit += n
        i = idx[0]
        callers[frames[i - 1] if i > 0 else "<root>"] += n
        j = idx[-1]
        callees[frames[j + 1] if j + 1 < len(frames) else "<self>"] += n
    print(f"{focus}: {100 * hit / total:.2f} % inclusive")
    print("callers:")
    for m, n in callers.most_common(top):
        print(f"  {100 * n / total:6.2f}  {m}")
    print("callees:")
    for m, n in callees.most_common(top):
        print(f"  {100 * n / total:6.2f}  {m}")


if __name__ == "__main__":
    main(sys.argv)
