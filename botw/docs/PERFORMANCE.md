# Performance analysis

Scenario S1 (Shrine of Resurrection, idle, 1080p, res scale 1×, BotW 1.6.0), `botw/scripts/bench_s1.sh <mode> <label>`,
statistics after a 90 s warm-up. Machine in `BASELINE.md`. Raw data in `botw/results/<label>/`.
Run-to-run spread is ±2–4 FPS median on this desktop (other applications were running during some runs); A/B
comparisons therefore use the same binary with an environment switch, alternating order, two runs per side.

## Runtime V2 (2026-10-15/16): CPU V2, GPU V2, HLE V2 — final matrix

Scene W2, `BOTWPC_PRESENT_IMMEDIATE=1`, DLSS off, 120 FPS mode, branch `runtime-v2` (`6ad74a0fe`), one binary,
configurations chosen at startup. `bench/v2/matrix.ps1`: 4 rounds, each round runs the five configurations back to back
(170 s, statistics after 90 s), runs flagged by `run_check.py` repeated (2 Game Overs). Guest CPU = CPU time of all
`HLE.GuestThread.*` threads over 105..145 s per frame (`GuestSampler --cputime`). Paired differences per round, 95 %
t interval (3 df). **The machine was not idle:** the user's Minecraft (Modrinth) and League client were running; the
legacy baseline drifted from 70.9 to 78.1 FPS over the session as their load changed, which the per-round pairing
absorbs but which makes absolute values lower than round 5 (84–86 FPS) and not comparable to it.

| Metric | Legacy | CPU V2 | GPU V2 | CPU V2 + GPU V2 | Full Runtime V2 (+ HLE V2) |
|---|---|---|---|---|---|
| Average FPS | 73.51 | 78.65 | 74.59 | 80.46 | **81.42** |
| Δ average FPS (95 % CI) | — | +5.14 (+1.42 .. +8.85) | +1.08 (−1.75 .. +3.91) | +6.95 (+3.28 .. +10.62) | **+7.91 (+6.17 .. +9.66), +10.8 %** |
| Median FPS | 74.62 | 80.17 | 75.88 | 82.45 | 82.77 (+8.15, +5.95 .. +10.36) |
| 1 % low | 48.6 | 50.1 | 48.1 | 48.5 | 53.6 (+4.95, −3.0 .. +12.9, n.s.) |
| Frame time (1000 / avg) | 13.63 ms | 12.75 ms | 13.45 ms | 12.47 ms | **12.30 ms (−1.33, −1.69 .. −0.96)** |
| P95 frame time | 15.88 ms | 15.13 ms | 15.84 ms | 15.00 ms | 14.44 ms (−1.44, −2.08 .. −0.81) |
| P99 frame time | 18.42 ms | 17.48 ms | 18.44 ms | 17.37 ms | 16.60 ms (−1.82, −3.85 .. +0.21, n.s.) |
| Guest CPU per frame | 30.15 ms | 25.28 ms | 30.10 ms | 25.31 ms | **25.06 ms (−5.09, −5.63 .. −4.55), −16.9 %** |
| GPU command thread per frame | 10.64 ms | 10.42 ms | 10.00 ms (−0.64, −0.93 .. −0.34) | 9.73 ms | 9.72 ms (−0.92, −1.33 .. −0.50) |
| Guest readback wait | 0.47 ms | 1.11 ms | 0.38 ms | 1.13 ms | 0.99 ms (+0.52) |
| Game speed | 1.000 | 1.000 | 1.000 | 0.999 | 1.000 |

Reading the matrix:
- **CPU V2 is the main gain** (+5.1 FPS alone): the guest's own CPU time per frame falls by ~4.9 ms (−16 %), and the
  guest then waits longer for GPU readbacks (+0.64 ms) — the chain moves to the GPU command thread.
- **GPU V2 alone** (round-5 fast dispatch + GPU V2 fidelity fix) lowers the GPU thread by 0.64 ms but does not move the
  frame on its own (+1.1 FPS, n.s.), as expected when the guest leads; **with CPU V2 the two add up** (+6.9 FPS): the
  GPU thread saving reaches the frame once the guest is faster. They are not additive in isolation.
- **HLE V2** adds +0.96 FPS on top (full − CPU+GPU), within noise for a single difference; its in-run A/B showed no
  effect (HLE_SCHEDULER_V2.md).
- Memory V2 has no retained component (MEMORY_TRACKING_V2.md); "CPU V2 + GPU V2 + Memory V2" equals "CPU V2 + GPU V2".

Raw data: `bench/v2/fin-*`, `bench/v2/fin.txt` (local). Earlier CPU-only comparison with the round-1 CPU V2 mask
(`0x1000F`): +6.57 FPS (+2.48 .. +10.67), `CPU_V2_JIT.md`.

### Distance to 120 FPS after Runtime V2

| | |
|---|---|
| Full Runtime V2, this session (loaded machine) | 81.4 FPS = 12.30 ms |
| Same improvement applied to round 5's idle-machine legacy (84.1 FPS, 11.89 ms) | ≈ 10.6 ms (≈ 94 FPS); not measured on an idle machine this round |
| Target | 8.33 ms; **remaining ≈ 2.2–4.0 ms** |
| Critical path (causal, CPU V2) | GPU command thread first (78 % of its delay reaches the frame), guest second (+20 % guest time → +1.42 ms) |
| GPU command thread | ~9.7 ms of work per frame; idle ~15 % waiting for submissions |

120 FPS is **not** reached. Reaching it needs roughly −3 ms on the GPU command thread (structural change: batched
off-thread binding/state work, NEXT_STEPS.md) together with a further ~15–20 % cut of guest time (more JIT allocator
work); neither alone is enough, because each chain becomes the limit as soon as the other shrinks.

### DLSS Off vs Quality (full Runtime V2, interleaved, 3 + 3 runs, `bench/v2/dlss2-*`)

Round 5 had seen ~1.5 ms more GPU-command-thread time with DLSS on, from one unpaired run per mode. Controlled now
(same binary and session, alternating, `run_check.py`-validated; three Game Overs repeated; the background load was
lower by then: ~89 FPS):

| Metric | DLSS off | DLSS Quality | Change (95 % CI) |
|---|---|---|---|
| Average FPS | 89.34 | 89.57 | +0.23 (−4.59 .. +5.05) |
| GPU command thread per frame | 8.86 ms | 8.84 ms | **−0.02 (−0.49 .. +0.46)** |
| Guest readback wait | 1.03 ms | 0.98 ms | −0.04 (−0.22 .. +0.14) |
| Host GPU busy per frame | 3.72 ms | 4.33 ms | +0.61 (the DLSS evaluation) |
| P95 / P99 | 12.98 / 14.36 ms | 12.94 / 14.27 ms | n.s. |

