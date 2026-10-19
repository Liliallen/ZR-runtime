# Xbox controller mapping

Built on Ryujinx's existing SDL2 gamepad backend (`Ryujinx.Input.SDL2`); no second input system.
Settings live in `<data>/botw/settings.json` (`controller` section), separate from Ryujinx's `Config.json`.

## What BOTW-PC adds
| Feature | Behaviour | Code |
|---|---|---|
| Automatic assignment | When Player 1 has no **connected** controller in the saved config (default keyboard config, or a saved pad that is unplugged), the best connected controller becomes Player 1. The saved config is not modified; when the pad goes away, the saved config applies again. | `Ryujinx.Input/HLE/AutoController.cs`, `NpadManager.ReloadConfiguration` |
| Hot plug / unplug / reconnect | SDL connect/disconnect events already trigger `ReloadConfiguration`; with auto-assignment any controller (not only the exact saved id) is picked up again. | `NpadManager` |
| Preferred controller | `preferred`: name substring; otherwise Xbox controllers first, then physical pads before "Steam Virtual Gamepad". Only one pad is ever assigned, so a pad seen both physically and through Steam Input is not read twice. | `AutoController.Choose` |
| Layouts | see below | `AutoController.Build` |
| Deadzone / range / trigger threshold / camera inversion | applied by the existing Ryujinx code from the generated config | `NpadController` |
| Camera response curve | `camera_curve` exponent on the right-stick magnitude after the deadzone (1 = linear, default) | `NpadController.ApplyCurve` |
| Rumble | on by default for auto-assigned pads, `rumble_strength` multiplier (0–2). Effects are now stopped when emulation is paused and when the controller is released/disconnected (Ryujinx starts them with infinite duration). | `NpadController.UpdateRumble/StopRumble` |
| Benchmarks | `BOTWPC_AUTO_CONTROLLER=0` disables auto-assignment (set by `bench_s1.sh`) | |

## Layouts (no double swap)
SDL reports Xbox buttons by position: SDL `A` = bottom, `B` = right, `X` = left, `Y` = top.

| Physical Xbox button | `nintendo` (default, position-true) | `xbox` (label-true) |
|---|---|---|
| A (bottom) | Switch **B** | Switch **A** |
| B (right) | Switch **A** | Switch **B** |
| X (left) | Switch **Y** | Switch **X** |
| Y (top) | Switch **X** | Switch **Y** |

LB/RB → L/R, LT/RT → ZL/ZR, View → −, Menu → +, stick clicks → L3/R3, D-pad → D-pad (both layouts).
`nintendo` matches a Switch Pro Controller's button positions (and the user-requested default table). `xbox` makes
the bottom button BotW's A (jump/interact/confirm). Controllers whose SDL name contains "Nintendo" already report
Nintendo labels and are never swapped. The Guide button is not bound (left to the OS/Steam).
In-game prompts show the Xbox button bound to each Switch button (`CONTROLLER_PROMPTS.md`).

## Hardware verification (this machine)
| Test | Result |
|---|---|
| Xbox Series X\|S, USB, kernel `xpad` | detected by SDL as "Xbox Series X Controller", rumble capability reported |
| Auto-assignment at game start | **passed**: log `Player 1 auto-assigned to "Xbox Series X Controller"`; game ran normally (`results/ctl-auto-1`) |
| Rumble commands (both motors, stop) | SDL returned success (`botw/tools/pad_check.py --rumble`); **physical vibration not observed by a person yet** |
| Every button / stick / trigger in game, menus, camera | **NOT TESTED** (needs a person; use `pad_check.py --watch 30` and play) |
| Unplug / replug while in game | **NOT TESTED** on hardware (code path: SDL events → reload) |
| Bluetooth, Xbox One, Elite, Steam Input | **NOT TESTED** (no hardware/setup) |
| Unit tests | `AutoControllerTests`: choice order, both layouts, clamping, camera curve, settings file round-trip/corruption |

## Rumble mapping
Unchanged Ryujinx approximation: right Joy-Con low/high band amplitudes → strong (low-frequency) motor
(85 %/15 %), left Joy-Con → weak motor (15 %/85 %), times `rumble_strength`. HD Rumble frequencies cannot be
reproduced on Xbox motors; this is an approximation, not an equivalent.

## Custom bindings
`bindings` in the `controller` section remaps the automatic config after the layout is applied:
Switch button (`A B X Y L R ZL ZR Plus Minus LeftStick RightStick DpadUp DpadDown DpadLeft DpadRight`) → SDL input
(`A`=bottom, `B`=right, `X`=left, `Y`=top, `LeftShoulder`, `RightShoulder`, `LeftTrigger`, `RightTrigger`,
`Back`/`Minus`, `Start`/`Plus`, `LeftStick`, `RightStick`, `Dpad*`, `Misc1` (Share), `Paddle1`–`Paddle4`, `Guide`,
`Unbound`). Example: `"bindings": { "X": "A", "B": "Y" }`. Unknown names are ignored with a log warning.
A saved Ryujinx input configuration for the connected pad (Settings > Input) is used instead of the automatic one and
can be remapped there; prompts follow whichever configuration runs.

## Triggers
LT/RT are analog; they press Switch ZL/ZR above `trigger_threshold` (default 0.5) and release only below
`trigger_threshold − trigger_hysteresis` (default 0.05), so a trigger resting near the threshold does not flicker.

## System buttons
| Xbox | Switch / action |
|---|---|
| View | − (a long hold enters virtual motion mode, see `VIRTUAL_MOTION.md`; a short press is −) |
| Menu | + |
| Share (`Misc1`, Series X\|S) | Capture: takes a screenshot (unless bound to a Switch button in `bindings`) |
| Guide | not intercepted (left to the OS/Steam); can be bound to a Switch button in `bindings` if SDL reports it |
| Elite paddles (`Paddle1`–`4`) | unbound; bind in `bindings` if SDL exposes them separately (many drivers report paddles as copies of other buttons) |

Joy-Con-only controls (SL/SR, Sync) have no Xbox equivalent and are not emulated (BotW does not need them with a
Pro Controller).

## Motion
Virtual gyroscope (keyboard or View-hold): `VIRTUAL_MOTION.md`.

## Controller test window
Settings > BotW-PC > **Controller test…** shows the controller name, connection (USB/Bluetooth from SDL's GUID),
rumble/gyro capability, and for every pressed physical button: the Switch button it produces, the glyph the game shows
for that Switch button (the running game's, or the one the next start will generate) and `Matched`/`MISMATCH`.
It also shows sticks, analog triggers with threshold/hysteresis, the D-pad, a rumble test and the motion state.
It uses the same `AutoController` + `ButtonPrompts` code as the game. (Not visually checked by a person yet.)

## Button prompts
In-game glyphs follow the active mapping: see `CONTROLLER_PROMPTS.md`.

## In-game overlay controls
The overlay menu (OVERLAY.md) reads the **physical** Xbox buttons from SDL's raw state, not the game mapping, so
Position/Label layouts and custom bindings never swap them: A select, B close, LB/RB page, D-pad / left stick move and
change, right stick scroll, **L3 + R3 open/close** (keyboard: Insert). A single L3 or R3 click waits for the chord
window (`overlay.chord_window_ms`, default 80 ms) and then reaches the game; a quick tap is replayed to the game.
Turning the overlay off (`overlay.enabled: false`) removes that delay. While the menu is open the game gets neutral
input.

