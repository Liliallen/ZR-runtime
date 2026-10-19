# Emulator architecture comparison for BotW (2026-10-15)

Ryujinx/Ryubing (this runtime) compared with yuzu, Eden, Cemu + FPS++ and bbport, from their source code, against the
costs measured in BOTW-PC on the W2 scene (PERFORMANCE.md, "Round 4"). Sources were read for their design: yuzu/Eden
are GPL-2.0+/GPL-3.0, Cemu is MPL-2.0 (file-level copyleft), FPS++ forbids redistribution of its parts, bbport is
GPL-2.0. **No code from them is in BOTW-PC**; ideas that were adopted are re-implemented and say so in their commits.

## Sources inspected

| Project | Revision | Where |
|---|---|---|
| yuzu (last mainline commit, via the Eden mirror) | `c280f95b1` (2024-03-03) | `github.com/eden-emulator/mirror` (no yuzu tags; mainline-0-1734 inferred) |
| Eden | `21c6ee319` (2026-10-12) | `github.com/UzuCore/eden` |
| Cemu | `2c212b524b49` (2026-10-13) | `github.com/cemu-project/Cemu` |
| cemu_graphic_packs (FPS++) | `54ece3f2ab5b` (2026-09-29) | `src/BreathOfTheWild/Mods/FPS++` |
| Ryubing upstream | mirror `793ca017f` (2026-08-05) | `github.com/Leuconoe/Ryubing` (the canonical `git.ryujinx.app` was unreachable again) |
| bloodborne_pc (bbport) | `3c57da4` | `docs/parallel_gpu.md` |
| ScarletNX | — | no public source found; does not run BotW (ARCHITECTURE_DECISION.md) |

Wii U (Cemu) and Switch (everything else) differ at every level that matters for porting: PowerPC vs ARM64 guest code,
GX2/Latte (PM4, R600-class) vs NVN/Maxwell command streams, explicit guest cache flushes (`GX2Invalidate`, `DCFlush`)
vs none on NVN. FPS++ patches are PowerPC machine code for BotW Wii U v208 and cannot apply to Switch 1.6.0.

## GPU command processing

