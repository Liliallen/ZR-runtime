# Environment and baseline

## Environment (inspected 2026-10-13)

| Item | Value |
|---|---|
| OS / kernel | Arch Linux, 7.2.9-arch1-1, KDE Plasma on **Wayland** (Xwayland available) |
| CPU | AMD Ryzen 7 7700, 8C/16T, up to 5.39 GHz |
| RAM | 30 GiB (+4 GiB swap) |
| GPU | NVIDIA GeForce RTX 5070 Ti, 16 GB, driver **615.78.08** (nvidia-open), Vulkan 1.4.351 |
| Vulkan loader | 1.4.363 (vulkan-icd-loader); no validation layers installed |
| Present modes (Wayland surface) | FIFO, IMMEDIATE, FIFO_LATEST_READY — **no MAILBOX** |
| Relevant device extensions | VK_NVX_binary_import, VK_NVX_image_view_handle, VK_NV_low_latency2, VK_KHR_present_wait(2), VK_EXT_present_timing, VK_EXT_present_mode_fifo_latest_ready, VK_KHR_pipeline_binary, VK_EXT_graphics_pipeline_library |
| NGX | `/usr/lib/libnvidia-ngx.so.615.78.08` from the driver; no DLSS snippet installed |
| Display | HDMI-A-2, 1920×1080, **99.65 Hz max** (rotated to portrait). 120 Hz output is not possible on this monitor. |
| Toolchains | gcc/g++ 16.2.1; **no** .NET SDK, CMake or Ninja installed system-wide |
| Installed emulator | Ryubing **1.3.3** Flatpak (`io.github.ryubing.Ryujinx`, user install) — left untouched |

## Emulator configuration in use (Flatpak, copied to the isolated `devdata/`)
Vulkan, resolution scale 1×, VSync "Switch", PPTC on, shader cache on, memory manager HostMappedUnsafe,
backend threading Auto (= on for NVIDIA), docked, anisotropy auto, AA none, bilinear scaling.

## Game
| Item | Value |
|---|---|
| Title | The Legend of Zelda: Breath of the Wild, `01007EF00011E000`, US base NSP v0 |
| Update | v786432 = **1.6.0** |
| DLC | Pack 1 + Pack 2 (`01007EF00011F001/2`, v196608 → DLC 3.0) |
| Build IDs | main **`8E9978D50BDD20B4C8395A106C27FFDE`**, rtld `39E10C07…`, subsdk0 (multimedia) `93FF8252…`, sdk (nnSdk 7.3.2) `FCF93A41…` |
| Installed mods/patches | none (exefs/romfs mod folders empty) |
| Saves | one autosave at the Shrine of Resurrection (~5 min played) |

## Source and build
The Ryubing Flathub repository was taken down by a Nintendo DMCA notice (2026-06-30) and
`git.ryujinx.app` is unreachable from this network. The Flatpak's build manifest names the exact
source commit (`e2143d4…`); a GitHub mirror containing that commit was fetched and checked out by
hash, so the tree is identical to the official source. Two NuGet packages exist only on the
unreachable feed; `scripts/make-offline-feed.sh` repacks the identical DLLs from the official Flatpak
binary (verified version strings). See `BUILD.md`.

Upstream tests on this branch: **Ryujinx.Tests 30 547 passed / 133 skipped / 0 failed**,
**Ryujinx.Tests.Memory 84/84 passed** (plus the BotW-PC unit tests).

## Benchmark scenarios
Only one save exists, so only these are testable:

| # | Scenario | How | Status |
|---|---|---|---|
| S0 | Title/main menu | launch, idle | measured |
| S1 | Shrine of Resurrection, idle | `bench/continue.input` loads the save | **primary scenario** |
| S2 | Shrine, scripted camera turn + walk | `bench/motion.input` | timing test |
| S3 | Opening cutscene (new game) | `bench/newgame.input` (overwrites the *dev copy* of the save only) | timing test |
| — | Open world, Kakariko, Korok Forest, combat, inventory/map | no save there | **untested** |

All runs: 1920×1080 output (BotW renders the 3D scene at 1600×900 docked), same save, same settings
unless noted; the first 90 s (boot + load) are excluded from statistics.

## Baseline result (S1, before any optimization; instrumentation only)

| Mode | Median FPS | 1 % low | p99 frame | GPU util | FIFO busy | Render thread |
|---|---|---|---|---|---|---|
| Stock (game cap 30) | 30.0 | 20.0 | 50.0 ms | 15.6 % | 35–52 % | 54–69 % of a core |
| VSync unbounded (headroom) | 52.7 | 26.2–36.3 | 26.5 ms | 17.8–20.6 % | 65 % | ~94 % |

**What prevented 120 FPS** (in order of discovery):
1. The game itself: BotW times everything off a 30 FPS present interval (see `FRAME_TIMING.md`).
2. Emulator throughput: ~53 FPS max. The render thread was saturated, but profiling showed it was
   mostly blocked in **`vkCreateFence`/`vkDestroyFence` (≈0.4–0.6 ms each, ~15 per frame)**, not
   GPU work (GPU ~18 % busy) — fixed by the fence pool (`OPTIMIZATIONS.md`).
3. Presentation: without MAILBOX the backend used blocking FIFO, capping at ~58 FPS once the game ran
   faster — fixed with FIFO_LATEST_READY.
4. Now: GPU command processing (`GPU.MainThread`) guest→host buffer synchronization (~82 % busy at
   ~94 FPS). Not yet addressed.

Raw data: `botw/results/` (`index.csv` lists every run).
