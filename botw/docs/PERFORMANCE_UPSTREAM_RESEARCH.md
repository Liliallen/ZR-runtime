# Upstream and open-source performance research (2026-10-14)

Reuse-first review for the 97 → 120 FPS (DLSS on) work. For each candidate: what it addresses, whether BOTW-PC
already has it, licence, and what was done. Measured results are in `PERFORMANCE.md`; this file records sources.

## Sources inspected

| Source | Access | Licence | Notes |
|---|---|---|---|
| Ryujinx (original) | `github.com/Ryujinx/Ryujinx` returns 404 (taken down 2024); its history up to the fork point is in this repository | MIT | PRs looked up by number in `git log` |
| Ryubing (canonical, `git.ryujinx.app/projects/Ryubing`) | **unreachable** from this machine and from the web fetcher on 2026-10-14 (connection refused / timeout) | MIT | BOTW-PC is based on Ryubing as of 2025-10-11 (`e2143d43b`, ryubing/ryujinx!50). Commits after that date could not be reviewed |
| `github.com/Leuconoe/Ryubing` (the `mirror` remote) | reachable | MIT | a personal fork (MCP control bridge), not upstream performance work |
| Eden (`github.com/UzuCore/eden`, 29 043 commits, last 2026-10-12) | cloned without blobs into `../.tools/research` (not built, not run) | **GPL-3.0** | C++ yuzu descendant: different GPU frontend (dirty flags, DMA pusher, its own buffer/texture caches). Algorithms may be re-implemented, code may not be copied into the MIT runtime |
| bloodborne_pc (`github.com/deadinside28/bloodborne_pc`) | cloned without blobs | **GPL-2.0** (the DLSS bridge directory is MIT) | shadPS4-derived; DLSS/FSR, camera and object motion vectors |
| bbhost (`github.com/droogie/bbhost`) | cloned without blobs | **GPL-3.0** | Bloodborne host; occlusion queries, frame pacing, perf-pass tooling |
| Khronos Vulkan-Samples, NVIDIA DLSS SDK docs | headers of DLSS SDK 310.9.1 (already used) | Apache-2.0 / NVIDIA RTX SDK | render-preset hints |

## Candidates

### Ryujinx #4667 "Vulkan: add situational Fast Flush mode" (riperiperi, 2023-04-11)
- **Addresses:** BotW builds one command buffer for 4–5 ms in long render passes; when the guest waits on sync right
  after, it waits for the whole buffer. Fast flush submits every 1.5 ms of recording while the average sync wait per
  frame exceeds 1.5 ms (exits below 0.1 ms).
- **In BOTW-PC:** yes (`AutoFlushCounter.cs`, `a64fee29d`). In the benchmark scene the average sync wait is
  0.4–0.9 ms/frame, so the mode never engages. Experiment: `BOTWPC_FAST_FLUSH=1` forces it (result in PERFORMANCE.md).

### Ryujinx #4087 "Vulkan: Don't flush commands when creating most sync" (riperiperi, 2022-12-29)
- **Addresses:** submitting a command buffer for every `WaitForIdle` sync (BotW mixes compute into render passes).
- **In BOTW-PC:** yes (`e20abbf9c`). Side effect relevant here: a sync created mid-frame is signalled by the fence of
  the command buffer it was recorded into, i.e. only after everything recorded after it in that buffer — including
  the DLSS motion-vector pass and evaluation at the end of the frame. Hence the DLSS flush experiment below.

### Eden: guest syncpoints released without waiting for host GPU completion (`video_core/fence_manager.h`)
- **Algorithm:** at its default (non-Accurate) GPU levels Eden runs the syncpoint increment as soon as the GPU thread
  processes it (`delay_fence == false` → `func()` immediately); only Accurate defers it to the host fence.
- **In BOTW-PC:** equivalent behaviour already: `ThreedClass.IncrementSyncpoint`/`GPFifoClass` increment the guest
  syncpoint on the GPU thread. Remaining host waits come from guest CPU access to GPU-written textures/buffers
  (`TextureGroupHandle.Sync`, `BufferModifiedRangeList.WaitForAndFlushRanges`), which Eden also waits for
  (`ShouldWait()` on pending async flushes). Nothing to port; identified the actual waits instead (`BOTWPC_SYNC_LOG`).

### Eden #3296 "Reduce CPU stalls in the GPU command processing pipeline" (2026-01-18)
- **Changes:** smaller scheduler lock scope across channels; DMA pusher 16-command prefetch; pre-allocated macro
  parameter vectors; last-executed macro cache instead of a hash lookup; unrolled dirty-register loop.
- **In BOTW-PC:** Ryujinx dispatches macros by array index (`GPFifoClass._macros[index]`), its macro argument queue
  (`Queue<FifoWord>`) reuses its storage, BotW uses one GPU channel, and GPFIFO entries are processed from a span.
  The equivalents already exist or do not apply; not ported.

