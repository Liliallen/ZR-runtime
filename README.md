# ZR-runtime

An unofficial, non-commercial fork of the [Ryujinx](https://git.ryujinx.app/ryubing/ryujinx) (Ryubing) Switch emulator,
tuned for one game: *The Legend of Zelda: Breath of the Wild*, version 1.6.0.
It runs on **Windows, Linux, macOS and iPadOS**, using a copy of the game that you own.

**This project is not affiliated with, endorsed or sponsored by Nintendo.** It contains no Nintendo code, game
files, keys, firmware or artwork. See [Legal](#legal) before using it.

## Platforms

| Platform | Graphics | Measured result (1.6.0, the author's own save) |
|---|---|---|
| Windows x64 | Vulkan | 60 / 90 FPS at full game speed; the 120 FPS mode reaches about 90 outdoors; with DLSS Frame Generation 2x, ~80 game FPS → ~160 shown (RTX 5070 Ti) |
| Linux x64 | Vulkan | same code as Windows; earlier rounds measured, the latest round not yet re-measured |
| macOS (Apple Silicon) | Vulkan → MoltenVK → Metal | median 50–55 FPS on an M4 MacBook Air |
| iPadOS (M1 iPad) | Vulkan → MoltenVK → Metal, no JIT | 30 FPS; drops to 24–27 FPS after 8–17 minutes when the iPad gets hot |

Everything specific to the game is gated on its title ID and the exact build ID of its executable, so other
software keeps the stock Ryujinx behaviour.

## What the fork adds

- Frame-rate modes (30 / 60 / 90 / 120) with corrected game speed and UI timing.
- High-frame-rate gameplay fixes: stamina regeneration, map cursor, shield surfing, bow slow motion, Flurry Rush and
  Korok timers are scaled to the real frame time, so they behave as at 30 FPS
  (`BOTWPC_FRAME_FIXES=0` turns them off; [`KNOWN_ISSUES.md`](botw/docs/KNOWN_ISSUES.md) #4).
- Videos (memories, story movies) play at the console's 30 FPS, then the chosen frame rate returns (`BOTWPC_VIDEO_30FPS=0` turns it off).
- NVIDIA DLSS Super Resolution and, new, DLSS Frame Generation 2x / 3x / 4x through Streamline (Windows, RTX 40/50,
  experimental, off by default): [`DLSS_FRAME_GENERATION.md`](botw/docs/DLSS_FRAME_GENERATION.md).
- Runtime V2 / V3 (selectable per component, experimental): reworked CPU JIT, GPU front end, memory tracking and HLE
  scheduler; V2 is checked against the original with differential tests.
- Vulkan and GPU front-end optimizations (fence pool, buffer lookup cache, readback sync). The world-load stall and GPU
  reset on NVIDIA drivers is fixed (host import of texture flush buffers is off; `KNOWN_ISSUES.md` #44).
- Xbox controller auto-assignment, button-prompt layouts, custom bindings, a virtual gyroscope. The prompts now also
  match a wireless pad that is switched on after the game starts.
- In-game overlay menu (L3 + R3) in English or Chinese: graphics, frame rate, frame generation, performance HUD
  (game → shown FPS), controller, mods.
- Mod and DLC management with version checks, safe mode and rollback.
- macOS app bundle and an iPadOS app (ahead-of-time compiled, no JIT needed on the device).

Details and measurements: [`botw/README.md`](botw/README.md) and [`botw/docs/`](botw/docs).

## Building

You build everything yourself from source. No binaries are published.

| Platform | Command | Notes |
|---|---|---|
| Linux | `botw/scripts/build.sh`, `botw/scripts/package.sh` | [`botw/docs/BUILD.md`](botw/docs/BUILD.md) |
| Windows | `dotnet publish src/Ryujinx/Ryujinx.csproj -c Release -r win-x64 --self-contained` | restore as in `BUILD.md` |
| macOS | `botw/scripts/build-macos.sh` | [`botw/docs/MACOS_PORT.md`](botw/docs/MACOS_PORT.md) |
| iPadOS | `botw/ipados/build.sh` | Xcode and your own (free) Apple developer account; [`botw/docs/IPADOS.md`](botw/docs/IPADOS.md) |

## What you need

- A Nintendo Switch and a legally purchased copy of the game, update 1.6.0 (DLC optional).
- The game files, system keys and firmware **dumped from your own console**.

This project does not provide these files, does not link to them and does not explain how to obtain them.
Issues or messages asking for them will be closed.

## Legal

- *Nintendo*, *Nintendo Switch*, *The Legend of Zelda* and *Breath of the Wild* are trademarks of Nintendo.
  They are used here only to say which game and platform this software is compatible with.
- The repository contains only source code written by the Ryujinx contributors and by this fork.
  No game files, keys, firmware, extracted or decompiled game code, game artwork or game screenshots are committed.
- Use it only with games and system files you dumped from hardware you own. Piracy is not supported.
- The iPadOS build embeds code translated from **your** copy of the game. It is for your own device only.
  Do not share the resulting `.ipa`, app or any file derived from the game.
- This project is free and non-commercial. It takes no donations and sells nothing.
- The NVIDIA DLSS SDK and the NVIDIA Streamline SDK are downloaded at build time under NVIDIA's licenses and are not committed.
- This README is not legal advice. The laws on emulation and on making copies differ between countries.
  Check yours.

## License and credits

MIT, like upstream Ryujinx: see [LICENSE.txt](LICENSE.txt) and
[THIRDPARTY.md](distribution/legal/THIRDPARTY.md) (libvpx under BSD, ffmpeg under LGPLv3, and others).

## Thanks

Built on (code used, under each project's license):

| Project | Used for |
|---|---|
| Ryujinx by gdkchan and contributors | the emulator this fork is based on |
| [Ryubing](https://git.ryujinx.app/ryubing/ryujinx) | the maintained fork; base version 1.3.3 |
| [LibHac](https://git.ryujinx.app/ryubing/libhac) | file systems and containers |
| [MoltenVK](https://github.com/KhronosGroup/MoltenVK) | Vulkan on macOS and iPadOS (1.2.0, two local patches) |
| [SDL2](https://github.com/libsdl-org/SDL) | input, windowing and audio |
| [Silk.NET](https://github.com/dotnet/Silk.NET) | Vulkan bindings |
| [OpenTK](https://github.com/opentk/opentk) / [OpenAL Soft](https://github.com/kcat/openal-soft) | OpenGL and audio |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia), [FluentAvalonia](https://github.com/amwx/FluentAvalonia), [SkiaSharp](https://github.com/mono/SkiaSharp) | desktop user interface |
| [FFmpeg](https://ffmpeg.org/), [libvpx](https://chromium.googlesource.com/webm/libvpx) | video decoding |
| [shaderc](https://github.com/google/shaderc), [Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers) | shader compilation and headers |
| [NVIDIA DLSS SDK](https://github.com/NVIDIA/DLSS) | DLSS (downloaded at build time, not committed) |
| [NVIDIA Streamline](https://github.com/NVIDIA-RTX/Streamline) | DLSS Frame Generation and Reflex (downloaded at build time, not committed) |

Studied as references (ideas and published findings only, no code copied):

| Project | What we learned from it |
|---|---|
| [FPS++](https://github.com/cemu-project/cemu_graphic_packs) (Cemu graphic packs) | which game systems break at high frame rates, and the test checklist |
| [Cemu](https://github.com/cemu-project/Cemu) | comparison of the Wii U version |
| MeloNX | that Ryujinx can run on iOS with NativeAOT |
| [StikDebug](https://github.com/StephenDev0/StikDebug) | how iPadOS 26 enables JIT for development apps |
| [Eden](https://git.eden-emu.dev/eden-emu/eden), [dynarmic](https://github.com/azahar-emu/dynarmic) | comparison of CPU back ends |
| [UKMM](https://github.com/NiceneNerd/ukmm) | the mod format the mod installer accepts |
| [d2r-mobile](https://github.com/jhaizhou-ops/d2r-mobile) | the native-execution approach on Apple chips (the game's code runs directly, its graphics are rebuilt on Metal), first made for Diablo II: Resurrected |
| [nxnative](https://github.com/seedds/nxnative) | the multi-game native runtime grown from d2r-mobile; the Apple-native route for this game (30 FPS on an M1 iPad, no JIT) is developed in [a fork of it](https://github.com/Liliallen/nxnative/tree/botw-1.6.0) (branch `botw-1.6.0`) |

Full third-party license texts: [THIRDPARTY.md](distribution/legal/THIRDPARTY.md).
