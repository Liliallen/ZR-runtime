# Cemu-Switch: Nintendo Switch BotW 1.6.0 inside a Cemu-derived macOS app

Branch `cemu-switch` here (BOTW-PC side) and branch `cemu-switch` of a Cemu checkout (`cemu-project/Cemu` at
`a53cf0e`). Output: `Cemu-Switch.app`, a separate bundle (`info.cemu.Cemu-Switch`, data in
`~/Library/Application Support/Cemu-Switch`), next to an unmodified upstream `Cemu.app`. Machine: MacBook Air M4,
macOS 27.0.1.

## Two modes, one process

| | Wii U mode | Switch mode |
|---|---|---|
| Entry | Cemu's game list / File > Load | File > Boot Nintendo Switch BotW 1.6.0… |
| CPU | Cemu PPC recompiler | BOTW-PC LightningJit (ARM64 → ARM64) |
| GPU | Cemu Latte → Metal (or Vulkan/MoltenVK) | BOTW-PC Maxwell front end → Vulkan → MoltenVK 1.2.0 → Metal |
| OS | Cemu Cafe OS HLE | BOTW-PC Horizon HLE (Runtime V2) |
| Window | Cemu's render canvas | `SwitchCanvas`: a wxWindow hosting a CAMetalLayer view, in Cemu's main window |

The Switch runtime is not a second program: Cemu loads .NET in-process (hostfxr, app-local runtime in
`Contents/Resources/dotnet`) and calls `Ryujinx.Headless.CemuSwitchEntry` (`[UnmanagedCallersOnly]`):

| Entry point | What |
|---|---|
| `Start(StartArgs*)` | data folder, game file, NSView + CAMetalLayer, pixel size, a "run on main thread" callback; boots on a runtime thread |
| `Stop()` | asks the game to exit, returns at once (shutdown needs the main thread for SDL; Cemu pumps events while polling) |
| `SetSize(w, h)` | canvas resize → swapchain size |
| `KeyEvent(kVK, down)` | keyboard of the canvas → BOTW-PC keyboard profiles (`HostKeyboardDriver`) |
| `SetFrameRate(fps)` | 30 / 60 / 90 / 120 at runtime; stored in the data folder's `Config.json` |
| `GetStats(Stats*)` | running, FPS, frame time, BotW game seconds (game speed), frame-rate mode |

Runtime changes needed for embedding (all inert when not embedded):
- `ExternalMetalWindow`: Vulkan surface from the host's CAMetalLayer (`VK_EXT_metal_surface`) instead of an SDL window.
- SDL starts without video (the host owns Cocoa); SDL calls that must run on the main thread go through the host
  callback (`SDL2Driver.MainThreadDispatcher`).
- `Assembly.SetEntryAssembly` (hosted without `Main`), `AppDataManager.Initialize` before SDL.
- Headless config bug fixed on the way (also affects the stock headless app): the main configuration was inherited
  before it was loaded, so the resolution scale was 0 → 0×0 render targets → Metal abort.

## Features

| Requirement | State |
|---|---|
| Wii U mode preserved | unchanged code path; Switch code is `#ifdef CEMU_SWITCH` (Apple + Metal builds only) |
| Switch BotW 1.6.0 + DLC 3.0 boots and plays | verified by hand: title → save load → Great Plateau, walk/camera, Stop → back to the game list |
| Save isolation | first boot clones (APFS `copyfile` CLONE) a chosen BOTW-PC data folder into Cemu-Switch's own folder; the source is only read (43 save files hash-identical before/after, `save-backup/hashes-*`) |
| Settings | BOTW-PC's `Config.json` of the copy (graphics, input profiles, mods); frame rate selectable in File > Switch frame rate |
| 30/60/90/120 FPS | same mechanism and timing correction as BOTW-PC (`FRAME_TIMING.md`); game speed shown live |
| Keyboard | canvas keys → BOTW-PC keyboard profile |
| Xbox controller | SDL GameController via BOTW-PC's auto-controller (hot-plug); **not tested** here (pad not connected during the session) |
| Motion | BOTW-PC virtual motion (`VIRTUAL_MOTION.md`), same config; **not tested** in Cemu-Switch |
| Fullscreen / windowed | Alt+Enter (Cemu's shortcut), resize follows |
| DLC / mods | from the data folder, as in BOTW-PC |
| Performance overlay | AppKit label over the game (FPS, ms, game speed, mode; File > Switch performance overlay) + title bar |
| Wii U loading while Switch runs | blocked (menu items disabled, `FileLoad` refuses) |

## Licensing

| Component | Licence | Use |
|---|---|---|
| Cemu | MPL-2.0 | modified files stay MPL-2.0 (file-level copyleft); new files `src/gui/wxgui/switch/*` MPL-2.0 |
| BOTW-PC / Ryujinx / Ryubing | MIT | runtime shipped as DLLs in the bundle, notices kept |
| .NET runtime | MIT | app-local copy |
| MoltenVK | Apache-2.0 | 1.2.0 (NuGet build), as in BOTW-PC |
| Dynarmic | 0BSD | not used (see below) |
| Eden | GPL-3.0 | not used, no code read for copying |
| Cemu FPS++ graphic pack | no redistribution "or parts of it" without permission (its README) | studied only; nothing copied (below) |

## Dynarmic

BOTW-PC's ARM64 path is LightningJit; Dynarmic's A64 frontend targets x86-64 and ARM64 hosts. Replacing the JIT
would mean a C++ CPU engine behind Ryujinx's `ICpuEngine` / `IExecutionContext` (exclusive monitor, SVC callbacks,
tracked-memory faults, PPTC replacement). Before doing that the question is whether guest CPU speed limits the
frame rate on this machine at all. `DYNARMIC_OPTIMIZATION.md` covers the design comparison (CPU V2 took ideas, no
code).

