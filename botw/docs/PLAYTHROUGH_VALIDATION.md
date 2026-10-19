# Playthrough validation matrix

Status: **PASS** (verified in the game, evidence given) · **PARTIAL** (part verified, gaps listed) · **FAIL** ·
**NOT TESTED**. Saves available: the opening save, and a progressed save made by the user in BOTW-PC (outside the
Shrine of Resurrection, Great Plateau, no runes/paraglider; see SAVE_TESTING.md). Outdoor tests run on a disposable copy
of it. Nothing below is claimed playable without a run that reached it.

## How tests are run (repeatable, no person needed)
- Isolated data: `../devdata` (dev copy) or a fresh `botw-pc --botw-setup` directory; the user's Flatpak saves are
  only ever read (byte-identity checked, `COMPATIBILITY.md`).
- Scripted input: `BOTWPC_INPUT_SCRIPT` (buttons, sticks, virtual motion), scenarios in `botw/bench/*.input`.
- Evidence: game-window screenshots (`BOTWPC_CAPTURE_INTERVAL`), frame log + game-speed counter, timeline traces,
  six-axis log; raw evidence stays in `../bench` (contains game imagery, not committed).
- To extend coverage, put a save from the target area into an isolated data dir and add a `.input` scenario.

## Opening and Great Plateau

| Scenario | Status | Procedure / evidence |
|---|---|---|
| Boot, title screen, Continue, load save | **PASS** | `continue.input`, every benchmark run; 30/60/90/120/uncapped |
| Opening cutscene (new game) at 30 and 120 FPS | **PASS** (3D cutscene speed equal) | `newgame.input`, `results/cs1-*`, `FRAME_TIMING.md` |
| Opening 2D title/flare sequence speed at 120 FPS | **PARTIAL** | UI-step patch verified on the title slideshow (15.3 s vs 15.5 s); opening sequence itself not re-run |
| Shrine of Resurrection: walk, camera turn, idle | **PASS** | `motion.input` (end state equal at 30 and ~120 FPS), screenshots |
| Pause menu, controls page, HUD prompts (Xbox glyphs) | **PASS** | `prompts.input`, both layouts (`CONTROLLER_PROMPTS.md`) |
| Walking to the Sheikah Slate pedestal (HUD action prompt) | **PASS** | `bench/explore8` (standalone-test data dir) |
| Taking the Sheikah Slate, leaving the shrine (exit cutscene) | **PASS (played by the user)** | the progressed save was created in BOTW-PC by the user: `Open_StartPoint=1`, `IsPlayed_Demo103_0=1` |
| Great Plateau outdoors: loading the progressed save, terrain/vegetation/sky/distant LOD rendering | **PASS** | `bench/out1-load`, `out3-30` |
| Walk, sprint (stamina wheel), jump, camera orbit, climbing a rock wall (climb prompt), picking up a mushroom (pickup prompt, item card) | **PASS** | `out3-30`, `dur2-vanilla`, `dur3-vanilla` |
| Fall damage → death → Game Over screen (Retry / Quit) | **PASS** | `out2-30` (scripted sprint off a cliff) |
| Sheikah Slate map (first open: no map data yet, "current position") and inventory (Tree Branch, Sheikah Slate) | **PASS** | `out3-30` |
| Outdoor sequence at 30 / 60 / 90 / 120-mode FPS: median 30.0 / 60.0 / 89.4 / 93.3, game speed 0.99–1.00 | **PASS (timing)**; 1 % lows 23–35 FPS (stutters at map/inventory transitions, not investigated) | `out4-60`, `out4-90`, `out4-120` (taken before a concurrent game session started) |
| Scope (R3) aimed by virtual motion | **PASS** | `scope-motion-kbd` vs `scope-control` (VIRTUAL_MOTION.md) |
| Combat with an enemy | NOT TESTED | no enemy near the save point; Tree Branch swings tested only in the air |
| First tower, four opening shrines, paraglider, leaving the plateau | NOT TESTED | the save has not reached them |

## Main world, main quest, DLC

| Area | Status | Notes |
|---|---|---|
| Villages, stables, towers, traversal, weather, fast travel, horses, NPCs | NOT TESTED | no save |
| Combat (incl. Flurry Rush, bow), climbing, gliding, swimming, ragdolls, Stasis/Magnesis | NOT TESTED | high-FPS physics untested (`FRAME_TIMING.md` list) |
| Motion-control shrines with virtual motion | NOT TESTED | virtual sensor data verified reaching the game (`VIRTUAL_MOTION.md`) |
| Divine Beasts, bosses, Hyrule Castle, final battle, ending | NOT TESTED | no save |
| DLC recognised (title "DLC Ver. 3.0", Master Mode menu entry, add-on page) | **PASS** | `results/dlc-both` |
| Master Mode run, Trial of the Sword, Champions' Ballad, DLC shrines, Master Cycle Zero | NOT TESTED | no save |