### Eden #4085 in-flight flush throttling, #4182/#4094/#4477 buffer-cache synchronisation reworks
- Accuracy fixes (TotK shrine flicker) or Eden-specific buffer-cache designs; no matching bottleneck measured here.

### bloodborne_pc object motion vectors (`docs/motion_vectors.md`, `vk_object_motion.cpp`, GPL-2.0)
- **Design:** per-draw vertex position history keyed by draw identity (shader, vertex streams, index content hash,
  instance) for skinned draws only, previous clip position written by a vertex-shader variant; camera vectors for the
  rest. Their own numbers: all G-buffer draws 77 → 49 FPS; restricted to skinned draws ~81–87 FPS.
- **Relevance:** the remaining DLSS ghosting is on Link (skinned). Porting needs a shader-translator variant and a
  history store in the Ryujinx GPU frontend — large; design reference only (licence). See DLSS.md for what was done.

### NVIDIA DLSS SDK 310.9.1 render presets
- Default presets: K (transformer) for DLAA/Quality/Balanced, M for Performance, L for Ultra Performance; J is "similar
  to K, slightly less ghosting at the cost of extra flickering". The bridge sets no hint, so the mode selects the
  model. Used for the ghosting comparison (DLSS.md).

## Architecture round (2026-10-15): pipelined GPU command processing

Question: can GPFIFO decoding, command preparation, draw state/binding and backend submission run as overlapping
stages? Sources were read for their design (licences forbid copying their code into the MIT runtime).

| Source | Licence | What it does | Fit for BOTW-PC |
|---|---|---|---|
| Ryujinx/Ryubing (this tree) | MIT | Two stages already: `GPU.MainThread` (decode, registers, macros, state, bindings, buffer/texture caches) → threaded GAL ring → `GUI.RenderThread` (Vulkan pipeline state, descriptors, barriers, render passes, recording, submit) | the base |
| Eden `video_core/gpu_thread.*`, `renderer_vulkan/vk_scheduler.*` | GPL-3.0 | Also two stages, split lower: the GPU thread does decode **and** pipeline/descriptor/barrier work; the scheduler worker only replays recorded `vkCmd*` chunks | no new stage boundary to reuse |
| bbport / bloodborne_pc `docs/parallel_gpu.md`, `vk_draw_pipe.h` (shadPS4 renderer) | GPL-2.0 | **Two-stage draw pipeline**: stage A decodes PM4, keeps the register file and selects pipelines; per direct draw it writes a packet (32-word register blocks changed since the last packet, user data, draw parameters) into a 16 MiB ring; stage B applies the blocks to its own register copy and owns the texture/buffer caches, barriers and recording. Everything else drains B first; fences and label writes run in stream order on B; a verify mode carries the full register file every Nth packet. Their result: +18–32 % FPS in a scene where the GPU command thread was 100 % busy | the design to follow: in W1 the GPU command thread is the critical thread (~0.5 ms slack) and the render thread is not (~2.3 ms slack; PERFORMANCE.md "Architecture round"). A per-command-buffer decode stage was measured slower (+1.3 ms); the hand-off must be per batch of draws, as in bbport |
| ScarletNX | — | no repository or design found (web search 2026-10-15); `ARCHITECTURE_DECISION.md` notes it does not run BotW | — |

Mapping bbport's design onto Ryujinx (worked out, not implemented): stage A = `GPFifoProcessor` decode +
`DeviceStateWithShadow` register file (shadow RAM modes) + MME macros (they read registers); stage B = register
callbacks, `DrawManager`/`StateUpdater`, buffer/texture managers, HLE macros. Measured split (`BOTWPC_PROFILE=1`,
corrected exclusive time): A ≈ 2.6 ms of 9.3 (decode and register writes 1.64, macro interpretation 0.68, fetch
0.27), B ≈ 6.7 ms. State that B-side code writes back into registers and that A would have to mirror:
`ConstantBufferUpdater` (uniform buffer offset), `DrawManager` (first vertex/instance), `ThreedClass.UpdateIndexBuffer`,
blend/colour-mask/shader-bind HLE macros, `RenderEnableCondition`. BotW also uses `DrawElementsIndirect` (HLE, reads
GPU-written memory: a drain point).

## Round 4 (2026-10-15): yuzu, Eden, Cemu + FPS++

The full comparison (sources and revisions, subsystem by subsystem, licences, what was adopted, tried or excluded) is
in `ARCHITECTURE_COMPARISON.md`. Adopted, re-implemented from the algorithm: the yuzu/Eden buffer-cache page table
(+1.1 FPS on W2) and bind-time binding memoization (−0.2 ms GPU thread). Tried and rejected: per-slot uniform dirty
masks (yuzu/Eden), coalesced reprotection (Eden #3288), a bbport-style two-stage front end. Excluded because they
change guest-visible results: yuzu Normal-accuracy fences, Cemu's asynchronous stale readbacks, FPS++'s fence skip.
The canonical Ryubing repository was still unreachable; the `mirror` remote (to 2026-08-05) was reviewed instead.
