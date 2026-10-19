#!/usr/bin/env python3
"""Copy the small, non-copyrighted outputs of benchmark runs into botw/results/ and build index.csv.

usage: collect_results.py <bench-dir> <results-dir> <label>[=note] ...
Copies summary.json, threads.json, counters.csv, gpu.csv, env.txt and framelog.csv (gzipped).
Game screenshots are NOT copied (they are Nintendo imagery); they stay in the local bench dir.
"""
import csv, gzip, json, os, shutil, sys

bench, results = sys.argv[1], sys.argv[2]
rows = []
for arg in sys.argv[3:]:
    label, _, note = arg.partition("=")
    src, dst = os.path.join(bench, label), os.path.join(results, label)
    if not os.path.isfile(os.path.join(src, "summary.json")):
        print(f"skip {label}: no summary.json")
        continue
    os.makedirs(dst, exist_ok=True)
    for f in ("summary.json", "threads.json", "counters.csv", "gpu.csv", "env.txt", "perf.txt"):
        if os.path.isfile(os.path.join(src, f)):
            shutil.copy(os.path.join(src, f), dst)
    if os.path.isfile(os.path.join(dst, "env.txt")):  # don't publish local home paths
        text = open(os.path.join(dst, "env.txt")).read().replace(os.path.expanduser("~"), "~")
        open(os.path.join(dst, "env.txt"), "w").write(text)
    if os.path.isfile(os.path.join(src, "framelog.csv")):
        with open(os.path.join(src, "framelog.csv"), "rb") as fi, gzip.open(os.path.join(dst, "framelog.csv.gz"), "wb") as fo:
            shutil.copyfileobj(fi, fo)
    cfg = {}
    if os.path.isfile(os.path.join(src, "Config.used.json")):
        c = json.load(open(os.path.join(src, "Config.used.json")))
        cfg = {k: c.get(k) for k in ("vsync_mode", "custom_vsync_interval", "backend_threading", "res_scale")}
        json.dump(cfg, open(os.path.join(dst, "config.json"), "w"), indent=1)
    s = json.load(open(os.path.join(src, "summary.json")))
    rows.append({"label": label, "note": note, **{k: cfg.get(k, "") for k in ("vsync_mode", "custom_vsync_interval", "backend_threading")},
                 **{k: s.get(k, "") for k in ("frames", "median_fps", "avg_fps", "one_pct_low_fps", "p99_ms", "stutters_2x_median",
                                               "game_speed", "fifo_pct_mean", "gpu_util_pct_mean", "vram_mib_peak", "gpu_power_w_mean")}})
# Merge into the existing index: rows with the same label are replaced, others kept in order.
index = os.path.join(results, "index.csv")
old = list(csv.DictReader(open(index))) if os.path.isfile(index) else []
labels = {r["label"] for r in rows}
merged = [r for r in old if r["label"] not in labels] + rows
with open(index, "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=list(rows[0].keys()), lineterminator="\n")
    w.writeheader()
    w.writerows(merged)
print(f"{len(rows)} runs added, {len(merged)} total -> {index}")
