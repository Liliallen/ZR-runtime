# Runtime V2 differential tests

How each Runtime V2 component is checked against the legacy implementation or an independent reference, what was run,
and what the tests found. Extends `GPU_FRONTEND_DIFFERENTIAL_TESTS.md` (round 5). All tests are in
`src/Ryujinx.Tests/BotwPc/`; those that need local game data skip themselves when it is absent.

## CPU V2 (ARMeilleure high tier)

| Test | Reference | Inputs | Run | Result |
|---|---|---|---|---|
| `CpuV2DifferentialTests.ExactFeaturesMatchUnicorn` | Unicorn 2 (independent AArch64 emulator) | 1 000 random programs, 20–170 instructions, ≤ 28 live registers: ADD/SUB(S) imm/shifted, AND/ORR/EOR/ANDS shifted (N), MOVN/MOVZ/MOVK, SBFM/BFM/UBFM, MADD/MSUB, UDIV/SDIV, LSLV/LSRV/ASRV/RORV, RBIT/REV*/CLZ/CLS, CSEL/CSINC/CSINV/CSNEG, CCMP/CCMN reg/imm, ADC/SBC(S), LDR/STR B/H/W/X, LDRSW, LDP/STP W/X, forward B.cond/CBZ/CBNZ/TBZ/TBNZ, counted loops (loop-carried phis), BL to a callee | CPU V2 exact features (`0xF`), forced high tier | **0 mismatches** (all X registers, NZCV, 4 KiB data page) |
| `…FlagLivenessMatchesUnicornForConformingPrograms` | Unicorn | 1 000 programs following AAPCS64 for NZCV (a compare right after each call; a callee sets flags before reading them) | `0x1000F` | **0 mismatches** (NZCV excluded only at the final RET, where the standard leaves it undefined) |
| `…TemporaryLivenessMatchesUnicornForConformingPrograms` | Unicorn | 500 programs that also rewrite X8–X17 after each call and never read an undefined temporary | `0x3000F` | **0 mismatches** (X8–X17 and NZCV excluded at the final RET) |
| `…SingleProgram` | Unicorn and a selectable baseline | one seed; `BOTWPC_FUZZ_REDUCE=1` shrinks a failing program to the instructions needed (NOP replacement), `BOTWPC_FUZZ_IR=1` dumps the IR of every pass | debugging aid | — |
| Stock instruction suite (`Ryujinx.Tests.Cpu`, 66 834 cases vs Unicorn) | Unicorn | single instructions and short sequences, randomized operands | `BOTWPC_TEST_CPU_V2=<mask> BOTWPC_TEST_FORCE_HIGHCQ=1` | see below |
| `JitCorpusTests.CorpusStatistics` | — (code quality) | BotW's 4 482 sampled functions from a local runtime image | each feature mask | `CPU_V2_JIT.md` |

**Stock suite in the high tier.** The suite normally runs in the low tier. Forcing the high tier makes about 9 700 of
its randomized cases fail with the **legacy** code generator as well (same 58–59 test methods, same failure rates with
either generator: a pre-existing interaction between the high tier and these tests, not investigated further here). The
comparison is therefore per method: a CPU V2 defect shows as a method failing only with CPU V2. One did:
`EnvironmentTests.FpFlagsPInvoke` — value numbering merged two identical floating-point additions on either side of a
flush-to-zero change. Fixed by restricting value numbering to integer operations and exact vector element moves.

**Defects found by the CPU V2 tests**

| # | Where | Symptom | Cause | Status |
|---|---|---|---|---|
| 1 | Legacy linear-scan allocator (ARMeilleure, pre-existing) | wrong register values (seed 636: X25 = 0), wrong NZCV (seeds 100328, 100540, 200095), **native access violation crashing the process** (seed 100915) | a conditional branch to its own fall-through block creates two identical CFG edges; edge resolution then splits "both" into a chain and runs the same spill/fill sequence twice on one path | fixed in CPU V2 (`DuplicateEdgeFix`); confirmed: every listed seed passes with only that fix enabled. Legacy left byte-identical (fallback); real games rarely branch to the next instruction |
| 2 | CPU V2 value numbering (development) | `FpFlagsPInvoke` fails | FP addition treated as pure | fixed (above) |
| 3 | CPU V2 value numbering + allocator (development) | X25 = host page-table pointer (seed 787) | exposure of defect 1 by changed allocation | fixed by 1 |
| 4 | Test generator (development) | false mismatches in the temporaries mode | MOVK and BFM read their destination; the generator treated it as write-only, so programs read undefined temporaries | generator fixed |

