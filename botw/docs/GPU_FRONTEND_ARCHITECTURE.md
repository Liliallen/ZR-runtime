# GPU front end architecture (BOTW-PC, Ryujinx-derived)

How a guest Maxwell command reaches the host GPU in BOTW-PC, what each stage costs in BotW (W2 scene), and what the
optimized front end changes. Companion documents: `MAXWELL_COMMAND_ANALYSIS.md` (the workload), `GPU_FRONTEND_REWRITE.md`
(components, decisions, measurements), `GPU_FRONTEND_DIFFERENTIAL_TESTS.md` (correctness), `STATE_TRACKING.md`,
`BINDING_OPTIMIZATION.md`, `ARCHITECTURE_COMPARISON.md` (Ryujinx / yuzu / Eden / Cemu / bbport).

## Pipeline

```
guest threads (NVN)                     GPU command thread ("GPU.MainThread")                       render thread
 GPFIFO entries ──► GPFifoDevice queue ──► DispatchCalls: fetch command buffer (guest memory)
                                          GPFifoProcessor.ProcessImpl: header state machine, per word:
                                            Send(): engine by sub-channel
                                              3D (ThreedClass.Write) ─► DeviceStateWithShadow (shadow RAM modes)
                                                                       └► DeviceState: store, compare, write callback
                                                                       └► StateUpdateTracker: mark state group dirty
                                              macros (MME): start / argument / call ─► MacroHLE (draws, shader bind,
                                                                                       uniform setup) or MME JIT
                                          draw (DrawManager.Draw):
                                            StateUpdater.Update: dirty state groups → GAL state calls
                                            CommitBindings: texture handles, buffer bindings (lookup, sync, host bind)
                                            backend draw ─────────────────────────► ThreadedRenderer queue ──► Vulkan
```

- **Optimized front end** (`BOTWPC_GPU_FRONTEND=optimized`, or `"gpu_frontend": "Optimized"` in the `graphics` section
  of `<data>/botw/settings.json`; default `Legacy`): replaces the per-word dispatch of 3D methods (green path below);
  everything after the register store — state groups, draws, bindings, caches, GAL, Vulkan — is the existing code.
- **Fast dispatch:** `ProcessImpl` → `SendThreed` (sub-channel 0) → `ThreedClass.WriteFast` → `FastRegisterWrite.Apply`
  (one kind byte per register: callback → legacy `Write`; otherwise shadow RAM mode, store, change test, state group).
  Macro starts/arguments/calls go directly to the GPFIFO class (same calls as `Send`).
- **Retained:** the GPU memory model, page-protection write tracking, buffer and texture caches (with round 4's page
  table and binding memo), shader cache, GAL and the threaded renderer, Vulkan backend, strict readback sync and the
  early submission of readback producers.

## Where the GPU command thread's time goes (W2)

Inclusive share of the GPU thread's busy samples (`bench/r4/fe-stacks1`, optimized build, 200 Hz stack sampling,
idle waits excluded); the legacy profile of round 4 (`r4-stacks2`) for comparison.

| Stage | Legacy (round 4) | Optimized | Notes |
|---|---|---|---|
| Command decode + dispatch (`ProcessImpl` + `Send`, self) | 18.2 % | 14.0 % | per word; fast dispatch removes `Send` for 3D |
| Draws (`DrawManager.Draw`, inclusive) | 57.7 % | 59.0 % | larger share of a smaller total |
| — binding commit (`CommitBindings`) | 38.8 % | 36.9 % | buffers 23.5 %, textures 10.4 % |
| — shader state (`UpdateShaderState`) | 5.9 % | 4.6 % | binary compare of the shader code |
| — vertex attributes / buffers / index buffer state | ~6 % | ~9.7 % | |
| Guest macros (HLE draw setup, `MacroHLE.Execute` self) | ~1 % | 1 % | |
| Uniform buffer binds and data (`ConstantBufferUpdater`) | 8 % | 8 % | |
| Write tracking reprotection (`VirtualProtect`) | 3–4.7 % | 2.3 % | required by the memory model |
| Threaded-renderer queue (`QueueCommand`) | 1.5 % | 6.9 % | mostly waking the render thread; see below |

The stack sampler suspends threads to walk their stacks, which makes the render thread block more often than in a
normal run; the queue-wake share above is inflated by that (an A/B of a render-thread poll that removes most wakes,
`render-thread-spin.patch`, measured no gain).

## Critical path (causal delays, in-run A/B, W2, optimized build, 89 FPS)

| Delay | Frame time | Transfer |
|---|---|---|
| GPU command thread +1.5 ms | +0.94 ms (FPS −6.88, CI −7.21 .. −6.56) | **63 %** (77 % before this round) |
| Render thread +1.5 ms | +0.18 ms (FPS −1.39, CI −1.84 .. −0.94) | 12 % (≈1.3 ms slack) |

The GPU command thread is still the main limit; the guest CPU's readback chain (a delay on the GPU thread also raises
the guest's sync wait by +0.77 ms) carries the rest.

## Cross-platform

All changed code is in `Ryujinx.Graphics.Gpu` (front end shared by every backend and OS) and uses no OS, GPU-vendor,
graphics-API or host-architecture specific code: plain managed code, no intrinsics, no page-size assumption. It applies
unchanged to Linux, macOS (MoltenVK, a future Metal backend) and iPadOS/ARM64. It was built and measured on Windows
x86-64 only. The benchmark-only `BOTWPC_PRESENT_IMMEDIATE` switch is in the Vulkan backend and affects presentation only.
