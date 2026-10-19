# GPU front end rewrite (round 5, 2026-10-15)

Goal: execute the same Maxwell command stream with fewer host CPU cycles on the GPU command thread, without changing
anything the guest can observe. Branch `gpu-frontend` (from `botw-pc` at `e31cf217e`). Scene W2 (the player's save,
`MAXWELL_COMMAND_ANALYSIS.md`), Windows, Ryzen 7 7700, RTX 5070 Ti, 1080p, DLSS off, 120 FPS mode.

## Outcome

- **Integrated:** a selectable optimized front end (`BOTWPC_GPU_FRONTEND=optimized` or settings `graphics.gpu_frontend`),
  a runtime differential verifier (`BOTWPC_GPU_VERIFY=1`) and one optimized component, **fast register dispatch**.
- **Default:** legacy. The optimized front end is faster in the measured scene and passed every check, but the promotion
  gates require gameplay coverage this save cannot give (dragons, horse, combat-heavy scenes, other platforms); see
  "Promotion".
- **Not rewritten:** state groups, draw path, binding commit, caches, GAL, Vulkan. The measurements below show where
  the time is and why a wholesale replacement of these would re-execute the same work.

## Method

1. **Workload:** per-frame counts of every kind of command word, register write, state group update and binding
   operation (`BOTWPC_FE_STATS`, `MAXWELL_COMMAND_ANALYSIS.md`).
2. **Cost map:** managed sampling profiles of the GPU thread at function level (self/inclusive), source line (IL offsets,
   `botw/tools/IlLines`) and instruction level (`GuestSampler --disasm`).
3. **Reference designs:** Eden (`21c6ee3`, GPL-3.0-or-later) and yuzu mainline headers (GPL-2.0-or-later) read for
   algorithms only; nothing copied (`ARCHITECTURE_COMPARISON.md`, licensing section).
4. **Each component:** an exactness argument (what legacy work it skips and why the result is identical), a runtime
   verifier check where the component predicts legacy results, an A/B switch, an in-run A/B, then keep or revert.

## Components

| Component | Switch | What it does | Verification | Measured (in-run A/B, W2) | Status |
|---|---|---|---|---|---|
| Fast register dispatch | `BOTWPC_FE_FAST_DISPATCH` | 3D methods from the command stream skip `Send()`; a register write is one kind-byte lookup + shadow RAM handling + compare/store + group bit (`FastRegisterWrite`); callback registers keep the legacy path | 698 M writes, 0 mismatches; unit tests incl. 8 M captured game writes and negative controls | with the sync memo (which alone did nothing): GPU thread **−0.41 ms/frame** (95 % CI −0.45 .. −0.36), **+1.61 FPS** (+1.00 .. +2.22), 1 % low +1.67 (+0.19 .. +3.14), p99 −0.24 ms | **kept** (`550b423ff`) |
| Buffer-sync memo | `BOTWPC_FE_SYNC_MEMO` | skip a bound buffer's lookup and synchronization when the same range was synchronized in the same GPU sequence | first version wrong for whole-buffer-tracked 4 KiB buffers (3 564 mismatches), fixed, 0 in 138 M | +0.13 FPS (−0.04 .. +0.29), GPU thread +0.03 ms | rejected, `buffer-sync-memo.patch` |
| Render-thread poll before blocking | `BOTWPC_FE_GAL_SPIN` | the render thread polls 50 µs for new commands before waiting on its event, so the GPU thread's wake-ups avoid a kernel call | scheduling only | −0.47 FPS (−0.99 .. +0.06) | rejected, `render-thread-spin.patch` |
| Command-buffer prefetch | `BOTWPC_FE_PREFETCH` | software prefetch of a command buffer's first 4 KiB before decoding (a third of the decode loop's samples were on the command-word load) | hint only | −0.03 FPS (−0.33 .. +0.28) | rejected, `command-prefetch.patch` |

Invalid measurements, not used: a first combined A/B (`fe-ab1`, +5.9 FPS) whose "off" side ran at 11.05 ms GPU-thread
time — far slower than any legacy run — did not reproduce (`fe-ab4`: +1.61 FPS); a fast-dispatch-only A/B (`fe-ab3`) and
a first render-thread-poll A/B (`fe-ab5`) hit a Game Over (night Keese) before the measurement window; two profiles
(`fe-dis3`, `fe-dis4`) ran with the display switched to 60 Hz. `botw/scripts/run_check.py` now flags all three cases.

## Final comparison (legacy vs optimized front end, same binary, interleaved)

`bench/r4/fe-final-*` (`final-fe.ps1`): 4 legacy and 4 optimized runs of the same binary (`b195283ef`), alternating,
170 s each, statistics after 90 s, every run checked by `run_check.py` (all valid). `BOTWPC_PRESENT_IMMEDIATE=1` (the
display was switched to 60 Hz during this round). Paired differences, 95 % CI (t, 3 df).

| Metric | Legacy | Optimized | Change |
|---|---|---|---|
| Average FPS | 84.06 | 86.23 | **+2.17 (+0.50 .. +3.84), +2.6 %** |
| Median FPS | 85.44 | 87.71 | +2.27 (+1.15 .. +3.39) |
| 1 % low FPS | 59.95 | 59.03 | −0.92 (−8.4 .. +6.6), not significant |
| P95 frame time | 13.85 ms | 13.44 ms | −0.41 (−1.17 .. +0.34), not significant |
| P99 frame time | 15.46 ms | 15.12 ms | −0.34 (−1.03 .. +0.35), not significant |
| GPU command thread (`GpuFifoMs`/frame) | 9.48 ms | 8.72 ms | **−0.77 (−1.25 .. −0.29), −8.1 %** |
| Guest wait for GPU data (`SyncWaitMs`/frame) | 0.436 ms | 0.322 ms | −0.115 (−0.173 .. −0.056) |
| Game speed | 1.000 | 1.000 | — |
| Process CPU (cores) | 4.86 | 4.84 | no change |
| Private memory | 4 932 MB | 4 987 MB | +55 (−105 .. +216), not significant |