**Re-run after fix (2):** with the exact features (`0xF`) every one of the 59 failing methods fails at the same rate as
with the legacy code generator in the forced high tier (difference below 2σ per method), and no method fails only with
CPU V2. With the flag ABI (`0x1000F`) 3 414 additional failures appear in 43 flag-setting-instruction methods; all but 33
assert NZCV after the test's final RET (undefined there under AAPCS64, i.e. expected for that mode). The 33 others assert
FPSR.IOC after FCMP/FCMPE/FCCMP + RET: with NZCV dead, the host `comiss` intrinsics that only produce NZCV are removed,
and the FPSR bit those tests read is a side effect of those host instructions on MXCSR, which ARMeilleure's fast-FP path
does not model as a dependency ("TODO: Store FPSR" in `InstEmitSimdHelper`). The legacy code generator drops the same
instructions whenever NZCV is overwritten before an exit; the difference is a property of the ABI mode, not a new defect.

## GPU V2

| Check | Reference | Run | Result |
|---|---|---|---|
| Fast register writes (`FrontendVerify.FastWrite`, round 5) | legacy write path | W2, `v2-verify-macro4` (CPU V2 + GPU V2) | 585.6 M checks, 0 mismatches |
| **HLE macros** (`MacroVerifier`, new) | the guest's own macro code in the reference `MacroInterpreter`, recording register view | W2, `v2-verify-macro3` | 230.0 M register comparisons, **21 328 mismatches, all `DrawArraysInstanced` / `VertexBufferDrawState.Count`** (HLE left the register stale) |
| HLE macros after the GPU V2 fix | same | W2, `v2-verify-macro4` | **238.0 M comparisons, 0 mismatches** |
| Most-recent buffer lookup (rejected component) | buffer page table | W2, `v2-verify-mru1` | 81.0 M hits, 0 mismatches |

Macro verification covers register side effects of HLE macros (what the guest macro would leave in the register file);
the draws themselves (callback registers) are executed by the same code in every mode. Buffer bindings, texture
bindings, GPU memory writes, syncpoints/semaphores and readbacks run unchanged code in GPU V2 (no component that changes
them was retained), so the round-5 statement "program unchanged" still holds for them.

## Memory V2

No retained behaviour change (`MEMORY_TRACKING_V2.md`). The rejected streaming handles ran a full in-run A/B (330 s)
without errors; their design keeps handles dirty (conservative), so no missed write was possible by construction.
Memory and tracking unit tests (`Ryujinx.Tests.Memory`) on the final code (`6ad74a0fe`): 84 passed, 0 failed.

## HLE V2

The yield schedule count changes only when a yield returns early. Checked in game: in-run A/B and the final matrix
(full Runtime V2 configuration) and the 30/60/90/120 FPS regression scenario (`PLAYTHROUGH_VALIDATION.md`) ran
with HLE V2 without hangs, missing wake-ups, timeouts or new log errors; game speed
1.000 (`PERFORMANCE.md`). There is no deterministic kernel test harness in Ryujinx.Tests to compare thread schedules,
and schedules of independent live runs are not comparable instruction by instruction.

## Final run on `6ad74a0fe`

| Suite | Result |
|---|---|
| `Ryujinx.Tests` `--filter FullyQualifiedName~BotwPc` | 77 passed, 0 failed, 4 skipped (debugging aids and tests that need local data or environment variables: `SingleProgram`, `DumpFunctionIr`, `CorpusStatistics`, `FastRegisterWritesMatchLegacyPathOnGameTrace`) |
| `Ryujinx.Tests.Memory` | 84 passed, 0 failed |
