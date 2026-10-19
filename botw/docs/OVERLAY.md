# In-game overlay menu

A host-side settings menu and performance HUD drawn over BotW while the game runs. It is not a game mod and does not
touch the game's own pause menu. Open it with **L3 + R3** (both stick clicks) on the controller, or **Insert** on the
keyboard; press the same again (or B / Esc / Menu) to close it.

## Reuse first: what was studied and what was reused
| Source | What it does | Used how |
|---|---|---|
| bbport overlay (`deadinside28/bloodborne_pc`, `gpu/shim/bbport_overlay.*`, GPL-2.0-or-later) | Dear ImGui menu drawn by the presenter into the swapchain image after the game frame, at display resolution; Insert / L3+R3; game input held neutral while open | **Architecture only** (same placement and input model). No code copied: GPL-2.0 vs BOTW-PC's MIT base. |
| SkiaSharp (MIT, already a Ryujinx dependency; Ryujinx's software-keyboard applet draws with it) | 2D drawing and text, including CJK, on Linux, macOS and iOS | Draws the menu and HUD on the CPU into a BGRA image. No new dependency. |
| Ryujinx `HelperShader` colour-blit program + `PipelineHelperShader.SetBlendState` | Full-screen textured blits in the Vulkan backend | Composites the overlay image with source-over blending (`BlendOverColor`). |
| Ryujinx pause (`TogglePauseEmulation`, the existing hotkey) | Suspends guest threads and audio | Pauses the game while the menu is open. No custom pause. |
| `ConfigurationState` (Config.json), `BotwPcSettings` (botw/settings.json), `ModStore`, `BotwDlc`, `GpuCounters`, Ryujinx statistics | Existing settings, mods, DLC and counters | The overlay edits the same objects the launcher and settings window use, and reads the same counters. |

Dear ImGui via ImGui.NET was considered. It would add a native dependency (cimgui) and a C# Vulkan renderer for ImGui
draw data, and it needs a CJK font atlas for Traditional Chinese. SkiaSharp needs none of this, and the menu is a
simple list UI.

## Architecture (platform split)

```
Ryujinx.Common/BotwPc/Overlay      platform-neutral: PadButtons, StickChord (L3+R3), NavRepeat, OverlayMenu (pages/items,
                                    Live / GameRestart / Info), OverlayFrame (image hand-off + present timing),
                                    OverlayInputGate (controller poll ↔ overlay), DlssRuntime (renderer status)
Ryujinx.HLE/BotwPc/Overlay          OverlayPainter (SkiaSharp → premultiplied BGRA, output resolution)
Ryujinx.Input/HLE/NpadManager       physical Player 1 buttons → gate; neutral game input while the overlay has focus
Ryujinx.Graphics.Vulkan/Window      uploads the image before the frame, blends it after scaling, before present
Ryujinx.Graphics.Gpu/Window         PresentLast: re-presents the last frame while the game is paused
Ryujinx/Systems/BotwOverlayHost     pages, applying settings, pause, keyboard/mouse, painting (host input thread)
Ryujinx/Systems/BotwOverlayHud      HUD numbers from existing counters
```

The host and the painter know nothing about Vulkan. A Metal/MoltenVK presenter only has to do what `Window.UpdateOverlay`
and `BlendOverColor` do: take `OverlayFrame`'s image and blend it at its position. The settings model (`OverlayMenu`
items with their apply kind) is the same on every platform. An iPad port would add touch handling in the host: rows and
tabs already report their rectangles for hit tests, and the layout scales with `overlay.scale`.

### Rendering
- Drawn after the game frame, after DLSS, after the game's own upscale and after Ryujinx's scaling filter, into the
  swapchain image at **native output resolution**. It is never part of DLSS input or history.
- The painter runs on the host input thread, not the render thread, and only when something changed: a menu change,
  or the HUD at 4 Hz. With only the HUD visible it paints and uploads just the HUD box. The presenter re-uploads only
  when the image version changes. No extra synchronisation: the upload goes through Ryujinx's preload command buffer
  before the frame's command buffer.
- Window size changes and fullscreen are followed (`OverlayFrame.TargetWidth/Height` = swapchain size).
- Font: picked by character (`SKFontManager.MatchCharacter('體')`), so a CJK-capable system font is used on any
  platform; menu text is English or Traditional Chinese, following the UI language.

### Input
- **L3 + R3**: a single stick click is held back from the game for the chord window (default 80 ms). If the other stick
  follows, the overlay toggles and both clicks are swallowed until released. Otherwise the click reaches the game late,
  and a tap shorter than the window is replayed for 60 ms so the game still sees it. Both behaviours are verified by
  unit tests and in game (a scripted single L3 tap made Link crouch).
- While open, Player 1's controller, the keyboard and the mouse give the game **neutral input** (`OverlayInputGate.Captures`).
  After closing, input returns only once the buttons that closed the menu are released, so no B press leaks.
- Navigation uses **physical Xbox buttons** from SDL's raw state, independent of the game's Nintendo mapping, bindings or
  layout:

  | Input | Action |
  |---|---|
  | D-pad / left stick | move / change value (← →) |
  | right stick | scroll rows |
  | A | select / confirm (or step the value) |
  | B, Menu | close |
  | LB / RB | previous / next page |
  | L3 + R3 | open / close |
  | Keyboard: arrows, Enter/Space, Esc/Backspace, PageUp/PageDown/Tab, Insert | same |
  | Mouse: hover selects, click confirms or steps, wheel scrolls, click a tab to switch pages | same |

  Held directions repeat in real time (380 ms, then every 90 ms) at any frame rate.
