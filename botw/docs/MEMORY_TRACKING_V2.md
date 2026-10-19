# Memory tracking V2 (Runtime V2)

Guest memory in BOTW-PC is host-mapped (`HostMappedUnsafe`); CPU writes to memory the GPU caches are detected by page
protection (`Ryujinx.Memory.Tracking`: `RegionHandle`, `MultiRegionHandle`, `VirtualRegion`). The GPU side consumes
dirty flags when it synchronizes buffers, textures and descriptor pools, then re-arms the protection. Code-cache
invalidation of the JIT is separate (unmap/`InvalidateJitCacheRegion`) and unchanged.

## Measurement first (new per-resource counters, `BOTWPC_PROFILE=1`, W2, CPU V2, `v2-prof-track1`)

| Per frame | Buffer | Texture | Pool | Total |
|---|---|---|---|---|
| Handle made dirty by a guest write | 62.3 | 0.6 | 4.9 | — |
| Handle re-armed (protection changed back) | 63.4 | 0.9 | 4.9 | — |
| Check of a volatile handle (never re-armed, always dirty) | 141.9 | 0 | 0 | — |
| Guest write faults | | | | 73 |
| Reprotect calls (`VirtualProtect`) | | | | 166 (0.54 ms incl. timer overhead) |
| Buffer syncs (copies from guest memory) | | | | 418 |

Where it costs: on the GPU command thread, `MultiRegionHandle.QueryModified` (dirty-bit scans of each synchronized
buffer range, ~25 000 per frame) 5.4 % inclusive and page reprotection ~4 % (`v2-stacks-gpu1`). On the guest threads,
fault handling (`MemoryTracking.VirtualMemoryEvent` 0.21 %, reprotect 0.2 %) is negligible (`v2-guest-managed1`).
Descriptor pools, suspected at first, cause only ~5 cycles per frame.

The pattern: ~63 buffer pages per frame are written by the CPU once per frame and synchronized by the GPU many times.
Ryujinx's own volatility heuristic (a handle that is dirty again within fewer than 3 checks, 5 times in a row, is never
re-armed again) does not catch them, because they are checked many times between writes; they cycle through
fault → unprotect → sync → re-arm every frame.

## Component tried: frame-adaptive streaming handles

A handle re-armed in 4 consecutive frames stops being re-armed for 60 frames ("streaming"): it keeps its dirty flag, so
every check copies its data (the conservative state, no write can be missed), guest writes stop faulting and the GPU
thread stops reprotecting it; afterwards normal tracking resumes. Cancelled by any pre-action (GPU data that must be
flushed before CPU access) or unmapping. Selected by `runtime.memory_tracking = V2`, A/B-switchable
(`BOTWPC_MEM_STREAMING`).

| In-run A/B (`v2-ab-memstream1`, CPU V2 + optimized front end) | Result |
|---|---|
| FPS | **−1.10** (95 % CI −1.44 .. −0.75) |
| Frame time | +0.19 ms |
| Guest wait for GPU readbacks | +0.27 ms (CI +0.20 .. +0.35) |
| GPU command thread | +0.09 ms, n.s. |

Always-dirty pages are copied (and uploaded by the render thread) at every synchronization, which costs more than the
faults and reprotects removed, and delays the readback producers' submission. **Rejected**
(`botw/experiments/memory-streaming-handles.patch`). Memory V2 therefore has no retained behaviour change; the counters
and the analysis are kept.

## Correctness properties (for any future Memory V2 work)

- Granularity is separated: guest pages (4 KiB), host protection granularity (`MemoryBlock.GetPageSize()`), and GPU
  tracking granularity (`MultiRegionHandle` granularity per resource) are already independent in this code; nothing
  here assumes they are equal.
- A handle may only stop being re-armed if it stays dirty (every consumer then re-reads guest memory) and no pre-action
  is registered (GPU-modified data that must be flushed first).
- JIT code invalidation does not use these handles; guest code changes go through unmap/invalidate.

## What would be needed for a real win here

The GPU thread's tracking cost is mostly the per-sync dirty-bit scan (`QueryModified`) and the per-frame re-arm of the
same ~63 pages. A design that avoids both without extra copies needs write detection that does not use protection
(Linux soft-dirty or userfaultfd write-protect, Windows write-watch on non-shared memory — neither fits Ryujinx's
shared placeholder mapping on Windows) or software write barriers emitted by the JIT for stores to tracked pages
(cost on every guest store). Neither was attempted (`NEXT_STEPS.md`).
