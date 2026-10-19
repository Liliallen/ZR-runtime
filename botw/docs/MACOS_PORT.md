# macOS port (Apple Silicon, 2026-10-17)

Branch `runtime-v2-macos` (from `runtime-v2` at `4c8cb99e7`). Machine: **MacBook Air** (Mac16,13, fanless) with Apple M4 (4 performance + 6 efficiency cores, 10-core GPU),
24 GB, macOS 27.0.1 (26A434), built-in Retina display 3420×2214. .NET SDK 9.0.319 (user-local `~/.dotnet`).

BOTW-PC runs natively on Apple Silicon: an arm64 .NET build, Ryujinx's ARM64 JIT (LightningJit) for the guest CPU,
Vulkan through MoltenVK (Metal), SDL2 for controllers and audio. No Wine, CrossOver or external emulator.

## Build, install, launch

```bash
botw/scripts/build-macos.sh                  # → ../dist-macos/BOTW-PC.app (+ BOTW-PC-macos-arm64.zip), ad-hoc signed
botw/scripts/setup-macos.sh ../dist-macos/BOTW-PC.app <existing data dir> <game dir>
open ../dist-macos/BOTW-PC.app               # or double-click in Finder: straight into the game, fullscreen
```

- **build-macos.sh** publishes `src/Ryujinx` self-contained for `osx-arm64` (single file, trimmed) and assembles
  `BOTW-PC.app`: `Contents/MacOS/BOTW-PC` (the Ryujinx host renamed; an executable named BOTW-PC starts in standalone
  mode, because Finder passes no arguments), `Contents/Frameworks/*.dylib` (MoltenVK, SDL2, FFmpeg, Skia, HarfBuzz,
  Avalonia native, `libarmeilleure-jitsupport`; install names fixed by upstream's `bundle_fix_up.py`), icon, Info.plist.
  Signed ad hoc with upstream's `distribution/macos/entitlements.xml` (`allow-jit`, `allow-unsigned-executable-memory`,
  `disable-library-validation`, `hypervisor`, …); `codesign --verify --deep --strict` passes.
- **LibHac**: `Ryujinx.LibHac 0.21.0-alpha.116` exists only on `git.ryujinx.app`, which is unreachable from this
  network (as on the Windows/Linux machines). `Directory.Packages.props` now takes `RyujinxLibHacVersion`
  (default: the alpha); the macOS build uses build.sh's local feed when it exists and nuget.org's `0.20.0` otherwise.
  Same API (0 build errors); the alpha only adds a fix for crashes with some mods (upstream commit `45b2e613c`).
- **setup-macos.sh** creates `~/Library/Application Support/BOTW-PC` (the macOS default data dir; `-r` still works)
  by **copying** keys, firmware, user profiles, saves, `Config.json` and `botw/settings.json` from an existing data
  dir (never modified; existing destination files are kept), plus the host-independent caches (guest shader cache,
  PPTC profile; not the x86-64 code cache), registers the base game, update and both DLC, rewrites Windows paths,
  sets DLSS off, selects Runtime V2 and disables the Apple Hypervisor.

Logs: `~/Library/Logs/Ryujinx/` (default data dir) or `<data dir>/Logs`.

## What had to change (and why)

| Change | Where | Why |
|---|---|---|
| macOS default data dir `~/Library/Application Support/BOTW-PC` | `BotwSetup.DefaultDataDir` | `~/.local/share` is a Linux convention |
| Executable named `BOTW-PC` implies `--botw` | `Program.cs` | a Finder-launched app gets no arguments |
| DLSS: no NGX bridge probe on macOS, overlay says "NVIDIA-only" | `Dlss.cs` | DLSS needs an NVIDIA GPU; the game's own upscale is used. MetalFX not investigated yet |
| BotW skips the Apple Hypervisor unless `BOTWPC_HYPERVISOR=1` | `ArmProcessContextFactory` | crashes, below |
| CPU V2 does not move ARM64 hosts to ARMeilleure unless `BOTWPC_ARM64_ARMEILLEURE=1` | `ArmProcessContextFactory` | crashes, below |
| CPU engine logged (`BOTW-PC CPU engine: …`) | `ArmProcessContextFactory` | the engine was invisible in logs |
| CPU tests map their JIT cache with `MAP_JIT` | `Ryujinx.Tests/Cpu/CpuContext.cs` | every ARMeilleure CPU test crashed the test host on macOS ARM64 (upstream too) |

