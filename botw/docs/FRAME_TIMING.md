# Frame timing: 30 / 60 / 90 / 120 FPS with real-time game speed

## How BotW keeps time (1.6.0, verified in the binary)
`ksys::VFR` (decomp: zeldaret/botw `src/KingSystem/System/VFR.cpp`, which targets 1.5.0) never reads wall-clock
time. `VFR::init(interval=2)` sets a 30 FPS baseline (`frameTime = 1/30 s`). Every frame
`VFR::updateInterval(new_interval)` sets `ratio = new_interval / 2`, and all per-frame quantities use
`deltaFrame = ratio` and `deltaTime = ratio × 1/30 s` (movement, animation, camera, Havok time factor).

In 1.6.0 (`main` build `8E9978D5…`) `VFR::updateInterval` is at main+`0x1c01180`; both callers load the instance
from `VFR::sInstance` at main+`0x2d1e170`. Field layout confirmed against the code and at runtime:
`mNewIntervalRatio +0x30`, `mIntervalA +0x34`, `mIntervalRatioA +0x3c`, `mInterval +0x150`, `mFrameRate +0x154`,
`mFrameTime +0x158`. When the interval is unchanged, `updateInterval` reloads the ratio from `mNewIntervalRatio`.

Consequence: rendering faster (e.g. raising the emulated VBlank rate) speeds the whole game up. Measured without
correction at 240 Hz: **game speed 3.06×** at 93 FPS (`s1-240hz-notiming`), 3.59× at 115 FPS (`motion-240-notiming`).
The 30 FPS cap is the sead framework waiting 2 VBlanks per frame (present interval 2).

## What BOTW-PC does (`src/Ryujinx.HLE/BotwPc/BotwTiming.cs`)
1. `BotwProfile` enables it only for title `01007EF00011E000` with main build `8E9978D50BDD20B4C8395A106C27FFDE`;
   other BotW builds are logged as unsupported and left alone.
2. Before the first write the VFR object is validated at runtime (`mInterval` 1–4, `mFrameRate == 60/mInterval`,
   `mFrameTime == 1/mFrameRate`); on mismatch the feature disables itself and logs an error.
3. On every frame the game queues, the measured frame time (exponential moving average, α = 0.2) gives
   `ratio = frameSeconds × 60 / mInterval`, clamped to `[1/16, game's own ratio]`, written to `mNewIntervalRatio`.
   The clamp keeps console behaviour below 30 FPS (the game slows down instead of taking oversized steps).
4. With VSync mode **Switch** (stock) nothing is ever written. `BOTWPC_TIMING=0` measures without writing.
5. It also accumulates the ratio the game actually used (`mIntervalRatioA`) to report **game seconds per real
   second** (`game_s` in `counters.csv`, `game_speed` in `summary.json`).

No game code is patched. The game still simulates once per rendered frame, so at 120 FPS it steps at ~120 Hz
(no interpolation, no generated frames).

## Selecting a frame rate
Existing Ryubing setting: **Settings > System > Enable custom refresh rate**, slider in % of 60 Hz.
BotW presents every 2nd VBlank: **200 % → 60 FPS, 300 % → 90 FPS, 400 % (max) → 120 FPS**. VSync mode
**Unbounded** = uncapped. The setting can be changed at runtime (hotkeys too); the correction follows.

## Results (S1, Shrine of Resurrection, 1080p)

| Setting | Configured | Measured median | 1 % low | p99 | Game speed | Stable? |
|---|---|---|---|---|---|---|
| Switch (stock) | 30 | 30.0 | 28.9 | 33.6 ms | 1.00 (untouched) | yes |
| 200 % (120 Hz) | 60 | 60.0 | 53.3 | 17.0 ms | **1.00** | yes |
| 300 % (180 Hz) | 90 | 90.2 | 70.3 | 13.7 ms | **1.00** | yes |
| 400 % (240 Hz) | 120 | **93.1** | 71.7 | 13.5 ms | **1.00** | no — throughput-limited here (see `OPTIMIZATIONS.md`) |
| Unbounded | — | 95.2 | 73.4 | 13.2 ms | **1.00** | varies with scene |
| 400 %, opening cutscene | 120 | **118.7** | 59.7 | 13.1 ms | **1.00** | close to target |

So: **configured 120 FPS: yes. Measured 120 FPS: in lighter scenes (cutscene 118.7 median). Stable 120 FPS in the
Shrine: no (~93). Correct ~120 Hz simulation: yes for the 3D game; not for UI/2D layout animations (below).**
The monitor here refreshes at 99.65 Hz, so at most ~100 distinct frames per second are displayed.

## Tests

| Test | Method | Result |
|---|---|---|
| Real-time progression | `game_speed` over 50–60 s windows, all modes above | 0.996–1.000 with correction; 3.06×/3.59× without |
| Movement & camera physics | `motion.input`: camera turn 1.0 s + walk 1.5 s at 30 FPS, ~104 FPS corrected, ~115 FPS uncorrected; compare final frames | corrected run ends in the same place/angle as 30 FPS; uncorrected overshoots ~3× (`bench/motion-compare.png`, local) |
| 3D cutscene speed | `newgame.input`, 1 s captures at 30 vs ~119 FPS, frame-matching (`tools/align_timelines.py`) | constant offset ±1 s over 40 s of the in-engine cutscene ⇒ same playback speed (`results/cs1-align.txt`) |
| Stock behaviour | Switch VSync with module loaded | 30.0 FPS, game speed 1.00, no writes |
| Unit tests | `Ryujinx.Tests/BotwPc/BotwProfileTests.cs` | ratio formula, clamps, version gate, layout check: pass |
| Gameplay at 30/60/~85 FPS (2026-10-15) | `regress.input` on the player's save (jump, walk, attacks, throw, camera turn, sprint + jump) with `BOTWPC_CAMERA_LOG`, compared by `camera_track.py` | Link's net travel within 0.5 % of 30 FPS, walking within 0.2 %, throw identical; camera turn 1.6–3 % smaller per second and standing-jump peak ~0.03 m lower at high frame rate (KNOWN_ISSUES #32); 30 FPS runs bit-identical |

## UI / 2D animations (fixed, `src/Ryujinx.HLE/BotwPc/BotwUiTiming.cs`)
How it was found (1.6.0, no symbols; method reusable for other per-frame constants):
1. `BOTWPC_DUMP_RUNTIME=<file>` dumps main *after* relocation, so vtables hold real addresses. The RTTI name
   `N2nn4ui2d18AnimTransformBasicE` leads to `nn::ui2d::AnimTransformBasic`'s vtable (main+0x2c221a8; its
   `UpdateFrame` is an empty `ret`; frame float at +0x20, resource at +0x18).
