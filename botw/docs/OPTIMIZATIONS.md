# Optimizations

All numbers are from scenario S1 (Shrine of Resurrection, idle, 1080p output, BotW 1.6.0) on the machine in
`BASELINE.md`, statistics after a 90 s warm-up, raw data in `botw/results/<label>/`.
"Uncapped" = VSync Unbounded, used to measure throughput headroom.

---

### 1. Vulkan fence pool
- **Files changed:** `Ryujinx.Graphics.Vulkan/FencePool.cs` (new), `FenceHolder.cs`, `CommandBufferPool.cs`
- **Reason:** sampling profiles showed the render thread ~94 % "busy" while the GPU was ~18 % utilized. Counters
  showed `vkCreateFence` + `vkDestroyFence` at **0.35–0.6 ms per call** on driver 615.78, ~15 calls per frame
  (5.2–7.8 ms create + 5.9 ms destroy per frame), because `CommandBufferPool` replaces the fence every time a
  command buffer is recycled (old fences may still be referenced elsewhere).
- **Before:** fence destroyed when its last reference is released; a new fence created per reuse.
- **After:** when the last reference is released and the fence is signaled, it is reset (`vkResetFences`,
  measured 0.003 ms/frame total) and pooled (max 64); unsignaled fences are destroyed as before. Pool is
  per `CommandBufferPool`, drained on dispose. `BOTWPC_FENCE_POOL=0` restores the old path.
- **Performance result (same binary, env A/B):**

  | | pool off (`s1-unb-fencepool0`) | pool on (`s1-unb-fencepool1`) |
  |---|---|---|
  | uncapped median FPS | 52.75 | **92.3** (+75 %) |
  | 1 % low | 36.3 | **67.9** |
  | p99 frame time | 26.5 ms | **13.9 ms** |
  | GPU utilization | 17.8 % | 32.6 % |

  At the stock 30 FPS cap: render thread 54 % → 30 % of a core, 1 % low 20.7 → 27.8 FPS, p99 41.7 → 34.5 ms
  (`s1-30fps-gpu` vs `s1-30fps-fencepool1`).
- **Correctness tests:** in-game captures identical in content for both runs; no error log lines (checked with
  `grep -a "|E|"`); full upstream test suites pass. The change only alters *when* a VkFence object is recycled:
  reset happens only after the fence is signaled and nothing references it (Vulkan requires fences to be
  unsignaled-or-not-pending before reset; a signaled fence is not pending).
- **Known limitations:** a fence returned concurrently with pool disposal is leaked until device destruction
  (shutdown only, commented in code). Not tested with Vulkan validation layers (not installed). Generic change:
  benefits every game, but only measured with BotW on NVIDIA 615.78.

---

### 2. FIFO_LATEST_READY presentation
- **Files changed:** `Ryujinx.Graphics.Vulkan/PresentModeFifoLatestReady.cs` (new), `VulkanInitialization.cs`
  (enable `VK_EXT_present_mode_fifo_latest_ready` + feature when supported), `VulkanRenderer.cs`, `Window.cs`
- **Reason:** NVIDIA on Wayland exposes FIFO, IMMEDIATE and FIFO_LATEST_READY but no MAILBOX. Outside Unbounded
  mode Ryujinx fell back to blocking FIFO; with the emulated VBlank raised (240/360/480 Hz) BotW stayed at
  **~58 FPS** regardless of the rate (`s1-120-timing`, `s1-hz360`, `s1-hz480`).
- **After:** preference order Immediate (Unbounded only) → Mailbox → **FIFO_LATEST_READY** → FIFO.
  `BOTWPC_FIFO_LATEST_READY=0` disables it.
- **Performance result:** 240 Hz custom refresh: 58.3 → **93.1** FPS median (`s1-120-timing` → `s1-240hz-v2`;
  the latter also contains the timing change, which does not affect frame rate: `s1-240hz-notiming` 92.9).
- **Correctness tests:** no tearing mode is used outside Unbounded; frames are presented at the monitor refresh
  (99.65 Hz) with the newest ready image, like MAILBOX.
- **Known limitations:** when the game renders faster than the monitor refreshes (e.g. 120 FPS on this 99.65 Hz
  monitor) some rendered frames are never displayed — inherent to mailbox-style presentation.