The GPU-thread cost does not reproduce: DLSS's per-draw camera capture is a cheap early-return check, and the
evaluation runs on the host GPU, which is ~45 % busy in W2. No change was needed in the shared code. Image check:
`DLSS: active` in every Quality run's log; screenshots normal. One Runtime V2 + DLSS Quality run (`dlss-quality-2`)
crashed with an unhandled .NET exception after DLSS activation (no managed stack recorded: KNOWN_ISSUES #43); it did not
recur in three later Quality runs with crash dumps enabled, nor in the 20 non-DLSS matrix runs.

## Round 5 (2026-10-15): optimized GPU front end

Scene W2 as in round 4, `BOTWPC_PRESENT_IMMEDIATE=1` (the display was at 60 Hz during this round), branch
`gpu-frontend`. Full write-up: `GPU_FRONTEND_REWRITE.md` (components, decisions), `GPU_FRONTEND_ARCHITECTURE.md` (cost
map, critical path), `GPU_FRONTEND_DIFFERENTIAL_TESTS.md`, `MAXWELL_COMMAND_ANALYSIS.md`.

| | Legacy front end | Optimized front end | Change (95 % CI) |
|---|---|---|---|
| Average FPS (4 + 4 interleaved runs, same binary) | 84.06 | 86.23 | +2.17 (+0.50 .. +3.84) |
| Median FPS | 85.44 | 87.71 | +2.27 (+1.15 .. +3.39) |
| 1 % low / P95 / P99 | 59.95 / 13.85 / 15.46 ms | 59.03 / 13.44 / 15.12 ms | not significant |
| GPU command thread per frame | 9.48 ms | 8.72 ms | −0.77 (−1.25 .. −0.29) |
| Decode + register writes (region timer) | 2.20 ms | 1.62 ms | −0.59 |
| In-run A/B (`fe-ab4-both`) | | | +1.61 FPS (+1.00 .. +2.22), GPU thread −0.41 ms |
| Game speed, CPU cores, memory | 1.000, 4.86, 4.9 GB | 1.000, 4.84, 5.0 GB | no change |

Critical path after the change (causal delays, optimized, 89 FPS): GPU command thread +1.5 ms → frame +0.94 ms (63 %
transfer, was 77 %); render thread +1.5 ms → +0.18 ms (≈1.3 ms slack).

Correctness: runtime differential verification 0 mismatches (698 M register writes; final build 813 M, see
`GPU_FRONTEND_DIFFERENTIAL_TESTS.md`), unit tests incl. 8 M captured game writes; camera-trajectory regression at 30 FPS
identical to legacy to 0.01 m / 0.1°, 60/90/120 modes within 0.6 % net travel, game speed 1.000; DLSS Quality active.

Measurement pitfalls found: `GuestSampler --managed/--disasm` freezes the process for ~50 s while resolving symbols,
which makes that run's `game_speed` meaningless (0.43–0.69); a display at 60 Hz caps every windowed run at 60 FPS
(`run_check.py` flags it); a combined A/B whose "off" side was slower than any legacy run (+5.9 FPS) did not
reproduce and is not used.

## Round 4 (2026-10-15): the player's save, host-level profile, emulator architecture

**Scenario W2.** The player's own progressed save (played 2026-10-15 05:20–05:50; snapshot
`save-backup-player/save-20261015-0555`, restored into the disposable `wintest` before every run): outdoor eastern
Great Plateau at 19:30, forest at sunset, Link standing still. Windows 11, Ryzen 7 7700, RTX 5070 Ti, 1920×1080,
DLSS off, 120 FPS mode (240 Hz VBlank), BotW 1.6.0 + both DLC. Heavier than W1: **75.7 FPS** on the build of
`3eb80e7f8` (GPU thread 10.3 ms, GPU 52 % busy). Hazards (KNOWN_ISSUES #31): the clock moves into night during a run
and a Keese can kill the low-health Link (Game Over); comparisons are therefore in-run A/B (`BOTWPC_AB_PERIOD=10`,
330 s), analysed with `ab_phases.py` and `ab_counters.py` (paired GPU-thread time), which stop at a Game Over drop.
Raw data: `bench/r4/` (local).

### Tools added this round
| Tool | What it answers |
|---|---|
| `GuestSampler --managed` | self time per .NET method on any emulator thread (ClrMD resolves JIT addresses) |
| `GuestSampler --stacks <thread>` + `stack_profile.py` | inclusive time, callers and callees (runtime unwinder via ClrMD, 100 Hz) |
| `GuestSampler --disasm <thread>` | hottest methods disassembled (Iced) with samples per instruction |
| `AbPhase.Switch` / `BOTWPC_AB_SWITCHES` | any switch in the in-run A/B without a clock read per call |
| `ab_counters.py`, `ab_cut.py` | paired GPU-thread / sync-wait time per A/B period; Game Over cut |
| `BOTWPC_SYNC_LOG` guest call chains | the game functions (U-King.nss offsets) whose access waited for the GPU |
| `BOTWPC_SPLIT_PROBE` | would guest macros read stale registers if decoding ran ahead on another thread |
| `BOTWPC_CAMERA_LOG` + `camera_track.py` + `regress.input` | frame-rate regression from the camera trajectory |

### Where the GPU command thread's time goes (W2, `r4-sample2`, `r4-stacks`, `r4-diag2`)
Busy share of `GPU.MainThread` (it waits for the next submission ~22 % of the time): `DrawManager.Draw` 57.7 %
inclusive (almost all draws come through HLE draw macros); buffer binding commit 22.6 %; GPFIFO decode + method
dispatch 19.2 % self (`ProcessImpl`, `Send`; ~8.7 ns per command word, ~250 000 words/frame); texture binding commit
10.1 %; shader-state updates 6.2 % (of which byte compare of the shader binary 2 %); buffer range-list searches
4.5 %; `VirtualProtect` reprotects 3.3–4.7 %. Exclusive timers (`BOTWPC_PROFILE=1`): decode + register writes 2.25,
macros 0.86, register callbacks 1.29, draw state 2.09, texture commit 1.10, buffer commit 2.51 ms per frame
(~5 000 draws, ~14 000 macro calls). No single hotspot.

Measured and ruled out as causes: the threaded GAL queue's per-command atomics (≤ 1–2 % of the GPU thread, not the
false-sharing bottleneck it looked like in the code); guest page-fault handling (0.1–0.3 % of the guest threads,
~120 faults/frame — bloodborne_pc's +22 % from fault batching came from ~1 600/frame); GC (0.45 MiB/frame).

### Which thread is critical (causal delays, in-run A/B, current build)
| Delay | Frame time | FPS (95 % CI) | Run |
|---|---|---|---|
| GPU command thread +1.47 ms | **+1.14 ms** (77 % transfer) | −8.06 (−8.21 .. −7.91) | `r4ab-gpu1500` |
| render thread +1.5 ms | +0.11 ms | −0.86 (−1.30 .. −0.42) | `r4ab-ren1500` |

The GPU command thread is the limit; the render thread (~10.3 ms of exclusive work on W2) has ~1.4 ms slack, so up
to about 1.4 ms can come off the GPU thread before the render thread takes over.

### Readback dependencies, by game function (`BOTWPC_SYNC_LOG`, guest frame-pointer chains, 1.6.0)
| Resource read back | Thread | Guest call chain (U-King.nss offsets, innermost caller first) |
|---|---|---|
| 8×8 R32G32B32A32F (×2), 16×1 and 16×3 R16G16B16A16F textures (exposure/luminance statistics) | worker 53 (also 54) | `0x17f5654 ← 0x13ea518 ← 0x12bbbbc ← 0x12bf910 …` |
| 16×1 R16G16B16A16F | main thread 46 | `0x1459af4 ← 0x1826ca8 ← 0x17fd424 ← 0x12bbbbc …` |
| buffer `…92f000+2000`, `…95c000+1f000` | worker 53 | `0x1d6e30c ← 0x1d6ddac ← 0x1d57664 ← 0x1d74620 ← 0x14f8dbc …` |
| buffer `…956000+6000` | main thread 46 | `0x1d5cc0c ← 0x1d5abb4 ← 0x1d5b5c4 …` |

Ghidra on the relocated module (local dump, never committed): `0x13e8a20` (7 KB) renders one view — it walks the
views' 0x400-byte records, calls renderer objects through vtable slot +0x38 and builds pass descriptors — and the
read of last frame's luminance results happens below it; `0x1d6dde0` sets up a draw's uniform blocks (binder called
with slot, GPU address, 16, size) and the waiting buffer access is in its callee. The waits total ~0.3–0.4 ms per
frame; what makes them matter is that the producers are near the end of the frame's command stream (critical path
below). Exact data is kept: no stale readback (contrast with Cemu, ARCHITECTURE_COMPARISON.md).