- Steam Input: only Player 1's assigned controller is read, so a second (virtual) pad cannot toggle the overlay twice.

### Pause
"Pause game while open" (default on) uses Ryujinx's own pause, which suspends the guest threads and audio. While
paused the game presents nothing, so the render loop re-presents the last frame (`Gpu.Window.PresentLast`) whenever a
new overlay image is published. The menu keeps updating and the game state is frozen. With the option off, the game
keeps running under the menu but receives neutral input (verified: a scripted walk while the menu was open did not move
Link).

## Pages and when settings apply

| Page | Setting | Applies | Notes |
|---|---|---|---|
| Graphics | Upscaler: Off / DLSS Quality / Balanced / Performance | **Live** | NGX is initialised at start when the overlay is enabled, so DLSS can be switched on and off in game. Disabled with the reason when NGX/DLSS is unavailable or `BOTWPC_DLSS` fixes it. Shows the runtime status (running with sizes / selected but idle / failed). A failed mode falls back to the game's upscale and is retried only for another mode. |
| | DLAA | n/a (shown disabled) | BotW never renders at output size |
| | Output filter: Bilinear / Nearest / AMD FSR 1 / Area; FSR sharpness | **Live** | Ryujinx's window scaling filter; FSR 1 is spatial |
| | Resolution scale 1–4× | **Live** | Ryujinx resolution scaling |
| | Anti-aliasing (FXAA / SMAA) | **Live** | Ryujinx post-processing |
| | Display mode (windowed / fullscreen) | **Live** | |
| | Output resolution, shader compilation activity | info | |
| Frame rate | Target 30 / 60 / 90 / 120 (experimental) / Unbounded | **Live** | Same VSync settings as the settings window; BotW timing and UI animation corrections apply at every rate |
| | Measured FPS, game speed | info | Measured separately from the target; warning when below 90 % of the target |
| Performance | HUD: Hidden / FPS / Detailed / Developer; overlay size | **Live** | |
| Controller | Layout (Position / Xbox labels), deadzones, camera curve and speed, inversion, trigger threshold, vibration | **Live** (input reloaded at once) | Changing the layout makes the on-screen glyphs stale until the game restarts; the page says so (`ButtonPrompts.Stale`) |
| | Button prompts (Auto / Xbox / Original) | **Game restart** | |
| | Controller test window | action | opens on the desktop |
| Motion | Source, keyboard / View-hold virtual gyro, speed, fine factor, inversions, hold threshold, recenter | **Live** | |
| Mods & DLC | Profile, safe mode, conflicts, installed mods (enable/disable), DLC status | **Game restart** for mod changes | Mods are never hot-swapped; installing stays in the Mod Manager |
| System | Pause while open, chord window, revert changes since opening, restore defaults, close | Live | |

Settings are saved when the menu closes: `botw/settings.json` and `Config.json`. These are the files the launcher and
the settings window use, so changes show up in both directions.

There is **no automatic game restart**. A second instance briefly sharing the save directory with the first could
damage saves. Settings marked RESTART say "save in the game, then restart BOTW-PC".

## Verification (in game, progressed save copy, game window captures with `botw/native/xshot`)

| Test | Result | Evidence |
|---|---|---|
| L3 + R3 opens (scripted physical buttons), L3 + R3 closes | pass | `bench/ovl1` log + window captures |
| D-pad navigation, ← → change, RB / LB page switch | pass | `ovl1-xshot` |
| Insert opens, arrows / PageDown / Escape navigate and close (scripted keys) | pass | `ovl1` |
| Mouse: hover + click on a row (scripted pointer inside the app) | pass (HUD switched on) | `ovl2` |
| Single L3 tap still reaches the game (Link crouches) | pass | `ovl1-xshot` last frames |
| Game input while open (pause off): scripted walk did not move Link; after closing the same walk did | pass | `ovl2-xshot` |
| DLSS Quality selected live → feature created 1600×900 → 1920×1080; switched off live → game upscale | pass | `ovl2/stdout.log` |
| Frame-rate target 30 → 60 live: 30 → 60 FPS at once, game speed 1.00 | pass | `ovl2/framelog.csv` |
| Pause while open: menu keeps updating (re-present), game frozen | pass | `ovl1-xshot` (HUD "paused") |
| Settings persist (`overlay.hud` written to settings.json) | pass | `ovl1`, `ovl2` |
| Overlay cost, S1 uncapped: overlay disabled 93.71, enabled + HUD hidden 93.57, HUD detailed 93.20 FPS median (1 % low 67.2 / 63.8 / 64.0) | ≈ 0.5 %, within noise | `bench/ovlcost-*` |
| Unit tests: chord (fire, swallow, replay, late pass), real-time repeat, menu navigation | 6 pass | `OverlayTests` |
| **A person pressing the physical L3 + R3 / using the mouse** | **NOT TESTED** | scripted physical state and pointer only (same code path after the SDL read) |
| Fullscreen / 4K / 1440p output | NOT TESTED | tested at a 1412×793 window; layout scales with the output height |
| Controller disconnect while open | NOT TESTED | the gate then sees no buttons; Insert / mouse still work |