---

### 3. BotW high-frame-rate timing correction
See `FRAME_TIMING.md` for the mechanism and tests. Not a throughput optimization; it is what makes frame rates
above 30 usable (game speed 1.00× instead of 2–3.6×).

---

### Evaluated and rejected
- **Backend threading off:** uncapped 70.0 FPS vs ~94 (removes queue copies but serializes Vulkan encoding onto
  the GPU command thread). Kept default (on).

### 4. Buffer lookup cache (GPU command thread)
- **Files changed:** `Ryujinx.Graphics.Gpu/Memory/BufferCache.cs`
- **Reason:** `perf` showed buffer lookups (binary search over the buffer range list under a write lock in
  `CreateBufferAligned`, then again in `GetBuffer`/`SynchronizeBufferRange`) at ≈20 % of `GPU.MainThread`;
  BotW performs ~52 000 lookups per frame (~13 per draw) — see `PERFORMANCE.md`.
- **Before:** every lookup binary-searches `_buffers` (and takes the write lock in `CreateBufferAligned`).
- **After:** a 1 024-entry direct-mapped cache (key: 4 KiB page of the looked-up address) of buffers that fully
  contained a previous lookup. A hit is returned only if it fully contains the new range, which in a non-overlapping
  list means it is exactly what `FindOverlap` would return. The cache is cleared at both places buffers are removed
  from the list (merging into a bigger buffer); `_buffers` is only mutated on the GPU thread, which is also the only
  user of the cache. Generic (all games). `BOTWPC_BUFFER_LOOKUP_CACHE=0` disables it.
- **Performance result (same binary, env A/B, uncapped, 2 runs each):**

  | | cache off | cache on |
  |---|---|---|
  | median FPS | 83.9 / 81.6 | **87.2 / 86.1** (+4.7 %) |
  | GPU thread ms/frame | 10.07 / 10.42 | **8.30 / 8.95** (−16 %) |
  | 1 % low (run 2; run 1 of both sides had ~20 external stutters) | 56.4 | **58.4** |
  | hit rate | — | 95 % (48 967 hits, 2 696 misses per frame, `prof-lookup2`) |
- **Correctness tests:** game speed 1.00 in all runs; in-game captures normal; no error log lines; BotW-PC and
  upstream tests pass.
- **Known limitations:** FPS gain is smaller than the GPU-thread gain because another limit takes over (`PERFORMANCE.md`).

### Profiling cost fix
`ThreadedRenderer.New` read the clock on every queued command (added with the counters); now only when the queue is full.
Fine-grained per-draw timers are behind `BOTWPC_PROFILE=1`.

### Measured and ruled out
- **Double copy of guest buffer data** (threaded queue + upload): 0.19 ms/frame for the queue copy, 0.67 ms/frame for
  all uploads on the render thread, and no staging-buffer uploads at all in S1. Removing a copy would save ≤2 %; not done.
  (An earlier `dotnet-trace` estimate of 25 % was a sampling artifact, see `PERFORMANCE.md`.)
- **GC:** 0.06 ms/frame of pauses. **Render-thread wake-ups:** 4 000/frame but 0.19 ms total.

### 5. Skip redundant vertex/uniform buffer binds (GPU command thread)
- **Files changed:** `Ryujinx.Graphics.Gpu/Memory/BufferManager.cs`
- **Reason:** `SetVertexBuffer` and `SetGraphicsUniformBuffer` marked all vertex (or all graphics uniform) bindings dirty
  on every call, so the next draw looked up every buffer of that kind again and re-sent all of them to the backend.
  Counted with `BOTWPC_PROFILE=1` (`BindSets`/`BindSkips`, S1 on Windows): 29 600 such calls per frame, **72 % of them
  set exactly the binding already in place** (`bench/win-prof-skip`).
- **After:** a call that sets the same physical range (and stride/divisor) does not set the dirty flag, as upstream
  already does for storage buffers. Host buffer changes still rebind everything through `Rebind()`. The index buffer
  keeps the unconditional flag because `VtgAsComputeState` overrides it on the backend and relies on the next set to
  restore it. Vertex and uniform buffers are read-only, so the skipped `GetBufferRange` differs from the non-dirty
  path's `SynchronizeBufferRange` only in the lookup. `BOTWPC_SKIP_REDUNDANT_BINDS=0` restores the old behaviour.
