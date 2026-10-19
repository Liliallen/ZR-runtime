# Graphics settings

Where each graphics option lives, what it really changes, and when it applies. The in-game overlay (OVERLAY.md), the
settings window and the launcher all edit the same two files: `Config.json` (Ryujinx) and `botw/settings.json` (BOTW-PC).

| Option | Stored in | What it changes | Applies |
|---|---|---|---|
| Upscaler: Off / DLSS Quality / Balanced / Performance | `botw/settings.json` → `graphics.dlss` (`BOTWPC_DLSS` overrides) | Replaces BotW's own 900p→1080p upscale pass with DLSS; input = the game's render size (1600×900 outdoors), so the modes only select the NGX preset | **live** (feature recreated, history reset); NGX initialised at start when DLSS is selected or the overlay is enabled |
| DLAA | — | not offered: the game never renders at output size | — |
| Output filter (Bilinear / Nearest / FSR 1 / Area), FSR sharpness | `Config.json` | Ryujinx scales the finished frame to the window; FSR 1 is spatial | live |
| Resolution scale (1–4×) | `Config.json` → `res_scale` | Ryujinx renders the game's targets larger (DLSS input grows too) | live |
| Anti-aliasing (FXAA, SMAA) | `Config.json` | Ryujinx post-processing on the finished frame | live |
| Display mode | window state | windowed / fullscreen | live |
| Frame-rate target 30 / 60 / 90 / 120 / unbounded | `Config.json` (VSync mode + custom refresh 120/180/240 Hz) | emulated VBlank rate; BotW timing correction keeps game speed 1.00 | live |
| Performance HUD, overlay size | `botw/settings.json` → `overlay` | host overlay only | live |
| 1080p / Disable-FXAA ExeFS patches (community mods) | mod folder | the game renders natively at 1080p, so DLSS has no upscale to replace | game restart |

Measured costs (S1, uncapped): DLSS Quality −5 % (93.4 → 88.3 FPS, DLSS.md); overlay with the detailed HUD about
−0.5 % (OVERLAY.md). Not exposed, because no runtime behaviour exists behind it: presentation mode (Ryujinx chooses
FIFO / FIFO-latest-ready / immediate from the VSync mode), DLSS sharpness (deprecated in the DLSS SDK), MetalFX
(Apple, not integrated), FSR 2+ (not integrated).
