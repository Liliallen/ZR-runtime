# Maxwell command stream of BotW (W2)

What the GPU command thread is asked to do per frame in the W2 scene (the player's save, outdoor eastern Great Plateau
at 19:30, Link standing; BotW 1.6.0, 120 FPS mode, 1920×1080, DLSS off). Measured with `BOTWPC_FE_STATS=1`
(`FrontendStats`, counts on the GPU command thread, averaged over 10 s windows of ~660 frames; run `bench/r4/fe-stats1`,
steady-state windows). Counts are per presented frame. Register names are the fields of `ThreedClassState` at those
byte offsets.

## Command words

| Kind | Per frame | Notes |
|---|---|---|
| Method headers (incl. immediate-data headers) | 112 000 | ~69 000 of them are `ImmdDataMethod` single writes |
| 3D register writes without a write callback | 142 000 | 70 % write the value the register already has |
| 3D register writes with a write callback | 15 000 | uniform data words, bind groups, draws, syncpoints, ... |
| 3D register writes that change the value | 47 000 | the only ones that mark a state group dirty |
| Macro starts | 14 000 | almost all HLE draw macros (`DrawElements*`), shader bind, uniform setup |
| Macro arguments | 29 000 | ~2 per call |
| GPFIFO class methods | 750 | semaphores, syncpoints, waits |
| Compute class methods | 500 | |
| Inline-to-memory data words | 8 500 | fast path (`TryFastI2mBufferUpdate`) |
| Uniform buffer data words (multi-word) | 1 700 | fast path (`TryFastUniformBufferUpdate`) |
| **Total words** (`GpuCounters.Methods`) | **~254 000** | in ~4 700 command buffers (~54 words each) |

The stream is dominated by short methods: one header per 2.3 words. The shadow RAM control (`SetMmeShadowRamControl`) is
in a track mode nearly all the time, so every register write from 0x200 bytes on is also stored in the shadow state
(`DeviceStateWithShadow`).

## Hottest 3D registers (writes / value changes per frame)

| Offset | Register | Writes | Changes |
|---|---|---|---|
| 0x2380–0x238c | `UniformBufferState` (size, address high/low, offset) | 8 200 each | 340–6 500 |
| 0x2390 | `UniformBufferUpdateData` (callback) | 6 500 | 6 150 |
| 0x2410 | `UniformBufferBindVertex` (callback) | 6 400 | 2 600 |
| 0x2490 | `UniformBufferBindFragment` (callback) | 2 100 | 1 100 |
| 0x1a2c | reserved 0x68b (macro scratch) | 3 600 | 0 |
| 0x1118 | reserved 0x446 | 3 400 | 3 250 |
| 0x1434 | `FirstVertex` | 3 400 | 1 600 |
| 0x17c8–0x17d8 | `IndexBufferState` | 2 800 each | 0–1 160 |
| 0x1160–0x119c | `VertexAttribState[0..15]` | 1 340 each | 0–425 |
| 0x1c00–0x1cf0 | `VertexBufferState[0..15]` | 1 340 each | 30–400 |
| 0x1880 | `VertexBufferInstanced` | 1 340 | 4 |

NVN re-emits the whole vertex attribute and vertex buffer layout (32 registers) whenever a draw's vertex format may have
changed, ~1 340 times per frame, nearly always with the same values. Per draw, BotW points the uniform buffer state at a
new offset of a large uniform ring and binds it (vertex stage most often), then calls an HLE draw macro.

## Draws and state

| | Per frame |
|---|---|
| Draws | 5 030 (99.4 % indexed; via HLE `DrawElements`, `DrawElementsInstanced`, `DrawElementsIndirect`) |
| Clears | 0 in this scene |
| State group updates (`StateUpdateTracker` callbacks) | 8 370 |
| — `UpdateIndexBufferState` | 5 000 (forced dirty by every indexed draw) |
| — `UpdateVertexBufferState` | 1 520 |
| — `UpdateVertexAttribState` | 620 |
| — `UpdateShaderState` | 425 |
| — render targets, scissor, blend, face, viewport, depth | 70–145 each |
| GPU sequence number advances (`AdvanceSequence`) | ~195 |

## Bindings per frame

| | Re-resolved (lookup + sync + host bind) | Only synchronized |
|---|---|---|
| Index buffer | 2 575 | 2 430 |
| Vertex buffers | 2 430 | 9 970 |
| Uniform buffers | 24 770 | 7 090 |
| Storage buffers | 3 | 0 |
| Textures | 17 800 handles read and compared per frame; 1 630 changed (9 %) | |

When any uniform slot of a draw changes (78 % of draws), Ryujinx re-resolves every uniform binding of every stage of
the bound program (`BufferManager.BindBuffers`) and sends them all to the backend.

## Implications for the front end

- **Per word:** decode, dispatch and the register store are paid ~254 000 times; most writes change nothing. A cheaper
  path per write (fast register dispatch, `GPU_FRONTEND_REWRITE.md`) is the measured win of this round.
- **Per draw:** ~5 000 draws × (index buffer state, binding commit for ~2 vertex + ~6 uniform buffers + ~3.5 textures,
  backend draw) is the larger total, but each item is already a few tens of ns after rounds 3–4 (lookup cache, page
  table, binding memo). The per-draw synchronization of every bound buffer is required by Ryujinx's write-tracking
  model; skipping it inside a GPU sequence was measured to gain nothing (`buffer-sync-memo.patch`).
- **Batching:** runs are short (2.3 words per header, ~2 macro arguments per call, ~54 words per command buffer), so
  run-level batching or cross-thread hand-off has little to amortize (two thread-split prototypes measured slower,
  round 4).
