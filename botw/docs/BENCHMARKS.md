# Benchmarks

All raw outputs: `botw/results/<label>/` (summary.json, framelog.csv.gz, counters.csv, gpu.csv, threads.json,
config.json) and `botw/results/index.csv`. Screenshots/contact sheets are kept locally in `../bench/` only.

## Method
- Build: Release, this branch; isolated data dir; same save; 1920×1080 window, resolution scale 1×.
- `botw/scripts/run.sh <seconds> <label>` with `BOTWPC_INPUT_SCRIPT=botw/bench/continue.input` loads the save
  (Shrine of Resurrection) by scripted input; statistics exclude the first 90 s.
- FPS = guest frames queued for presentation (what the game rendered), from `BOTWPC_FRAMELOG`.
  1 % low = 1000 / mean of the worst 1 % frame times. Stutter = frame > 2× median.
- GPU utilization/clock/power/VRAM: `nvidia-smi` every 0.5 s. Thread CPU: `/proc` sampling.
- Game speed: game seconds (from BotW's own VFR ratio) per wall second.
- Every in-game run was checked on its contact sheet to really be in-game.
- Single runs per configuration; run-to-run spread seen on repeated configurations: ±2 FPS median
  (e.g. uncapped pool-on: 92.3, 93.9, 95.2).

## Headline results (S1, Shrine of Resurrection)

| Configuration | Median FPS | 1 % low | p99 | Game speed | GPU util | Label |
|---|---|---|---|---|---|---|
| Baseline, stock 30 cap | 30.0 | 20.0 | 50.0 ms | 1.00 | 15.6 % | `s1-baseline` / `s1-30fps-gpu` |
| Baseline, uncapped | 52.7 | 30.1 | 30.4 ms | ~1.75 (uncorrected) | 20.6 % | `s1-unbounded` |
| + fence pool, stock 30 cap | 30.0 | **27.8** | **34.5 ms** | 1.00 | 15.1 % | `s1-30fps-fencepool1` |
| + fence pool, uncapped | **92.3** | **67.9** | **13.9 ms** | — | 32.6 % | `s1-unb-fencepool1` |
| Final, 60 FPS mode | 60.0 | 53.3 | 17.0 ms | **1.00** | 20.9 % | `s1-120hz-v2` |
| Final, 90 FPS mode | 90.2 | 70.3 | 13.7 ms | **1.00** | 32.0 % | `s1-180hz-v2` |
| Final, 120 FPS mode | 93.1 | 71.7 | 13.5 ms | **1.00** | 31.6 % | `s1-240hz-v2` |
| Final, uncapped | 95.2 | 73.4 | 13.2 ms | **1.00** | — | `s1-unbounded-timing` |
| Final, 120 FPS mode, opening cutscene | 118.7 | 59.7 | 13.1 ms | **1.00** | — | `cs1-240` |

The uncorrected game speed for the baseline uncapped row is inferred (52.7 FPS ÷ 30); it was not measured, the
metric was added later. VRAM peak in all S1 runs: 1.2–1.6 GiB of 16 GiB.

## Per-frame backend counters (uncapped, before → after fence pool)
| Per frame | before | after |
|---|---|---|
| command-buffer submissions | 10.7 | 6.5 |
| vkCreateFence time | 5.2 ms | 0 (reused) |
| vkDestroyFence time | 5.9 ms | 0 |
| vkResetFences time | — | 0.003 ms |
| real GPU fence waits | 0.002 ms | — |
| guest sync waits (GPU thread) | 3.7 ms | 0.75 ms |

## Not benchmarked
Open world, Kakariko, Korok Forest, combat, inventory/map (no saves there); 1440p/4K output; resolution scale > 1×;
the official Flatpak binary (it has no frame log; the branch's first commit adds only observational logging).
