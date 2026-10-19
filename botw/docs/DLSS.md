# NVIDIA DLSS Super Resolution

## Status by milestone

| Milestone | Status | Evidence |
|---|---|---|
| SDK compiling | **done** | `botw/native/ngx_bridge/build.sh` (g++, static `libnvsdk_ngx.a`, SDK symbols hidden) |
| SDK initializing | **done (standalone)** | NGX init Success, `SuperSampling.Available = 1`, RTX 5070 Ti / 615.78 / DLSS 310.9.1 |
| Successful inference | **done (standalone)** | 64 evaluations, 0 failures; output beats bilinear (below) |
| Valid temporal inputs (conventions) | **verified on synthetic data** | wrong jitter or MV sign makes the test fail |
| Upscale injection point identified | **done (verified)** | per-draw census + shader disassembly, below |
| Camera view-projection located | **done (static analysis)** | vertex shaders, below; runtime values not yet captured |
| Running inside the emulator | **done** | NGX initialised on Ryujinx's Vulkan device (extensions + `bufferDeviceAddress`), `BOTW-PC DLSS: NGX init Success` |
| Injection at the verified upscale | **done** | the game's upscale draw (program `973785cd7b40`, issued as a 3-index `DrawIndexed`) is replaced by DLSS; HUD drawn afterwards at 1080p |
| Jitter | **done, verified** | Halton(2,3) viewport offsets on scene geometry only; sign chosen by PSNR against a 2× render (below) |
| Camera motion vectors | **done, verified** | compute pass from depth + world→clip matrices (cb3 rows 3–6); conventions and precision verified on dumped frames (below) |
| Object motion vectors (Link, enemies, foliage) | **not done** | moving objects get camera-only vectors |
| Visually correct gameplay | **improved** | static and slow scenes clean and sharper than the game's upscale; the trail of copies of Link's head on camera turns (shrine) is fixed by disocclusion handling (2026-10-14, below); wind-animated foliage still has camera-only vectors |
| Performance | **measured (Windows, outdoor W1)** | DLSS Quality 84.5 vs off 89.6 FPS at first; the cost came from a readback wait, not from DLSS's GPU time (+0.5 ms on a ~40 % busy GPU); after the strict readback sync, Quality 90.5 FPS = off before (PERFORMANCE.md) |
| Outdoor gameplay (progressed save, Great Plateau) | **runs (fixed)** | outdoors the game uses a second upscale program (`b164ac0219a2`); before it was recognised DLSS silently stayed inactive outdoors (camera data captured, no feature created). Both variants are matched now; verified active outdoors and in the shrine. Same inputs (1600×900 R10G10B10A2, D32), mean/σ colour of DLSS frames within the range of vanilla frames (no grading pass lost). Outdoor performance with DLSS not yet measured cleanly (see PERFORMANCE.md) |
| User option | **done (experimental)** | Settings > BotW-PC > Graphics: Off / Quality / Balanced / Performance (`graphics.dlss` in `botw/settings.json`); `BOTWPC_DLSS` overrides it; verified that the setting alone engages DLSS in game and that Off leaves it untouched (`bench/dlss-setting`, `bench/dlss-default-off`) |
| Packaging | **local package** | `package.sh` copies the bridge, the DLSS runtime and NVIDIA's license into `ngx/`; dev launcher uses `.tools/ngx-bridge` |

DLSS is selected in Settings > BotW-PC or with `BOTWPC_DLSS=quality|balanced|performance|dlaa` (+ `BOTWPC_NGX_DIR`
for a bridge outside `<app>/ngx`). No spatial filter is presented as DLSS. The UI carries the ghosting caveat below.

**What the modes mean here:** DLSS replaces the game's own 900p→1080p upscale pass, so its input is always the
game's render size (dynamic; 1600×900 outdoors). The mode only selects the NGX preset, not the input resolution.
DLAA is not offered in the UI: the game never renders at output size. With a 1080p ExeFS patch (e.g. Wollnashorn's
`1080p` pchtxt) the game renders natively at 1080p and skips the upscale pass, so DLSS has nothing to replace and
stays idle (observed: `bench/mods-1080p-dlss`, no feature created).

**Licensing / packaging:** the bridge links NVIDIA's `libnvsdk_ngx.a` and loads `libnvidia-ngx-dlss.so` from the
user's own DLSS SDK. The SDK license allows distributing SDK runtime files "as incorporated in object code format
into a software application", but also forbids terms that make the SDK subject to an open-source license or
"redistributable at no charge" (section 2e). Neither file is in Git; `package.sh` puts them, with NVIDIA's license,
only into the local package. Publishing a package that contains them needs a license review first.