## System reliability

| Item | Status | Evidence / notes |
|---|---|---|
| Save data integrity | **PASS** (not corrupted) | dev save byte-identical to the original after all runs; saving new progress NOT TESTED |
| Video: 3D rendering in tested scenes | **PASS** | screenshots; no artifacts seen in S1, title, opening |
| Audio | NOT TESTED | no game-only audio capture was done in these runs |
| Input: scripted logical input | **PASS** | all scenarios |
| Input: physical Xbox controller pressed by a person | NOT TESTED | detection/auto-assignment PASS |
| Prompts match the mapping | **PASS** (pause/controls/HUD text) | `CONTROLLER_PROMPTS.md` |
| Game timing 30/60/90/120 | **PASS** in S1 + cutscene (game speed 1.00); UI animations fixed (title) | `FRAME_TIMING.md` |
| Standalone launch (`botw-pc`), packaged build | **PASS** | `bench/standalone-*` |
| DLSS (experimental) | **PARTIAL** | static/slow scenes OK; ghost trail on fast turns (`DLSS.md`) |
| Mods: real community mods (exefs patches, romfs gameplay mods, DLC-mount mod, conflicts, profiles) | **PARTIAL** | MOD_COMPATIBILITY.md: exefs patches verified in the image, conflicts and DLC routing verified, one incompatible mod found (crash reproduced); gameplay effects of the gameplay mods not observed |
| Long sessions (> 5 min), memory growth | NOT TESTED | runs were ≤ 4 min |
| Controller disconnect/reconnect in game, window mode changes, safe mode in game | NOT TESTED | code paths only |

## Runtime V2 (CPU V2 + GPU V2 + HLE V2, 2026-10-16)

Same binary (`runtime-v2` `6ad74a0fe`), configuration selected at startup; the player's Great Plateau save (W2) in an
isolated data directory. Legacy is the reference. Everything not listed here is NOT TESTED with Runtime V2.

| Scenario | Status | Procedure / evidence |
|---|---|---|
| Boot, load save, PPTC with the V2 code cache | **PASS** | 52 runs with CPU V2 (matrix, A/B, DLSS, regression), all configurations |
| Frame-rate regression (`regress.input`: rest, jump, walk, attacks, throw, camera turn, sprint + jump) at 30/60/90/120-mode | **PASS** | `bench/v2/regress-v2full-{30,60,90,120}`: game speed 0.999–1.000, net travel 18.47–18.67 m, 12 screenshots each; at 30 and 60 FPS identical to round 5's legacy runs (10.70 m walk, −85.4° / −84.8° turn); camera turn at 90/120 FPS −82.8° as in round 5 (−82.5°): pre-existing frame-rate dependence of that segment, legacy shows it too |
| Legacy reference in the same session | (reference) | `regress-legacy-*`, `regress-legacyb-30/60`: 90/120 within 0.2 m and 1° of V2; two legacy 30 FPS runs loaded 0.4–2.8 s later than V2, which shifts the wall-clock input script against the game (walk carried into the attack segment, one missed jump) — a harness margin, not a game-logic difference |
| Frame rate in the 120 FPS mode of this scenario | (measurement) | V2 94.5 FPS vs legacy 87.5 FPS (single runs, loaded machine) |
| Published build (`botw-pc-runtime-v2`) | **PASS** | `regress-deliv-120`: CPU V2 (Flags), GPU front end V2, HLE V2 selected; 73 810 functions from the V2 PPTC; 95.0 FPS, game speed 1.000 |
| Enemy attack → Game Over (night enemies in W2) | **PASS** (screen and retry) | incidental in matrix runs; repeated runs |
| DLSS Quality | **PARTIAL** | active and image normal in 4 runs; one crash in another run (KNOWN_ISSUES #43, open) |
| Log errors | **PASS** | one `WaitOnSyncpoint … took more than 1000ms` during save loading in every run of every configuration (also legacy, also round 4/5: 76 of 77 runs): pre-existing |
| Saves | **PASS** (not modified) | 105 protected save files (devdata 43, original 19-file backup, player snapshot 43) SHA-256-identical before and after; saving new progress NOT TESTED |
| Combat by the player, horse, cutscenes, shrines, Divine Beasts, DLC content, long sessions, audio | NOT TESTED | no save / no scenario |
