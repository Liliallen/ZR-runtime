# GPU front end V2 (Runtime V2)

`graphics.gpu_frontend = V2` / `BOTWPC_GPU_FRONTEND=v2`: the round-5 optimized front end (fast register dispatch,
`GPU_FRONTEND_REWRITE.md`) plus the GPU V2 changes below. Legacy and `Optimized` behave exactly as before.

## Where the GPU command thread stands after CPU V2

With CPU V2 the guest submits earlier and the GPU command thread is again the main limit:

| Causal test (in-run A/B, W2, CPU V2 + optimized front end) | Frame time | Transfer |
|---|---|---|
| GPU command thread +1.5 ms per frame (`v2-causal-gpu1500-cpuv2`) | +1.18 ms (FPS −6.79, CI −7.21 .. −6.37) | **78 %** (63 % in round 5, before CPU V2) |
| Guest threads +20 % run time (`v2-causal-guest20-cpuv2`) | +1.42 ms (FPS −7.67, CI −8.57 .. −6.77) | (legacy JIT: +2.6 ms) |

GPU-thread profile on CPU V2 (`v2-stacks-gpu1`, 3 939 stack samples, 100 Hz): the thread now waits for submissions
14.9 % of the time; of the rest, buffer binding commit 21.6 % inclusive (`CommitGraphicsBindings` 6.4 % self,
`GetBufferRange` 9.0 %, page-table lookups in `CreateBufferAligned` 4.6 %), command decode 14.5 % self, texture binding
8.4 %, shader state 6.7 % (shader-code byte compare 1.9 %), memory tracking queries 5.4 % and page reprotection ~4 %
(`MEMORY_TRACKING_V2.md`). Same shape as round 5: spread over many necessary per-draw steps.

## Components

| Component | What | Verification | Measured | Status |
|---|---|---|---|---|
| **HLE macro differential verifier** (`MacroVerifier`) | Under `BOTWPC_GPU_VERIFY=1`, every HLE macro call also runs the guest's own macro code in the reference `MacroInterpreter` (same arguments, recording view of the registers, no side effects); afterwards every register without a write callback that the guest code writes must hold the same value in the real state. Callback registers (draw triggers, binds) are counted, not compared: their effect is the HLE function's action. Mismatches are logged with the register's field name | it is the verification | verify mode only | **kept** (diagnostic) |
| **DrawArraysInstanced register fidelity** | The verifier found that Ryujinx's HLE `DrawArraysInstanced` draws with the right vertex range but leaves `VertexBufferDrawState` (first, count) stale; BotW's macro writes them (21 328 differences in 230 M comparisons, all this one register pair). A later non-macro `DrawEnd` or a shadow-RAM replay would see a different value than on hardware. GPU V2 writes both registers through the normal register path before the draw (no callback; the vertex-buffer group was already forced dirty by the draw) | W2, verify mode: **238 M HLE macro register comparisons, 0 mismatches**; 586 M fast-write checks, 0 mismatches (`v2-verify-macro4`) | no cost outside verify mode beyond two register writes per such draw | **kept** (GPU V2) |
| Most-recently-matched buffer lookup | Check the last buffer that contained a looked-up range before the page table (cached buffers never overlap, so a containing buffer is the page table's answer; cleared whenever buffers are removed) | 81 M hits compared with the page table, 0 mismatches (`v2-verify-mru1`) | +0.20 FPS (95 % CI −0.09 .. +0.49), GPU thread +0.20 ms n.s. (`v2-ab-mru2`; first attempt invalid: Game Over) | rejected, `botw/experiments/gpu-mru-buffer-lookup.patch` (needs `GpuFrontend.MruBuffer` and `FrontendVerify.Check.MruBuffer` re-added) |
| Shader lookup without the code compare | considered: needs write tracking that also sees GPU-side flushes of shader memory; not exact with Ryujinx's untracked GPU writes | — | — | not implemented |
| Texture/sampler pool descriptor verification instead of page tracking | considered after counters showed pools cause only ~5 dirty/re-arm cycles per frame (buffers ~63) | — | — | not worth it in BotW |

## What was not changed, and why

The per-draw cost (~1.8 µs per draw, ~5 000 draws per frame) is the sum of bindings, state updates, macro dispatch and
backend queueing that the memory model requires; round 4/5 and this round tried every isolated shortcut found
(binding memo and page table kept; per-slot uniform dirty, sync memo, render-thread poll, command prefetch, MRU lookup
rejected). The remaining lever is structural: moving the expensive half (bindings, state, caches: ~6.4 ms) off the
command thread with batched hand-off, which requires the binding resolution to be split from the buffer cache's
synchronization. Two finer-grained splits measured slower (round 3/4). Not attempted in this round
(`NEXT_STEPS.md`).

## Portability

All GPU V2 code is in `Ryujinx.Graphics.Gpu` (shared by every backend and OS); no Vulkan, NVIDIA or Windows
dependency. DLSS and the Vulkan backend are untouched.