## Bridge (`botw/native/ngx_bridge/`)
Flat C API (`botw_ngx_configure`, `_instance_extensions`, `_device_extensions`, `_init`, `_optimal_render_size`,
`_create`, `_evaluate`, `_release`, `_shutdown`, `_result_string`) meant for P/Invoke from the Vulkan backend.
Linux: `build.sh` (g++, `libbotw_ngx_bridge.so`); Windows: `build.ps1` (MSVC, `botw_ngx_bridge.dll` +
`nvngx_dlss.dll` from SDK 310.9.1 in `<app>
gx`). `_evaluate` takes an optional bias-current-colour mask (NULL).
Environment: `BOTWPC_DLSS_PRESET=<letter>` sets the render-preset hint for all modes, `BOTWPC_NGX_LOG=1` enables NGX's
verbose log (files in `<data>/botw/ngx`).
NGX needs instance extension(s) and the device extensions `VK_NVX_binary_import`, `VK_NVX_image_view_handle`,
`VK_KHR_buffer_device_address`, `VK_KHR_push_descriptor` (all present on this GPU). Inputs in
`VK_IMAGE_LAYOUT_GENERAL` with SAMPLED usage, output with STORAGE usage.

Conventions, pinned by the test:
- motion vectors = **previous position − current position**, render pixels;
- the API takes the **sample offset from the pixel centre**; the bridge passes its negation to NGX
  (`InJitterOffset = −offset`).

Render sizes reported for 1920×1080 output: Performance 960×540, Balanced 1114×626, Quality 1280×720, DLAA 1920×1080.

## Standalone test (`test_ngx_bridge`)
A zone plate scrolls 1.5 render px/frame at 960×540 with Halton(2,3) jitter and exact motion vectors; 64 frames of
DLSS Performance to 1920×1080 are compared with a 4×4-supersampled truth (`botw/results/dlss-synthetic-test.log`):

| Signs | PSNR DLSS | PSNR bilinear | Result |
|---|---|---|---|
| bridge convention | **17.46 dB** | 16.31 dB | PASS |
| MV sign flipped | 15.28 | 16.31 | FAIL |
| jitter sign flipped | 9.13 | 16.31 | FAIL |
| both flipped | 7.45 | 16.31 | FAIL |

Per band (bridge convention): rings of 7.8 px period **29.8 vs 20.4 dB**, 11.6 px **27.5 vs 25.4 dB** — DLSS
recovers detail a single frame cannot contain (bilinear shows moiré). It is worse than bilinear on the smooth
centre (30.6 vs 45.5 dB) and 150–300 px band (20.4 vs 33.1 dB), and a vertical artifact band is visible in the
output; with synthetic content this is a sanity test, not an image-quality verdict.

## Licensing
The SDK (headers, `libnvsdk_ngx.a`, `libnvidia-ngx-dlss.so.310.9.1`) is downloaded by `build.sh` into `../.tools`
and is **not** committed. NVIDIA's license allows shipping the runtime library inside an application, forbids
distributing the SDK stand-alone or under an open-source license, and asks for attribution and notice before a
commercial release. The bridge source is MIT.

## How BotW renders (render-target census, `BOTWPC_RT_CENSUS`, `botw/results/rt-census-s1.csv`)
Docked, 1080p output, ~3 900 draws in the measured frame:
1. shadow cascades 896×896 D16;
2. **G-buffer at 1600×900**: RGBA8/sRGB/R11G11B10F/R32F targets + **D32F depth**;
3. half-res (800×450) lighting/AO passes; **scene HDR colour 1600×900 R11G11B10F**, transparents into it;
4. bloom mip chain; **tonemap to 1600×900 RGB10A2**;
5. 1920×1080 RGB10A2 target with 10 draws, then a small UI target, final sRGB copy, present.

### Verified upscale point (frame 3000, S1, `BOTWPC_RT_CENSUS` per-draw detail; shaders disassembled locally)
| Pass | Draw | Program (SPIR-V hash) | Reads | Writes | Meaning |
|---|---|---|---|---|---|
| 118 | 0, 3 vertices | `bde778037f7e` | 1600×900 R11G11B10F scene HDR + 320×180 R11G11B10F bloom | 1600×900 RGB10A2 (D32F 1600×900 bound) | tonemap / composite |
| **119** | **0, 3 vertices** | **`973785cd7b40`** | **1600×900 RGB10A2 (output of 118) only** | **1920×1080 RGB10A2** | **900p → 1080p upscale** |
| 119 | 1–9, 6 vertices | glyph/icon programs | 2048×4096 R8 font atlas, small icons | same 1080p target | HUD, drawn after the upscale |
| 121, 123 | | | | 1080p, then sRGB 1080p copy | more HUD; final copy to the presented image |