- **Performance result (Windows 11, same binary, env A/B, S1 120 mode, DLSS Quality, alternating, 2 runs each):**

  | | skip off | skip on |
  |---|---|---|
  | median FPS | 87.2 / 85.6 | 87.8 / 86.3 |
  | GPU thread ms/frame | 8.44 / 8.60 | 8.36 / 8.46 (−1.3 %) |

  Within run-to-run noise: the skipped lookups mostly hit the buffer lookup cache and were cheap.
- **Correctness tests:** no new log errors; 5-minute scripted play (Great Plateau, menus, map, Game Over, shrine) with
  screenshots every 20 s rendered normally (`bench/win-play5-next`).

### Windows measurements (2026-10-14, Ryzen 7 7700, RTX 5070 Ti, driver 616.92, VBS/HVCI enabled)
- S1 120 mode: 85–88 FPS median, same as Linux on this machine. 20-minute scripted play (`bench/win-play20`):
  104 FPS median, 1 % low 60, working set flat at 8.2–8.3 GB (no growth), no crashes.
- `dotnet-trace` (sampled thread time) on Windows again over-weights native transitions: it showed
  `RegionHandle.Reprotect` 11 % and `CircularSpanPool.Insert` 19 % of the GPU thread. Direct timers
  (`BOTWPC_PROFILE=1`, `bench/win-prof-track`): 252 reprotects per frame on all threads = **0.73 ms** (2.9 µs each,
  `VirtualProtect`), 119 guest write faults per frame, queue copy 0.15 ms. Not worth changing.
- `framelog.csv`/`counters.csv` could get interleaved lines and NUL runs: a late `System.Timers.Timer` callback ran
  `FlushFrameLog` concurrently with the next one. The flush is now serialized (`PerformanceStatistics.cs`).

### 6. Strict sync for frequently read-back textures (GPU front end, all platforms)
See PERFORMANCE.md ("DLSS round"): `TextureGroupHandle.SignalModifying` creates the unbind sync of textures the guest
reads back often as strict, so the producing commands are submitted at once. W1, 3 runs each: DLSS Quality
85.9 → 90.5 FPS (+5.4 %), 1 % low 63.6 → 68.3, sync wait 0.78 → 0.33 ms; DLSS off 89.1 → 90.5. Shared code (no
platform or API branch); measured on Windows only. `BOTWPC_STRICT_READBACK_SYNC=0` restores upstream.

### Platform-agnostic round (2026-10-14): measured, reverted
All in shared code, W1 with DLSS **off**, same binary with an environment switch, alternating, 3 runs each. Per-run
GPU-thread times and FPS overlapped completely, so each change was reverted (commit messages of `72581d27c`,
`2d23120e2` record them); the diagnostics that found the candidates are kept (`BOTWPC_STAGE_LOG`, `BOTWPC_SVC_LOG`).

| Experiment | Where | Idea | avg FPS (on / off) | GPU thread ms (on / off) |
|---|---|---|---|---|
| bulk GPFIFO writes | `GPFifoProcessor`, `ThreedClass` | hand runs of incrementing 3D methods to the engine as a span (229 000 words/frame) | 91.0 / 90.7 | 8.31 / 8.42 |
| keep contained index buffer | `BufferManager.SetIndexBuffer` | `UpdateIndexBufferState` ran on every draw (first + count changes the size) and rebound the index buffer | 91.3 / 90.7 | 8.33 / 8.42 |
| both together | | | 91.0 / 90.7 | 8.36 / 8.42 |
| GPU thread AboveNormal priority | `ThreadedRenderer` | protect the critical thread from other applications | 91.1 / 90.6 | 8.36 / 8.43 |
| yield fast path | `KScheduler.Yield` | Horizon's yield schedule count (mesosphere/yuzu/Eden algorithm, re-implemented): a thread spins on svcSleepThread(0) ~150 000/s | 90.4 / 90.4 | 8.42 / 8.47 |

