#!/usr/bin/env python3
"""Markdown table over repeated bench_win.ps1 runs: bench_table.py <results root> <label prefix> [<label prefix> ...]
Runs are directories named <prefix>-<n> (or <prefix><n>) containing summary.json (and cpu.txt). Each cell is the mean
over the runs with the min..max range for FPS."""
import json
import os
import re
import statistics
import sys

COLS = [
    ("avg FPS", lambda s: s["avg_fps"], 1),
    ("median FPS", lambda s: s["median_fps"], 1),
    ("1% low", lambda s: s["one_pct_low_fps"], 1),
    ("p95 ms", lambda s: s["p95_ms"], 2),
    ("p99 ms", lambda s: s["p99_ms"], 2),
    ("GPU thread ms", lambda s: s["per_frame"]["GpuFifoMs"], 2),
    ("GPU busy ms", lambda s: s["per_frame"]["GpuBusyMs"], 2),
    ("sync wait ms", lambda s: s["per_frame"]["SyncWaitMs"], 2),
    ("submit ms", lambda s: s["per_frame"]["QueueSubmitMs"], 2),
    ("present ms", lambda s: s["per_frame"]["PresentMs"], 2),
    ("GPU util %", lambda s: s.get("gpu_util_pct_mean"), 0),
    ("VRAM MiB", lambda s: s.get("vram_mib_peak"), 0),
    ("CPU cores", lambda s: s.get("cpu_cores"), 2),
    ("game speed", lambda s: s.get("game_speed"), 3),
]


def load(root, prefix):
    runs = []
    for name in sorted(os.listdir(root)):
        if not re.fullmatch(re.escape(prefix) + r"-?\d+", name):
            continue
        path = os.path.join(root, name, "summary.json")
        try:
            s = json.load(open(path, encoding="utf-8-sig"))
        except (OSError, ValueError):
            continue
        try:
            m = re.search(r"cpu_cores_mean=([\d.]+)", open(os.path.join(root, name, "cpu.txt"), encoding="utf-8-sig").read())
            s["cpu_cores"] = float(m.group(1)) if m else None
        except OSError:
            s["cpu_cores"] = None
        if s.get("frames"):
            runs.append(s)
    return runs


def cell(runs, f, digits, spread):
    vals = [v for v in (f(r) for r in runs) if v is not None]
    if not vals:
        return "-"
    text = f"{statistics.mean(vals):.{digits}f}"
    if spread and len(vals) > 1:
        text += f" ({min(vals):.{digits}f}..{max(vals):.{digits}f})"
    return text


def main():
    root, prefixes = sys.argv[1], sys.argv[2:]
    print("| config | runs | " + " | ".join(c[0] for c in COLS) + " |")
    print("|---|---|" + "---|" * len(COLS))
    for p in prefixes:
        runs = load(root, p)
        cells = [cell(runs, f, d, i < 3) for i, (_, f, d) in enumerate(COLS)]
        print(f"| {p} | {len(runs)} | " + " | ".join(cells) + " |")


if __name__ == "__main__":
    main()