2. The vtable sharing its slots but overriding `UpdateFrame` is `eui::Animator` (main+0x2bbe308):
   `UpdateFrame(progress)` at main+0x136b000 does `frame += speed(+0x50) * progress`.
3. Ryujinx's GDB stub (`enable_gdb_stub`) + gdb breakpoint there: one caller (main+0x136de74), progress 1.0/1.5 on
   the title; the caller gets progress from a virtual getter (main+0x1a35d20) = UI manager (+0xb20).
4. The only per-frame writer of +0xb20 (main+0x18522a0) computes `max(interval,1) * 0.5` from VFR's integer interval.

Fix: those 7 instructions now load VFR's ratio in use (`mIntervalRatioA`, +0x3c) — the value BOTW-PC's timing
correction drives. On stock 30/20 FPS the ratio equals interval/2, so the console behaviour is unchanged. The patch
is applied in memory after loading, only when the original instructions match (an ExeFS mod changing them disables
it with a warning); PPTC retranslates the changed function by hash. `BOTWPC_UI_TIMING=0` turns it off.

Verification — title-menu artwork slideshow (a 2D layout animation), screenshots every 1 s, 90 s, no input
(`bench/ui-title-*`): period between artwork changes

| Mode | Patch | Measured FPS | Period |
|---|---|---|---|
| 30 FPS (stock) | off | 30.0 | ≈15.5 s |
| 30 FPS (stock) | on | 30.0 | ≈15.5 s (unchanged) |
| 120 FPS | off | 120.0 | ≈4–5 s (≈3.5× too fast) |
| 120 FPS | **on** | 120.0 | **≈15.3 s** |

Not yet checked: the opening title/flare sequence, HUD, pause-menu transitions and dialogue text speed (expected to
follow, since they are eui layouts driven by the same step), and menu key-repeat (may be counted separately).

## Defects found (see `KNOWN_ISSUES.md`)
- **(Fixed above for UI-step-driven layouts.) UI / 2D layout animations ran per frame, not per VFR delta.** The opening "title text + light flare" sequence
  (a 2D layout) took 15 s at 120 FPS vs 56 s at 30 FPS (~4×). The following 3D cutscene is correct, so the
  in-game result of this is the 120 FPS run *reaching* the cutscene 35 s earlier. Menu D-pad key-repeat also
  appears frame-based (a 0.2 s hold at 120 FPS skipped menu items). HUD animations are therefore expected to be
  faster at high FPS. Fix requires finding BotW's UI layout frame step (not done).
- When the game changes its present interval (loading, some scenes) the game computes one frame with its own ratio
  before the next write. Not observed as a visible problem.

## Not yet tested (no save at these places / not scripted)
Climbing, gliding, swimming, horse, combat incl. Flurry Rush, bow aiming, weapon throwing, stamina, bombs,
Magnesis/Stasis, shrine mechanisms, ragdolls, water, Koroks, fast travel, video (`.mp4`) playback and voice/audio
sync. Cemu FPS++ (Wii U) documents fixes for ragdolls, water drag, hidden Koroks, Master Cycle, cutscenes and
loading — the same issues are plausible here and must be checked before calling 60/90/120 production-ready.

## Held-button repeat (`src/Ryujinx.HLE/BotwPc/BotwInputRepeat.cs`)
sead::ControllerBase::updateDerivativeParams_ (1.6.0: main+0x126e3b0) counts held frames per button and fires
"repeat" at `delay` frames and then every `pulse` frames (per-button u8 arrays at +0xb0 / +0xd0, counts at +0x30; layout
from open-ead/sead, confirmed by the strb/ldrb offsets in the 1.6.0 code). The game sets these values from tables
(e.g. delay 6, pulse 2), so they cannot be rescaled once. Instead, the repeat loop (main+0x126e490..0x126e524) is
rewritten in place, same 38 instructions, to compare against `delay * k` and `pulse * k` with
`k = max(1, round(1 / VFR ratio in use))` (VFR +0x3c). Hold counts still advance once per frame. At 30 and 20 FPS k = 1
(console behaviour). The original instructions are verified first; `BOTWPC_INPUT_REPEAT=0` disables it.

| Test | Result |
|---|---|
| Patched loop executed on the ARM64 JIT (`BotwInputRepeatTests`, delay 6 / pulse 2): 30 FPS repeats on frames 7, 9, 11, …; 90 FPS on 19, 25, 31, … (same times); 20 FPS and "no VFR yet" behave like the console; pulse 0 never repeats | pass |
| Title menu, holding Down for 0.3 / 0.5 / 0.7 s (`bench/rep2-*`): cursor row 1 / 1 / 3 at 30 FPS, 1 / 1 / 3 at 90 FPS patched, 5 / 5 / 5 (bottom) at 90 FPS unpatched | pass |

