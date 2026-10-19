# macOS performance campaign (Apple Silicon, 2026-10-17)

Branch `macos-perf` (from `runtime-v2-macos`). Machine: MacBook Air M4 (Mac16,13, fanless), 4 P + 6 E cores,
10-core GPU, 24 GB, macOS 27.0.1, on AC power. Baseline build: the macOS port (`dist-macos/rollback/BOTW-PC-port1.app`
keeps it for rollback).

## Method (what made the numbers trustworthy)

Tools in `../mac-bench` (local, next to the repo):

| Tool | What |
|---|---|
| `matrix.sh` | one run per spec on a fresh APFS clone of a template data dir (saves never touched), scripted input, frame log, counters, GPU-utilization + CPU sampler, display kept awake |
| `ab.sh` | interleaved A/B rounds with a cool-down between runs (thermal drift) |
| `window.py` | FPS (avg, median, 1 % low, p99), game speed, GPU utilization, process CPU over a fixed window |
| `profile.sh` + `symsample.py` | macOS `sample` (1 kHz native stacks) with managed frames symbolized from the .NET perf map (`DOTNET_PerfMapEnabled=1`, ReadyToRun off) |
| `utilsample.py` | `IOAccelerator` "Device Utilization %" (GPU busy, no root needed) and process CPU, on the frame log's clock |

Pitfalls found and fixed in the method itself (each invalidated earlier numbers):
1. **Night-time save:** the user's save puts Link outdoors at night; an idle Link is attacked and the Game Over screen
   renders fast. Benchmarks moved to the Windows campaign's S1 scene (Shrine of Resurrection, `bench-saves/shrine`),
   indoors, deterministic.
2. **Window placement:** an auto window based on submits per frame depended on the variable under test (strict sync
   changes submits per frame). Fixed window 95–135 s after the first frame.
3. **EventPipe thread-time sampling is biased** (threads are sampled at safe points): it blamed a lock in
   `AddressSpacePartitioned.FindPartition` for 49 % of the GPU thread. Unbiased `sample` profiling showed no such
   thing. Only native sampling is used for decisions.
4. **Locked screen / sleeping display throttles presentation:** the same configuration ran at ~35 FPS while the Mac
   was locked and at 47–51 FPS unlocked. Runs now hold a display assertion (`caffeinate -u -d -i`); comparisons were
   only made within the same condition.
5. **Thermals:** a fanless Air drifts ±15 % over back-to-back runs. Interleaved rounds + 45 s cool-down; in-run A/B
   (`BOTWPC_AB_PERIOD`) where the switch allows it.

## Where the time goes (S1, 60 FPS mode, unlocked, ~51 FPS)

`sample`, symbolized, busy share of wall time per thread:

| Thread | Busy | Hottest |
|---|---|---|
| **MoltenVK command-encoding queue** (Vulkan → Metal) | **99.4 %** | `MVKResourcesCommandEncoderState::encodeMetalArgumentBuffer` 25.8 %, `cache_getImp` + `objc_msgSend` 21 %, `IOGPUResourceListAddResource` 3 % |
| GAL render thread (Vulkan backend) | 46.5 % | descriptor-set writes (`MVKDescriptorSet::write`, `DescriptorSetUpdater.UpdateAndBind`), memmove |
| Guest CPU threads (LightningJit) | 34–36 % | guest code; signal-based memory tracking 1–2 % |
| GPU command thread (Maxwell front end) | 35.5 % | `GPFifoProcessor.ProcessImpl`, binding commits |
| GPU (IOAccelerator) | ~82 % | |

The frame is limited by MoltenVK's single serial encoding queue, with the GPU close behind. ~4 050 Vulkan descriptor
set binds per frame (BotW binds a new uniform-buffer set for almost every draw); with Metal argument buffers MoltenVK
re-encodes each newly bound set and declares every resource in it with `useResource:` on every draw.
The guest CPU and the Maxwell front end have headroom on this machine.

## Experiments (S1, 60 FPS mode, same condition per comparison)

