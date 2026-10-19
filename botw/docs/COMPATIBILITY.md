# Compatibility

## Game versions
BotW-specific behaviour is keyed on the **main NSO build ID**, never on the version string:

| Version | Main build ID | Status |
|---|---|---|
| 1.6.0 | `8E9978D50BDD20B4C8395A106C27FFDE` | supported (timing correction, UI-step patch) |
| anything else | — | detected as BotW, logged `BOTW-PC profile: unsupported BotW build (...)`, stock behaviour |

Adding a version means re-deriving `VfrInstanceOffset` for that build (find the callers of `VFR::updateInterval`,
e.g. with `botw/tools/find_bl.py` on an image dumped with `BOTWPC_DUMP_IMAGES`) and adding it to
`BotwProfile.Versions`. The runtime layout check refuses to write if the object does not look like `ksys::VFR`.

Third-party high-FPS mods: the public 1.6.0 "60FPS" pchtxt (theboy181 et al.) and nx-optimizer's UltraCam
(closed-source exlaunch module, CC BY-NC) change the same present-interval/VFR logic. **Do not combine them with
BOTW-PC's timing correction**; they were not tested together and the pchtxt carries no license, so it is not
redistributed here. Ryubing's normal mod loader (pchtxt/IPS/romfs) is unchanged.

BOTW-PC features that read or patch game data check the content too: the UI-step patch compares the 7 original
instructions before writing; the prompt overlay checks font/texture formats and sizes and skips a file otherwise; the
DLSS upscale replacement matches the exact SPIR-V hash of BotW's upscale program and the D32F/RGB10A2 inputs.

## Other games
- BotW timing code, UI-step patch and prompt overlay: never applied to other titles (title-ID check).
- DLSS (`BOTWPC_DLSS`): only replaces a draw whose program hash matches BotW's upscale; other games are untouched
  (NGX extensions are still enabled on the device when requested).
- GAL `SetBotwCamera`: default no-op for other backends; only sent while `BOTWPC_DLSS` is set.
- Fence pool, FIFO_LATEST_READY: generic backend changes active for every game. Each has a kill switch
  (`BOTWPC_FENCE_POOL=0`, `BOTWPC_FIFO_LATEST_READY=0`). Only measured with BotW.
- Diagnostics (frame log, counters, capture, census, image dump, input script): off unless their variable is set,
  except the counters, which cost one interlocked add per flush/fence event.

## Saves and installation
- The Flatpak install and its data were never written to; development used `../devdata`, a copy.
- Save format is untouched; the timing change only alters in-memory per-frame deltas. The new-game cutscene test
  overwrote only the dev copy of the autosave, which was restored from `../bench/save-backup` afterwards.
- Config.json format is unchanged (version 70). BOTW-PC settings live in `<data>/botw/settings.json`.
- BOTW-PC never wrote the user's saves: the dev save's `game_data.sav` was still byte-identical to the Flatpak
  original on 2026-10-14 (SHA-256 prefix `407cc48e…`) after all test runs.

## Environment assumptions
- DLSS bridge: Linux x86-64, NVIDIA driver with NGX (`libnvidia-ngx.so`), DLSS snippet 310.9.1 next to the bridge.
- FIFO_LATEST_READY needs `VK_EXT_present_mode_fifo_latest_ready`; otherwise the original MAILBOX/FIFO logic applies.