| Subsystem | Ryujinx/Ryubing (BOTW-PC) | yuzu / Eden | Cemu | Reuse for BOTW-PC | Measured here |
|---|---|---|---|---|---|
| Decode | `GPFifoProcessor`: header state machine, per-word `Send` → engine register write with redundancy check, per-register callbacks; fast paths for uniform-buffer data and inline-to-memory spans | `DmaPusher`: whole segment read, non-incrementing runs as `CallMultiMethod`; a 64 K executable-method bitset; plain writes appended to a method sink, drained before the next executable method; u8 dirty-flag tables | `LatteCommandProcessor`: flat register array, no callbacks; draws continue a "draw pass" while only buffers/constants change | algorithm only (GPL / different API) | decode + register writes 2.2 ms, callbacks 1.3 ms of ~9–10 ms; an earlier bulk-write fast path measured −0.11 ms (not significant) |
| Macros | MME JIT/interpreter; **HLE for DrawElements/Instanced/Indirect**, shader bind, blend, uniform setup | xbyak JIT; HLE for indirect draws, clears, shader bind (DrawElements not HLE'd) | n/a (PPC GX2 API, no MME) | Ryujinx already HLEs more draw macros than yuzu | macros 0.86 ms exclusive |
| Threading | GPU thread (decode + state + bindings + caches) → threaded GAL ring (per-command atomics) → render thread (Vulkan) | GPU thread decodes and does pipeline/descriptor work; `vk_scheduler` worker replays 32 KB chunks of recorded lambdas, handed over every 8 draws | one thread decodes and records Vulkan | — | **render thread has ~1.4 ms slack on W2** (causal test); queue atomics ~1–2 % of the GPU thread |
| Two-stage decode (bbport design) | — | — | — | design reference | **implemented, −1.30 FPS** (packets cost as much as the work moved); rejected |

## State, bindings, caches

| Subsystem | Ryujinx/Ryubing (BOTW-PC) | yuzu / Eden | Cemu | Reuse | Measured here |
|---|---|---|---|---|---|
| Buffer lookup | sorted non-overlapping list (binary search, write lock in `CreateBuffer`); BOTW-PC direct-mapped cache | flat page table `addr >> 16 → BufferId` | page-hashed buffer cache | **re-implemented: `BufferPageTable` (4 KiB pages)** | **+1.12 FPS (CI +0.40 .. +1.85), GPU thread −0.32 ms: kept** |
| Uniform/vertex bindings | resolve every binding when any of its kind changes; re-resolve on each set | VA translated at bind time, buffer id resolved lazily, per-slot dirty masks | dirty bitmasks, only remapped constant ranges copied | **re-implemented: binding memo** (skip identical sets); per-slot dirty | memo: GPU thread −0.20 ms (CI −0.33 .. −0.07): kept. Per-slot dirty: no effect, rejected |
| CPU-write detection | page protection (`RegionHandle`, `MultiRegionHandle` bitmaps, sequence numbers), reprotect per 4 KiB page | page protection + per-4 MB bitmaps of CPU/GPU/cached state; Eden coalesces page ranges before protection changes | none: guest flush calls + page hashing | batching re-implemented | **no effect** (BotW's dirty pages are not runs), rejected |
| Shader lookup | address dictionary + **byte compare of the shader binary on every shader change** | address map, invalidated by page-protection callbacks; no compare | guest hash keys | algorithm only | compare ≈ 1.7–2 % of the GPU thread; not done (needs the untracked-write hooks described in PERFORMANCE.md) |
| Texture bindings | handles re-read from constant buffers per draw; pool modification sequence skip | raw TIC/TSC cached per index, image dirty flag from write callbacks | per draw-pass | partly present already | texture commit 1.1 ms |
| Upstream Ryubing after the fork | 2025-10 .. 2026-08: allocation work ("Memory changes 3.x"), lock-free buffer lookups, invalid-texture bitmap | — | — | MIT, could be merged | allocation is 0.45 MiB/frame (GC 0.07 ms): little to gain; not merged this round |

## CPU/GPU synchronization and readback

| | Ryujinx/Ryubing (BOTW-PC) | yuzu / Eden | Cemu |
|---|---|---|---|
| Guest syncpoints/semaphores | incremented when the GPU thread processes them | Normal accuracy: signalled at submit, before host completion (**skips sync**); High: after the host fence | timestamps written when the command processor decodes them |
| Guest reads of GPU-written data | memory tracking flushes, waiting for the host fence; BOTW-PC submits the producing commands early (strict sync) | reactive flush on fault; downloads at fences; Normal mode skips some flushes | async texture readback started 5 draws later, polled each vsync; **the CPU reads stale data until it lands**; forced only at `GX2DrawDone` |
| Adoptable? | — | only the High-accuracy design; Normal-mode shortcuts change guest-visible results | no: stale readback data and fence skipping change what the game reads |

BotW reads exposure/lighting statistics back every frame (8×8 RGBA32F, 16×1/16×3 RGBA16F textures and two small
buffers, guest call chains in PERFORMANCE.md). Cemu's speed on this path comes from letting the game read last
frame's values; BOTW-PC keeps exact data and instead submits the producing work early.

## Guest CPU and timing

| | Ryujinx (ARMeilleure) | yuzu/Eden (dynarmic) | Cemu |
|---|---|---|---|
| JIT | tiered (PPTC cache), host-mapped memory, dispatch tables | block linking, return stack buffer, fastmem with recompile-on-fault, global exclusive monitor | PPC recompiler |
| Measured in BotW | flat guest profile (167 functions for 50 % of the main thread); fault handling ≈ 0.1–0.3 % of guest threads | — | — |
| High frame rate | BOTW-PC drives `ksys::VFR`'s ratio at runtime (no code patch); UI step and button repeat patched | none | FPS++ hooks the per-frame tick, feeds averaged FPS into ~20 hard-coded 30 FPS constants (arrows, stamina, flurry rush, GUI, map, audio loop, runes, Mastercycle), pins ragdoll impulses and Korok movement to 1/30, scales water drag |

FPS++'s list is the audit list for BOTW-PC's 60/90/120 modes (regression test: `botw/bench/regress.input`,
PERFORMANCE.md). Its open issues #544/#595 (dragons path at 1/3–1/4 speed since v834) follow its Korok `1/dt := 30`
pin in shared movement code; BOTW-PC pins no timestep, but dragons were not reachable from the test save.

## What to reuse, adapt or leave

| Item | Verdict | Why |
|---|---|---|
| Page table for buffer lookups (yuzu/Eden) | **adopted (re-implemented)** | measured +1.1 FPS |
| Skip unchanged binding sets (yuzu bind-time translation idea) | **adopted (re-implemented)** | measured −0.2 ms GPU thread |
| Per-slot uniform dirty masks (yuzu/Eden) | tried, rejected | no effect in BotW |
| Coalesced reprotection (Eden #3288) | tried, rejected | no effect in BotW |
| Two-stage draw pipeline (bbport) | tried, rejected | communication ≥ work moved in Ryujinx |
| Address-keyed shader cache with write-tracking invalidation (yuzu) | candidate | ~2 % of the GPU thread; needs post-write notification for untracked GPU flushes |
| Chunked GAL handoff (yuzu `vk_scheduler`) | candidate, low priority | helps the render thread, which has slack |
| Normal-accuracy fences, early semaphores, unsafe pushbuffer reads (yuzu) | **not adoptable** | skip synchronization |
| Async stale readback, fence skip, GX2DrawDone shortcuts (Cemu/FPS++) | **not adoptable** | change guest-visible data |
| FPS++ machine-code patches | **not portable** | PowerPC Wii U v208 addresses; licence forbids redistribution |
| FPS++ per-system timing list | **audit list only** | find the 1.6.0 equivalents first (REA/Ghidra), then decide |
| Upstream Ryubing allocation work | optional merge | MIT; allocation is not a bottleneck here |

## Cross-platform note

Both adopted changes are in `Ryujinx.Graphics.Gpu` (front end shared by every backend and OS); they add no OS,
GPU-vendor or graphics-API dependency, so they apply unchanged to Linux, macOS (MoltenVK) and a future iPadOS build.
They were measured on Windows only (Linux and Apple builds not run this round).

## Round 5 (GPU front end): what Eden does per word and per draw

Read from Eden `21c6ee3` (UzuCore/eden, 2026-10-12; files `GPL-3.0-or-later`, most also carrying the original yuzu
`GPL-2.0-or-later` header; the pre-fork yuzu tree itself was not available, the "mirror" HEAD is Eden). Ideas only;
BOTW-PC's code is independent (MIT).

| Stage | Eden | Ryujinx / BOTW-PC | Consequence for BOTW-PC |
|---|---|---|---|
| Plain register write | 64 K-bit `execution_mask`; a non-executable method is appended to `method_sink` (one bit test + vector push) and replayed before the next executable method: store + `dirty.flags[table[m]] = true` for two tables, **no value compare** | store with value compare, then state group bit; shadow RAM handled per write | BOTW-PC's fast dispatch does the per-write work in one table lookup; a sink would add a push and a replay per word and lose the compare that keeps 70 % of BotW's writes from dirtying anything |
| Runs | non-incrementing runs via `CallMultiMethod` (constant-buffer data as one memcpy, macro parameters as one insert) | constant-buffer data and inline data already have run fast paths (`TryFastUniformBufferUpdate`, `TryFastI2mBufferUpdate`) | nothing left: BotW's other runs are ~2 words |
| Command buffer fetch | whole segment as a zero-copy span into guest memory (safe mode flushes GPU-written data) | span into guest memory, flush unless skipped | same |
| Macros | HLE by code hash: indirect draws, multi-layer clear, shader bind (skips unchanged program), const-buffer setup, clears, TF setup; JIT (xbyak) for the rest, each `Send` a full `CallMethod` | HLE also for `DrawElements`/`DrawElementsInstanced` (BotW's main draw macros) and uniform setup | Ryujinx already HLEs more of BotW's macros |
| Buffers per draw | index/vertex re-resolved only when dirty, uniform when its binding was nulled by a bind; **`SynchronizeBuffer` for every bound vertex/index buffer every draw**; small uniform buffers copied from guest memory into a stream buffer every draw (no dirty check) | lookup + sync when dirty, sync only otherwise; per-sequence bitmap makes repeat checks cheap | equivalent; Ryujinx's model was kept |
| Textures per draw | every handle re-read from guest memory every draw; the 32-byte TIC/TSC entry re-read and compared with a cached copy; image-view lookup per binding | handle re-read and compared with the cached handle; descriptor re-read only when the handle or pool changed | Ryujinx does less per draw here |
| Pipeline per draw | stage refresh only when shaders dirty; fixed-function key repacked from registers every draw, compared with the current pipeline's key and its transitions | shader lookup only when the shader group is dirty (code compare); pipeline state kept incrementally | different trade-off; no gain expected from switching |

So the two emulators do the same per-draw work in different shapes; Eden's per-word sink is its distinct advantage, and
BOTW-PC's fast register dispatch is the equivalent saving for Ryujinx's register model (`GPU_FRONTEND_REWRITE.md`).

### Licensing decisions (round 5)

| Source | Licence | Used as |
|---|---|---|
| Eden (`src/video_core`) | GPL-3.0-or-later (with yuzu GPL-2.0-or-later headers) | design reading only; no code copied, no file translated |
| Ryujinx / Ryubing | MIT | base code (modified in place, MIT notices kept) |
| Cemu, FPS++ | MPL-2.0 / redistribution forbidden | nothing used this round |
| bloodborne_pc | GPL-2.0 | nothing used this round (its two-stage design was tried in round 4 and rejected) |

## Runtime V2 (2026-10-15/16): what each emulator does where BOTW-PC changed

| Area | Ryujinx/Ryubing legacy | yuzu / Eden | BOTW-PC Runtime V2 | Source of the idea |
|---|---|---|---|---|
| CPU JIT unit | function (tiered, PPTC) | basic block (Dynarmic) | unchanged | — |
| Flags at calls/returns | always written back | written back at block ends | NZCV dead at BL/BLR/RET (AAPCS64), `cpu_v2_abi` | ABI reasoning; ARMeilleure's whole-function units make it possible |
| Redundant pure operations | none removed across blocks | Dynarmic: per-block passes | dominator-scoped value numbering + truncation folding | classic compiler technique |
| Spill placement | store at every register→stack split | — (block-local allocator) | store once after the definition | classic (Wimmer, "spill at definition") |
| SSA deconstruction | copies at predecessor ends | — | copies on forward critical edges; reverse copy hints | classic (Boissinot et al. idea of edge placement; hints) |
| Branch to fall-through | two identical CFG edges → allocator defect | — | removed before compilation (fixes legacy defect) | found by differential tests |
| Yield | always enters the scheduler | Horizon yield schedule count | Horizon yield schedule count (HLE V2) | mesosphere / yuzu / Eden kernel (algorithm re-implemented; GPL code not copied) |
| HLE macro fidelity | `DrawArraysInstanced` skips the vertex-range registers | not checked this round | registers written (GPU V2) | found by the new macro verifier |
| CPU write tracking | page protection, volatility heuristic | page protection + bitmaps | unchanged (streaming handles tried, slower) | — |

Licensing: as in round 5 — no GPL code (yuzu/Eden, mesosphere/Atmosphère) was copied or translated; algorithms were
re-implemented from their descriptions. Dynarmic (0BSD) was a design reference only (`DYNARMIC_OPTIMIZATION.md`).
