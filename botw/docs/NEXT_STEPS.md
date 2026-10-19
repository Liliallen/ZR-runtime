# Next steps (in priority order, 2026-10-16, after Runtime V2)

Runtime V2 status: CPU V2 (ARMeilleure high tier) is the measured win (PERFORMANCE.md, "Runtime V2"); GPU V2 adds
verification and one fidelity fix; HLE V2 is neutral; Memory V2 has no retained change. Defaults remain legacy until
the gameplay gate is passed on saves beyond the Great Plateau.

1. **Promote CPU V2** (`runtime.cpu_core = V2`, ABI `Flags`) after playing saves with dragons, horse, combat, shrines,
   towns and the DLC trials on it (and one session with `cpu_v2_abi: Exact` as a control if anything looks off). The
   differential tests, the corpus and W2 all pass; what is missing is breadth of game content.
2. **GPU command thread (now the first limit, 78 % transfer):** the remaining lever is structural — move binding
   resolution, buffer/texture synchronization and draw-state updates (~6.4 ms) off the command thread with *batched*
   hand-off (per group of draws, not per command buffer), keeping one decode. Needs the buffer cache's synchronization
   split from binding resolution first. Every isolated shortcut on this thread has measured ≤ 0.2 ms (rounds 3–5, V2).
3. **CPU V2, next allocator work:** loop-header edge resolution still emits long register shuffles (up to ~25 moves per
   back edge in the hottest function); a loop-aware allocation order or interval splitting at loop boundaries; context
   rematerialization (reload an unmodified guest register from its context slot instead of spilling it); measure with
   `JitCorpusTests` first, then in game.
4. **Guest causal re-check after (2)/(3):** with CPU V2, +20 % guest time still costs 1.4 ms; re-run
   `BOTWPC_DELAY_GUEST_PCT` and `BOTWPC_DELAY_GPU_US` after each structural change to see which chain leads.
5. **DLSS GPU-thread cost:** see PERFORMANCE.md (Runtime V2, DLSS) for this round's controlled comparison.
6. **Memory tracking:** the ~63 buffer pages written every frame cycle fault → sync → re-arm; protection-free write
   detection (soft-dirty / userfaultfd-wp on Linux, JIT write barriers) is the only route that avoids both faults and
   extra copies (`MEMORY_TRACKING_V2.md`).
7. **Linux and Apple:** run the same matrix on Linux (shared code); build the AArch64 host backend on macOS to check the
   CPU V2 IR passes there (the x86-only cleanup pass does not apply); iPadOS needs a JIT-less fallback.

## Carried over from round 5

1. **Person-in-the-loop checks** (cannot be automated here): the in-game overlay with the real controller (L3 + R3,
   navigation, no stray L3/R3 in game), mouse and keyboard; every Xbox button in the controller test window;
   Bluetooth, Steam Input, reconnect; rumble feel; View-hold virtual motion. Report any `MISMATCH`.
2. **Saves beyond the Great Plateau** (user-supplied, copied into an isolated data dir) to extend
   `PLAYTHROUGH_VALIDATION.md` and `regress.input`: dragons (FPS++ issues #544/#595 are about their paths at high frame
   rate), horse, glide, swim, combat with ragdolls, water physics, Korok chases, Master Mode / Trial of the Sword /
   Champions' Ballad (Master Cycle). FPS++'s per-system list (ARCHITECTURE_COMPARISON.md) is the checklist; each item
   needs its 1.6.0 counterpart located (Ghidra on the relocated dump) before any fix.
3. **Optimized GPU front end → default** (`GPU_FRONTEND_REWRITE.md`, promotion gates): run the regression scenario and
   longer play sessions with `BOTWPC_GPU_FRONTEND=optimized` on later saves (dragons, horse, combat, shrines, towns) and
   on Linux; if no regression appears, switch the default (the legacy path stays selectable).
4. **120 FPS** (W2: optimized ~86–89 FPS = 11.2–11.6 ms, needs 8.33 ms). Evidence after round 5:
   - GPU command thread still critical (63 % of a delay reaches the frame, was 77 %); render thread ≈1.3 ms slack.
   - Decode + register writes are down to ~6.4 ns per word (1.6 ms/frame); the rest of the GPU thread is the draw path
     (binding commit ~37 %, state updates, backend queueing), each part required by the memory model and already
     reduced. Three more exact shortcuts measured nothing (sync memo, render-thread poll, command prefetch).
   - Remaining single-thread candidates (each ~0.1–0.3 ms, unmeasured): shader lookup without the code compare
     (needs write tracking on shader code incl. untracked GPU flushes), cached vertex-attribute format resolution,
     GPU-VA translation cache for uniform-ring binds. They do not add up to 3 ms.
   - **DLSS mode** adds ~1.5 ms to the GPU thread in W2 (legacy 10.96 vs 9.48 ms/frame, one run each): profile the
     per-draw camera capture and DLSS bookkeeping on the GPU thread first; it is the largest unexplained cost found.
   - Guest CPU: a GPU-thread delay also raises the guest's readback wait (+0.77 ms); the frame's tail after the readback
     is guest JIT code (ARMeilleure), the next lever once the GPU thread drops further.
5. **Benchmark scene**: W2's night enemies ended ~40 % of long runs this round; a second player save (daytime, safe
   spot) would make A/B runs cheaper. A GPFIFO capture/replay harness with the guest memory reads it depends on is needed
   before any front-end component that changes memory-side behaviour (`GPU_FRONTEND_DIFFERENTIAL_TESTS.md`).
6. **Cross-platform measurements**: re-run W2's in-run A/Bs on Linux (same shared code) and, when an Apple build
   exists, on macOS/MoltenVK; nothing this round depends on Windows or NVIDIA.
7. **DLSS quality**: wind-animated foliage and character animation still lack object motion vectors (DLSS.md).
8. **Overlay**: fullscreen / 1440p / 4K check, touch input for iPad, a Metal/MoltenVK presenter hook.
9. **Mods**: UKMM merge of the conflicting real mods (needs the user's unpacked RomFS dump).
10. **Upstream Ryubing**: merge the post-fork mirror commits (allocation work, lock-free buffer lookups, invalid-texture
   bitmap; MIT) when the canonical repository is reachable again; low expected FPS gain here.

## Not worth pursuing (measured)
Thread-split GPU front end (both granularities), per-slot uniform dirty masks, batched page reprotection, buffer-sync memo, render-thread poll before blocking, command-buffer prefetch, larger fault
windows, GPU-thread priority, render-thread buffer-map tables, GPFIFO bulk writes (OPTIMIZATIONS.md,
botw/experiments/README.md). Cemu's stale readbacks, yuzu's Normal-accuracy fences and FPS++'s fence skip are excluded
because they change what the game reads.

## AOT / static recompilation — verdict: not now
See `ARCHITECTURE_DECISION.md` and `BACKEND_COMPARISON.md`: AOT reaches JIT parity at best (mk8-recomp), ScarletNX does
not run BotW, and the guest code is not the first limit (the GPU command thread is).