Everything else (GPU front end V2, HLE scheduler V2, memory tracking, BotW timing, input repeat, prompts, overlay,
mods, DLC checks, AutoController, virtual motion) runs unchanged. All changes are behind `OperatingSystem.IsMacOS()`,
an ARM64-host check or the executable name; x86-64 Windows/Linux behaviour is unchanged.

## Crashes found and fixed

### 1. Apple Hypervisor: corrupted guest pointers (fixed by not using it for BotW)
The Windows `Config.json` carries `use_hypervisor: true` (meaningless on Windows). On the Mac that selects
`Ryujinx.Cpu.AppleHv`, which runs guest code natively. BotW then dies with an unhandled
`InvalidMemoryRegionException` from a Worker thread: null+0x50 at `U-King+0x12970dc` (`ldr x8, [x1, #0x50]` with
`x1 = arg->+0x128 == 0`, reached from different callers), null, or garbage VAs such as `0x1AD7000600000059` during
the save load. **5 of 7 runs** crashed between 37 and 177 s, with the legacy runtime as well as with Runtime V2
(first suspected GPU V2: 3/3 crashes vs 0/5, then legacy + HV crashed at 39 s). Upstream reported the same
instability (Ryujinx PR #6585: hypervisor off by default on macOS, "BotW pretty much unplayable via HV").
A plausible cause in the code: `HvAddressSpaceRange` defers TLB invalidation after page-protection changes until some
vCPU next traps to the host, so other vCPUs keep stale permissions meanwhile. Not proven; the JIT has none of it.
**Fix:** for BotW the factory uses the JIT unless `BOTWPC_HYPERVISOR=1`; `setup-macos.sh` also writes
`use_hypervisor: false`. LightningJit: **0 crashes in 20+ runs** (each 100–200 s of scripted play).

### 2. ARMeilleure (CPU V2's JIT) on ARM64 macOS: crash at gameplay start (avoided, root cause open)
On ARM64 hosts upstream runs LightningJit; ARMeilleure is only used with the software page table or the GDB stub.
CPU V2 is an ARMeilleure code generator, so the first macOS commit routed `cpu_core: V2` to ARMeilleure. In game:

| ARMeilleure configuration | Result |
|---|---|
| CPU V2 (0x100CF), host-tracked memory (16 KiB host pages) | .NET abort while unwinding a fault in JIT code at 42 s, 2/2 |
| legacy code generator (`BOTWPC_CPU_V2_FEATURES=0`), host-tracked | same, 44 s |
| CPU V2, PPTC off | SIGSEGV in JIT code at 23 s, FAR `0x00800072f10435c8` |
| CPU V2 or legacy, `SoftwarePageTable` | title screen at 120 FPS, then no frame after the save load (hang) |

So it is not CPU V2: ARMeilleure itself is not usable for BotW on macOS ARM64 today (an upstream path that never
ships there). CPU V2 falls back to LightningJit with a log line; `BOTWPC_ARM64_ARMEILLEURE=1` re-enables the path for
further work.

**CPU V2 correctness on ARM64 is verified at unit level.** The CPU test host crashed on macOS ARM64 for every
ARMeilleure test, in unmodified upstream 1.3.3 as well: `CpuContext` built its translator with
`JitMemoryAllocator(forJit: false)`, and `JitCache` writes through `pthread_jit_write_protect_np` into a region that
was never mapped `MAP_JIT`/executable (SIGBUS, PC in an unmapped page). With `forJit: true`:
`CpuTestAluImm` 352/352 and the BotwPc suite **77 passed, 4 skipped (need local game data), 0 failed**, including
`CpuV2DifferentialTests` (random A64 programs on Unicorn vs legacy ARMeilleure vs CPU V2, high tier, ARM64 backend).

### 3. Test host and `--botw` on macOS (fixed)
See the table above (MAP_JIT for tests, executable name, data dir).

## Gameplay validation (M4)

Scripted input (`BOTWPC_INPUT_SCRIPT`), keyboard-free and controller-free (`BOTWPC_AUTO_CONTROLLER=0`), on a
disposable APFS clone of the data dir per run (`mac-bench/matrix.sh`, local); captures every 10–20 s:

- Title screen (Ver. 1.6.0, DLC Ver. 3.0) → *Continue* → load the user's save → Great Plateau (初始台地) at night →
  walk, sprint, jump, camera pans, inventory, map. Rendering correct (terrain, foliage, shadows, water, HUD, Sheikah
  Slate UI, CJK text, Game Over screen).
- **Audio**: SDL2 backend opens the stream (240-sample target); while the game runs CoreAudio holds
  `audio-out BuiltInSpeakerDevice`, and releases it when it exits.
- **Controller**: the wired Xbox Series X controller is auto-assigned to Player 1 at start and on hot-plug
  (`AutoController`), with Xbox prompts. Physical-button play was left to the user (the agent cannot press buttons).
- **Stability**: LightningJit 20+ runs, no crash, no hang with the bundled MoltenVK.
- **Saves**: the 43 original save files in `devdata` are byte-identical (SHA-256) after all runs; every test used a
  copy.

## Performance (M4, Runtime V2 defaults, docked, 1×, FXAA, LightningJit)

Window: 60–90 s after the first frame (save loaded, walking and camera turns outdoors at night; later the idle Link
gets attacked, and the Game Over screen would inflate the numbers).

| Mode | Emulated VBlank | Measured FPS (60–90 s) | Game speed |
|---|---|---|---|
| 30 (Switch VSync) | 60 Hz | 28.6–30.0 (median 30.0) | 0.99 (stock, nothing written) |
| 60 | 120 Hz | 35–43 | 0.98 |
| 90 | 180 Hz | 32–40 | 0.97–0.98 |
| 120 | 240 Hz | 30–45 | 0.98 |

The 30 FPS mode is the stable one. Higher modes are honest: no frame generation, the game simulates once per rendered
frame with BotW-PC's VFR correction (game speed ≈ 1). The M4 reaches ~35–40 FPS outdoors, so 60/90/120 cap
nothing in this scene. Counters at ~35 FPS: GPU busy 72 % of wall time (Vulkan timestamps), GPU command thread 57 %, of
which 32 % waiting for GPU sync → **GPU-bound**. On this fanless MacBook Air, sustained runs throttle: the same 60 FPS scene measured 37–43 FPS in the first hour of testing and 28 FPS after about an hour of back-to-back runs (macOS reports no thermal level on Apple Silicon, but the drift is monotonic across configurations).

| Experiment | Result | Kept |
|---|---|---|
| GPU front end V2 + HLE V2 vs legacy (LightningJit) | 40.5/41.5 vs 42.5/43.4 FPS whole-run average, within noise | V2 kept (same speed, stays the Runtime V2 config) |
| FXAA off / handheld (720p) instead of docked (900p), 60 FPS mode, 2× interleaved | 28.9–29.5 / 31.5 vs 28.2–28.3 FPS (hot machine); ≤ +10 %, within the thermal drift | not changed (user settings kept) |
| MoltenVK 1.4.2 (Homebrew) instead of the bundled 1.2.0 | not faster (32 vs 30–40 FPS), **one freeze** (frames stop at 68 s, process alive) | no |
| Apple Hypervisor | crashes (above) | no |
| ARMeilleure + CPU V2 | crashes/hangs (above) | no |

## Not done / limits
- 60 FPS is not reached outdoors on a base M4; Pro/Max GPUs should scale. MetalFX upscaling not investigated.
- ARMeilleure on macOS ARM64 (and with it CPU V2 in game) needs an upstream-level fix.
- The Apple Hypervisor's deferred TLB invalidation is a hypothesis, not a proven root cause.
- The app is ad-hoc signed: it runs on this Mac; another Mac needs `xattr -dr com.apple.quarantine BOTW-PC.app` or a
  Developer ID signature + notarization.
- LibHac 0.20.0 instead of 0.21.0-alpha.116 until `git.ryujinx.app` is reachable.