| Change | Measurement | Result | Kept |
|---|---|---|---|
| **Strict readback sync off on macOS** (`TextureGroupHandle`, BOTW-PC's own Windows optimisation) | locked: 3 rounds; unlocked: 2 rounds; 95–135 s | 1 % low **24.6–31.1 vs 20.7–22.4** (unlocked), 24.9–25.8 vs 14.0–22.4 (locked); game speed **0.995–1.000 vs 0.970–0.993**; median FPS within noise | **yes** (macOS default; Windows/Linux unchanged) |
| HUD frame time before the first frame | user report ("0.0 FPS / 41015990.0 ms") | fixed | **yes** |
| User-interactive QoS on GPU, render, guest and audio threads | 3 rounds | median 35.4–38.7 vs 34.8–40.1, no gain | no (removed) |
| MoltenVK `PREFILL_METAL_COMMAND_BUFFERS` | 1 round | 28.1 vs 35–38 FPS (−20 %) | no |
| Metal argument buffers off | 1 run | MSL compile errors: BotW's shaders need > 31 buffer slots | impossible |
| Push descriptors on MoltenVK (disabled upstream) | 1 run | renders, then the GPU hangs at 90 s | no |
| MoltenVK 1.4.2 (Homebrew) | 3 + 3 runs | not faster; **frames stop (freeze) in 3 of 6 runs** | no |
| Redundant descriptor-set bind elimination (same layout, same bytes, same command buffer) | counters | only 1.3 % of 4 050 binds/frame are exact repeats (per-draw UBO offsets) | no (removed) |
| Retina 1× drawable (`contentsScale` 1, swapchain 1710×1003 vs 3420×2006) | 3 rounds | 35.5/48.8/51.4 vs 35.8/51.1/46.9: presentation is not a cost | no (removed) |
| MoltenVK 1.2.0 rebuilt from source with a per-encoder `useResource:` dedup (patch in `.tools/src/MoltenVK-1.2.0`) | `sample`: encoder ms/frame; 3 FPS rounds | encoder −5.4 % per frame, FPS unchanged (42.1/42.1 vs 41.5/43.2), worse 1 % low in one round; the self-built 1.2.0 froze once with the patch **off** (SDK 27 build vs the bundled SDK 12.1 build) | no |

Every rejected item was measured, not assumed; the bundled MoltenVK 1.2.0 stays (0 freezes in 50+ runs).

## Native Metal backend vs MoltenVK

Ryubing shipped a native Metal backend and reverted it (#701, "supports only a few games"). It was restored on top of
BOTW-PC as a prototype on branch `macos-metal-proto` (commit `6be386235`, app `dist-metal/`): conflicts with later
upstream refactors resolved, the removed shader-translation-delay hack turned into `BOTWPC_METAL_SHADER_DELAY_MS`
(without ~30 ms BotW stalls at "Loading 1145 shaders"), and Metal screen captures implemented (were TODO) so rendering
could be verified from GPU readbacks while the Mac was locked.

Same S1 scene, AA off on both (the Metal path has no FXAA), 2 interleaved rounds:

| Backend | Median FPS | 1 % low | GPU busy | Process CPU |
|---|---|---|---|---|
| MoltenVK (Vulkan backend) | 35.6 / 35.4 | 24.9 / 26.5 | 78–79 % | 3.4 cores |
| Native Metal (prototype) | 25.7 / 26.8 | 19.8 | 82–85 % | 1.3–1.5 cores |

Native Metal renders the shrine correctly and saves ~2 CPU cores (the MoltenVK translation cost is real), but spends
~45 % more GPU time per frame (85 % busy at 26 FPS vs 78 % at 35 FPS) and is slower overall. Not adopted; kept as a
separate prototype branch.

## Not done / limits

- **Ceiling:** at ~80 % GPU busy with MoltenVK, the M4's 10-core GPU allows ~45–50 FPS in S1 at docked resolution
  even with a free encoder. Stable 60 FPS needs less GPU work per frame (a faster GPU, or lower internal resolution);
  90/120 are not reachable on this machine. The 30 FPS mode is stable; higher modes run at 40–50 FPS with game speed 1.00.
- **Guest CPU (LightningJit):** 34–36 % busy per guest thread at 50 FPS. *Corrected 2026-10-18:* busy time hid a
  serial dependency. A causal test showed that slowing the guest by 30 % / 60 % adds 3.0 / 6.8 ms per frame
  (`CEMU_SWITCH.md`, Dynarmic section). A faster JIT can gain at most ~10 % here, though, because the MoltenVK
  encoder (99 % busy) is the next limit. ARMeilleure (CPU V2) remains unusable in game on macOS (MACOS_PORT.md).
- **Thermals:** sustained back-to-back runs on the fanless Air lose 10–20 % (47–51 → ~42 FPS in S1).
- MetalFX not implemented: upscaling would not raise simulation FPS and the frame is not upscale-bound.
