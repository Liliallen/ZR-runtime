#!/usr/bin/env python3
"""Check game controllers through the same SDL2 library the emulator bundles.

usage: pad_check.py [--libsdl PATH] [--rumble] [--watch SECONDS]
Lists controllers (name, mapping, rumble support). --rumble plays 0.4 s on each motor and reports SDL's return
codes (0 = accepted by the driver; whether the motor moved must be felt). --watch prints button/axis changes so a
person can verify every input. Uses SDL's own Xbox mapping, i.e. exactly what Ryujinx.Input.SDL2 sees.
"""
import ctypes, os, sys, time

SDL_INIT_GAMECONTROLLER = 0x2000
BUTTONS = ["A", "B", "X", "Y", "Back(View)", "Guide", "Start(Menu)", "LeftStick", "RightStick", "LB", "RB",
           "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "Misc", "Paddle1", "Paddle2", "Paddle3", "Paddle4", "Touchpad"]
AXES = ["LeftX", "LeftY", "RightX", "RightY", "LT", "RT"]


def load(path):
    sdl = ctypes.CDLL(path)
    sdl.SDL_GameControllerName.restype = ctypes.c_char_p
    sdl.SDL_GameControllerMapping.restype = ctypes.c_char_p
    sdl.SDL_GetError.restype = ctypes.c_char_p
    sdl.SDL_GameControllerOpen.restype = ctypes.c_void_p
    for f in ("SDL_GameControllerName", "SDL_GameControllerMapping", "SDL_GameControllerHasRumble",
              "SDL_GameControllerGetButton", "SDL_GameControllerGetAxis", "SDL_GameControllerClose"):
        getattr(sdl, f).argtypes = [ctypes.c_void_p] + ([ctypes.c_int] if "Get" in f and "Mapping" not in f else [])
    sdl.SDL_GameControllerGetAxis.restype = ctypes.c_int16
    sdl.SDL_GameControllerRumble.argtypes = [ctypes.c_void_p, ctypes.c_uint16, ctypes.c_uint16, ctypes.c_uint32]
    return sdl


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    lib = os.path.join(here, "../../../build-botwpc/runtimes/linux-x64/native/libSDL2.so")
    if "--libsdl" in argv:
        lib = argv[argv.index("--libsdl") + 1]
    sdl = load(lib)
    if sdl.SDL_Init(SDL_INIT_GAMECONTROLLER) != 0:
        sys.exit("SDL_Init failed: " + sdl.SDL_GetError().decode())
    sdl.SDL_PumpEvents()
    pads = []
    for i in range(sdl.SDL_NumJoysticks()):
        if not sdl.SDL_IsGameController(i):
            continue
        h = sdl.SDL_GameControllerOpen(i)
        name = sdl.SDL_GameControllerName(h).decode(errors="replace")
        rumble = bool(sdl.SDL_GameControllerHasRumble(h))
        print(f"controller {i}: {name!r} rumble={rumble}")
        mapping = (sdl.SDL_GameControllerMapping(h) or b"").decode(errors="replace")
        print("  mapping:", mapping.split(",", 2)[-1][:200])
        pads.append((h, name))
    if not pads:
        print("no game controllers found")
    if "--rumble" in argv:
        for h, name in pads:
            for label, lo, hi in (("low-frequency (strong)", 0xFFFF, 0), ("high-frequency (weak)", 0, 0xFFFF)):
                rc = sdl.SDL_GameControllerRumble(h, lo, hi, 400)
                print(f"rumble {label} on {name!r}: rc={rc}" + ("" if rc == 0 else " " + sdl.SDL_GetError().decode()))
                time.sleep(0.6)
            print(f"stop on {name!r}: rc={sdl.SDL_GameControllerRumble(h, 0, 0, 0)}")
    if "--watch" in argv:
        end = time.time() + float(argv[argv.index("--watch") + 1])
        last = {}
        while time.time() < end:
            sdl.SDL_PumpEvents()
            sdl.SDL_GameControllerUpdate()
            for h, name in pads:
                for b, bn in enumerate(BUTTONS):
                    v = sdl.SDL_GameControllerGetButton(h, b)
                    if last.get((h, bn)) not in (None, v):
                        print(f"{bn} {'down' if v else 'up'}")
                    last[(h, bn)] = v
                for a, an in enumerate(AXES):
                    v = sdl.SDL_GameControllerGetAxis(h, a) // 8192
                    if last.get((h, an)) not in (None, v):
                        print(f"{an} {v * 8192}")
                    last[(h, an)] = v
            time.sleep(0.01)
    for h, _ in pads:
        sdl.SDL_GameControllerClose(h)
    sdl.SDL_Quit()


if __name__ == "__main__":
    main(sys.argv)
