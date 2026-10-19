# Backend comparison (Phase B)

Machine: RTX 5070 Ti 16 GB, NVIDIA 615.78, Arch Linux, KDE Plasma Wayland, 1920×1080 @ 99.65 Hz (`BASELINE.md`).
Game: BotW Switch 1.0.0 base + 1.6.0 update (main build `8E9978D5…`, identical for every Switch runtime) + DLC 1/2,
the user's own files. Measured = run here; **Literature** = from the sources in the last section; **Estimate** = my judgement.

## Measurement method
- `native/present_layer`: a Vulkan layer (enabled only through `VK_LAYER_PATH`/`VK_INSTANCE_LAYERS`) that logs a
  timestamp per `vkQueuePresentKHR`. Same method for every Vulkan runtime; checked against BOTW-PC's own frame log
  (100 576 presents vs 100 311 logged frames in the same run).
- `scripts/present_stats.py`: median / 1 % low / p95 / p99 from the present log.
- `scripts/bench_title.sh <botwpc|eden>`: scenario **T0** (no input needed): boot, update + DLC, idle on the title menu at
  the stock 30 FPS; samples process CPU time, RSS, GPU utilisation and VRAM (`nvidia-smi`, whole GPU incl. desktop)
  once per second from 60 s.
- `scripts/bench_eden.sh`: scenario **S1** on Eden (same save copied into Eden's isolated NAND). Eden has no input
  scripting, so a person must press A to load the save — **not run yet** (see "Not measured").
- Eden v0.2.1 (`Eden-Linux-v0.2.1-amd64-clang-pgo.AppImage`, official host `stable.eden-emu.dev`; no checksum is
  published, SHA-256 of the file used here `7a28bf98…25ba6d`) runs from `alt-runtimes/eden-0.2.1/` with an isolated
  `HOME` (keys, firmware 20.5.0, save copied in; nothing outside that folder is touched). Update/DLC are loaded from
  external content folders (`Paths\external_content_dirs`).

## Results

| Architecture | Scenario | FPS median | 1 % low | p99 frame | CPU (cores busy) | GPU util | RSS | Compatibility (here) | Engineering cost to adopt |
|---|---|---|---|---|---|---|---|---|---|
| **Ryubing / BOTW-PC** | T0 title, 30 FPS | 30.0 | 29.6 | 33.4 ms | 0.52 | 11.6 % | 6.4 GB | boots, plays (S1, opening, menus); update/DLC/mods/prompts | baseline |
| **Ryubing / BOTW-PC** | S1 in game, uncapped | 93–95 | 72–73 | 13.2 ms | GPU thread ~74 %, render thread ~77 % | GPU ~37 % busy (timestamps) | — | as above | — |
| **Ryubing / BOTW-PC** | S1 in game, 60 / 90 FPS mode | 60.0 / 90.2 | 53.3 / 70.3 | 17.0 / 13.7 ms | | | | game speed 1.00 | — |
| **Eden 0.2.1** | T0 title, 30 FPS | 30.0 | 29.9 | 33.4 ms | **0.27** | 15.6 % | 5.7 GB | boots 1.6.0 with update + DLC (title shows "DLC Ver. 3.0"); save recognised (title offers Continue) | **high**: C++/GPL-3.0 rewrite of every BOTW-PC feature |
| **Eden 0.2.1** | T0 with speed limit off + Immediate | **30.0** | | | | | | no uncapped mode for BotW: the game's own 2-VBlank pacing holds | |
| **Eden 0.2.1** | S1 in game | **not measured** (needs a person to press A) | | | | | | | |
| **Citron** | — | not run | | | | | | original project shut down 2026-02 (Literature); "Citron Neo" community nightlies, no signed artifacts | high + governance risk |
| **ScarletNX** | — | not run | | | | | | static AArch64→C recompiler, 3 days old; runs other games, **no BotW report** (Literature) | very high (research) |
| **mk8-recomp (hybrid AOT)** | — | not run | | | | | | MK8D only; AOT at best equals JIT (56–60 vs 60 FPS, Literature) | very high |
| **Cemu (Wii U BotW)** | separate platform | not run | | | | | | different game version (Wii U v208), different saves/mods/DLC; 60–120 FPS with FPS++ (Literature) | n/a — not a Switch 1.6.0 runtime |

What the measured numbers say:
- On the same input-free scene both runtimes hold 30 FPS with equally flat pacing; **Eden used about half the CPU
  time** (0.27 vs 0.52 cores; BOTW-PC's run also had its frame log/counters on) and a little less RAM. This hints at
  lower fixed per-frame overhead in Eden but says nothing about the in-game per-draw cost that limits BOTW-PC's
  120 FPS (3 900 draws/frame in S1 vs a near-empty 2D title).
- Eden has no BotW high-frame-rate support: uncapping the speed limit does not raise the frame rate, and the game
  timing (VFR) correction BOTW-PC uses does not exist there; 60 FPS on Eden relies on community ExeFS patches.

## Not measured (and why)
- **Eden in game (S1)**: no safe input automation. Eden has no input-script feature (its TAS player starts only from a
  hotkey/menu), and synthetic X11 keys (XTEST) were tried and abandoned: on KDE Wayland they cannot be guaranteed to
  reach only the Eden window. Procedure for a person: configure the Xbox pad as Eden's Player 1, run
  `scripts/bench_eden.sh 30 <label>` (or `unb`), press A twice when the title appears, leave Link idle.
- Citron, ScarletNX, mk8-recomp, Cemu: not built/run (see table; no BotW Switch path or not the same game).

## Literature (checked 2026-10-13)
- Eden: https://git.eden-emu.dev/eden-emu/eden (GPL-3.0-or-later, active; dynarmic JIT on x86-64, NCE only on ARM64),
  release https://git.eden-emu.dev/eden-emu/eden/releases/tag/v0.2.1
- Citron shutdown: https://retrohandhelds.gg/citron-popular-switch-emulator-is-gone-but-its-not-nintendos-fault/ ;
  successor https://github.com/citron-neo/emulator
- ScarletNX: https://github.com/scarletnx/ScarletNX (GPL-3.0+, AArch64→C→native module, interpreter fallback)
- mk8-recomp: https://github.com/dougchansan/mk8-recomp (no LICENSE file; LEGAL.md says GPL-3.0-or-later)
- Cemu / FPS++: https://github.com/cemu-project/Cemu (MPL-2.0),
  https://github.com/cemu-project/cemu_graphic_packs/tree/master/src/BreathOfTheWild/Mods/FPS%2B%2B — FPS++ forbids
  redistribution of its code; used as documentation only.
- bbhost https://github.com/droogie/bbhost, bloodborne_pc https://github.com/deadinside28/bloodborne_pc (PS4 x86-64:
  native execution is possible there, not for Switch AArch64).
- No credible published BotW (Switch) benchmark comparing Ryubing with yuzu-derived emulators was found.