The upscale fragment shader does 9 bilinear taps (4 with constant offsets (0,0)…(1,1)) around the pixel, then
7 `min`/`max` pairs, `abs`, one compare/select: a spatial upscale with neighbourhood clamping (anti-ringing/edge
handling). It reads no history texture, so BotW applies **no temporal AA** here. Replacing exactly this draw with
DLSS keeps the HUD at output resolution and untouched. The program hash identifies it at runtime for this build.
SPIR-V of game shaders is written only to a local, uncommitted directory (`BOTWPC_RT_CENSUS_SPIRV`).

### Camera matrix location (static analysis of the G-buffer vertex shaders)
`botw/tools/spv_trace.py` traces what feeds `gl_Position`. For the terrain program (`c1fbbff64080`, first draw of
the G-buffer pass) and an object program (`aee557202678`):

```
clip.x = dot(world, c3[7])   clip.y = dot(world, c3[8])   clip.z = dot(world, c3[9]) (+ a c1 term)   clip.w = dot(world, c3[10])
```
i.e. the world → clip **view-projection matrix is vec4 rows 7–10 of the vertex-stage constant buffer in slot 3**
(guest byte offset 0x70–0xAF), shared by terrain and objects. With it and the previous frame's value, camera motion
vectors follow from depth: `prevClip = VP_prev · inverse(VP_cur) · (ndc.xy, depth, 1)`.
Next: read these 64 bytes at the first G-buffer draw of each frame (GPU thread, after constant-buffer updates are
flushed), check that they are stable while idle and change with camera motion (`bench/motion.input`).

## In-game implementation (BOTW-PC Vulkan backend)

| Piece | Where | Notes |
|---|---|---|
| Bridge loading, NGX extensions, init | `Ryujinx.Graphics.Vulkan/Dlss.cs`, `VulkanInitialization` | off unless `BOTWPC_DLSS` is set; any failure leaves the game's upscale |
| Upscale replacement | `PipelineFull.Dlss.cs` | colour = texture sampled by the upscale (1600×900 RGB10A2, tonemapped), depth = last D32F scene depth, output = the bound 1920×1080 target; feature created once per size (command buffer flushed and waited), then `botw_ngx_evaluate` every frame |
| Jitter | `PipelineBase.Dlss.cs` | viewport offset for draws into the scene-size target with depth and more than 3 vertices; full-screen passes, shadows and UI unjittered |
| Camera data | new GAL command `SetBotwCamera` (`IPipeline` default no-op, threaded command) sent by `StateUpdater` | first 16:9 scene draw after the output pass: cb3 bytes 0x30–0x6F |
| Motion vectors | `Shaders/BotwDlssMotionShaderSource.comp` | `prevClip = (prevVP · curVP⁻¹) · curClip` (matrix product in double precision on the CPU) |

BotW scene camera constants (vertex cb3, found with `BOTWPC_DLSS_CB_DUMP=1`): rows 0–2 view (3×4), **3–6 world→clip**,
7–10 projection (infinite far, near 0.1, OpenGL clip space), 11–13 inverse view, 14 = (near 0.1, far 25000, …).
(The earlier static analysis that pointed at rows 7–10 was of shaders working in view space.)

### Verification
- **Jitter sign** (static S1, 5 captures each, PSNR vs a 2× render downsampled to 1080p, centre / door / floor
  regions): game upscale 32.8 / 33.6 / 40.9 dB; DLSS without jitter 30.6 / 31.7 / 39.7; DLSS with jitter, sample
  offset = −viewport offset: **37.6 / 39.2 / 44.9**; +offset 37.3 / 38.8 / 44.0; mixed signs 31.3–35.4.
- **Motion-vector conventions** (`BOTWPC_DLSS_DUMP=<dir>`: colour, depth, MVs and matrices of two consecutive frames,
  local only): of 8 combinations, depth = NDC·0.5+0.5 with NDC y flipped gives the lowest reprojection error
  (0.0020 vs 0.0077 without motion compensation, 26 px vertical motion). During a fast camera turn the shader's
  vectors (41.8, 3.4) px match the shift measured by block matching (40, 3) px; a float32 world-space version was off
  by ~1 px in x, fixed by the double-precision clip→clip matrix.
- **Visual, camera turn** (`bench/turn-*`, local): DLSS without motion vectors smears into blocks; with them the scene
  stays coherent and sharp, but a trail of copies of Link's head appears to the side during the turn
  (history from the area Link uncovers is kept). Not fixed; probably needs object motion vectors / better
  disocclusion inputs. Static and slow scenes look clean.

## Windows measurements and ghosting fix (2026-10-14)

