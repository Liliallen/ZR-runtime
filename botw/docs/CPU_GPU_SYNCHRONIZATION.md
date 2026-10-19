# CPU/GPU synchronization and the frame's critical path (Runtime V2)

How a BotW frame flows through the emulator, where each side waits for the other, and what CPU V2 changed. W2 scene
(player's save, eastern Great Plateau, 120 FPS mode, DLSS off, immediate present). Earlier analysis: PERFORMANCE.md
("Architecture round", "Round 4"); readback call chains by guest function: PERFORMANCE.md, round 4.

```
 guest main thread (46) ─┐ frame N game logic, job dispatch     ┌─ waits for workers ─┐ frame tail
 guest workers (53, 54) ─┤ jobs; worker 53 reads back frame N-1's ├─ exposure/luminance ┤
                         │ GPU results (exposure, luminance)      │  readback wait      │
 GPU command thread ─────┴─ decodes frame N-1's commands, draws, binds ── reaches the readback producers late ──
 render thread ─────────── records/submits Vulkan command buffers (early submission of readback producers)
 host GPU ──────────────── executes; fence signals; guest's readback completes
```

## The two chains

1. **GPU chain:** the guest reads back data produced near the end of the previous frame's command stream. Its wait
   ends only after the GPU command thread has processed everything up to the producers, the render thread has
   submitted them and the host GPU has executed them (strict, exact readbacks; BOTW-PC never returns stale data).
2. **Guest chain:** the main thread's own work plus waiting for its workers.

## Causal measurements (in-run A/B, each a 330 s run with 10 s alternating periods)

| Build | Delay | Frame time | Readback wait | Meaning |
|---|---|---|---|---|
| Round 5 (legacy JIT) | GPU command thread +1.5 ms | +0.94 ms (63 %) | +0.77 ms | GPU chain partly critical |
| Round 5 | render thread +1.5 ms | +0.18 ms (12 %) | | ~1.3 ms slack |
| Round 5 build, legacy JIT (`v2-causal-guestpct20`) | guest threads +20 % | **+2.60 ms** | −0.34 ms | guest chain strongly critical |
| CPU V2 (`v2-causal-gpu1500-cpuv2`) | GPU command thread +1.5 ms | **+1.18 ms (78 %)** | +0.49 ms | GPU chain now dominant |
| CPU V2 (`v2-causal-guest20-cpuv2`) | guest threads +20 % | +1.42 ms | −0.43 ms | guest chain still matters |

`BOTWPC_DELAY_GUEST_PCT` (new) stretches each guest thread's run between supervisor calls by n %, i.e. it models slower
JIT code; an earlier fixed per-thread delay (1.5 ms per frame) was a bad model (+15 ms: ~10 guest threads in a chain).

Interpretation: before CPU V2 the guest's own code was the larger lever (a 20 % slower guest cost 2.6 ms); CPU V2 cut
the guest CPU time per frame by ~15 % (−5.1 ms of CPU across all guest threads), after which the guest reaches its
readbacks earlier and waits longer for them (readback wait 0.44 → 1.05 ms) — the GPU command thread is now the first
limit (78 % of its delay reaches the frame), with the guest second.

## Synchronization mechanisms (unchanged by Runtime V2, verified intact)

| Mechanism | Where | Runtime V2 |
|---|---|---|
| Guest syncpoints / semaphores | GPU command thread increments when it processes them | unchanged |
| Readbacks of GPU-written memory | memory tracking flush, waits for the host fence; producers submitted early | unchanged; still exact |
| `Entry1Sync.Wait` GPFIFO entries | drain before fetch | unchanged |
| Guest thread wake-ups | host events per guest thread, 4–7 µs | unchanged |
| Yields | Horizon yield schedule count in HLE V2 | fast path only (HLE_SCHEDULER_V2.md) |

The macro differential verifier (GPU_FRONTEND_V2.md) found no difference in synchronization registers (syncpoint and
semaphore methods are callback registers, executed by the same code in all modes).

## Distance to 120 FPS

See `PERFORMANCE.md` (Runtime V2, final matrix). The GPU command thread does ~9.5 ms of work per frame with CPU V2
(78 % of each ms reaches the frame time); the guest chain contributes behind it. Reaching 8.33 ms needs both the GPU
thread (−3 ms or more) and the guest (another −15 to −20 % of guest time) to shrink together.