**Correction (2026-10-15):** the priority and yield rows are not valid: from the second pair on, both sides ran with the feature off (switch leak in `bench_win.ps1`, KNOWN_ISSUES.md #29). Only the bulk-write and index rows are valid comparisons.

Why they did not register: the .NET JIT already inlines the per-word and per-draw paths and the buffer lookup cache
serves ~95 % of lookups, so the removed work was a few ns per call; the yields and other applications were not on the
critical path at the measured load. GPU-thread time is spread over many sub-1 % pieces (PERFORMANCE.md breakdown).

### 7. Skip unchanged vertex/index/uniform buffer sets (binding memo, GPU command thread, all platforms)
- **Files changed:** `Ryujinx.Graphics.Gpu/Memory/BufferManager.cs`, `MemoryManager.cs` (page-table sequence),
  `BufferCache.cs` (buffer-set sequence); commit `f7449f0b5`.
- **Reason:** W2 profile (`r4-stacks`): uniform buffer binds re-translated the GPU VA and re-checked buffer existence
  (3.5 % of the busy GPU thread in `CreateBufferAligned` under `ConstantBufferUpdater.Bind`) although 72 % of
  ~33 000 vertex/uniform sets per frame set the binding already in place; indexed draws with 8/16-bit indices re-set
  vertex and index buffers on almost every draw because their size follows the base vertex and index count.
- **After:** a per-slot memo of the last (GPU VA, size) with the GPU page-table and buffer-cache sequences; while both are
  unchanged an identical set (vertex/index: a contained range) is skipped. Hit rate 69 % (`r4-diag2`).
  `BOTWPC_BIND_MEMO=0` disables it.
- **Result (W2, in-run A/B, 11 pairs, `r4ab-memo`):** GPU thread −0.20 ms/frame (95 % CI −0.33 .. −0.07), sync wait
  −0.09 ms; FPS +0.76 (CI −0.33 .. +1.84, not significant by itself).
- **Correctness:** translation identical and buffer present by construction of the two sequences; memo recorded on every
  full set (also while off); `Rebind()` and the vertex-as-compute index override unchanged. Regression scenario and
  screenshots identical (PERFORMANCE.md, Round 4).

### 8. Page table from guest pages to buffers (GPU command thread, all platforms; yuzu/Eden algorithm)
- **Files changed:** `Ryujinx.Graphics.Gpu/Memory/BufferPageTable.cs` (new), `BufferCache.cs`; commit `d817adf00`.
- **Reason:** the direct-mapped lookup cache still missed ~2 900 times per frame on W2 (per-draw uniform data walks
  through a large buffer, so each page change is a first touch); every miss was a binary search, under the range
  list's write lock when coming from `CreateBuffer`.
- **After:** a two-level table maps every 4 KiB page to the buffer covering it (buffers never overlap and are 4 KiB
  aligned), maintained where buffers are added and merged away; a hit is checked to contain the range, so a stale entry
  can only fall back to the search. Re-implemented from the yuzu/Eden buffer cache design (GPL; no code copied).
  `BOTWPC_BUFFER_PAGE_TABLE=0` stops using it.
- **Result (W2, in-run A/B, 11 pairs, `r4ab-pagetable`):** **FPS +1.12 (95 % CI +0.40 .. +1.85)**, frame time −0.155 ms,
  GPU thread −0.32 ms/frame (CI −0.41 .. −0.23), sync wait −0.12 ms; 1 % low and p99 unchanged within their intervals.

### 9. Optimized GPU front end: fast register dispatch (GPU command thread, all platforms; selectable)
- **Files:** `Engine/GpuFrontend.cs` (selector, differential verifier), `Engine/FastRegisterWrite.cs`,
  `Engine/Threed/ThreedClass.cs` (`WriteFast`), `Engine/GPFifo/GPFifoProcessor.cs` (`SendThreed`, `SendMacro`),
  `StateUpdateTracker`/`StateUpdater` (`GroupOf`, `SetDirtyGroup`), `BotwPcSettings` (`graphics.gpu_frontend`).
- **Reason:** BotW sends ~254 000 command words per frame, ~157 000 of them 3D register writes (70 % unchanged values)
  with the shadow RAM in track mode; each went through `Send()` and three wrapper layers with two table lookups.
- **After:** `BOTWPC_GPU_FRONTEND=optimized` (default legacy): 3D methods skip `Send()`; one kind byte per register
  (callback → legacy path, else state group) and the shadow RAM handling in one inlined step.
- **Result (W2):** +2.17 FPS (95 % CI +0.50 .. +3.84) over 4 + 4 interleaved runs, GPU command thread −0.77 ms/frame
  (−1.25 .. −0.29); in-run A/B +1.61 FPS, −0.41 ms. Decode + register writes −27 %.
- **Correctness:** runtime differential verifier (legacy write performed, fast prediction compared) 0 mismatches in
  698 M writes; unit tests on the real register layout incl. 8 M captured game writes and negative controls; 30 FPS
  camera trajectory identical to legacy.
- **Limits:** measured on W2 only; default stays legacy until wider gameplay validation (`GPU_FRONTEND_REWRITE.md`).

### 10. Runtime V2: CPU V2 code generator, GPU V2 fidelity, HLE V2 yield count (all platforms; selectable)
- **Files changed:** `src/ARMeilleure/**` (CpuV2.cs, ValueNumbering.cs, DeadMoveElimination.cs, LinearScanAllocator.cs,
  LiveInterval.cs, RegisterUsage.cs, SsaDeconstruction.cs, Compiler.cs, X86/CodeGenerator.cs, InstEmitFlow*.cs, PTC),
  `Ryujinx.Cpu/Jit/JitCpuContext.cs`, `Ryujinx.Graphics.Gpu/Engine/MME/{MacroHLE,MacroVerifier,Macro}.cs`,
  `Ryujinx.HLE/HOS/Kernel/{Threading/KScheduler,Threading/KThread,Process/KProcess}.cs`, `Ryujinx.Common/BotwPc/{RuntimeV2,BotwPcSettings}.cs`.
- **Reason:** the guest JIT emitted ~9 host instructions per guest instruction, over half of them register moves and
  spills, and a 20 % slower guest cost 2.6 ms per frame (CPU_V2_JIT.md, CPU_GPU_SYNCHRONIZATION.md).
- **Result (W2, final matrix):** full Runtime V2 +7.91 FPS (95 % CI +6.17 .. +9.66), frame time −1.33 ms, guest CPU
  −16.9 %, GPU thread −0.92 ms; CPU V2 alone +5.14 FPS (PERFORMANCE.md, "Runtime V2").
- **Default:** legacy (gameplay gate needs saves beyond the Great Plateau). Rejected components: `botw/experiments/README.md`.

### Round 5 (2026-10-15): measured and rejected
Patches and numbers in `botw/experiments/README.md`.

| Experiment | Result (W2, in-run A/B) |
|---|---|
| buffer-sync memo (skip same-sequence re-synchronization of bound buffers) | +0.13 FPS (CI −0.04 .. +0.29), GPU thread +0.03 ms: no effect; first version had a correctness flaw the verifier found |
| render-thread poll before blocking (fewer kernel wake-ups from the GPU thread) | −0.47 FPS (CI −0.99 .. +0.06): no gain |
| software prefetch of command buffers | −0.03 FPS (CI −0.33 .. +0.28): no effect |

### Round 4 (2026-10-15): measured and rejected
Patches and numbers in `botw/experiments/README.md`.

| Experiment | Source of the idea | Result (W2, in-run A/B) |
|---|---|---|
| per-slot uniform buffer dirty state | yuzu/Eden buffer cache | GPU thread +0.06 ms (CI −0.16 .. +0.27): no effect |
| two-stage GPU front end (decode + guest macros on their own thread) | bbport draw pipeline | **−1.30 FPS** (CI −1.50 .. −1.10): packet execution cost what it moved |
| batched page reprotection | Eden #3288 | GPU thread +0.03 ms (CI −0.03 .. +0.09): no effect |
| threaded GAL queue false sharing (hypothesis only) | — | refuted by the profile: queue code ≤ 1–2 % of the GPU thread |
| larger fault-unprotect window (bbport) | bloodborne_pc | not tried: fault handling is 0.1–0.3 % of BotW's guest threads (~120 faults/frame, not ~1 600) |
