# CPU V2: ARMeilleure high-quality tier rewrite (Runtime V2, 2026-10-15)

BOTW-PC runs BotW's AArch64 code with **ARMeilleure**, Ryujinx's C# JIT, not Dynarmic (Dynarmic is the yuzu/Eden JIT;
`DYNARMIC_OPTIMIZATION.md` compares the two and lists which Dynarmic techniques were adopted). CPU V2 is a set of
changes to ARMeilleure's high-quality (HighCq, SSA) tier — the tier every one of BotW's ~73 500 profiled functions runs
in once the PPTC cache is warm. The low tier, the decoder and the instruction emitters are shared and unchanged.

Selection: `runtime.cpu_core` = `Legacy` (default) / `V2` in `<data>/botw/settings.json`, or `BOTWPC_CPU_CORE=legacy|v2`;
`runtime.cpu_v2_abi` = `Exact` / `Flags` (default) / `Temporaries` (`BOTWPC_CPU_V2_ABI`); experiments:
`BOTWPC_CPU_V2_FEATURES=<hex>` (`ARMeilleure.CpuV2Features`). Chosen once, before the first translation
(`JitCpuContext` static constructor); never switched while guest code runs. Each feature set has its own PPTC code
cache file (`<version>-<selector>-v2-<mask>.cache`); the PPTC profile (`.info`) is shared, so switching rebuilds only the
code cache (~21 s with 15 threads on the first start).

## Where the guest time went (measured first)

`GuestSampler --jit` (new) samples the guest threads and disassembles the hottest JIT functions from the live process
with samples per host instruction (`bench/v2/v2-jit2`, W2). The new offline corpus harness
(`Ryujinx.Tests/BotwPc/JitCorpusTests`) recompiles the 4 482 functions that took guest samples, from a local runtime
image of the 1.6.0 main module (never committed), exactly as the emulator does (HighCq, PPTC-relocatable,
HostMappedUnsafe), and classifies every emitted x86-64 instruction; weights = the function's share of guest samples.
The image is the main module only (`U-King`, guest 0x8504000..0xB26A000, SHA-256 of the local image
`1e81ba31…0f74`): 3 991 of the 4 482 sampled functions, **86.5 % of the guest JIT samples**; the rest (nnSdk and other
modules) decode as empty in the harness and contribute almost nothing to the totals.

| | Legacy | Notes |
|---|---|---|
| Host instructions per guest instruction | 9.06 static, 8.86 sample-weighted | Dynarmic-class JITs are typically 2–4 |
| Register-to-register moves | 17.7 % (15.4 % weighted) | phi copies, split moves, 32/64-bit conversions |
| Stack spills + reloads | 27.9 % (20.8 % weighted) | |
| NZCV materialization (setcc + movzx) | 4.0 % | four booleans per flag-setting instruction, kept live to every exit |
| Live samples on moves / spills | 29 % / 26 % of JIT samples | `v2-jit2`, `bench/v2/tools/jitclass.py` |

Causal test (new `BOTWPC_DELAY_GUEST_PCT`, each guest thread runs n % longer, in-run A/B): **+20 % guest time → +2.6 ms
frame time** (−11.6 FPS, 95 % CI −13.8 .. −9.4, `v2-causal-guestpct20`). The guest CPU is on the critical path, so code
quality of the JIT is a frame-time lever, not only a CPU-load one.

## Components

