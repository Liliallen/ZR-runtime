# Motion controls without a gyroscope (virtual motion)

Xbox controllers have no gyroscope, but BotW reads Player 1's six-axis sensor (it calls `StartSixAxisSensor` at
boot) for motion puzzles and optional gyro aiming. BOTW-PC simulates a motion controller and feeds the game
**gyroscope and accelerometer data**, not stick input.

## Controls

| Keyboard (default, all rebindable) | Xbox controller (hold **View**) | Effect |
|---|---|---|
| F8 | hold View ≥ 0.30 s (or long-press to toggle) | virtual motion mode on/off |
| ↑ / ↓ | right stick up / down | pitch forward / back |
| ← / → | right stick left / right | roll left / right |
| Q / E | LB / RB | yaw left / right |
| R | R3 | recenter (flat, face up) |
| Shift | LT | fine adjustment (× `fine_factor`, default 0.25) |

- Several axes can turn at once; speed is `rate_degrees_per_second` (default 90 °/s) × key (1) or stick magnitude
  (after `stick_deadzone`).
- Releasing keys/the stick stops the rotation and **keeps the tilt**.
- A **short View tap** (< threshold) is sent to the game as View (Switch −) when released, so the map/Minus still works.
  While View is held and undecided, the game does not see it (no accidental map opening).
- While controller motion mode is on, View, LB, RB, R3, LT and the right stick are hidden from the game; anything
  still held when the mode ends stays hidden until released (no stray press or camera jump).
- While keyboard motion is on, its keys are hidden from a keyboard Player 1. The toggle key is always hidden; when it
  equals Ryujinx's screenshot hotkey (both F8 by default) the toggle wins while virtual motion is in use —
  the controller's **Share** button takes screenshots instead.

## Settings (`<data>/botw/settings.json` → `motion`, also Settings > BotW-PC > Motion controls)

| Key | Default | Meaning |
|---|---|---|
| `source` | `Auto` | `Auto`: the controller's own sensors (or CemuHook/DSU) when available and enabled, else virtual; `Real`; `Virtual`; `Off` (no motion data) |
| `rate_degrees_per_second`, `fine_factor` | 90, 0.25 | rotation speed and fine factor |
| `invert_pitch`, `invert_roll`, `invert_yaw` | false | axis inversion |
| `keyboard`, `key_*` | true, see above | keyboard virtual motion and its keys (Ryujinx key names: `Up`, `Q`, `F8`, `ShiftLeft`, ...) |
| `controller`, `hold_threshold_ms`, `controller_toggle`, `stick_deadzone` | true, 300, false, 0.15 | View-hold mode |

## How it works

`Ryujinx.Input/Motion/VirtualMotion.cs`
- `VirtualMotion` keeps a unit quaternion (device → world, SDL controller axes: X right, Y out of the face, Z towards
  the player). Rates are integrated in the device frame over **real elapsed time** (`Stopwatch`, dt clamped to 0.1 s),
  so the result does not depend on the frame rate (unit test: identical orientation at 30/60/90/120 Hz updates).
- Gyroscope = the commanded angular velocity (°/s); accelerometer = gravity expressed in the device frame (1 G),
  i.e. always consistent with the orientation.
- `VirtualMotionController` turns keyboard and raw controller state into rates and decides which inputs to hide/replay.

`NpadController.ApplyVirtualMotion` feeds the data through **the same path as a real SDL controller's sensors**
(axis swap `(x, −z, y)`, `MotionInput`), and supplies the orientation matrix directly (`MotionInput.OrientationOverride`)
instead of the filter's estimate. A unit test integrates the same data with Ryujinx's own sensor filter and checks the
two orientations agree, so conventions (axes, signs, matrix layout) match a real gyro controller.

Only Player 1 gets virtual motion. Mouse-based motion and external gyro devices are not implemented (external
devices already work through Ryujinx's CemuHook/DSU client, `source: Real`).

## Diagnostics

- `BOTWPC_MOTION_LOG=1` logs Player 1's six-axis data as sent to the game once per second.
- Settings > BotW-PC > Controller test… shows pitch/roll/yaw, the gravity vector, the gyroscope and whether keyboard
  or controller motion mode is on (the running game's state, or a local preview when no game runs).
- Input scripts (`BOTWPC_INPUT_SCRIPT`) accept `start dur motion pitch roll yaw` lines for reproducible tests,
  e.g. `botw/bench/motion-virtual.input`.

## Verification

| Test | Result |
|---|---|
| Unit tests (`VirtualMotionTests`, 8): gravity vs orientation, frame-rate independence, keyboard toggle/rotate/hold/recenter, View tap replay, View hold + suppression + latch on exit, toggle mode + fine, not-in-use, agreement with Ryujinx's sensor filter | pass |
| In game (`bench/motion-virtual-1`, Shrine of Resurrection, Xbox pad without gyro → `Auto` picks virtual): scripted pitch 90°, roll 90°, yaw 90°, combined; the logged six-axis data changes accordingly (e.g. accelerometer (0, 0, −1) flat → (0, 1, 0) after pitching forward 90°, orientation matrix and integrated rotation follow) and stays when the rotation stops | pass (data reaches the game's HID input) |
| **In game, motion-aimed scope** (progressed save copy, Great Plateau, keyboard Player 1 → virtual motion; `botw/bench/scope-motion.input`): R3 opens the scope, then only scripted motion: yaw left 2 s, pitch 2 s, yaw right 2 s. The scope view turns away from the tree during the yaw and tilts down to the ground during the pitch; in the control run (same script without the motion lines, `bench/scope-control`) the view does not move | **pass** — the game uses the virtual sensor for aiming (`bench/scope-motion-kbd`) |
| **A motion-controlled shrine solved with virtual motion** | **NOT TESTED** — the progressed save is on the Great Plateau without the runes; no motion shrine is reachable. Needs a save at e.g. a ball-maze shrine. |
| Physical keyboard and View-hold by a person, in game | **NOT TESTED** (needs a person); logic covered by unit tests |
| 30/60/90/120 FPS in a motion puzzle | **NOT TESTED** in a puzzle; integration is time-based (unit test) |