Causal test (below): guest threads slowed by a fixed percentage in alternating 10 s periods of one run.

BOTW-PC.app, S1, 60 FPS mode, `BOTWPC_DELAY_GUEST_PCT=n` + `BOTWPC_AB_PERIOD=10` (each guest thread spins n % of
its run time at every SVC, every second 10 s period), 90–285 s, `ab_phases.py` (each "on" period vs the mean of its
neighbours), unlocked:

| Guest slowed by | FPS off → on | Δ frame time | 95 % CI of ΔFPS | 1 % low |
|---|---|---|---|---|
| 30 % | 46.2 → 40.5 | **+3.0 ms** | −6.0 .. −4.8 | 34.0 → 26.2 |
| 60 % | 44.2 → 34.0 | **+6.8 ms** | −10.6 .. −9.8 | 30.5 → 16.6 |

The guest CPU **is** on the critical path: about 10–11 ms of the ~22 ms frame scale with guest speed. This corrects
`MACOS_PERFORMANCE.md`, which inferred the opposite from busy time (34–36 % per guest thread) — busy time does not
show serial dependencies.

What a faster JIT could gain is bounded from the other side: the MoltenVK encoding queue is 99.4 % busy at ~51 FPS
(≈19.5 ms of encoding per frame), so even an infinitely fast guest leaves the frame at ≥ ~19.5 ms (≤ ~51 FPS, about
+10 % over the 46 FPS here). A slowdown test measures the slope going down; going up stops at the next bottleneck.

Decision: **Dynarmic is not integrated in this round.**
- Upside: at most ~+10 % here, and only if Dynarmic's A64→AArch64 code were markedly faster than LightningJit, a
  near 1:1 ARM64→ARM64 translator. That is not established; no measurement of Dynarmic on BotW's code exists.
- Cost: a C++ engine behind `ICpuEngine`/`IExecutionContext`, covering:
  - the exclusive monitor;
  - SVC and undefined-instruction callbacks;
  - tracked-memory faults through the host-mapped page table;
  - PPTC (no equivalent);
  - per-thread contexts.
  All of this sits on the path that must stay correct for saves.