| Feature (`CpuV2Features`) | Code | What changes | Exact? |
|---|---|---|---|
| `ValueNumbering` | `CodeGen/Optimizations/ValueNumbering.cs` | Dominator-scoped value numbering of pure integer IR operations (commutative operands ordered), plus folding of `ConvertI64ToI32(ZeroExtend32/SignExtend32(x))` to x — the A64 emitter wraps every 32-bit operation in truncation/extension. Never merges loads, stores, calls, atomics, barriers, floating-point arithmetic or comparisons (FP results depend on the rounding/flush-to-zero state, which guest code can change between two identical operations) | yes |
| `SpillAtDefinition` | `LinearScanAllocator.PlaceSpillsAtDefinitions` | A value whose split intervals go to its stack slot more than once is stored once, right after its (single, or x86 two-address pair of) definition; the register-to-stack copies at split points and block edges are dropped. SSA values never change and siblings share one slot, so the slot is valid wherever a spilled interval reads it | yes |
| `DuplicateEdgeFix` | `Compiler.RemoveDuplicateEdges` | A conditional branch whose target is its own fall-through (e.g. `CBZ x0, .+4`) gives a block two edges to one successor; the allocator then emits that edge's resolution moves twice on one path (spill, fill, spill again), corrupting a value or crashing. The branch is removed before compilation. **Latent defect of the legacy allocator**, found by the CPU V2 differential tests (below) | yes (fixes legacy) |
| `PhiCopyOnEdge` | `SsaDeconstruction.cs` | SSA deconstruction appended every phi copy to the end of each predecessor, so a predecessor that branches elsewhere ran the copies on its other path too — typically the hot path past an early-exit check. Copies of a forward critical edge now go into a block on that edge; loop back edges keep them in the latch (no extra jump per iteration) | yes |
| `ReverseCopyHints` (round 2) | `LinearScanAllocator.NumberLocals`, `LiveInterval.TryGetCopyDestRegister` | The allocator already let a copy's destination prefer its source's register; it now also lets a value that is later copied into an already allocated interval (typically the phi temporary of a loop back edge, allocated at the loop entry) take that interval's register, so the copy becomes a same-register move that is not emitted. Registered at every copy, including later definitions of multiply defined phi temporaries. Register moves −9.7 % | yes (allocation choice only) |
| `CodeCleanup` (round 2) | `DeadMoveElimination.cs`, `CodeGenerator.FallsThroughTo` | Removes register moves and fills that a later move of the same resolution run overwrites before any read (split and edge resolution at one point), and omits a jump whose target is reached by falling through blocks that emit no code (e.g. edge blocks whose copies all coalesced). Jumps to the next instruction 12 177 → 3 760, dead moves 14 858 → 7 020 in the corpus | yes |
| `AbiFlagLiveness` (`cpu_v2_abi: Flags`) | `RegisterUsage.RunAbiPass`, `InstEmitFlow.Ret`, `InstEmitFlowHelper.EmitTableBranch` | Context loads/stores from a backward liveness analysis where each context store is a use of what it writes back. At A64 BL/BLR call sites and RET only, NZCV is not written back — AAPCS64 leaves the flags undefined on entry to and return from a call, and no AArch64 convention (incl. TLS descriptors and outline atomics) passes flags. The flag computations behind fused compare-and-branch then die. Every other exit (indirect jumps, decoder-limit exits, synchronization returns, SVC and exception paths) writes back every modified register | ABI-exact, not architecturally exact for code that reads NZCV across a call |
| `AbiTemporaryLiveness` (`Temporaries`, experimental) | same | Also X9–X17 (X8 at RET) and V16–V31 | standard convention only: special conventions keep temporaries live across calls |

Legacy (`CpuV2Features.None`) is byte-identical to the previous ARMeilleure: every change is behind its flag.

## Code quality (corpus, 4 482 hot functions, `bench/v2/corpus/*.csv.txt`)

| Configuration | Host instructions | per guest | weighted | Code bytes | Compile (1 thread) |
|---|---|---|---|---|---|
| Legacy | 8 947 145 | 9.06 | 8.86 | 49.7 MB | 16.6 s |
| + value numbering | 8 850 097 | 8.96 | 8.74 | 49.5 MB | — |
| + NZCV liveness (flags ABI) | 7 718 613 | 7.82 | 7.69 | 43.3 MB | 15.4 s |
| + spill at definition (≥ 1 spilled child) | 7 439 671 | 7.53 | 7.52 | 41.1 MB | 15.0 s |
| + phi copies on edges, spill at definition ≥ 2 children (**CPU V2 default, 0x1000F, benchmarked**) | **6 659 340** | **6.74** | **7.03** | **36.3 MB** | 17.8 s |
| same with spill at definition ≥ 1 child (not benchmarked in game) | 6 595 414 | 6.68 | 6.96 | 35.8 MB | 17.2 s |
| + reverse copy hints (round 2) | 6 554 050 | 6.64 | 6.93 | 35.9 MB | 17.7 s |
| + code cleanup (round 2; **CPU V2 default from round 2, 0x100CF**) | **6 533 070** | **6.62** | **6.91** | **35.8 MB** | 17.5 s |
| + temporaries ABI (experimental, 0x3000F) | not adopted | | | | |

Tried and not adopted (corpus, round 2): preferring caller-saved host registers on ties (`+0.07 %` instructions,
push/pop −0.8 %), and materializing relocatable constants (the guest memory base) once per function instead of once per
block (`mov imm64` 218 k → 69 k but reloads +81 k, moves +30 k: no net change, more loads).

Whole PPTC code cache (all 73 575 functions): legacy 185.1 MB, CPU V2 155.5 MB (−16 %).