**Modes on Windows** (W1 outdoor scene, 3 runs each, PERFORMANCE.md): off 89.6, Quality 84.5, Balanced 84.8,
Performance 86.2, DLAA preset 86.4 FPS before the readback-sync fix. All modes were verified active from the log with
their own NGX quality value; with `BOTWPC_NGX_LOG=1` NGX's own log confirms the presets: K for DLAA / Quality /
Balanced, M for Performance (SDK 310 title defaults; `BOTWPC_DLSS_PRESET=J|K|L|M` overrides, bridge). The input is the
game's 1600×900 in every mode, so the modes differ only in the model. DLSS adds ~0.5 ms of GPU time per frame; the FPS
loss came from guest readbacks waiting for command buffers that also contained the DLSS passes, fixed by the strict
readback sync (`TextureGroupHandle`), after which Quality runs at 90.5 FPS.

**Ghosting: trail of copies of Link's head on camera turns.** Repeatable test: `bench/pan.input` (camera turn right,
then left, Link standing), captures every 0.1 s (`BOTWPC_CAPTURE_INTERVAL=0.1@77+9`), crops compared with
`scripts/ghost_sheet.ps1`, all on disposable save copies (a shrine-only copy for the Shrine of Resurrection).

| Experiment | Result |
|---|---|
| outdoor turn (Great Plateau), Quality K / J / Performance M / DLAA | no trail; foliage slightly soft during the turn |
| shrine turn, Quality K, Quality with preset J, Performance M | **trail in every preset**; DLSS off: none |
| `BOTWPC_DLSS_JITTER=0` | trail remains (sharper) |
| `BOTWPC_DLSS_MV=0` (zero motion vectors) | no trail (background soft) → the trail is carried by the motion vectors |
| depth flag inverted / reversed depth passed with the flag | weaker / unchanged |
| dumped DLSS inputs (`BOTWPC_DLSS_DUMP`) | colour has no trail or halo; depth: walls 0.995, Link 0.983 (standard depth, near smaller); motion: background +17–18 px/frame, Link ≈ 0 — inputs correct |
| bias-current-colour mask on the disoccluded pixels | mask correct (strip beside the head), **no visible effect** with presets K/J/M |
| motion-vector dilation near nearer objects | no effect (reverted) |
| **off-screen motion vector for disoccluded pixels** (1 texel) | solid copies gone, thin edge fragments left |
| **same with the 3×3 previous-depth neighbourhood** | **trail gone** (`bench/ghostS-q-final`); outdoor turn unchanged (`bench/ghost-q-final`) |

Root cause: when the camera orbits Link, his head stays put on screen while the background slides behind it. The
background revealed beside the head reprojects into last frame's head pixels; DLSS's colour clamp normally rejects
that history, but in the dark, low-contrast shrine the head and background colours are too close, so the head was
re-used and then carried along with the background every frame. Camera-only motion vectors are correct here (the
head does not move); what was missing is disocclusion handling.

Fix (`Shaders/BotwDlssMotionShaderSource.comp`, `PipelineFull.Dlss.cs`): the motion pass now also writes reversed
depth (1 − depth = near/z) into a ping-pong pair; for each pixel it compares the expected previous depth of its
reprojected point with the nearest of the previous frame's 3×3 depth texels there, and if something more than 20 %
nearer was there (`BOTWPC_DLSS_DISOCCLUSION_THRESHOLD`), the pixel has no valid history and gets an off-screen motion
vector, so DLSS uses the current frame. DLSS receives the reversed depth with `DepthInverted`. Switches:
`BOTWPC_DLSS_DISOCCLUSION_MASK=0` (detection off), `BOTWPC_DLSS_DISOCCLUSION_MV=0` (keep camera motion),
`BOTWPC_DLSS_BIAS_MASK=1` (also pass the mask to DLSS), `BOTWPC_DLSS_REVERSED_DEPTH=0` (game depth as before).
The SPIR-V is compiled with glslang 16.5.0 (`glslang -V --target-env vulkan1.2 -S comp`). Limits: object motion
(wind-animated foliage, Link's own animation) still has camera-only vectors; DLAA input is still 900p.

## Remaining plan
1. ~~Ghosting behind Link~~ fixed for the camera-turn trail (disocclusion, above). **Moving objects** (wind-animated
   foliage, animation) still need object motion vectors (previous-frame transforms for skinned/animated draws, as
   bloodborne_pc does, GPL-2.0: design reference only).
2. **History reset on camera cuts:** implemented (the screen centre at mid depth reprojecting by more than a quarter
   of the screen resets DLSS history; no false resets during the fast camera turn test); not yet observed on a real
   cutscene cut or fast travel.
3. ~~Settings UI~~ done (experimental, with the ghosting caveat); local packaging done.
4. **Resolution:** BotW renders its scene at a fixed 1600×900 (docked), so DLSS currently reconstructs 900p → 1080p
   (all quality modes use the same input). Higher outputs (1440p/4K) need Ryujinx's resolution scale on the output
   targets only; not tried.
5. Frame Generation: out of scope (separate feature, no Linux Vulkan path found).