### Changes kept (in-run A/B, W2)
| Change | FPS (95 % CI) | GPU thread ms/frame (95 % CI) | Commit |
|---|---|---|---|
| binding memo: skip unchanged vertex/index/uniform sets | +0.76 (−0.33 .. +1.84) | **−0.20 (−0.33 .. −0.07)** | `f7449f0b5` |
| page table for buffer lookups (yuzu/Eden algorithm) | **+1.12 (+0.40 .. +1.85)** | **−0.32 (−0.41 .. −0.23)** | `d817adf00` |

### Architecture prototypes, measured and rejected
- **Two-stage GPU front end** (bbport's design adapted to Ryujinx; `botw/experiments/gpfifo-two-stage-split.patch`).
  Feasibility was measured first: `BOTWPC_SPLIT_PROBE` saw 688 818 guest-macro register reads with no stale value in
  gameplay (only the Falcon04 scratch register at boot, mirrored). The prototype — `GPU.Decode` fetching, decoding and
  running guest macros against its own register copy, the GPU thread executing compact packets from a 16 MiB ring
  published per GPFIFO entry, `Entry1Sync.Wait` entries draining the GPU thread before they are fetched — ran
  correctly (identical image, game speed 1.000) but **−1.30 FPS** (95 % CI −1.50 .. −1.10). Stage timers: the GPU
  thread spent 9.0 ms/frame executing packets, the same as decoding and executing everything before, while
  `GPU.Decode` spent 2.5 ms/frame: the few ns of work per command word cost as much again to ship across cores and
  dispatch. ~46 sync drains per frame also serialize the stages. Together with the per-buffer variant of round 3
  (−8.66 FPS), this closes the thread-split direction for Ryujinx's front end at this granularity.
- Per-slot uniform dirty state (yuzu/Eden) and batched reprotection (Eden #3288): no effect (OPTIMIZATIONS.md).

### Frame-rate regression (`regress.input`, camera trajectory, current build)
30 FPS is deterministic (two runs identical to 0.01 m). At 60 FPS and the 120 mode (~85 FPS): walking 2 s 10.70 vs
10.68–10.70 m, throw 1.14 m in all, **Link's net travel over the scenario within 0.5 %** (18.56 m at 30, 18.47 at 60,
18.54/18.59 at ~85), game speed 0.997–1.001. Small pre-existing differences: camera turn 1.6–3 % smaller per second
and standing-jump camera peak ~0.03 m lower at high frame rate (KNOWN_ISSUES #32). One run with this round's changes
switched off deviated by 3 m because a Keese knocked Link back (screenshots) — the scene hazard, not timing.
Not testable from this save: dragons (FPS++ issues #544/#595), horse, glide, swim, Divine Beasts.

### Before / after this round (old build `3eb80e7f8` vs new `f83ca0316`, interleaved, 4 runs each, W2)
Separate binaries, so runs alternate old/new in one session (`bench/r4/r4-final-*`, 170 s, statistics after 90 s);
differences are paired per round, 95 % t interval over 4 pairs. Every run checked: one log error (the usual syncpoint
wait while loading), game speed 1.000, no Game Over.

| Metric | Before | After | Change (95 % CI) |
|---|---|---|---|
| Average FPS | 85.16 (84.81..85.75) | **87.01** (86.21..87.96) | **+1.85 (+0.13 .. +3.57), +2.2 %** |
| Median FPS | 86.17 | 88.26 | +2.10 (+0.26 .. +3.94) |
| 1 % low | 63.89 | 65.11 | +1.22 (−1.67 .. +4.12), not significant |
| Frame time p95 | 13.32 ms | 13.14 ms | −0.18 (−0.57 .. +0.21), not significant |
| Frame time p99 | 14.94 ms | 14.67 ms | −0.27 (−0.76 .. +0.22), not significant |
| GPU command thread (`GpuFifoMs`) | 9.38 ms | **8.95 ms** | **−0.42 (−0.73 .. −0.12), −4.5 %** |
| Readback sync wait (`SyncWaitMs`) | 0.52 ms | 0.37 ms | −0.15 (−0.30 .. +0.01) |
| GPU execution (`GpuBusyMs`) | 3.72 ms | 3.74 ms | unchanged |
| CPU use / working set | 4.6 cores / 7.97 GB | 4.6 cores / 7.95 GB | unchanged |
| Game speed | 1.000 | 1.000 | — |

The first W2 baseline of the morning (75.7 FPS, `r4-base-1`) is not comparable: the area had just been visited for
the first time and its shader/pipeline caches were still being filled; all later comparisons are within one session.

### Distance to 120 FPS on W2
After this round W2 runs at ~87 FPS = 11.5 ms; 120 FPS needs 8.33 ms, i.e. **−3.2 ms per frame**. The chain is
GPU command thread (8.95 ms, 77 % of any change reaches the frame) → render-thread submission of the readback producers
→ GPU execution → the game's readback and the rest of its frame (~2–3 ms of guest code). The render thread
(~10.3 ms of work, profiled) has ~1.4 ms of slack, so the GPU thread can usefully lose ~1.4 ms on its own (≈ −1.1 ms
of frame time, ~96 FPS); beyond that the render thread and the guest tail must shrink together with it. Every
remaining single-thread GPU candidate measured or estimated this round is 0.1–0.3 ms, and both thread-split designs
lost more to communication than they moved. **120 FPS on W2 is not reachable with the measured levers**; the
realistic next ceiling is ~92–96 FPS from several more small single-thread reductions (NEXT_STEPS.md). Ryujinx's
architecture still costs time per command word and per draw (≈ 1.8 µs per draw on the GPU thread), but no
alternative architecture examined (yuzu/Eden, Cemu, bbport) offers a transferable design that removes it without
either rewriting the GPU front end wholesale or weakening synchronization.

## Architecture round (2026-10-15): where the frame goes across threads, pipelining

W1 (PERFORMANCE.md below), **DLSS off**, Windows. Tools added this round: `ScopeProfile` (exclusive time per region),
`GuestSampler` + `guest_profile.py` (guest CPU), `critical_path.py` / `thread_gantt.py` (timeline), `CausalDelay`
(causal profiling), `AbPhase` + `ab_phases.py` (A/B inside one run). Raw data in `bench/r3*`.

### Measurement method
- **A/B inside one run** (`BOTWPC_AB_PERIOD=10`, 330 s, `ab_phases.py`): the switch is on in every second 10 s period
  and each "on" period is compared with its two neighbours. W1 is not identical between runs (camera framing, Link in
  view or not, light and weather differ in the screenshots), so separate runs of one configuration spread by several
  FPS; paired periods inside one run gave 95 % intervals of about ±0.5 FPS from a single run.
- **Bug found in the old method:** `bench_win.ps1` cleared only a fixed list of variables, so a `-SetEnv` switch
  stayed set for the following runs of the same session (fixed in `694a76230`; each run now writes `env.txt`).
  Affected and discarded: `r3c-ren1500-*`, all second-round `r3c-*` runs, `r3ab-ren1500`, `r3ab-bmap`, `r3-prof2`,
  `r3-wake` (ran with leftover delays). Earlier rounds: see KNOWN_ISSUES.md #29.
- Runs while another application used the GPU/CPU (`r3ab-pipe3`, `r3ab-bmap2`, `r3ab-ren1500b`: a game was running,
  A periods 49–72 FPS) are discarded. The repeated runs (`r3ab-pipe3c`, `r3ab-bmap3`, `r3ab-ren1500c`, `r3ab-ren3000`)
  ran with only a browser playing a video in the background (~1.7 cores, constant over the run, so it cancels in the
  paired comparison; it lowers the A periods of the first two to ~85 FPS).

### 1. Profiler correction: the old GPU-thread breakdown double counted
The previous timers were inclusive and nested: a macro call contains the HLE draw it performs, a register callback
contains `DrawManager.Draw`. Corrected exclusive time per frame (`r3-prof3`, `BOTWPC_PROFILE=1`, timer cost 16 ns per
region + 37 ns in its parent, calibrated after warm-up and subtracted):

| GPU command thread (`GPU.MainThread`) | ms | | Render thread (`GUI.RenderThread`) | ms |
|---|---|---|---|---|
| buffer binding commit | 2.33 | | command loop + GAL commands not listed | 2.40 |
| decode + register writes (`Process`) | 1.89 | | descriptor updates | 1.38 |
| other draw-state groups | 1.78 | | command buffer submission | 0.94 |
| register callbacks | 1.05 | | buffer data uploads | 0.89 |
| texture binding commit | 0.88 | | render pass begin | 0.83 |
| macro execution | 0.71 | | pipeline lookup/creation | 0.80 |
| fetch / outside `Process` | 0.26 | | draw call itself | 0.79 |
| UBO flush, backend draw, I2M | 0.36 | | present | 0.45 |
| | | | buffer handle mapping (`BufferMap`) | 0.37 |
| | | | barriers | 0.13 |
| **sum** | **9.25** | | **sum** | **8.97** |

Check: the corrected GPU-thread sum (9.3 ms in `r3-prof-scope`) matches the unprofiled `GpuFifoMs` (9.0 ms,
`r3-plain-off`) within 4 %. Pure command-word parsing (`BOTWPC_PARSE_PROBE`) is 0.77 ms of the 1.89. The old
report's "macros ~1.3 ms, decode ~2.4 ms" were derived from nested timers: macros are 7.3 ms inclusive (HLE draws)
but 0.7 ms exclusive. The two threads do almost the same amount of work per frame.

### 2. Cross-thread critical path (timeline `r3-timeline`, 642 frames, 12.2 ms median)
- `critical_path.py`: the GPU thread reaches the last read-back producer 8.8 ms after the submission; only 0.26 ms of
  its work comes after it. The render thread submits that command buffer 0.20 ms later. The guest's sync waits now
  total ~0.4 ms per frame (strict readback sync works).
- `thread_gantt.py`: the game's main thread (46) is busy 0–7 ms, waits for its workers 7–11 ms, then finishes the
  frame; worker 53 is busy ~4–11 ms. The GPU thread is 100 % busy 0–7 ms and finishes the frame's commands at ~9.5 ms.
- Guest wake-up latency (`SchedWake`, `r3-wake`, measured under a leftover delay): 4–7 µs per wake-up,
  0.15–0.3 ms per frame per busy guest thread: not a lever.

### 3. Causal tests: which thread is on the critical path?
Busy delay of 1.5 ms per frame on the GPU thread, in-run A/B (`r3ab-gpu1500`, 11 pairs): **frame time +1.01 ms**
(90.1 → 82.6 FPS, 95 % CI −8.05 .. −6.89 FPS). Separate clean runs: +1.5 ms → +0.77 ms, +3.0 ms → +2.1 ms
(`r3c-*-1`). So the GPU thread has only ~0.5 ms of slack: about two thirds of any change in its time reaches the
frame time.

Render thread (delay once per present, in-run A/B, 11 pairs each):

| Delay | Frame time | FPS (95 % CI) | Run |
|---|---|---|---|
| +1.5 ms | +0.04 ms | −0.47 (−1.02 .. +0.09), not significant | `r3ab-ren1500c` |
| +3.0 ms | +0.63 ms | −4.95 (−5.22 .. −4.68) | `r3ab-ren3000` |

So the render thread has **~2.3 ms of slack** (its 9.0 ms of work in an 11.1 ms frame) and is **not** on the critical
path; the earlier expectation that it is the next limit was wrong. The GPU command thread (~0.5 ms slack) is.

### 4. Guest CPU (`r3-sample`, external sampler at ~600 Hz, 40 s)
| Thread | running | JIT guest code | managed + runtime | stubs |
|---|---|---|---|---|
| main (46) | 58 % | 54 % | 4 % | 0.1 % |
| worker 53 | 61 % | 57 % | 3.5 % | 0.0 % |
| worker 54 | 43 % | 40 % | 3 % | — |

The guest profile is flat: the main thread needs 167 functions for 50 % of its guest time, the top function is 1.5 %.
All 70 491 functions come from the PPTC cache at tier 1 (none re-translated), dispatch stubs are 0.1 %, HLE/managed
code ~4 %. Making the guest faster therefore needs general ARMeilleure code quality, not a hotspot fix.

### 5. Pipelined GPU command processing: prototype
Design reference: bbport's two-stage draw pipeline (PERFORMANCE_UPSTREAM_RESEARCH.md). Measured split for Ryujinx:
stage A (decode, register file, macros) ≈ 2.6–2.9 ms, stage B (callbacks, draw state, bindings, caches) ≈ 6.4 ms.
The full bbport-style split (register state handed over per draw, ~6 state write-backs to mirror, HLE indirect
draws as drain points) was **not** started: first a minimal prototype had to show that a thread hand-off at this
granularity costs less than the work it moves. (When this was decided the render thread was assumed to cap the gain
at ~0.3 ms; the render-thread causal test above later showed ~2.3 ms of slack, so the cap is the guest chain, not
the render thread.)

Minimal prototype instead (`BOTWPC_GPFIFO_PIPELINE=1`, `GPFifoDevice`): a `GPU.Decode` thread decodes command
buffers into immutable `DecodedCommand` lists (method calls, inline-data and UBO spans) through a bounded queue
(256 buffers); the GPU thread executes them in order. Everything with side effects stays on the GPU thread.

| Version | Result (in-run A/B) | Cause |
|---|---|---|
| v1: decode prefetched buffers, hand non-prefetched ones back | **19 FPS** vs 91 (`r3ab-pipe`) | BotW submits ~4 700 GPFIFO entries per frame and almost all are *not* prefetched (after an `Entry1Sync.Wait` entry nothing in the submission is); every hand-back cost two thread wake-ups |
| v2: + signal only a sleeping side | 19 FPS (`r3ab-pipe2`) | same hand-back ping-pong |
| v3: decode non-prefetched buffers too (unflushed read + snapshot), GPU thread fetches as before and compares the words before using the decoded list; on a mismatch the stage is switched off and the queue is executed from the GPU thread's fetch | **−8.66 FPS** (95 % CI −9.74 .. −7.58), frame time **+1.34 ms**, 1 % low −4.2, p99 +1.3 ms (`r3ab-pipe3c`, 10 pairs) | moves only the parse (0.77 ms) but adds a second read of every command buffer, a word compare, a decoded-list pass and cross-core traffic for ~4 700 buffers per frame on the critical GPU thread: the hand-off costs more than the work moved. Reverted |

No snapshot mismatch occurred in ~16 "on" periods (the fallback never ran).

### 6. Render thread: lock-free buffer handle table (prototype)
`BufferMap` maps threaded buffer handles to backend handles with a lock and a dictionary lookup per bound buffer
(0.37 ms per frame). Prototype: a paged table indexed by the (sequential) handle, written and read only on the render
thread; other threads keep the locked dictionary. Always on (`r3chk-bmap`) it runs normally (81 FPS, no errors; that
single run only shows it works). Clean in-run A/B (`r3ab-bmap3`, 11 pairs): **+0.14 FPS (95 % CI −0.24 .. +0.53),
frame time −0.01 ms: no effect**, as expected for a thread with ~2.3 ms of slack. Reverted.
Neither prototype is in the build; the patches are in git history (`fc4d203ae`, `botw/experiments/`).

### Remaining critical path and distance to 120 FPS
Clean A periods of the in-run A/Bs: **90–91 FPS (10.9–11.1 ms)**. 120 FPS needs 8.33 ms: **−2.6 to −2.8 ms per
frame (−24 %)**. The critical chain per frame: GPU command thread to the last read-back producer ≈ 8.8 ms (≈ 9.2 ms of
work, ~0.5 ms slack) → render-thread record/submit ≈ 0.2 ms → GPU execution of the last chunk + wake ≈ 1.5–2 ms →
guest tail ≈ 1 ms. The render thread is off that chain (~2.3 ms slack) until it is ~2 ms faster than today's frame.
So the lever is the GPU command thread: about two thirds of each ms removed from it reaches the frame time, until
the guest's own chain (main thread ≈ 7 ms of JIT code, then worker 53) becomes the limit; reaching 8.33 ms needs
roughly −3.5 to −4 ms of GPU-thread time **and** a shorter guest chain. No single measured change does that.
Measured levers: a fine-grained decode stage on its own thread costs +1.3 ms (hand-off per command buffer is more
expensive than parsing it); render-thread work (BufferMap, descriptors, GAL loop) has no frame-time effect at
present; wake latency ≤ 0.2 ms; guest code: flat profile, needs JIT code quality work. A worthwhile GPU-thread split
must move the expensive stage-B work (buffer/texture binding commits 3.2 ms, draw-state 1.8 ms, callbacks 1.1 ms)
with hand-offs per batch of draws, not per command buffer.

## DLSS round on Windows (2026-10-14): scenario W1, baseline, critical path, optimizations

### Scenario W1 (outdoor, reproducible)
Copy of the player's progressed save (Great Plateau, next to the Shrine of Resurrection exit, dense grass and trees),
restored before **every** run into a disposable data directory; `continue.input` loads it, Link then stands still with
a fixed camera. `botw/scripts/bench_win.ps1` (standalone `--botw` mode, fullscreen 1920×1080 on the 165 Hz display,
Mailbox presentation, 120 FPS mode = custom 240 Hz VBlank, BotW 1.6.0 + both DLC), statistics after 90 s for ~70 s,
`nvidia-smi` and process CPU in the same window, one screenshot per run to verify the scene. `bench_table.py` averages
runs (min..max in brackets). Windows 11, Ryzen 7 7700, RTX 5070 Ti (616.92), VBS/HVCI enabled. No profiling switches
in the comparison runs; profiling runs are separate. The user's "~97 FPS with DLSS" could not be pinned to a scene:
W1 is a heavy outdoor view (84–86 FPS with DLSS at baseline); the 20-minute scripted play (`bench/win-play20`, mostly
shrine interior) measured 104 FPS median.

### Baseline: DLSS modes (3 runs each, `bench/base-*`)
Every mode verified active in the log with its own preset (`feature created 1600x900 → 1920x1080, quality 2/1/0/5`).
BotW renders the scene at 1600×900 in every mode; DLSS replaces only the 900p → 1080p upscale, so the modes differ in
the NGX model (SDK 310 defaults: K for DLAA/Quality/Balanced, M for Performance), not in render resolution. There is
no "native 1080p" mode without a resolution mod (DLSS.md).

| Mode | avg FPS | median | 1 % low | p99 ms | GPU thread ms | GPU busy ms | sync wait ms | GPU util | VRAM MiB |
|---|---|---|---|---|---|---|---|---|---|
| DLSS off (game upscale) | 89.6 (88.8..90.6) | 91.2 | 66.2 | 14.34 | 8.39 | 3.59 | 0.46 | 37 % | 3481 |
| Quality (K) | 84.5 (84.3..85.0) | 85.4 | 61.6 | 15.12 | 8.64 | 4.11 | 0.89 | 39 % | 3687 |
| Balanced (K) | 84.8 (84.2..85.0) | 85.5 | 63.0 | 15.04 | 8.60 | 4.14 | 0.87 | 40 % | 3688 |
| Performance (M) | 86.2 (85.9..86.5) | 87.2 | 64.7 | 14.71 | 8.41 | 4.16 | 0.83 | 40 % | 3685 |
| DLAA preset (K) | 86.4 (85.7..86.9) | 87.3 | 65.5 | 14.61 | 8.37 | 4.20 | 0.81 | 41 % | 3668 |

Game speed 1.000 in all runs, ~4.4–4.5 CPU cores used, `vkQueueSubmit` 0.3 ms, present 0.36 ms, acquire 0.01 ms per
frame. Interpretation: all modes within ~2 FPS of each other and the GPU ~40 % busy → **not GPU-bound**; DLSS costs
0.5–0.6 ms of GPU time (busy 3.6 → 4.1–4.2 ms), but the FPS loss (−4 to −6 %) followed the **sync wait doubling**
(0.46 → 0.89 ms): the GPU work that the guest waits for now also contained the DLSS passes (below).

### Critical path (timeline, `bench/prof-q-both`, `prof2-q`; `timeline_frame.py`, profiling overhead included)
Median frame, times relative to the guest queueing frame N (frame ≈ 12.2–12.6 ms with profiling):

| ms | Thread | Event |
|---|---|---|
| −0.2 | HLE.OsThread.4 | the guest submits **all** GPU work of frame N in 2 GPFIFO submissions at the end of its frame |
| −0.2 → 10.7 | GPU.MainThread | processes frame N's commands (8.4 ms without profiling) |
| 0 → 6.8 | main thread (HLE.GuestThread.46) | computes frame N+1 |
| 6.8 → 10.0–10.4 | main thread | waits for worker jobs |
| 8.8 → 10.4 | workers 53/54 | **read back GPU-computed textures of frame N** (sync waits) |
| 11.2 → 11.5 | main thread | its own readback, then finishes and queues frame N+1 |

`BOTWPC_SYNC_LOG=1` names the readbacks: two 8×8 R32G32B32A32F and 16×1 / 16×3 R16G16B16A16F textures plus a 4 KiB
buffer, read by the guest CPU every frame (most likely exposure/lighting statistics). The readback producers are near
the end of the frame's command stream, so the chain is **GPU-thread processing of frame N (8.4 ms) → readback → guest
tail of frame N+1 (≈ 2.6 ms) → next submission**; 8.4 + 2.6 ≈ 11.0 ms ≈ 90 FPS. The game's main thread also does
≈ 8.6 ms of its own CPU work per frame (6.8 before and ~1.8 after the wait, profiled), so even an infinitely fast
GPU thread would leave this scene at roughly 115 FPS: **120 FPS here needs both the GPU thread (−30 %) and the guest
CPU code to get faster.**

### GPU command thread breakdown (`BOTWPC_PROFILE=1`, `bench/prof2-q`, `prof3-q`; inflated by the timers)
| Part | ms/frame | Notes |
|---|---|---|
| draw state update (`StateUpdater.Update`) | 5.3–5.5 | 4 124 draws; buffer bindings 2.4, texture bindings 0.9, other state ~2.0 |
| macro execution outside draws | ~1.3 | 11 900 macro calls (HLE draws counted above) |
| register write callbacks | 1.1 | 14 250 |
| GPFIFO decode + register writes | ~2.4 | **229 000 command words per frame** (~10 ns each) |
| uniform-buffer write-back | 0.3 | 6 900 flushes |
| backend draw queueing | 0.2 | |
| inline-to-memory, reprotection, threaded-queue copies | 0.04 / 0.75 (all threads) / 0.15 | |

No single hotspot: reaching −30 % needs several independent reductions (see NEXT_STEPS.md).

### Frame-rate modes and game speed (final build, W1, DLSS Quality, `bench/mode-*`, `final-q-new`)
| Mode | avg FPS | 1 % low | p99 ms | game speed |
|---|---|---|---|---|
| 30 (Switch VSync) | 30.0 | 28.4 | 34.0 | 1.000 |
| 60 (120 Hz custom) | 60.0 | 55.1 | 17.3 | 1.000 |
| 90 (180 Hz) | 89.7 | 68.3 | 14.0 | 1.000 |
| 120 (240 Hz) | 88.2 | 66.0 | 14.4 | 1.000 (throughput-limited, see critical path) |

5-minute scripted play on the final build (`bench/play5-final`): 102 FPS avg, game speed 0.999, no new log errors,
screenshots normal (title, outdoors, inventory, map, Game Over, shrine, menus).

### Cross-platform scope of this round
| Change | Code | Windows | Linux | macOS / iPadOS (Metal/MoltenVK, no JIT) |
|---|---|---|---|---|
| strict readback sync | GPU front end (`Ryujinx.Graphics.Gpu`) | measured | same code, not re-measured | applies (backend-independent `CreateSync(strict)`) |
| redundant vertex/uniform bind skip | GPU front end | measured (within noise) | same code | applies |
| DLSS disocclusion fix | Vulkan backend, NVIDIA only | measured | needs the rebuilt `.so` | n/a (no DLSS on Apple; nothing else depends on it) |
| profiling (stage, sync-wait, SVC logs, timeline paths) | Common / Gpu / HLE | used | portable | portable |

No change adds an OS, GPU-vendor or graphics-API dependency to shared code; the only platform-specific additions are
the Windows NGX bridge build and Windows benchmark scripts.

### Optimizations (same binary, environment A/B, W1, DLSS Quality unless noted, 3 runs each, alternating)

| Change | avg FPS | median | 1 % low | sync wait ms | Result |
|---|---|---|---|---|---|
| baseline (`BOTWPC_STRICT_READBACK_SYNC=0`) | 85.9 (84.3..86.9) | 86.9 | 63.6 | 0.78 | |
| submit before DLSS passes (experiment) | 89.0 (88.7..89.3) | 90.3 | 66.2 | 0.39 | superseded, reverted |
| **strict sync for read-back textures** | **90.5 (90.3..90.9)** | **91.9** | **68.3** | **0.33** | **kept (+5.4 %, 1 % low +7.4 %)** |
| both | 90.0 (88.6..90.8) | 91.4 | 66.9 | 0.37 | no gain over strict alone |
| DLSS off, baseline | 89.1 (88.5..89.5) | 90.2 | 67.5 | 0.49 | |
| DLSS off, strict | 90.5 (89.6..91.7) | 91.8 | 68.1 | 0.32 | +1.6 % |
| strict + forced fast flush (#4667 mode) | 90.2 (90.0..90.2) | 91.4 | 62.8 | 0.31 | 1 % low worse, reverted |
| strict alone (repeat, same session as above) | 89.8 (89.6..90.1) | 91.1 | 67.0 | 0.30 | |

**Correction (2026-10-15):** the last two rows (`ab2-*`) are valid for their first pair only; runs 2–3 of "strict alone" also had fast flush forced (switch leak, KNOWN_ISSUES.md #29). The rows above them (`ab1-*`) set every switch explicitly and are valid.

**Strict sync for read-back textures** (`TextureGroupHandle.SignalModifying`): upstream already creates a sync when a
texture that is flushed often is unbound, but not strict, so it signalled only with its command buffer after all
later work of the frame (including the DLSS motion pass and evaluation) and the guest's readback had to interrupt the
backend to submit it. Strict submits right after the producing pass. With it, DLSS Quality runs as fast as DLSS off
did before (90.5 vs 89.6 FPS) — the DLSS throughput penalty in W1 is gone. Extra submits: 9 → ~12 per frame.

Also measured earlier in this round: skipping redundant vertex/uniform buffer binds (OPTIMIZATIONS.md #5, −1.3 % GPU
thread, FPS within noise).

**DLSS disocclusion fix cost** (DLSS.md; `bench/ab3-*`, 3 runs each, alternating; a higher background load from other
applications — browser ~1.5 cores, `explorer.exe` ~0.9 core — lowered all absolute numbers in this and the next table):

| | avg FPS | median | 1 % low | GPU busy ms | GPU thread ms |
|---|---|---|---|---|---|
| fix off (`BOTWPC_DLSS_DISOCCLUSION_MASK=0`, `BOTWPC_DLSS_REVERSED_DEPTH=0`) | 81.6 (80.0..83.8) | 83.9 | 50.6 | 4.40 | 9.21 |
| fix on (default) | 82.3 (80.8..84.8) | 83.9 | 54.0 | 4.41 | 9.23 |

**Correction (2026-10-15):** runs 2–3 of "fix on" ran with the fix off (switch leak, KNOWN_ISSUES.md #29), so this table compares off with off after the first pair; the cost of the fix is not established by it (the visual result is unaffected).

### Before / after this round (same session, alternating, 3 runs each, DLSS Quality, `bench/final-q-*`)
| Build | avg FPS | median | 1 % low | p95 ms | p99 ms | sync wait ms |
|---|---|---|---|---|---|---|
| before (`042c25905` binary, the packaged build) | 84.6 (79.4..87.5) | 85.6 | 60.6 | 13.64 | 15.55 | 0.77 |
| after (readback sync + DLSS disocclusion) | **88.2 (84.4..90.4)** | **89.1** | **66.0** | 12.69 | 14.40 | 0.37 |

+4.3 % average, +8.9 % 1 % low, p99 −1.15 ms; game speed 1.000 in both. In a quieter period earlier the same changes
measured 85.9 → 90.5 FPS (table above). 120 FPS was **not** reached in W1; see the critical path above and
NEXT_STEPS.md for what it would take. DLSS Frame Generation is not used: all numbers are rendered game frames.

## Profiling tools

| Tool | What it measures | How |
|---|---|---|
| `counters.csv` (always with `BOTWPC_FRAMELOG`) | per 750 ms window, written as **per frame** averages in `summary.json` | `GpuCounters` |
| `GpuFifoMs` | CPU wall time of `GPFifoDevice.DispatchCalls` on `GPU.MainThread` (command decoding + state + bindings) | Stopwatch |
| `GpuBusyMs` | **GPU time domain**: union of command-buffer execution intervals from Vulkan timestamps (TOP/BOTTOM_OF_PIPE) | `vkCmdWriteTimestamp` |
| `QueueSubmitMs`, `AcquireMs`, `PresentMs`, `CbWaitMs`, `SyncWaitMs`, `Fence*Ms` | render-thread Vulkan calls and waits | Stopwatch |
| `ShaderCompiles/Ms`, `PipelineCreates/Ms` | host program and `vkCreate*Pipelines` cost (shader-cache stutter) | Stopwatch |
| `Up{Direct,Mirror,Inline,Staging}[Bytes]` | which `BufferHolder.SetData` path each guest→host upload took | counts |
| `Gen0/1/2`, `AllocMiB`, `GcPauseMs` | managed GC activity | `GC` APIs |
| `BOTWPC_PROFILE=1` adds | `CommitGfx[Ms]` (per draw), `BufSyncs/Bytes/CopyMs` (threaded-queue copy), `RenderIdles/Ms`, `GalWakes/Ms`, `UpMs`, `BufLookupHits/Misses` | costs ~3–7 % FPS, off by default |
| `threads.json` | CPU % per thread (guest/JIT threads, GPU thread, render thread) from `/proc` | `threadstat.py` |
| `BOTWPC_PERF=<s>` | Linux `perf` sampling with JIT symbols (`perf.txt`); `perf` runs from `../.tools/perf-pkg` (Arch package unpacked locally, nothing installed) | needs `DOTNET_PerfMapEnabled=1`, `DOTNET_EnableWriteXorExecute=0` (set by `run.sh`) |

CPU stages on different threads overlap in time; per-frame milliseconds of different threads must not be added.
`GpuBusyMs` is GPU time and is not comparable with CPU stage times.

**Correction:** the earlier `dotnet-trace` analysis (in `OPTIMIZATIONS.md`, now corrected) attributed 25 % of the GPU
thread to copying into the threaded queue. The direct timer measures **0.19 ms/frame (≈2 %)** for that copy
(596 uploads, 1.4 MB per frame). The EventPipe sampler only stops threads at safe points and its self time on this
thread is all "UNMANAGED_CODE_TIME", so its attribution was unreliable; `perf` is used instead.

## Where a frame goes (uncapped, before the lookup cache, `prof-unb-base`, 93 FPS = 10.8 ms)

| Stage | ms / frame | Notes |
|---|---|---|
| GPU command thread busy (`GpuFifoMs`) | **9.2** | 3 940 draws/frame → 2.3 µs per draw |
| … of which `CommitGraphicsBindings` | 3.4 | buffer bindings + guest→host sync |
| … threaded-queue copies of buffer data | 0.19 | 1.4 MB/frame — *not* a bottleneck |
| render thread idle (waiting for commands) | 4.1 | it drains the queue on every draw |
| guest→host uploads on render thread | 0.67 | 145 direct-mapped writes, 449 `vkCmdUpdateBuffer`, 0 staging |
| `vkQueueSubmit` / acquire / present | 0.12 / 0.03 / 0.10 | |
| **GPU execution (`GpuBusyMs`)** | **3.9** | the GPU is ~37 % busy |
| GC pauses | 0.06 | not a factor |
| shader/pipeline creation (warm cache) | 0 | |

`perf` self time on `GPU.MainThread` (`prof-unb-perf`): `GPFifoProcessor.Send` 11 %, `BufferManager.CommitGraphicsBindings`
9 %, `BufferCache.GetBufferRange` 6.7 %, `BufferCache.CreateBufferAligned` 6.2 %, `GPFifoProcessor.Process` 5.7 %,
`UpdateVertexBufferState` 4 %, `SynchronizeBufferRange` 3.5 %, `CommitTextureBindings` 2.8 %; buffer lookups
(`CreateBufferAligned`/`GetBufferRange`/`FindOverlap`/lock) together ≈ 20 %. Inclusive: `DrawManager.Draw` 64 %,
`CommitBindings` 44 %.

Conclusion: the frame rate was bound by per-draw CPU work on the GPU command thread, not by the GPU, buffer copies,
GC, submission or presentation.

## After the buffer lookup cache (`ab2-*`)
The GPU thread drops to 8.3–8.9 ms/frame and to 74 % CPU, the render thread to 77 %. **No thread is saturated any more**,
but the frame rate rose only ~5 %: the limit is now a dependency chain between threads (guest frame N waits for GPU
work of an earlier frame → GPU thread → render thread → GPU → fence), not throughput of a single thread. Finding that
bubble (per-frame timeline of guest syncpoint waits vs. GPU-thread and render-thread activity) is the next step toward
120 FPS; see `NEXT_STEPS.md`.

## 120 FPS status
Not reached in S1: ~86 FPS uncapped on this (loaded) desktop with all optimizations, game speed 1.00. The 2D/3D
opening cutscene reached 118.7 FPS earlier. The display is 99.65 Hz, so at most ~100 rendered frames per second can be
shown on this monitor regardless.

## Cross-thread timeline (`BOTWPC_TIMELINE=<file>:<start s>:<duration s>`)
`Ryujinx.Common/BotwPc/TimelineTrace.cs` records complete events (thread, start, duration, guest frame number) and
writes Chrome-trace JSON for chrome://tracing or ui.perfetto.dev. Sources: every `GpuCounters` timed region (GPFIFO
processing, render-thread idle, queue submit, acquire/present, fence and host-sync waits, uploads with
`BOTWPC_PROFILE=1`), guest kernel waits (`WaitSynchronization`, condition variables, `WaitForAddress` with address,
timeout and the guest return-address chain, `ArbitrateLock`), SurfaceFlinger `Compose`, and `GuestFrameQueued`.
Regions on different threads overlap and are never summed. `scripts/timeline_report.py` summarises a trace and
finds the threads working while the game's main thread is blocked.

### Critical path at 120 FPS mode, S1 (`bench/timeline-120d`, with `BOTWPC_PROFILE=1`, 84 FPS, one 12.2 ms frame)
```
 0.0 ms  guest queues frame N; GPU.MainThread starts processing its GPFIFO          ─┐
 0–7.3   game main thread computes frame N+1 (overlaps)                              │ 8.7 ms GPU command
 7.3     main thread blocks in a job-wait loop (main+0x13a03d0: 1 ms timed waits on   │ processing = the
         worker completion events) …                                                 │ longest term
 8.6     GPFIFO for frame N done; render thread records/submits, presents at 9.2    ─┘
 9.95    worker thread waits on the host GPU fence of frame N (SyncWait, 0.8 ms)
11.4     job done → main thread resumes, finishes frame N+1 → queues it at 12.2
```
BotW keeps one frame in flight: a worker job waits for the GPU to finish frame N before frame N+1 can be submitted.
Frame time ≈ GPU-thread command processing (8.7 ms) + render/GPU/fence tail (~2 ms) + guest tail (~1.3 ms). The GPU
itself is busy only ~3.8 ms per frame. **Reaching 8.3 ms (120 FPS) needs the GPU command thread ~40 % faster**
(per-draw cost, `GPFifoProcessor.Send`, bindings) or the tail shortened; guest CPU work is not on the critical path
(the main thread idles ~3.7 ms per frame waiting). Unbounded and 120 FPS modes are limited the same way (91–94 FPS).

### Measurement pitfall found
Ryujinx's GDB stub (`enable_gdb_stub`, used for the UI-timing analysis) stayed enabled in `devdata/Config.json` after
a debugging run; with it, guest code ran ~1.65× slower per frame and S1 dropped to ~55 FPS for every build. Runs
between 00:05 and 01:53 on 2026-10-14 are affected for absolute performance (not for image or timing tests); the
DLSS cost below was re-measured after disabling it. Check `enable_gdb_stub` before benchmarking.

### DLSS cost (S1 uncapped, same session, stub off)
| | Median FPS | 1 % low | GPU busy ms/frame |
|---|---|---|---|
| DLSS off (game's upscale) | 93.4 | 68.1 | 3.80 |
| DLSS Quality | 88.3 | 66.3 | 4.23 |

## Outdoor scene (progressed save, Great Plateau, `botw/bench/outdoor.input`)
Same scripted sequence (walk, sprint, jump, camera pans, map, inventory), 1080p, no other game instance running
(`bench/out4-*`, before 03:00 on 2026-10-14):

| Mode | Median FPS | 1 % low | p99 ms | Game speed |
|---|---|---|---|---|
| 30 (stock) | 30.0 | 27.3 | 33.8 | 1.00 |
| 60 | 60.0 | 23.2 | 25.0 | 0.99 |
| 90 | 89.4 | 34.7 | 20.3 | 1.00 |
| 120 mode | 93.3 | 30.0 | 21.7 | 1.00 |

The outdoor 120-mode limit is the same as in the shrine (~93 FPS): the GPU command thread chain described above, not
the scene. The 1 % lows come from the map/inventory transitions inside the sequence.
**Invalid measurements:** an `out4-120dlss` row (90.8 FPS) was taken while DLSS was in fact inactive outdoors (fixed
later, DLSS.md). Later DLSS runs (`out5-120dlss` 76 FPS, `s1-120dlss-recheck` 68 FPS) ran while a second BotW instance was
being played on the same machine. Neither number is used; clean outdoor DLSS numbers are still to be taken.