Instruction mix, CPU V2 default (weighted %): register moves 13.8 (legacy 15.4), stack reloads 8.6 (10.2), stack stores
6.8 (10.7), NZCV setcc+movzx 2.5 (3.5). The largest remaining item is still register allocation (`NEXT_STEPS.md`).

## Correctness

`RUNTIME_V2_DIFFERENTIAL_TESTS.md` has the full matrix. In short:

- **Random-program differential tests** (`CpuV2DifferentialTests`, new): programs of 20–170 instructions using up to 28
  registers (ALU, shifts, bitfields, multiply/divide, conditional select/compare, carry arithmetic, loads/stores/pairs,
  forward branches, counted loops, calls) run on Unicorn and on ARMeilleure forced into the high tier. Exact features:
  1 000 programs, 0 mismatches; flag ABI: 1 000 conforming programs, 0 mismatches (NZCV excluded only at the final RET);
  temporaries ABI: 500, 0 mismatches.
- **Defects found and fixed during development:** (1) the duplicate-edge allocator defect above — it also makes the
  **legacy** JIT return wrong values (seed 636: X25) and crash the process with an access violation (seed 100915);
  (2) value numbering merged two floating-point additions across a flush-to-zero change (`EnvironmentTests.FpFlagsPInvoke`);
  fixed by restricting it to integer operations.
- **Stock instruction suite** (66 834 cases vs Unicorn) with the high tier forced: legacy and CPU V2 fail the same
  methods at the same rates (the forced high tier fails ~9 700 randomized cases with either code generator, a
  pre-existing harness/tier interaction); the only method failing only with CPU V2 was defect (2), see
  `RUNTIME_V2_DIFFERENTIAL_TESTS.md` for the re-run after the fix.
- **In game:** W2 runs with correct image, game speed 1.000 and no new log errors; see below and `PLAYTHROUGH_VALIDATION.md`.

## In-game result (W2, legacy vs CPU V2, same binary, startup switch)

`bench/v2/matrix.ps1`, 4 + 4 interleaved runs of 170 s (statistics after 90 s), DLSS off, immediate present, GPU front
end legacy in both; runs flagged by `run_check.py` (night-enemy Game Over) repeated. Guest CPU = CPU time of all
`HLE.GuestThread.*` threads over 105..145 s (`GuestSampler --cputime`) per frame. **The machine was not idle**: the
user's Minecraft (Modrinth) and League of Legends client were running, so absolute FPS is ~20 % below round 5 and not
comparable to it; legacy and CPU V2 alternate, so each pair saw the same background. Paired differences, 95 % CI (3 df).

| Metric | Legacy | CPU V2 | Change |
|---|---|---|---|
| Average FPS | 66.43 | 73.00 | **+6.57 (+2.48 .. +10.67), +9.9 %** |
| Median FPS | 67.79 | 74.24 | +6.45 (+2.34 .. +10.57) |
| Frame time (1000 / avg) | 15.06 ms | 13.72 ms | **−1.34 (−2.02 .. −0.65)** |
| P95 frame time | 17.80 ms | 16.04 ms | −1.77 (−2.62 .. −0.91) |
| P99 frame time | 21.48 ms | 19.03 ms | −2.46 (−4.53 .. −0.39) |
| 1 % low | 36.9 | 44.3 | +7.4 (−4.0 .. +18.9), not significant |
| Guest CPU time per frame (all guest threads) | 32.9 ms | 27.8 ms | **−5.1 (−7.0 .. −3.3), −15.6 %** |
| GPU command thread per frame | 11.66 ms | 11.24 ms | −0.42 (−0.96 .. +0.11), not significant |
| Guest wait for GPU readbacks | 0.44 ms | 1.05 ms | +0.61 (+0.51 .. +0.71) |
| Game speed | 0.999 | 1.000 | — |

The guest now reaches its readbacks earlier and waits longer for the GPU command thread: after CPU V2 the GPU thread is
again the first limit (`PERFORMANCE.md`, Runtime V2 causal tests). Raw data: `bench/v2/cpu2-*`, `bench/v2/cpu2.txt`.

## Portability

All IR changes (value numbering, liveness, phi placement, duplicate edges) are in the shared IR pipeline and apply to the
AArch64 host backend too (`CodeGen/Arm64/CodeGenerator.cs` calls the same passes); `SpillAtDefinition` is in the shared
linear-scan allocator. Only measured on Windows x86-64. iPadOS forbids writable+executable memory for third-party apps:
a JIT build there needs the debugger/entitlement path or an interpreter fallback; nothing here changes that constraint.
