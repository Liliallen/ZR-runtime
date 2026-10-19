# BOTW-PC

A Breath of the Wild–focused fork of the Ryubing (Ryujinx) Switch emulator, on branch `botw-pc`.
Upstream base: Ryubing **1.3.3**, commit `e2143d43bcb6762340d8a01f20e7b5fdf104f02f` (branch `upstream-1.3.3`).
Everything BotW-specific is gated on title `01007EF00011E000` **and** the exact main executable build ID,
so other games keep stock behaviour.

## What works today (measured, see `docs/`)

| Feature | Status |
|---|---|
| Offline source build of Ryubing 1.3.3 (official feed unreachable) | works, `scripts/build.sh` |
| Vulkan fence pool (generic) | **+75 % headroom**: 52.8 → 92.3 FPS median uncapped |
| Buffer lookup cache (generic) | GPU command thread −16 % per frame, +4.7 % FPS (A/B) — `docs/OPTIMIZATIONS.md` |
| High-FPS game timing for BotW 1.6.0 | 60 / 90 FPS reached with game speed 1.00×; 120 FPS mode reaches ~86–93 FPS in the Shrine (not 120) |
| `FIFO_LATEST_READY` presentation (NVIDIA/Wayland has no MAILBOX) | removes a ~58 FPS cap |
| Profiling | stage timers, Vulkan GPU timestamps, `perf` with JIT symbols — `docs/PERFORMANCE.md` |
| Xbox controller | auto-assigned to Player 1, position/label layouts, custom bindings, rumble stop on pause — `docs/CONTROLLER_MAPPING.md` |
| Virtual gyroscope | keyboard keys or holding View turn a simulated controller; the game aims the scope with it (verified in game); a motion shrine is not reachable with the available save — `docs/VIRTUAL_MOTION.md` |
| In-game overlay menu (L3 + R3 / Insert) | graphics (live DLSS / FSR 1 filter / res scale / AA), frame rate (live), performance HUD, controller, motion, mods & DLC, pause; verified in game with scripted physical input, keys and pointer; ≈0.5 % cost — `docs/OVERLAY.md` |
| Held-button repeat at high FPS | menus scroll at console speed at 60–120 FPS (sead repeat loop patched) — `docs/FRAME_TIMING.md` |
| Xbox button prompts in game | glyphs generated from the active mapping (text font + HUD textures); verified in game for both layouts with a real Series X\|S pad — `docs/CONTROLLER_PROMPTS.md` |
| Mods | safe installer (.zip/folder), UKMM deploy output, load order, conflicts, profiles, safe mode, rollback; loader order verified in game — `docs/MODDING.md` |
| DLC | Ryujinx's DLC manager + BotW version/compatibility/hash verification; game recognises the Expansion Pass (Master Mode, DLC Ver. 3.0) — `docs/DLC.md` |
| Settings > BotW-PC | frame-rate presets, controller, prompts, custom bindings, motion, controller test window, safe mode |
| Standalone launcher | `botw-pc` / `Ryujinx --botw`: isolated data dir, first-run import, direct launch; self-contained package — verified booting into the game from the package |
| DLSS Super Resolution | **runs in game** (`BOTWPC_DLSS=quality`): replaces BotW's 900p→1080p upscale, jitter + camera motion vectors verified; ghost trail behind Link on fast turns, so not in the UI yet — `docs/DLSS.md` |

## macOS (Apple Silicon)

```bash
botw/scripts/build-macos.sh                                        # → ../dist-macos/BOTW-PC.app (Finder-launchable)
botw/scripts/setup-macos.sh ../dist-macos/BOTW-PC.app <data dir> <game dir>   # copies, never modifies the source
```
Native arm64, LightningJit, MoltenVK, SDL2; data in `~/Library/Application Support/BOTW-PC`. Crashes found, measured
frame rates and limits: `docs/MACOS_PORT.md`.

## Standalone (BOTW-PC as an application)

```bash
botw/scripts/package.sh                                   # self-contained build in ../dist/botw-pc (+ tarball)
../dist/botw-pc/botw-pc --botw-setup --import <existing Ryujinx data dir> --import-saves \
    --game <base.nsp> --update <update.nsp> --dlc <dlc1.nsp> --dlc <dlc2.nsp>   # copies, never modifies the source
../dist/botw-pc/botw-pc                                   # straight into the game, fullscreen, closes with the game
```
`botw-pc` = `Ryujinx --botw`: own data directory (`~/.local/share/BOTW-PC`, or `portable/` next to the binaries,
or `-r <dir>`), finds the game from `settings.json → game.path` or the configured game folders, launches it directly
(no game library), and exits when the game stops (`game.exit_with_game`). `botw/packaging/botw-pc.desktop` is a
desktop entry. No AppImage is built unless `appimagetool` is installed; the tarball is a portable installation.
It still runs on Ryujinx's emulation core (JIT, HLE, GPU) in-process — no external Ryujinx is needed.

