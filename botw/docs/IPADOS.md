# BOTW-iPad: Breath of the Wild (Switch 1.6.0) on iPadOS

Branch `ipados` (from `cemu-switch`). Target: iPad Pro 11" 3rd gen (M1, iPad13,4, 8 GB, iPadOS 26.6.x).
Build machine: MacBook Air M4, Xcode 27, .NET 9 SDK.

## Architecture choice (evidence, not popularity)

| Candidate | What it is | For BotW 1.6.0 on this iPad |
|---|---|---|
| **BOTW-PC Runtime V2** (this repo) | Ryubing fork + BotW timing/UI/input fixes, CPU V2, GPU V2, measured on macOS (`MACOS_PERFORMANCE.md`) | runs the user's Switch 1.6.0 files and saves; macOS M4: S1 median 50-55 FPS at game speed 1.00; NativeAOT build verified (below) |
| MeloNX | Ryujinx fork for iOS (NativeAOT, LightningJit, MoltenVK) | proves the Ryujinx-on-iOS approach; its own files' licence is unclear (MIT file vs GPLv3/AGPL statements), so nothing is copied, only the design is reused; no BotW fixes; reported 30 FPS caps, ~6 GB RAM |
| Eden / yuzu (dynarmic) | C++ Switch emulators | iOS ports: "no game has ever run", no interpreter (2026); GPL-3.0 |
| Cemu / MeloCafe | Wii U emulator | needs Wii U BotW files, not available here; different game binary (not interchangeable with Switch saves) |
| Dynarmic | A64 JIT library | no advantage over LightningJit for ARM64→ARM64 (`CEMU_SWITCH.md`: guest CPU matters, but the M4's MoltenVK encoder caps gains at ~10 %) and the same JIT restriction |

Selected: BOTW-PC Runtime V2 as a NativeAOT library + a native UIKit app, LightningJit (with an ahead-of-time image),
Vulkan → MoltenVK 1.2.0 → Metal (the native Metal backend was 25-30 % slower on the M4).

## iPadOS execution of guest code

Findings (research + this device):
- iPadOS gives development apps no JIT entitlement; generated code runs only after a debugger attached once
  (CS_DEBUGGED). On TXM devices (A15+/M2+) the debugger must also prepare each executable range (StikDebug/lldb
  "JIT26" protocol: `mov x16, #cmd; brk #0xf00d`). The M1 is treated as non-TXM on 26.x by StikDebug and MeloNX;
  iPadOS 27 is expected to treat it as TXM.
- No legitimate route keeps JIT across launches without re-attaching (TrollStore/CoreTrust is an exploit, not used).

Implemented modes:

| Mode | How | Needs |
|---|---|---|
| JIT | `IosJitArena`: one RX mapping + RW alias (no W+X, no mprotect flips); the app probes the alias route (non-TXM, pages stay clean) and falls back to debugger-prepared pages (JIT26) | StikDebug on the iPad, or `run.sh` on a Mac, once per launch; the debugger detaches before the game runs |
| **AOT (no JIT)** | guest code compiled on the Mac by the same LightningJit (position-independent mode), linked into the app's `__TEXT`, looked up by guest address and verified by an XXH3-128 hash of the guest code; code missing from the image runs in the interpreter (below) | nothing at launch |
| Hybrid | AOT image + JIT for misses | JIT as above |

## Building and installing

1. iOS dependencies from source (SDL2, MoltenVK 1.2.0 with the two patches in `botw/ipados/patches`), output in
   `../.tools/ios/out` (see `patches/README`).
2. The AOT image from your own game files (Mac, ~5 min): `botw/ipados/aot_image.sh <BOTW-PC data dir> <game.nsp> <out>`.
3. `BOTWPC_AOT_DIR=<out>/img botw/ipados/build.sh` builds the NativeAOT runtime, the Xcode project, the signed app
   (Xcode account of a free team is enough; the device must trust the developer once) and `dist-ipados/BOTW-iPad.ipa`.
4. `botw/ipados/push_data.sh <BOTW-PC data dir> <game files>` copies keys, saves and game files into the app's Documents
   (the source folder is only read).

The IPA embeds the AOT image, which is code translated from the game: it is for your own device, never share it.
Free-team provisioning expires after 7 days (re-run build.sh); this build's profile expires 2026-10-25.

## Device results (iPad Pro 11" M1, iPadOS 26, 2026-10-18)

All runs: no JIT (no debugger, AOT image only), the user's 1.6.0 save, handheld 720p, the scripted outdoor sequence
(`botw/bench/outdoor.input`: load, walk, sprint, jump, camera pans, map, inventory, then idle on the Great Plateau),
iPad on its charger (battery stayed at 100 %, so no battery drain figures), data from `BOTWPC_FRAMELOG`
(guest frame times), the app's `botw_stats.csv` (footprint, `os_proc_available_memory`, thermal state, CPU time)
and GPU timestamps (`counters.csv`).

| | before | after |
|---|---|---|
| Loading the save | killed by jetsam at 7.72 GB footprint | loads; 6.2-6.6 GB footprint, 1.4-1.8 GB headroom of 7.96 GB |
| AOT misses | 0xa3273e0 after 4 min, 0x97f4504 after 22 min (both switch tables) | none in 15+5+5+5 min of runs |

30 FPS mode, nominal thermal state (60-325 s, 2 runs):

| avg FPS | median | 1 % low | P95 | P99 | GPU busy / frame | CPU | footprint |
|---|---|---|---|---|---|---|---|
| 30.0 | 30.2 | 20.1-20.4 | 44.3-44.5 ms | 45.7-46.1 ms | 24.4-24.5 ms | 2.31 cores (2.53 with .NET's default spinning) | 6.25-6.35 GB |

P95/P99 near 45 ms: the game queues a late frame about every tenth frame and catches up with the next two (45, 28,
27 ms); the same pattern is on macOS at 30 FPS (P99 39.7 ms). Sustained play: the thermal state reaches *serious*
after 8-17 minutes (on the charger), the SoC lowers its clocks and the CPU-side GPU threads become the limit:
24-27 FPS at game speed 0.8-0.93 (CPU time rises to ~4.5 cores because the same work runs at lower clocks).

60 FPS mode (nominal thermal state at start, 60-325 s): avg 35.0, median 37.2, 1 % low 18.6, P95 41.5 ms, P99 46.5 ms,
GPU busy 19.3 ms per frame, 3.55 CPU cores, *serious* thermal state reached after ~5 minutes. The GPU is not saturated;
the limit is the CPU side of rendering (GPU.MainThread + GUI.RenderLoop, a serial chain), so 60 FPS is not reached on
the M1 and 90/120 are not attempted. 30 FPS is the recommended (and default) mode.

Where the CPU goes (BOTWPC_THREADLOG, nominal): GUI.RenderLoop (Vulkan backend + MoltenVK encoding) 40-44 % of a
core, GPU.MainThread (Maxwell command processing) 37-40 %, guest threads 46/53/54 25-28/21-25/15-19 %, Metal and
.NET pool threads ~10 % each.

Fixes made for the device (all also active on macOS unless noted):
- Private heap pages are no longer zero-filled on map (they are zeroed on unmap instead, so they read as zero):
  the fill made BotW's whole ~2.7 GB heap resident. macOS footprint 9.06 -> 6.53 GB, FPS unchanged.
- MoltenVK: argument buffers on iOS Tier 2 GPUs (BotW binds more than 31 buffers), shader library cache trimmed.
- iOS: guest scheduler and GAL events sleep without spinning (-9 % CPU time at the same FPS).
- AOT entry points: switch tables whose bound check or base is far from the BR.
- FXAA and the area/FSR scaling passes are off on the iPad: with either enabled the presented image is black
  (FXAA alone reproduces it; MoltenVK reports no error through VK_EXT_debug_utils either). Root cause not found; `-antiAliasing Fxaa` / `-scalingFilter`
  launch arguments re-enable them for testing.

## Not verified on the device

- Physical controllers (Xbox/PlayStation/MFi), rumble and gyro aiming: implemented (GameController, CoreMotion,
  UIImpactFeedbackGenerator) but need someone holding the iPad; the runs above use the input script.
- Touch controls and the settings overlay (L3+R3) beyond rendering: same reason.
- JIT and hybrid modes on the device: the JIT26/alias code paths are implemented and tested on the Mac, but a
  debugger-enabled launch (StikDebug or `run.sh`) over Wi-Fi stalled while lldb read the 700 MB image; not measured.
- Battery drain (the iPad stayed on its charger).

## Interpreter (code missing from the image, no JIT)

Static analysis cannot find every entry point (switch tables entered from several paths, computed targets); two such
misses paused the game on the iPad. `src/Ryujinx.Cpu/LightningJit/Interpreter` runs that code instead:
- Entered like a translated function (native entry, guest address in the context's dispatch address, next address
  returned), with the translated code's control flow: calls go through the dispatch stub, branches to code that has
  native code tail-call it (nesting bounded at 32, then the dispatch loop continues), RET returns, backward branches
  and SVC/BRK/UDF are sync points.
- Integer, loads/stores (exclusives, SIMD structures), branches and system instructions are interpreted. FP/SIMD
  data processing runs on the host's unit: the instruction with its registers renamed to V0-V7/X0-X3 is a
  two-instruction stub, called by a trampoline stub; results, FPCR modes and FPSR flags are the hardware's. The
  image carries the stubs of every FP/SIMD instruction in the game's code (826 after renaming).
- An instruction it lacks still falls back to the JIT request; none was hit in testing.

Validation:
- `InterpreterTests` (macOS, LightningJit as reference, each instruction from the same random state: registers,
  NZCV, FPSR, memory): 41,630 encodings sampled from the game's code (295 mnemonics), 142,616 runs, 0 mismatches.
- `BOTWPC_INTERPRET=N` interprets one function in N: macOS with JIT N=10 and without JIT (AOT image) N=10: boot,
  load the save, outdoor sequence, correct images; iPad without JIT, N=10: same (5.6 G interpreted instructions,
  no missing stub, no unsupported instruction). Speed ~18 M instructions/s per thread, so a missed hot loop is slow,
  but nothing stops.
- The differential tests found LightningJit bugs, fixed for all platforms: FP/SIMD register 31 was never
  loaded/stored (treated as the zero register), instructions that read their destination lacked that flag
  (AESE/AESD, SHA1/SHA256, SMLAL/UMLAL family, SSRA/USRA family, SABA/UABA family, BIC/ORR vector immediate, TBX,
  FMOV Vd.D[1]), 16-bit by-element Rm decoding, FMOV (general) marked as writing NZCV.
