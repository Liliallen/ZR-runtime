# Dynarmic and ARMeilleure: what CPU V2 took from where

The Runtime V2 brief asked for changes to "the Dynarmic JIT". BOTW-PC does not use Dynarmic: it is a Ryujinx/Ryubing
fork, and Ryujinx's AArch64 JIT is **ARMeilleure** (C#, in `src/ARMeilleure`, MIT). Dynarmic (C++, used by yuzu and its
successors such as Eden) was therefore used as a design reference, and CPU V2 was implemented in ARMeilleure, the code
that actually runs BotW. No Dynarmic code was copied; nothing in this round came from GPL sources.

## Pipeline mapping

| Stage | Dynarmic | ARMeilleure (BOTW-PC) | CPU V2 change |
|---|---|---|---|
| Unit of translation | basic block (with block linking, return stack buffer) | whole guest function (decoder follows branches up to calls/indirect jumps), tiered: low quality first, high quality after a call count, PPTC disk cache | none (BotW's ~73 500 functions all run from PPTC in the high tier) |
| Decoder / emitters | A64 decoder → IR emitter per instruction | `Decoders`, `Instructions/InstEmit*` | BL/BLR/RET mark AAPCS64 boundaries for the liveness pass |
| Guest register state across units | in `JitState`, loaded/stored at block boundaries; `A64GetSetElimination` removes redundant get/set inside a block | in `NativeContext`; `RegisterUsage` loads live-in registers at entry and after calls, stores modified ones at exits and before calls; SSA renames them to locals in between | backward liveness with ABI-aware call/return stores (`AbiFlagLiveness`): NZCV not written back at BL/BLR/RET |
| IR optimization | constant propagation, dead code elimination, get/set elimination, identity removal, polyfills | constant folding, simplification, copy propagation, compare propagation, DCE (`Optimizer.cs`), tail merge, block placement | **value numbering** (dominator-scoped, integer only) with truncation/extension folding |
| Flags | NZCV computed lazily, kept in host flags inside a block | four booleans per flag-setting instruction (setcc + movzx), fused compare-and-branch for B.cond | dead after calls/returns under the flag ABI, so most of them are eliminated |
| Register allocation | per block, simple allocator over host registers, spills to `JitState` | linear scan (Wimmer) over the whole function, SSA out via phi copies | store-once spills, phi copies on edges, reverse copy hints, dead-move cleanup, duplicate-edge fix |
| Memory access | fastmem (host-mapped, fault handler + recompile on fault), page table fallback | host-mapped (`HostMappedUnsafe` here): base + address, base materialized per block | unchanged (tried a function-wide base: no net gain) |
| Dispatch | block linking, RSB for returns, fast dispatch table | function table (sparse two-level), direct calls through table slots, return address check | unchanged |

## Techniques compared

| Dynarmic technique | Status in ARMeilleure before | CPU V2 |
|---|---|---|
| Get/set elimination of guest registers inside a unit | covered by SSA over the whole function | — |
| Dead flag computation removal | only within a function (flags always written back at exits/calls) | **adopted in spirit:** flags dead at calls/returns (ABI) — ARMeilleure's whole-function units make this possible where a block JIT cannot know the next block's needs |
| Constant propagation / DCE | present | value numbering adds CSE |
| Block linking | function table calls are already direct loads of the target's host address | — |
| Fastmem | present (host-mapped modes) | — |
| Register allocation | Dynarmic's is local and simple; ARMeilleure's linear scan is global but produced 27 % spills/reloads and 17 % moves in BotW's hot code | **rewritten parts of spill and copy placement**, see CPU_V2_JIT.md |

## Why not port Dynarmic

Dynarmic is C++ with its own runtime interface (callbacks, memory, exclusive monitor, exceptions) and would replace
ARMeilleure's PPTC cache, Ryujinx's HLE interface (`ExecutionContext`, SVC handlers, signal handling for tracked memory)
and the AArch64 host backend at once. The measured problem (register allocation and flag liveness in ARMeilleure's high
tier) could be fixed in place, keeping PPTC, the HLE interface and both host backends, and making every change testable
against the legacy code generator in the same binary.

## Licensing

| Source | Licence | Use |
|---|---|---|
| ARMeilleure / Ryujinx | MIT | modified in place, notices kept |
| Dynarmic | 0BSD (current upstream) | design comparison only; no code |
| Unicorn (test reference, existing dependency of `Ryujinx.Tests.Unicorn`) | GPL-2.0 (library used only by the test project, not shipped) | reference emulator in tests |
| Iced (new test/tool dependency) | MIT | x86-64 disassembly in tests and GuestSampler |
