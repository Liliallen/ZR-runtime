# Architecture map (Ryubing 1.3.3 + BOTW-PC)

Paths are relative to `src/`. Arrows are call/data flow.

## Threads that matter for BotW (measured names)

| Thread | Role | Load at ~94 FPS uncapped (S1) / W1 2026-10-15 (exclusive work per frame) |
|---|---|---|
| `HLE.GuestThread.*` | guest CPU cores running ARMeilleure JIT code | busiest single thread 59 % / main 58 % running (54 % JIT code, flat profile), worker 61 % |
| `GPU.MainThread` | GPFIFO command processing (Maxwell 3D engine, macros, state, buffer/texture sync) | 82 % (critical path) / 9.2 ms, ~0.5 ms slack |
| `GUI.RenderThread` | Vulkan backend: executes the threaded-GAL command queue, records & submits | 83 % (≈40 % of it idle-waiting / spinning) / 9.0 ms |
| `SurfaceFlinger.Composer` | emulated display: VBlank timing, buffer composition | ~5 % |

Frame structure measured on Windows, outdoor W1 (PERFORMANCE.md, "Critical path"): the guest submits all GPU work of
frame N in two GPFIFO submissions at the end of its frame; `GPU.MainThread` processes it (8.4 ms) while the guest
computes frame N+1; guest workers then read back small GPU-computed textures of frame N (`TextureGroupHandle.Sync`,
`BufferModifiedRangeList.WaitForAndFlushRanges` → `SyncManager.Wait`) before the main thread can finish N+1.

## CPU
- JIT: `ARMeilleure/Translation/Translator.cs` (tiered: functions re-JIT at high quality after 100 calls),
  persistent cache `ARMeilleure/Translation/PTC/Ptc.cs` + `PtcProfiler.cs` (BotW: ~42 800 functions cached, ~100 MB).
- Execution/memory managers: `Ryujinx.Cpu/Jit/JitCpuContext.cs`, `MemoryManagerHostMapped.cs` (HostMappedUnsafe mode in use).
- Guest-memory write tracking for GPU coherency: `Ryujinx.Memory/Tracking/MemoryTracking.cs`, `MultiRegionHandle.cs`, `RegionHandle.cs`
  (page protection + `Reprotect`).

## Horizon OS (HLE)
- Services: `Ryujinx.HLE/HOS/Services/**` and `Ryujinx.Horizon/**`.
- Process loading: `Ryujinx.HLE/Loaders/Processes/ProcessLoaderHelper.cs` (`LoadNsos` → build IDs, load bases,
  cheats, **BOTW-PC profile detection**).
- Mods/patches: `Ryujinx.HLE/HOS/ModLoader.cs`, `Loaders/Mods/IPSwitchPatcher.cs` (pchtxt), `IPSPatcher.cs`, `MemPatch.cs`.
- Input: `Ryujinx.Input/HLE/NpadManager.cs` → `Ryujinx.HLE/HOS/Services/Hid` (**InputScript hook** for player 1).

## GPU front end
```
guest nvgpu submit → Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoDevice.DispatchCalls
  → GPFifoProcessor.Process/Send → GPFifoClass.CallMme → Engine/MME/MacroHLE (DrawElements*, ...)
  → Engine/Threed/DrawManager.Draw → StateUpdater.CommitBindings
       → Memory/BufferManager.CommitGraphicsBindings → BufferCache.GetBufferRange
            → Buffer.SynchronizeMemory/RegionModified → renderer SetBufferData   (hot: ~46 % of GPU.MainThread)
       → Image/TextureBindingsManager.CommitBindings, TextureManager.UpdateRenderTargets
  → IRenderer (GAL)
```
Shaders: `Ryujinx.Graphics.Gpu/Shader/ShaderCache.cs` (+ `DiskCache/` guest/host caches) →
`Ryujinx.Graphics.Shader/Translation/Translator.cs` (Maxwell decode → IR) → `CodeGen/Spirv/SpirvGenerator.cs` →
Vulkan `ShaderCollection.cs`, pipelines `PipelineState.cs`/`PipelineConverter.cs`. With warm caches no runtime
pipeline creation was seen in the BotW profiles.

## GAL threading and Vulkan backend
```
GAL calls (GPU.MainThread) → Ryujinx.Graphics.GAL/Multithreading/ThreadedRenderer (command ring + CircularSpanPool data copies)
  → GUI.RenderThread: Ryujinx.Graphics.Vulkan
       PipelineBase/PipelineFull (draw state, render passes, flush heuristics in AutoFlushCounter)
       BufferManager/BufferHolder/StagingBuffer (uploads), TextureView/TextureStorage
       CommandBufferPool (16 command buffers; FenceHolder per submission)  ← FencePool
       SyncManager (host sync for guest readbacks; strict = submit now), Window.Present (swapchain)  ← FIFO_LATEST_READY
```

## Presentation and frame pacing
```
guest queues buffer → HLE/HOS/Services/SurfaceFlinger: OnFrameAvailable  ← BotwTiming.OnFrameQueued, frame log
SurfaceFlinger.HandleComposition (thread "SurfaceFlinger.Composer"):
   every 1/TargetVSyncInterval s: SignalVsync; every swapInterval VBlanks: Compose → Gpu.Window.EnqueueFrameThreadSafe
Ryujinx/Systems/AppHost.RenderLoop → Device.PresentFrame → Gpu/Window.Present → Vulkan/Window.Present
```
`TargetVSyncInterval` is 60 (Switch), the custom value (Custom) or 1 (Unbounded); it comes from
`Ryujinx.HLE/Switch.cs` `UpdateVSyncInterval` and the UI (`Settings > System > custom refresh rate`).

