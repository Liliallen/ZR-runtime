# GPU state tracking

How register writes become host state in BOTW-PC, what it costs in BotW, and what was changed or evaluated.

## Legacy design (Ryujinx)

- **Register file:** `DeviceState<ThreedClassState>` holds the 3D registers; `DeviceStateWithShadow` adds the MME shadow
  RAM: from 0x200 bytes on, track modes also store each value in a shadow file, replay mode writes the shadow value
  instead of the method argument, passthrough ignores the shadow.
- **Write callbacks:** 33 3D register fields (some are arrays) run a handler on write (draws, clears, syncpoints, uniform data and binds, DMA,
  inline data, ...). Their effects are immediate, in command order.
- **State groups:** `StateUpdateTracker` maps each register to one of 28 groups (vertex buffers, blend, depth, viewport,
  shader, render targets, ...). A write that changes a register's value sets the group's dirty bit (`WriteWithRedundancyCheck`
  compares first). At a draw, `StateUpdater.Update` runs the update callback of every dirty group in a fixed order
  (shader after pipeline state, render targets after shader), then commits bindings.
- **Forced dirtiness:** some groups are forced on every draw regardless of writes: `IndexBufferState` for every indexed
  draw (index count and first index come with the draw), `VertexBufferState` when the draw kind, first vertex or index
  type changes.

This is already the incremental model the task describes: persistent register state, per-group dirty bits, value-change
detection, lazy derived state at draw time, immediate side effects for callback registers. yuzu/Eden use the same
structure (`dirty.tables`, `dirty.flags`), without the value compare (`ARCHITECTURE_COMPARISON.md`).

## Measured in BotW (W2, per frame)

| | Count |
|---|---|
| 3D register writes from the command stream | 157 000 (47 000 change the value) |
| State group updates at draws | 8 370 for 5 030 draws |
| — forced `UpdateIndexBufferState` | 5 000 |
| — `UpdateVertexBufferState` / `UpdateVertexAttribState` | 1 520 / 620 |
| — all other groups together | ~1 230 |

The state groups themselves are cheap; their cost is the per-write path that maintains them (dispatch, shadow RAM
handling, compare, group lookup) and the per-draw binding commit.

## Change: fast register writes (`FastRegisterWrite`, optimized front end)

One byte per register is built at start from the legacy tables: `Slow` (the register has a write callback, or is out
of range) or the state group to dirty (or none). A command-stream write of a non-callback register then does: shadow RAM
mode (track: also store in the shadow; replay: take the shadow value; passthrough or below 0x200 bytes: nothing),
compare, store, set the group bit — the same operations as the legacy path, without its three wrapper layers and two
table lookups. Callback registers take the legacy path, so their side effects and order are untouched. Verification and
measurements: `GPU_FRONTEND_DIFFERENTIAL_TESTS.md`, `GPU_FRONTEND_REWRITE.md`.

## Evaluated, not changed

| Idea | Why not |
|---|---|
| Defer plain writes into a method sink until the next executable method (Eden's `method_sink`) | Ryujinx's macro engine reads registers during macros and callbacks read them immediately; the fast path already does a plain write in a few instructions, so a sink would add a buffer write and a replay per word for nothing. Eden's sink also skips the value compare and dirties unconditionally |
| Collapse repeated writes of the same register before a draw | The value compare already makes an unchanged write a no-op for state; intermediate values can be observed by macros and callbacks (round 4's split probe found reads of registers written by the stream) |
| Skip the forced `UpdateIndexBufferState` per indexed draw | its cost is the binding memo check (round 4); the index count really changes per draw |
| Classify all methods into the six classes of the task and defer the "pure" ones | Equivalent to the existing callback/group split for 3D; the remaining classes (synchronization, memory, macro) already execute immediately through their handlers |
