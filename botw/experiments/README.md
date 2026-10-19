# Measured and rejected prototypes (2026-10-15)

Not part of the build. The patches (against `cd4cf0816`) and `ab_batch.ps1` were removed after the clean in-run A/B
(`BOTWPC_AB_PERIOD=10`, 330 s, `botw/scripts/ab_phases.py`); they remain in git history at `fc4d203ae`
(`git show fc4d203ae:botw/experiments/<name>.patch | git apply`). Details in PERFORMANCE.md, "Architecture round".

| Patch | Switch | What | Status |
|---|---|---|---|
| `gpfifo-decode-stage-v3.patch` | `BOTWPC_GPFIFO_PIPELINE=1` | `GPU.Decode` thread decodes GPFIFO command buffers ahead into immutable `DecodedCommand` lists (bounded queue, 256 buffers); the GPU thread executes them in order. Non-prefetched buffers are read without flushing and checked word by word against the GPU thread's own in-order fetch before use; a mismatch switches the stage off and replays from the GPU thread's fetch. Signals only a sleeping side. | v1/v2: 19 FPS vs 91. v3: correct (no mismatch), but **−8.66 FPS** (95 % CI −9.74 .. −7.58, `r3ab-pipe3c`): rejected |
| `buffermap-handle-table.patch` | `BOTWPC_BUFFERMAP_TABLE` (on unless `0`) | Render-thread paged table for threaded buffer handles instead of lock + dictionary per lookup; other threads keep the dictionary | **+0.14 FPS** (95 % CI −0.24 .. +0.53, `r3ab-bmap3`): no effect, rejected |

## 2026-10-15, second round (W2: the player's save, outdoor eastern Great Plateau; patches against `9eff8a480`)

Both are kept here, not in the build. Apply with `git apply botw/experiments/<name>.patch`.

