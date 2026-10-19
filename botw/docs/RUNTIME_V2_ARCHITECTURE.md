# Runtime V2 architecture (2026-10-15/16)

Runtime V2 is a set of selectable replacements inside BOTW-PC's Ryujinx-derived runtime, chosen once at startup and
measured against the legacy runtime in the same binary. Branch `runtime-v2` (from `gpu-frontend` at `7dad73c8d`),
worktree `BOTW-PC-v2/`. Every component is behind its own switch; the legacy paths are byte-identical when V2 is off.

```
            BotW AArch64 code (U-King, nnSdk)
                          |
        +-----------------+------------------+
        |  CPU V2: ARMeilleure high tier      |  value numbering, store-once spills, phi copies on edges,
        |  (CPU_V2_JIT.md)                    |  reverse copy hints, cleanup, NZCV call liveness, duplicate-edge fix
        +-----------------+------------------+
                          |  SVC / IPC
        +-----------------+------------------+
        |  HLE V2: kernel scheduler           |  Horizon yield schedule count (HLE_SCHEDULER_V2.md)
        +-----------------+------------------+
                          |  guest memory (host-mapped), write tracking (MEMORY_TRACKING_V2.md)
        +-----------------+------------------+
        |  GPU V2: Maxwell front end          |  round-5 fast dispatch + HLE macro differential verifier
        |  (GPU_FRONTEND_V2.md)               |  + DrawArraysInstanced register fidelity
        +-----------------+------------------+
                          |  GAL (threaded) -> Vulkan, DLSS (unchanged)
```

## Selection

`<data>/botw/settings.json`:

```json
{
  "runtime": { "cpu_core": "V2", "cpu_v2_abi": "Flags", "memory_tracking": "Legacy", "hle_scheduler": "V2" },
  "graphics": { "gpu_frontend": "V2" }
}
```

| Component | Setting | Environment override | Values | Default |
|---|---|---|---|---|
| CPU JIT | `runtime.cpu_core` | `BOTWPC_CPU_CORE` | `Legacy`, `V2` | Legacy |
| CPU V2 ABI level | `runtime.cpu_v2_abi` | `BOTWPC_CPU_V2_ABI` | `Exact`, `Flags`, `Temporaries` | Flags |
| CPU V2 feature mask (experiments) | — | `BOTWPC_CPU_V2_FEATURES` (hex) | `ARMeilleure.CpuV2Features` | — |
| GPU front end | `graphics.gpu_frontend` | `BOTWPC_GPU_FRONTEND` | `Legacy`, `Optimized`, `V2` | Legacy |
| Memory tracking | `runtime.memory_tracking` | `BOTWPC_MEMORY_TRACKING` | `Legacy`, `V2` | Legacy (V2 currently has no retained component) |
| HLE scheduler | `runtime.hle_scheduler` | `BOTWPC_HLE_SCHEDULER` | `Legacy`, `V2` | Legacy |

The selection is resolved once (`Ryujinx.Common.BotwPc.RuntimeV2`, logged as `BOTW-PC Runtime: ...` at start):
CPU V2 before the first translation (`JitCpuContext` static constructor), the GPU front end when the GPU context starts.
Nothing is switched while guest instructions or GPU commands are in flight. CPU V2 feature sets get separate PPTC code
caches (`<version>-<selector>-v2-<mask>.cache`) sharing one profile, so switching back and forth never loads code
compiled by another code generator (first start with a new mask: ~21 s of background compilation at boot).

## What is shared between the cores

- **Memory:** CPU (host-mapped guest memory, page-protection write tracking) and GPU (buffer/texture/pool caches) use
  the same `Ryujinx.Memory.Tracking` handles. Runtime V2 measured them per resource kind (73 guest write faults and 166
  reprotects per frame in W2, nearly all buffer pages) and tried one change (streaming handles), which was slower and
  was not kept (`MEMORY_TRACKING_V2.md`).
- **Synchronization:** guest syncpoints/semaphores and readbacks are unchanged (strict, exact); CPU V2 made the guest
  reach its readbacks earlier, which moved the bottleneck to the GPU command thread (`CPU_GPU_SYNCHRONIZATION.md`).
- **Scheduling:** HLE V2 only changes when a yield does nothing; thread selection and priorities are unchanged.

## Correctness instruments added

| Instrument | Checks | Doc |
|---|---|---|
| `CpuV2DifferentialTests` | random AArch64 programs, CPU V2 vs Unicorn (exact and AAPCS64 modes) | `RUNTIME_V2_DIFFERENTIAL_TESTS.md` |
| CPU suite hooks (`BOTWPC_TEST_CPU_V2`, `BOTWPC_TEST_FORCE_HIGHCQ`, `BOTWPC_TEST_IR`) | the stock 66 834-case instruction suite against CPU V2 in the high tier | same |
| `JitCorpusTests` | BotW's own hot functions recompiled offline, code quality per feature | `CPU_V2_JIT.md` |
| `MacroVerifier` (`BOTWPC_GPU_VERIFY=1`) | every HLE macro against the guest macro code in the reference interpreter | `GPU_FRONTEND_V2.md` |
| `FrontendVerify` (round 5) | fast register writes vs the legacy write path | `GPU_FRONTEND_DIFFERENTIAL_TESTS.md` |

## Tools added

`GuestSampler --jit` (hot JIT code disassembly with host/guest ratios), `BOTWPC_DELAY_GUEST_PCT` (proportional guest
causal delay), per-resource memory tracking counters and HLE yield counters (`BOTWPC_PROFILE=1`), `bench/v2/*.ps1`
(local: matrix, A/B, stacks, managed sampling), `bench/v2/tools/*.py` (matrix statistics, JIT instruction classes,
guest non-JIT time).

## Platform scope

All changes are in shared managed code: the ARMeilleure IR passes run for the x86-64 and AArch64 host backends,
`SpillAtDefinition`/`ReverseCopyHints` are in the shared allocator, `CodeCleanup` (dead resolution moves, jump
elision) is wired into the x86-64 code generator only (the AArch64 code generator keeps its legacy emission), and
nothing depends on Windows except the benchmarks. Only Windows x86-64 was built and measured. Linux x86-64 should behave the same (same code paths, not run);
macOS/iPadOS need an AArch64 host build, which was neither built nor tested — and iPadOS allows JIT only under
debugger/entitlement conditions, so a shipping iPadOS runtime needs an interpreter or AOT fallback regardless of V2.
