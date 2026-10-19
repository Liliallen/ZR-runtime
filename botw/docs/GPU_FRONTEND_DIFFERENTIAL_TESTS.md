# Differential verification of the optimized GPU front end

The optimized front end (`BOTWPC_GPU_FRONTEND=optimized`, `GPU_FRONTEND_REWRITE.md`) may only skip or shorten work when
the result is the legacy result. Two independent checks enforce that: a runtime verifier that compares every optimized
step with the legacy computation inside the running game, and unit tests that replay register write streams (random and
captured from the game) through both paths. Neither runs the two front ends at the same time against the same mutable
state: the runtime verifier lets the legacy code perform the step (its effects are the ones that apply) and compares the
optimized prediction computed on copies; the unit tests use two separate register files.

## Runtime verifier (`BOTWPC_GPU_VERIFY=1`, `FrontendVerify`)

- Every comparison is counted per check; a mismatch is logged as an error with the frame number, the draw within the
  frame and component-specific details (first 50 in full), and a cumulative summary every 10 s:
  `BOTW-PC FE verify (cumulative): FastWrite <n> checks / <m> mismatches;`.
- Verification makes the optimized paths slower than legacy (~41 FPS instead of ~88 in W2). It is never on in
  benchmarks or normal play.

### Check `FastWrite` (fast register dispatch)

For every 3D register write that the fast path would handle (no write callback), the verifier
1. reads the register and its shadow value,
2. computes the fast path's result on copies (`FastRegisterWrite.Apply`: shadow RAM mode, stored value, state group),
3. lets the legacy path perform the write (`DeviceStateWithShadow.WriteWithRedundancyCheck` + `StateUpdater.SetDirty`),
4. compares stored value, shadow value, "value changed", the dirtied state group, and that the register has no write
   callback.

Writes of registers with a callback, and everything else the fast dispatch routes (macro starts, arguments and calls,
GPFIFO class methods), call the same functions with the same arguments in the same order as `Send()`; they are not
compared at run time (identical by construction) and are covered by the gameplay runs.

### Check `SyncMemo` (rejected component, kept as a record)

The buffer-sync memo skipped a bound buffer's guest-to-host synchronization when the same range had been synchronized
in the same GPU sequence. The verifier ran the legacy lookup and synchronization on every skipped one and compared the
buffer found, the host range and whether the synchronization did any work (`Buffer.SyncWork`).

- First verification run (`fe-verify1`): **3 564 mismatches in 232 million checks**, all on 4 KiB buffers. Cause: the
  whole-buffer (non-granular) tracking path records the sequence number only when it found the buffer dirty, so a clean
  check does not gate later writes in the same sequence. The equivalence argument had covered only the granular path.
- Fixed (`Buffer.SyncGatedBySequence`), re-verified: **0 mismatches in 138 million checks** (`fe-verify3`).
- Then measured: no performance effect, so the component was removed (`botw/experiments/buffer-sync-memo.patch`).

This is the verifier doing its job: a plausible "provably no-op" skip was wrong for one buffer class, and the error was
found from logged mismatches before any benchmark, not from screenshots.

### Results

| Run | Build | Checks | Mismatches |
|---|---|---|---|
| `fe-verify1` | sync memo (first version) | SyncMemo 232 M | **3 564** (design flaw, fixed) |
| `fe-verify2` | sync memo fixed + fast dispatch (passthrough shadow only) | SyncMemo 171 M, FastWrite 0.3 M | 0 |
| `fe-verify3` | sync memo fixed + fast dispatch (all shadow modes) | SyncMemo 138 M, FastWrite 698 M | 0 |
| `fe-verify-final` | final build `b195283ef` (fast dispatch) | FastWrite 813 M | **0** |

`fe-verify2` also showed that the fast path almost never applied: BotW keeps the shadow RAM in a track mode, which the
first version sent to the legacy path. The track and replay modes were then added to the fast path, under the same
check.

## Unit tests (`src/Ryujinx.Tests/BotwPc/GpuFrontendTests.cs`)

Two register files built on the real 3D register layout (`ThreedClassState`): the legacy one
(`DeviceStateWithShadow` with write callbacks on real callback registers + `StateUpdateTracker` with state groups on
real register fields) and the fast one (`FastRegisterWrite`). After **every** write the tests compare the whole
register file, the whole shadow register file, every state group's dirty bit and the callbacks run; every 64 writes a
"draw" consumes the dirty groups on both sides.

| Test | Input | Result |
|---|---|---|
| `FastRegisterWritesMatchLegacyPath(1..3)` | 3 × 300 000 random writes: a hot set of registers, few distinct values (many redundant writes), shadow RAM mode changes (track, track with filter, passthrough, replay) | pass |
| `SlowKindsAreExactlyTheCallbackAndOutOfRangeRegisters` | the kind table against the legacy callback table and group mapping, all 0xe00 methods | pass |
| `DetectsWrongFastPaths` | negative controls: a kind table that drops a state group, or misses a callback, must be reported; replay must take the shadow value | pass (both faults detected) |
| `FastRegisterWritesMatchLegacyPathOnGameTrace` | the game's own 3D register write stream: 8 000 000 writes captured in W2 gameplay (`BOTWPC_FE_TRACE`, ~50 frames), replayed with `BOTWPC_FE_TRACE_REPLAY=<file>` | pass (ignored when no capture is given) |

The game capture holds register values of the running game and stays local (`bench/local-traces/`, never committed);
the committed tests use synthetic streams.

## Coverage and limits

| Area (task list) | Status for the shipped optimized front end |
|---|---|
| Maxwell register state | **PASS**: runtime (698 M writes) + unit tests (random, negative controls, 8 M game writes) |
| Macro side effects | not compared: macro code, HLE and arguments are routed to the unchanged functions (identical calls); covered by gameplay |
| Buffer bindings | unchanged code in the shipped build; the one buffer component tried was verified (0 mismatches after the fix) and then rejected |
| Texture bindings | unchanged code |
| GPU memory side effects, syncpoints, fences, readback values | unchanged code paths; covered by gameplay and the camera-trajectory regression |
| Draw output | screenshots of every benchmark run checked; no pixel-exact comparison (the two modes cannot render the same frame: the game is not frame-deterministic at 85+ FPS) |

A whole-front-end differential test (two front ends, one command stream, all memory effects) would need a GPFIFO
capture with every guest memory read it depends on (vertex, index, uniform, texture and shader data). That replay
harness does not exist yet; it is listed in NEXT_STEPS.md for when a component changes memory-side behaviour.