| Patch | Switch | What | Status |
|---|---|---|---|
| `ubo-slot-dirty.patch` | `BOTWPC_UBO_SLOT_DIRTY` | Per-slot dirty bits for graphics uniform buffers (yuzu/Eden idea): a changed slot re-resolves and re-sends only itself, and a shader switch with the same (binding, slot) layout keeps the uniform bindings | GPU thread **+0.06 ms** (95 % CI −0.16 .. +0.27), FPS +0.48 (CI −3.2 .. +4.1) before the scene change, `r4ab-uboslot`: no effect, rejected |
| `gpfifo-two-stage-split.patch` | `BOTWPC_GPFIFO_SPLIT=1` (A/B: `BOTWPC_AB_SWITCHES=BOTWPC_GPFIFO_SPLIT`) | Two-stage GPU front end: `GPU.Decode` fetches GPFIFO entries, decodes them and runs the guest MME macros against its own copy of the 3D registers (callback effects mirrored as found by `BOTWPC_SPLIT_PROBE`); the GPU thread executes compact packets (register writes, inline/uniform data spans, HLE macro calls with arguments) from a 16 MiB ring published per entry. Entries with `Entry1Sync.Wait` drain the GPU thread before they are fetched (hardware GPFIFO semantics). Mode changes only at drained entry boundaries, so it can be A/B'd inside one run | correct (no errors, identical image, game speed 1.000), but **−1.30 FPS** (95 % CI −1.50 .. −1.10), frame time +0.16 ms, `r4ab-split`. Stage timers (`r4-split3`): the GPU thread still spent 9.0 ms/frame executing packets (vs 9.0 ms doing everything before) while `GPU.Decode` took 2.5 ms/frame of decoding and macros off it; ~46 sync drains per frame. The per-word work moved (a few ns) costs about as much again to ship across cores and dispatch: rejected |
| `batched-reprotect.patch` | `BOTWPC_BATCH_REPROTECT` | Re-arming write tracking for a run of dirty pages with one VirtualProtect/mprotect per contiguous run instead of one per 4 KiB page (Eden #3288 idea): protection changes collected under the tracking lock and applied before the dirty data is read | GPU thread **+0.03 ms** (95 % CI −0.03 .. +0.09), FPS −0.28 (CI −0.76 .. +0.20), `r4ab-reprotect`: no effect (BotW's dirty pages are not contiguous runs), rejected. Memory and tracking unit tests passed with it |

## 2026-10-15, round 5 (GPU front end; W2; patches against `b195283ef`, branch `gpu-frontend`)

Components of the optimized GPU front end (`BOTWPC_GPU_FRONTEND=optimized`) that were implemented, verified and measured
in-run (`BOTWPC_AB_PERIOD`), then removed. Apply with `git apply botw/experiments/<name>.patch`. Details in
`botw/docs/GPU_FRONTEND_REWRITE.md`.

| Patch | Switch | What | Status |
|---|---|---|---|
| `buffer-sync-memo.patch` | `BOTWPC_FE_SYNC_MEMO` | Per bound buffer slot, remember the buffer and GPU sequence of its last guest-to-host synchronization; skip lookup + synchronization when the same range was synchronized in the same sequence and no forced dirty range, virtual dependant or buffer-cache change can make it do work (`Buffer.SyncVersion`, `SyncGatedBySequence`) | Differential verifier found the first version wrong for whole-buffer-tracked 4 KiB buffers (3 564 mismatches / 232 M checks), fixed (0 / 138 M). **+0.13 FPS** (95 % CI −0.04 .. +0.29), GPU thread +0.03 ms (`fe-ab2-sync`): no effect, rejected |
| `render-thread-spin.patch` | `BOTWPC_FE_GAL_SPIN`, `BOTWPC_FE_GAL_SPIN_US` | The render thread polls (default 50 µs) for new GAL commands before blocking, so the GPU thread's wake-up is a memory write instead of a kernel call | **−0.47 FPS** (CI −0.99 .. +0.06), `fe-ab6-spin`: rejected (the large `ManualResetEventSlim.Set` share in a stack profile was inflated by the sampler suspending threads) |
| `command-prefetch.patch` | `BOTWPC_FE_PREFETCH` | `Sse.Prefetch0` over the first 4 KiB of each command buffer before decoding | **−0.03 FPS** (CI −0.33 .. +0.28), `fe-ab7-prefetch`: rejected |

## 2026-10-15/16, Runtime V2 (branch `runtime-v2`; patches against `2f37b5386`)

Measured in-run (`BOTWPC_AB_PERIOD=10`, 330 s, `ab_phases.py`) on the CPU V2 build, or statically on the JIT corpus
(`JitCorpusTests`, 4 482 BotW functions). Apply with `git apply botw/experiments/<name>.patch`.

| Patch / change | Switch | What | Status |
|---|---|---|---|
| `memory-streaming-handles.patch` | `BOTWPC_MEMORY_TRACKING=v2`, `BOTWPC_MEM_STREAMING` | Memory V2: a tracking handle re-armed in 4 consecutive frames stays unprotected and dirty for 60 frames (no write faults, no reprotects; every sync copies) | **−1.10 FPS** (95 % CI −1.44 .. −0.75), readback wait +0.27 ms (`v2-ab-memstream1`): the extra copies/uploads cost more than the ~63 fault/re-arm cycles per frame they remove. Rejected |
| `gpu-mru-buffer-lookup.patch` | `BOTWPC_GPU_FRONTEND=v2`, `BOTWPC_GPU_MRU_BUFFER` | GPU V2: check the last matched buffer before the buffer page table (exact: cached buffers never overlap) | 81 M hits verified, 0 mismatches; **+0.20 FPS** (CI −0.09 .. +0.49), GPU thread +0.20 ms n.s. (`v2-ab-mru2`). Rejected (re-add `GpuFrontend.MruBuffer` and `FrontendVerify.Check.MruBuffer` to apply) |
| (not archived) caller-saved registers first | `CpuV2Features` bit 4 | linear scan prefers a caller-saved host register free for the whole interval | corpus: +0.07 % host instructions, push/pop −0.8 %. Not adopted |
| (not archived) function-wide relocatable constants | `CpuV2Features` bit 5 | the guest memory base materialized once per function instead of once per block | corpus: `mov imm64` −149 k, reloads/moves +130 k, no net change. Not adopted |
| (kept, experimental) `AbiTemporaryLiveness` | `runtime.cpu_v2_abi = Temporaries` | also X8–X17/V16–V31 dead at calls and returns | corpus −9 % more instructions, 500 conforming random programs pass; not default: special conventions (TLS descriptors, some runtime helpers) keep temporaries live across calls |