## Configuration
`Ryujinx/Systems/Configuration/ConfigurationState*.cs`, `ConfigurationFileFormat.cs` (Config.json, version 70)
→ `Ryujinx.HLE/HleConfiguration.cs`. BOTW-PC adds no new config file: the frame-rate target uses the existing
custom-refresh-rate setting, and diagnostics/kill switches are environment variables (see `botw/README.md`).

## BOTW-PC additions (where they hook in)

| Component | File | Hook |
|---|---|---|
| Game/version gate | `Ryujinx.HLE/BotwPc/BotwProfile.cs` | `ProcessLoaderHelper.LoadNsos` after build IDs are known |
| Timing correction | `Ryujinx.HLE/BotwPc/BotwTiming.cs` | `SurfaceFlinger.OnFrameAvailable/OnFrameReplaced` |
| Fence pool | `Ryujinx.Graphics.Vulkan/FencePool.cs` | `CommandBufferPool` → `FenceHolder` |
| Present mode | `Ryujinx.Graphics.Vulkan/PresentModeFifoLatestReady.cs` | `VulkanInitialization.CreateDevice`, `Window.ChooseSwapPresentMode` |
| Counters | `Ryujinx.Common/BotwPc/GpuCounters.cs` | flush call sites, fence/sync waits; written by `PerformanceStatistics` |
| Frame log / capture | `Ryujinx.HLE/PerformanceStatistics.cs`, `Ryujinx/Systems/AppHost.cs` | per guest frame / UI update |
| Scripted input | `Ryujinx.HLE/BotwPc/InputScript.cs` | `NpadManager.Update` |
| RT census | `Ryujinx.Graphics.Vulkan/RenderTargetCensus.cs` | `PipelineBase.SetRenderTargets/Draw*`, `Window.Present` |
| DLSS | `botw/native/ngx_bridge/` + `Ryujinx.Graphics.Vulkan/Dlss.cs`, `PipelineFull.Dlss.cs`, `PipelineBase.Dlss.cs` | device creation; `Draw/DrawIndexed` of the upscale program; viewport jitter in draws |
| Camera data for DLSS | GAL `IPipeline.SetBotwCamera` + `SetBotwCameraCommand` | `Gpu/Engine/Threed/StateUpdater.Update` |
| DLSS motion / depth / disocclusion | `Ryujinx.Graphics.Vulkan/Shaders/BotwDlssMotionShaderSource.comp` (SPIR-V checked in) | `PipelineFull.Dlss.UpdateMotionVectors` before each evaluation |
| Strict readback sync | `Ryujinx.Graphics.Gpu/Image/TextureGroupHandle.SignalModifying` | unbind of textures the guest reads back often |
| Redundant bind skip | `Ryujinx.Graphics.Gpu/Memory/BufferManager.SetVertexBuffer/SetGraphicsUniformBuffer` | per draw |
| Sync-wait log | `Ryujinx.Common/BotwPc/SyncWaitLog.cs` (`BOTWPC_SYNC_LOG=1`) | `TextureGroupHandle.Sync`, `BufferModifiedRangeList.WaitForAndFlushRanges` |
| Windows benchmarks | `botw/scripts/bench_win.ps1`, `bench_table.py`, `timeline_frame.py`, `ghost_sheet.ps1` | standalone mode on a disposable data directory |
| UI animation timing | `Ryujinx.HLE/BotwPc/BotwUiTiming.cs` | `ProcessLoaderHelper` after loading the NSOs |
| Xbox prompt overlay | `Ryujinx.HLE/BotwPc/Prompts/` + `Ryujinx.Common/BotwPc/ButtonPrompts.cs` | `ModLoader.ApplyRomFsMods`; mapping set in `AppHost.LoadGuestApplication` |
| Virtual motion | `Ryujinx.Input/Motion/VirtualMotion.cs` | `NpadManager.Update` → `NpadController.ApplyVirtualMotion` |
| Standalone launcher | `Ryujinx/Program.cs` (`--botw`, `--botw-setup`), `Ryujinx.HLE/BotwPc/BotwSetup.cs`, `MainWindow.FindBotwForStandalone` | process start |
| Timeline profiler | `Ryujinx.Common/BotwPc/TimelineTrace.cs` | `GpuCounters` regions, kernel waits (`Syscall.cs`), `SurfaceFlinger` |
| In-game overlay | `Ryujinx.Common/BotwPc/Overlay/` (model, input gate, image hand-off), `Ryujinx.HLE/BotwPc/Overlay/OverlayPainter.cs` (SkiaSharp), `Ryujinx/Systems/BotwOverlayHost.cs` (pages, pause, apply) | `NpadManager.Update` (L3+R3 gate, neutral input), `AppHost.UpdateFrame`, Vulkan `Window.Present` (blend after scaling), `Gpu.Window.PresentLast` while paused |
| Held-button repeat timing | `Ryujinx.HLE/BotwPc/BotwInputRepeat.cs` | `ProcessLoaderHelper` after loading the NSOs (patches sead's repeat loop) |
| Guest perf map / relocated dump | `ARMeilleure/Diagnostics/GuestPerfMap.cs`, `BotwTiming.DumpRelocatedMain` | translator, PPTC load; first VFR validation |

The upscaler path designed for DLSS is described in `DLSS.md`; it deliberately sits at the GPU render-target
level (replacing BotW's own 900p→1080p upscale draw), not in shader translation and not in `Window.Present`
(which only sees the final image with the HUD already composited).