## Quick start

```bash
botw/scripts/build.sh                     # builds into ../build-botwpc (installs nothing system-wide)
../build-botwpc/Ryujinx -r ../devdata     # isolated data dir; Settings > BotW-PC > Frame rate
botw/scripts/bench_s1.sh unb my-run       # fixed benchmark scenario (30|60|90|120|unb), results in ../bench/my-run
../build-botwpc/Ryujinx --botw-mods -r ../devdata list      # mod CLI (docs/MODDING.md)
../build-botwpc/Ryujinx --botw-dlc -r ../devdata --verify   # DLC check (docs/DLC.md)
```

## Layout

| Path | Contents |
|---|---|
| `src/Ryujinx.HLE/BotwPc/` | version detection (`BotwProfile`), timing correction (`BotwTiming`), `InputScript`, DLC checks (`BotwDlc`), mods (`Mods/`), button prompt overlay (`Prompts/`) |
| `src/Ryujinx.Common/BotwPc/` | `GpuCounters`, `BotwPcSettings` (`<data>/botw/settings.json`), `ButtonPrompts` (prompt mapping) |
| `src/Ryujinx.Input/HLE/AutoController.cs` | controller auto-assignment and layouts |
| `src/Ryujinx.Graphics.Vulkan/FencePool.cs`, `PresentModeFifoLatestReady.cs`, `RenderTargetCensus.cs` | Vulkan changes |
| `native/ngx_bridge/` | C bridge over the NVIDIA DLSS SDK + standalone inference test |
| `scripts/` | build, run/benchmark, statistics |
| `tools/` | offline-feed helper, disassembly helpers, timeline alignment |
| `results/` | raw benchmark outputs (no game imagery) and `index.csv` |
| `docs/` | architecture, baseline, optimizations, timing, DLSS, compatibility, known issues, next steps |

## Runtime switches (all default to the behaviour described above)

| Variable | Effect |
|---|---|
| `BOTWPC_FENCE_POOL=0` | disable fence reuse |
| `BOTWPC_BUFFER_LOOKUP_CACHE=0` | disable the buffer lookup cache |
| `BOTWPC_PROFILE=1` | per-draw profiling timers (costs a few % FPS) |
| `BOTWPC_AUTO_CONTROLLER=0` | no automatic controller assignment (benchmarks) |
| `BOTWPC_SAFE_MODE=1` | start without mods, ExeFS patches or cheats |
| `BOTWPC_PROMPTS=0` | keep the game's own button glyphs |
| `BOTWPC_MOTION_LOG=1` | log Player 1's six-axis data once per second |
| `BOTWPC_DLSS=quality\|balanced\|performance\|dlaa` + `BOTWPC_NGX_DIR=<bridge dir>` | DLSS in place of the game's upscale (experimental) |
| `BOTWPC_FIFO_LATEST_READY=0` | never pick FIFO_LATEST_READY |
| `BOTWPC_TIMING=0` | measure BotW game speed but do not correct it |
| `BOTWPC_UI_TIMING=0` | keep frame-based UI animations (no UI step patch) |
| `BOTWPC_GUEST_PERFMAP=<file>` | perf map of translated guest functions (set by `run.sh` with `BOTWPC_PERF`) |
| `BOTWPC_TIMELINE=<file>:<start>:<seconds>` | cross-thread timeline (Chrome/Perfetto JSON), `scripts/timeline_report.py` |
| `BOTWPC_DUMP_RUNTIME=<file>` | dump the relocated main module once the game runs (local analysis only) |
| `BOTWPC_FRAMELOG=<csv>` | per-frame log (+ `counters.csv` beside it) |
| `BOTWPC_CAPTURE_INTERVAL=<s>` | periodic screenshots |
| `BOTWPC_INPUT_SCRIPT=<file>` | timed controller input (benchmarks) |
| `BOTWPC_RT_CENSUS=<frame>:<n>:<csv>` | render-target census |
| `BOTWPC_DUMP_IMAGES=<dir>` | dump loaded executables for local analysis (never commit) |

## Legal

No game files, keys, firmware, extracted code or game screenshots are in this repository.
The NVIDIA DLSS SDK is downloaded at build time and is not committed (NVIDIA RTX SDK license).