Region timers (`BOTWPC_PROFILE=1`, 2 + 2 alternating runs `fe-prof-*`, 100..160 s; the timers slow both modes to
~64 FPS, so these attribute time, they do not measure FPS):

| Region (exclusive, ms per frame) | Legacy | Optimized | Change |
|---|---|---|---|
| Command decode + register writes (`Process`) | 2.20 | 1.62 | **−0.59 (−27 %)**: 8.7 → 6.4 ns per command word |
| Guest macros | 0.82 | 0.76 | −0.06 |
| Register write callbacks | 1.05 | 1.00 | −0.05 |
| State updates at draws (`DrawUpdate`) | 1.78 | 1.81 | +0.03 (unchanged code) |
| Texture binding commit | 1.06 | 1.07 | +0.01 (unchanged code) |
| Buffer binding commit | 2.66 | 2.69 | +0.03 (unchanged code) |
| Render thread, all batches (`RenderBatch`) | 2.67 | 2.74 | +0.07 |
| Guest CPU threads (sum, `GuestSampler --cputime`) | 29.9 | 31.1 | per run 29.3 / 28.7 and 30.5 / 33.4: no consistent difference |

In-run A/B of the same change (`fe-ab4-both`, scene drift cancelled): +1.61 FPS (+1.00 .. +2.22), GPU command thread
−0.41 ms (−0.45 .. −0.36), 1 % low +1.67 (+0.19 .. +3.14), p99 −0.24 ms (−0.47 .. −0.01).

## Why not more of a rewrite

- **Per word:** after fast dispatch, decode + dispatch is ~14 % of the GPU thread's busy time for ~254 000 words, a few
  ns per word. A third of it is the load of the command word itself (memory the game's CPU threads just wrote; a
  prefetch did not help). yuzu/Eden's method sink would add a buffer write and a replay per plain write
  (`STATE_TRACKING.md`).
- **Per draw:** ~59 % of the busy time is the draw path for ~5 000 draws: binding commit (37 %), state groups, backend
  queueing. Each part is required by the memory model (per-draw synchronization of bound buffers, per-draw texture
  handle reads — Eden does both too) and was already cut in rounds 3–4. The one exact shortcut left for buffers
  (sync memo) measured nothing.
- **Threads:** two thread-split designs were measured slower in round 4; cross-core hand-off costs as much as the
  per-word work it moves.
- A replacement front end that keeps the guest-visible semantics would therefore execute the same lookups,
  synchronizations and queue writes through new code; the measured lever was the per-word dispatch, which is done.

## Promotion

| Gate | Status |
|---|---|
| A. Functional completeness | the optimized front end changes only the dispatch of 3D methods; everything else is the legacy code. Every command type BotW sends runs (5 runs of W2 + regression scenario) |
| B. Differential correctness | pass for the changed component (register state); other areas unchanged (`GPU_FRONTEND_DIFFERENTIAL_TESTS.md`) |
| C. Gameplay | W2 idle and the walk/sprint/jump/throw regression at 30/60/90/120 modes, DLSS Quality; **not covered:** dragons, horse, glide, swim, shrines, combat-heavy scenes (save at the Great Plateau) |
| D. Performance | faster in W2 (see above); other scenes not measured |
| E. Stability | no errors, crashes or deadlocks in the runs of this round |
| F. Rollback | `BOTWPC_GPU_FRONTEND=legacy` (default) / settings value `Legacy`; each component switchable |
| G. Cross-platform | shared managed code; tested on Windows only |

Gates C and D are only partly met, so the default stays legacy; the setting is there for players and for the next
round's wider gameplay validation.

## Gameplay validation (optimized front end)

| Check | Result |
|---|---|
| W2 idle, 4 benchmark runs + verification run | screenshots normal, game speed 1.000; the only error line is the boot-time syncpoint timeout present in every build since round 4 |
| Camera-trajectory regression (`regress.input`: rest, jump, walk 2 s, attacks, throw, camera turn, sprint + jump) at 30 FPS | **identical to the legacy runs of round 4** in every segment (0.01 m / 0.1°; 30 FPS is deterministic) |
| Same at 60 / 90 / 120 modes (`regress-fe-*`, immediate present) | net travel 18.53 / 18.46 / 18.66 m vs 18.56 m at 30 FPS (≤ 0.6 %); walk 10.70–10.74 m; throw 1.14 m; game speed 1.000 in all; the 90 and 120 modes reached 78–79 FPS with the regression's screenshot captures |
| DLSS Quality (`fe-dlss-q`) | active (1600×900 → 1920×1080), image normal, game speed 1.000; 75.4 FPS optimized vs 72.0 legacy (`fe-dlss-q-legacy`, one run each) |
| Both DLC, audio | loaded / no audio errors in the logs |
| Not testable with this save | dragons, horse, glide, swim, combat-heavy scenes, shrines, towns, Divine Beasts; mods (none installed in the test data dir) |