- Order: the encoder ceiling has to come down first (fewer descriptor-set binds per frame, or the native Metal
  backend's GPU cost fixed). Only then does guest speed decide the frame rate, and a Dynarmic bridge becomes worth
  measuring with the same causal test.
- Licence: Dynarmic is 0BSD, so it would be usable in both Cemu (MPL-2.0) and BOTW-PC (MIT). Eden's fork and changes
  are GPL-3.0 and must not be used.

## FPS++ (Wii U) vs BOTW-PC (Switch)

FPS++ patches the Wii U executable (PowerPC addresses of v208); none of it can apply to the Switch 1.6.0 ARM64
binary, and its licence forbids reusing parts of it. What it fixes was compared with what BOTW-PC does:

| FPS++ patch | Problem at high FPS | BOTW-PC on Switch |
|---|---|---|
| GameSpeed (rolling-average FPS → game speed) | the game assumes 30 FPS per frame | `BotwTiming`: same idea through `ksys::VFR` (measured frame time → interval ratio, EMA α 0.2, clamped); game speed 0.996–1.000 measured |
| GameSpeed: GUI animation speed | HUD/UI layouts run per frame | `BotwUiTiming`: UI step from VFR's ratio (title slideshow 15.3 s at 120 vs 15.5 s at 30) |
| ControllerScrollSpeed (re-implemented `sead::ControllerBase::updateDerivativeParams_`) | menu key repeat is frame-counted | `BotwInputRepeat`: the same function's repeat loop rescaled (tested 30/90) |
| GameSpeed: stamina regen, arrow-time stamina drain, Flurry Rush distance, arrow speed/bounce, shield surfing, map scrolling, audio looping, rune/gyro sensitivity | hard-coded 30 / 1.0 constants | **not done**; untested on Switch (`KNOWN_ISSUES` #4) |
| Ragdolls, WaterPhysics, KorokChasing, MastercycleSpeed | per-frame impulses / quadratic timestep / per-frame paths | **not done**; untested on Switch |
| Cutscene (30 FPS cap in pre-rendered cutscenes) | some cutscenes desync | not done; in-engine cutscene speed verified correct at ~119 FPS |
| FenceMethod, LoadingScreen | Cemu GPU-sync / loading-screen behaviour | not applicable (emulator-specific) |

Adapting the open items means locating each constant in the 1.6.0 ARM64 binary (method in `FRAME_TIMING.md`) and a
save at the place to test it; neither was available in this round. 60/90/120 therefore stay "experimental"
for those systems, exactly as in BOTW-PC.

## Benchmark: Cemu-Switch vs BOTW-PC.app

S1 (Shrine of Resurrection save, scripted input), 60 FPS mode, same data template cloned per run, interleaved
rounds with a 45 s cool-down, fixed window 95–135 s after the first frame, display awake, Mac unlocked in all runs
(`mac-bench/ab.sh cvb 3`, `window.py`).

| Round | BOTW-PC.app median / 1 % low | Cemu-Switch median / 1 % low | game speed (both) | GPU busy |
|---|---|---|---|---|
| 1 | 44.6 / 25.1 | 50.5 / 33.2 | 0.996 / 0.999 | 80 / 83 % |
| 2 | 49.1 / 34.7 | 48.8 / 30.9 | 1.000 / 0.999 | 82 / 82 % |
| 3 | 46.1 / 31.8 | 50.4 / 31.9 | 0.999 / 0.998 | 81 / 83 % |

Same runtime, same frame rate within run-to-run noise (paired difference +5.9 / −0.3 / +4.3 FPS, n = 3: not
significant). Embedding costs nothing measurable; process CPU is slightly lower in Cemu-Switch (3.0–3.3 vs 3.0–3.5
cores: no Avalonia UI). Both are bound by MoltenVK's encoder and the GPU (`MACOS_PERFORMANCE.md`).

### Differential checks

| Check | Result |
|---|---|
| BOTW-PC test suite (`Ryujinx.Tests.BotwPc`, incl. CPU V2 differential vs legacy, 250 random programs) on this branch | 78 pass, 4 skipped (opt-in), 0 fail |
| Game speed embedded vs standalone (S1) | 0.998–0.999 vs 0.996–1.000 |
| Frame-rate menu, title screen | 120 → 30: 30.0 FPS, speed 1.00; 30 → 60: 60.0 FPS, speed 1.00 |
| Stop → boot again in the same process, then keyboard input | works (title, load menu) |

Bugs found by this work and fixed in shared BOTW-PC code:
- **Slow motion after lowering the frame rate at runtime** (also in BOTW-PC.app with the VSync hotkey): back in VSync
  mode Switch, `BotwTiming` stopped writing but left its last ratio in `mNewIntervalRatio`, which the game reloads until
  its interval changes: 120 → 30 FPS ran at 0.27× speed. The game's own ratio is now restored once
  (`SwitchingBackToConsoleTimingRestoresTheGameRatio` fails without the fix, 0.176 instead of 1.0).
- Headless could not boot twice per process (`ConfigurationState`/`VirtualFileSystem` singletons, input list
  accumulating).
- Headless inherited the configuration before loading it (resolution scale 0 → Metal abort).

## Blockers / not verified

- **Wii U BotW in Cemu:** no Wii U game files on this machine, so Cemu's own BotW (and FPS++ in Cemu) could not be run
  or benchmarked. The Cemu builds start and show the game list; nothing more is claimed.
- Xbox controller and motion in Cemu-Switch: implemented through the same BOTW-PC code, not exercised.
- Windows/Linux Cemu builds: the Switch code is compiled only for Apple + Metal; other platforms build as upstream
  (not built here).
