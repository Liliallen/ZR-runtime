# NVIDIA DLSS Frame Generation (Streamline)

Status 2026-10-28 (build-zr-fg1): working on Windows (RTX 5070 Ti, driver 616.92, Streamline 2.14.16, DLSS-G 310.9.16);
off by default, `Play-BOTW-FG.bat` turns it on. Foreground run (camp save, camera pans, walking, swimming): 2.00 frames
shown per game frame, game 78–80 FPS with `BOTWPC_DLSSG_FPS=160` (≈160 shown), desktop screenshots normal (colours, HUD).
Streamline interpolates only while the game window is in the foreground ("DLSS-G disabled: window not focused").

## How it works

| Piece | Where | Notes |
|---|---|---|
| Bridge (flat C API) | `botw/native/sl_bridge/` (`build.ps1`, MSVC, links `sl.interposer.lib`) | `slInit` (Vulkan, DLSS-G + Reflex + PCL, frame-based tagging, no OTA), feature check, options, frame tokens + Reflex sleep, PCL markers, constants, tags, state |
| Loading | `Ryujinx.Graphics.Vulkan/DlssG.cs` | `BOTWPC_DLSSG=2x|3x|4x` or `graphics.frame_generation`; `slInit` runs before Vulkan, which is then loaded from `<app>/streamline/sl.interposer.dll` (Streamline hooks instance/device/swapchain creation and present) |
| Window surface | `Ryujinx/UI/Renderer/EmbeddedWindowVulkan.cs` | on Windows the Win32 surface is created through the renderer's Vulkan API, not SPB's own `vulkan-1.dll`; otherwise Streamline cannot map the surface to a window and swapchain creation fails (`Could not find a window corresponding to this application`) |
| Inputs | `PipelineFull.Dlss.cs` | at BotW's upscale draw (DLSS.md): the existing camera motion-vector / reversed-depth compute pass (without the off-screen vectors for disocclusions that DLSS-SR uses), then the hud-less colour right after the upscale (HUD is drawn next into the same target). With DLSS-SR on, its output is the hud-less colour |
| Hud-less encoding | same | the upscale target is linear RGB10A2; the presented image is the same colour sRGB-encoded into RGBA8 by the game's final copy. The hud-less copy is drawn into an sRGB view and tagged as RGBA8 UNORM. Check (`BOTWPC_DLSSG_DUMP=<dir>`): 96.7 % of pixels identical to the presented frame, the rest exactly the HUD (hearts, minimap). A plain copy differed on 99.6 % |
| Camera constants | `DlssG.BuildConstants` | vertex cb3 rows 3..14 (`StateUpdater` sends 50 floats when the buffer has them): Streamline clip = BotW's OpenGL clip with z' = (w − z)/2 (reversed depth), row-vector matrices; camera vectors from the inverse view; mvec scale 1/render size |
| Present | `Window.DlssG.cs` | per frame: markers, constants, tags (`eValidUntilPresent`), DLSS-G on when the frame has inputs and the BOTW-PC menu is closed, else off (menus, map, loading, videos); off before swapchain recreation |
| Output limit | `BOTWPC_DLSSG_FPS` | DLSS-G on Vulkan has no VSync; the Reflex frame limiter (`slReflexSleep` every frame) caps the shown frames while it is on — with DLSS-G on, Reflex's limit counts shown frames (12.5 ms gave 40 game FPS at 2x), so 160 → 80 game FPS on the 165 Hz display |

Packaging: `<app>/streamline` = bridge + production `sl.interposer/common/dlss_g/reflex/pcl`, `nvngx_dlssg.dll`,
`nvngx_dlss.dll`, `NvLowLatencyVk.dll`, Streamline license. `nvngx_dlss.dll` is needed there too: once Streamline has
initialised NGX, the DLSS-SR bridge reported Super Resolution "NOT available" until the DLSS runtime was in Streamline's
plugin directory. `-Development` builds the development plugins (more log output); their JSON configs did not change
the focus check (`runWhenNoFocus`).

## Settings and the in-game menu

`graphics.frame_generation` (Off/X2/X3/X4) and `graphics.frame_generation_fps_limit` in `botw/settings.json`;
`BOTWPC_DLSSG` / `BOTWPC_DLSSG_FPS` override them at start. Streamline is loaded only if frame generation is on at start;
then the BOTW-PC menu (L3 + R3, Graphics → 補幀) changes the multiplier and the shown-frame limit live
(`DlssRuntime.FrameGenFrames/FrameGenFpsLimit`, read at every present). The performance HUD shows game → shown FPS
(game FPS × frames shown per game frame, a running average of `numFramesActuallyPresented`), and Detailed adds a
"Frame generation" row. Verified in the foreground: started 2x from the setting (80 → 160), switched to 3x in the menu →
3.00 frames shown per game frame, HUD 54 → 163; a close, fast-moving Bokoblin showed visible warping in a 3x frame.

## Measured (camp save, disposable data dir, window in the background)

| Run | Game FPS (55–95 s) | Notes |
|---|---|---|
| frame generation off (new build) | 44.9 | same path as before apart from the surface creation |
| on, background (no interpolation) | 44.3 | cost of the inputs and Streamline's proxy swapchain ~1 % |
| on, foreground, `BOTWPC_DLSSG_FPS=160` | 78–80 | 2.00 frames shown per game frame → ≈160 shown |

Logs: `BOTW-PC DLSS-G: … frames shown per game frame (last 600)` every 600 frames (2.00 expected at 2x in the
foreground), `BOTWPC_SL_LOG=1` for Streamline's own messages.

## Open

- Latency not measured; fast combat with moving enemies not checked visually.
- Motion vectors are camera-only (no object motion), as for DLSS-SR; fast-moving objects rely on DLSS-G's own flow.
- No UI alpha buffer (BotW draws the HUD into the frame); DLSS-G infers it from hud-less vs final.
- No option in the Avalonia settings window yet (the in-game BOTW-PC menu has it).
