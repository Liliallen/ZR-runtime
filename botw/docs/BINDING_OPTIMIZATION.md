# Buffer and texture binding work

Binding commit is the largest single part of the GPU command thread in BotW (≈37 % of its busy time inclusive in W2,
buffers 23 %, textures 10 %). This document records what that work is, what rounds 3–5 changed, and what was tried and
rejected, with the correctness conditions each change depends on.

## What happens per draw (Ryujinx)

For each of ~5 000 draws per frame (`MAXWELL_COMMAND_ANALYSIS.md`):

1. **Index buffer:** if its binding changed (2 575/frame): look up the host buffer, synchronize guest → host, bind;
   otherwise synchronize only (2 430/frame).
2. **Vertex buffers:** same, ~2 per draw (2 430 rebinds, 9 970 synchronizations per frame).
3. **Uniform buffers:** if any uniform slot of the program changed (78 % of draws): look up, synchronize and re-send
   every uniform binding of every stage (24 770/frame); otherwise synchronize each (7 090/frame).
4. **Storage buffers, transform feedback, buffer textures:** rare in BotW.
5. **Textures:** for each of ~3.5 texture bindings, read the handle from the bound constant buffer in guest memory,
   compare with the cached handle (and the texture's invalidation sequence and the pools' modification sequences);
   unchanged (91 %): synchronize the cached texture; changed: look up texture and sampler in the pools, bind.

"Synchronize" is the CPU-write detection: page-protection tracking marks guest pages written by the CPU; a
synchronization of a range uploads its dirty pages and re-protects them. Ryujinx does it once per GPU sequence per
tracking handle (a sequence bitmap), so later checks in the same sequence are cheap.

## Kept changes (rounds 3–4)

| Change | Idea | Measured | Doc |
|---|---|---|---|
| Lookup cache (round 3) | direct-mapped cache of the buffer containing a looked-up page | see OPTIMIZATIONS.md #4 | OPTIMIZATIONS.md |
| Skip redundant binds (round 3) | a set that equals the current binding does not dirty the kind | see OPTIMIZATIONS.md #5 | |
| Binding memo (round 4) | a vertex/index/uniform set of the (GPU VA, size) already bound, with unchanged page-table and buffer-cache sequences, skips translation and lookup | GPU thread −0.20 ms | #7 |
| Buffer page table (round 4, yuzu/Eden algorithm) | 4 KiB page → buffer table instead of a binary search under a lock | +1.12 FPS, GPU thread −0.32 ms | #8 |

Correctness conditions: a memo is valid only while the GPU page table (`MemoryManager.MappingSequence`) and the buffer
cache (`BufferCache.ModificationSequence`: buffers merged, removed or unmapped) are unchanged; GPU VA equality alone is
never used. The page table is updated under the buffer list's write lock and every hit re-checks containment.

## Round 5: tried and rejected

| Change | Design | Correctness | Measured | Status |
|---|---|---|---|---|
| Buffer-sync memo (`buffer-sync-memo.patch`) | per slot, remember the buffer and GPU sequence of the last synchronization; skip lookup + sync when the same range was synchronized in the same sequence and nothing else (forced dirty range, virtual dependants, buffer-cache change) can make the sync do work | first version **wrong** for 4 KiB whole-buffer-tracked buffers (verifier: 3 564 mismatches in 232 M), fixed, then 0 in 138 M | +0.13 FPS (95 % CI −0.04 .. +0.29), GPU thread +0.03 ms | rejected: the skipped work was as cheap as the memo check after round 4's page table |
| Per-slot uniform dirty (round 4, `ubo-slot-dirty.patch`) | re-resolve and re-send only changed uniform slots | — | +0.06 ms (CI −0.16 .. +0.27) | rejected (round 4) |

## Textures: analysed, not changed

The per-draw handle read cannot be skipped: the handles live in guest memory that the CPU and inline uniform updates
write at any time, and a CPU write is visible to the read immediately (unlike buffer contents, which are picked up once
per sequence). After the handle compare, an unchanged binding costs a flag test (`Texture.SynchronizeMemory`). yuzu/Eden
also re-read every handle and re-read the 32-byte descriptor per draw (`ARCHITECTURE_COMPARISON.md`). No exact shortcut
with a measurable gain was found.

## Not done, with the reason

| Idea (yuzu/Eden) | Reason |
|---|---|
| Upload small uniform buffers by copying from guest memory into a stream buffer per draw (Eden's `uniform_buffer_skip_cache_size` path) | changes the data path (copies instead of tracked host buffers) and memory traffic; large backend change, gain unmeasured; a candidate for the render thread when it becomes critical |
| Skip CPU-dirty checks for bound buffers | Eden does not skip them either; Ryujinx's per-sequence bitmap already makes repeated checks cheap |
