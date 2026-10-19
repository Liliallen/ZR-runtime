using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Input.Motion
{
    /// <summary>
    /// A simulated motion controller for pads without a gyroscope. Keeps an orientation quaternion, integrates angular
    /// velocity over real elapsed time and synthesises gyroscope and accelerometer readings that agree with it.
    /// Axes are SDL's controller axes (X right, Y out of the face, Z towards the player); identity = lying flat, face up.
    /// Output units match <see cref="IGamepad.GetMotionData"/>: gyroscope in degrees/s, accelerometer in G
    /// (reading +1 G on the axis pointing away from the ground, as a real sensor at rest does).
    /// </summary>
    public sealed class VirtualMotion
    {
        /// <summary>Device → world rotation.</summary>
        public Quaternion Orientation { get; private set; } = Quaternion.Identity;

        /// <summary>Angular velocity in the device frame, degrees per second (pitch about X, yaw about Y, roll about Z).</summary>
        public Vector3 Gyroscope { get; private set; }

        public Vector3 Accelerometer => Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(Orientation));

        /// <summary>Rotates by <paramref name="rateDegrees"/> (device frame) for <paramref name="seconds"/>.</summary>
        public void Integrate(Vector3 rateDegrees, float seconds)
        {
            Gyroscope = rateDegrees;
            Vector3 radians = rateDegrees * (MathF.PI / 180f);
            float angle = radians.Length() * seconds;

            if (angle > 0f)
            {
                Orientation = Quaternion.Normalize(Orientation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(radians), angle));
            }
        }

        public void Recenter()
        {
            Orientation = Quaternion.Identity;
            Gyroscope = Vector3.Zero;
        }

        /// <summary>Pitch (forward positive), roll (right positive) and yaw (left positive) in degrees, for display.</summary>
        public Vector3 EulerDegrees
        {
            get
            {
                // Forward tilt lowers the far edge (-Z); right roll lowers the right edge (+X).
                Vector3 far = Vector3.Transform(-Vector3.UnitZ, Orientation);
                Vector3 right = Vector3.Transform(Vector3.UnitX, Orientation);
                float pitch = MathF.Asin(Math.Clamp(-far.Y, -1f, 1f));
                float roll = MathF.Asin(Math.Clamp(-right.Y, -1f, 1f));
                float yaw = MathF.Atan2(-far.X, -far.Z);

                return new Vector3(pitch, roll, yaw) * (180f / MathF.PI);
            }
        }

        /// <summary>Converts a device-frame vector from SDL axes to the axes Ryujinx's motion filter uses (x, -z, y).</summary>
        public static Vector3 ToFilterAxes(Vector3 v) => new(v.X, -v.Z, v.Y);

        /// <summary>The orientation expressed in the motion filter's axes (a +90° rotation about X of SDL's axes).</summary>
        public Quaternion FilterOrientation
        {
            get
            {
                Quaternion r = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
                return Quaternion.Normalize(r * Orientation * Quaternion.Conjugate(r));
            }
        }
    }

    /// <summary>
    /// Turns keyboard keys and a held View button into virtual motion, and tells the caller which inputs to hide from
    /// the game while they are used for motion. One instance serves Player 1.
    /// </summary>
    public sealed class VirtualMotionController
    {
        private enum ViewState { Idle, Pending, Motion, Toggled }

        public VirtualMotion Motion { get; } = new();

        /// <summary>Keyboard motion mode (toggled with the toggle key).</summary>
        public bool KeyboardEngaged { get; private set; }

        /// <summary>Controller motion mode (View held past the threshold, or toggled).</summary>
        public bool ControllerEngaged { get; private set; }

        /// <summary>Virtual motion feeds the game (decided by the caller from <see cref="MotionSource"/>).</summary>
        public bool InUse { get; set; }

        /// <summary>Physical controller inputs the game must not see this update.</summary>
        public HashSet<GamepadButtonInputId> SuppressedButtons { get; } = [];

        /// <summary>Physical controller inputs to report as pressed this update (a short View tap, replayed on release).</summary>
        public HashSet<GamepadButtonInputId> InjectedButtons { get; } = [];

        public bool SuppressRightStick { get; private set; }

        /// <summary>Extra rate from a test script: (pitch forward, roll right, yaw left), -1..1 of the configured rate.</summary>
        public Vector3 ScriptedRate { get; set; }

        /// <summary>Keys the game must not see this update (keyboard Player 1).</summary>
        public HashSet<Key> SuppressedKeys { get; } = [];

        private const double TapPulseSeconds = 0.1;
        private static readonly GamepadButtonInputId[] _motionButtons =
            [GamepadButtonInputId.LeftShoulder, GamepadButtonInputId.RightShoulder, GamepadButtonInputId.RightStick, GamepadButtonInputId.LeftTrigger];

        private ViewState _view;
        private double _viewDownAt, _tapUntil, _lastTime = -1;
        private bool _togglePrev, _recenterKeyPrev, _r3Prev;
        private readonly HashSet<GamepadButtonInputId> _latched = [];
        private bool _stickLatched;
        private Key[] _keys;
        private string _keysSource;

        /// <param name="keyboard">Raw keyboard state, or null.</param>
        /// <param name="pad">Raw physical state of Player 1's controller, or null.</param>
        /// <param name="invertRightX">Undo the camera inversion that the driver applied to the raw right stick.</param>
        public void Update(KeyboardStateSnapshot keyboard, GamepadStateSnapshot? pad, bool invertRightX, bool invertRightY, MotionSettings settings, double now)
        {
            float dt = _lastTime < 0 ? 0f : (float)Math.Clamp(now - _lastTime, 0, 0.1);
            _lastTime = now;
            SuppressedButtons.Clear();
            InjectedButtons.Clear();
            SuppressedKeys.Clear();
            SuppressRightStick = false;

            Vector3 rate = Vector3.Zero; // (pitch forward, yaw left, roll right) in user terms, -1..1
            float fine = 1f;

            if (keyboard != null && settings.Keyboard)
            {
                rate += UpdateKeyboard(keyboard, settings, ref fine);
            }
            else
            {
                KeyboardEngaged = false;
            }

            if (pad.HasValue && settings.Controller)
            {
                rate += UpdateController(pad.Value, invertRightX, invertRightY, settings, now, ref fine);
            }
            else
            {
                ControllerEngaged = false;
                _view = ViewState.Idle;
            }

            rate += new Vector3(ScriptedRate.X, ScriptedRate.Z, ScriptedRate.Y);

            if (!InUse)
            {
                rate = Vector3.Zero;
            }

            float scale = settings.RateDegreesPerSecond * fine;
            float pitch = Math.Clamp(rate.X, -1, 1) * scale * (settings.InvertPitch ? -1 : 1);
            float yaw = Math.Clamp(rate.Y, -1, 1) * scale * (settings.InvertYaw ? -1 : 1);
            float roll = Math.Clamp(rate.Z, -1, 1) * scale * (settings.InvertRoll ? -1 : 1);

            // Device frame: forward pitch lowers the far edge = negative rotation about X; right roll = negative about Z;
            // left yaw = positive about Y.
            Motion.Integrate(new Vector3(-pitch, yaw, -roll), dt);
        }

        private Key[] Keys(MotionSettings s)
        {
            string source = string.Join(",", s.KeyPitchForward, s.KeyPitchBackward, s.KeyRollLeft, s.KeyRollRight, s.KeyYawLeft, s.KeyYawRight, s.KeyRecenter, s.KeyToggle, s.KeyFine);

            if (source != _keysSource)
            {
                _keysSource = source;
                _keys = [.. Array.ConvertAll(source.Split(','), name =>
                {
                    if (Enum.TryParse(name, true, out Key key) && key is not Key.Unknown and not Key.Unbound and not Key.Count)
                    {
                        return key;
                    }

                    Logger.Warning?.Print(LogClass.Hid, $"BOTW-PC virtual motion: unknown key \"{name}\", unbound");
                    return Key.Unbound;
                })];
            }

            return _keys;
        }

        private static bool Down(KeyboardStateSnapshot k, Key key) => key != Key.Unbound && k.IsPressed(key);

        private Vector3 UpdateKeyboard(KeyboardStateSnapshot k, MotionSettings s, ref float fine)
        {
            Key[] keys = Keys(s);
            (Key forward, Key backward, Key left, Key right, Key yawLeft, Key yawRight, Key recenter, Key toggle, Key fineKey) =
                (keys[0], keys[1], keys[2], keys[3], keys[4], keys[5], keys[6], keys[7], keys[8]);

            bool togglePressed = Down(k, toggle);

            if (togglePressed && !_togglePrev)
            {
                KeyboardEngaged = !KeyboardEngaged;
                Logger.Info?.Print(LogClass.Hid, $"BOTW-PC virtual motion (keyboard): {(KeyboardEngaged ? "on" : "off")}");
            }

            _togglePrev = togglePressed;

            if (toggle != Key.Unbound)
            {
                SuppressedKeys.Add(toggle);
            }

            if (!KeyboardEngaged)
            {
                _recenterKeyPrev = false;
                return Vector3.Zero;
            }

            foreach (Key key in keys)
            {
                if (key != Key.Unbound)
                {
                    SuppressedKeys.Add(key);
                }
            }

            bool recenterPressed = Down(k, recenter);

            if (recenterPressed && !_recenterKeyPrev)
            {
                Motion.Recenter();
            }

            _recenterKeyPrev = recenterPressed;

            if (Down(k, fineKey) || (fineKey == Key.ShiftLeft && k.IsPressed(Key.ShiftRight)))
            {
                fine = Math.Min(fine, s.FineFactor);
            }

            static float Axis(bool positive, bool negative) => (positive ? 1f : 0f) - (negative ? 1f : 0f);

            return new Vector3(
                Axis(Down(k, forward), Down(k, backward)),
                Axis(Down(k, yawLeft), Down(k, yawRight)),
                Axis(Down(k, right), Down(k, left)));
        }

        private Vector3 UpdateController(GamepadStateSnapshot pad, bool invertX, bool invertY, MotionSettings s, double now, ref float fine)
        {
            bool viewDown = pad.IsPressed(GamepadButtonInputId.Minus);
            double threshold = Math.Max(0, s.HoldThresholdMs) / 1000.0;

            switch (_view)
            {
                case ViewState.Idle:
                    if (viewDown)
                    {
                        _view = ViewState.Pending;
                        _viewDownAt = now;
                    }
                    break;

                case ViewState.Pending:
                    if (!viewDown)
                    {
                        // Short tap: the game gets View (Minus) now, as a short press.
                        _view = ViewState.Idle;
                        _tapUntil = now + TapPulseSeconds;
                    }
                    else if (now - _viewDownAt >= threshold)
                    {
                        _view = s.ControllerToggle ? ViewState.Toggled : ViewState.Motion;
                        ControllerEngaged = !s.ControllerToggle || !ControllerEngaged;
                        Logger.Info?.Print(LogClass.Hid, $"BOTW-PC virtual motion (controller): {(ControllerEngaged ? "on" : "off")}");
                    }
                    break;

                case ViewState.Motion:
                    if (!viewDown)
                    {
                        _view = ViewState.Idle;
                        ControllerEngaged = false;
                        Logger.Info?.Print(LogClass.Hid, "BOTW-PC virtual motion (controller): off");
                    }
                    break;

                case ViewState.Toggled:
                    if (!viewDown)
                    {
                        _view = ViewState.Idle;
                    }
                    break;
            }

            if (viewDown)
            {
                SuppressedButtons.Add(GamepadButtonInputId.Minus);
            }
            else if (now < _tapUntil)
            {
                InjectedButtons.Add(GamepadButtonInputId.Minus);
            }

            (float x, float y) = pad.GetStick(StickInputId.Right);
            x = invertX ? -x : x;
            y = invertY ? -y : y;
            float magnitude = MathF.Min(MathF.Sqrt(x * x + y * y), 1f);
            bool stickActive = magnitude > s.StickDeadzone;

            // Inputs used for motion stay hidden from the game until released, also after motion mode ends,
            // so leaving the mode never produces a stray LB/RB/R3/LT press or camera turn.
            foreach (GamepadButtonInputId button in _motionButtons)
            {
                if (ControllerEngaged && pad.IsPressed(button))
                {
                    _latched.Add(button);
                }
                else if (!pad.IsPressed(button))
                {
                    _latched.Remove(button);
                }
            }

            _stickLatched = stickActive && (ControllerEngaged || _stickLatched);

            foreach (GamepadButtonInputId button in _latched)
            {
                SuppressedButtons.Add(button);
            }

            SuppressRightStick = ControllerEngaged || _stickLatched;

            bool r3 = pad.IsPressed(GamepadButtonInputId.RightStick);

            if (ControllerEngaged && r3 && !_r3Prev)
            {
                Motion.Recenter();
            }

            _r3Prev = r3;

            if (!ControllerEngaged)
            {
                return Vector3.Zero;
            }

            if (pad.IsPressed(GamepadButtonInputId.LeftTrigger))
            {
                fine = Math.Min(fine, s.FineFactor);
            }

            Vector2 stick = Vector2.Zero;

            if (stickActive)
            {
                float scaled = (magnitude - s.StickDeadzone) / (1 - s.StickDeadzone);
                stick = new Vector2(x, y) / magnitude * scaled;
            }

            float yaw = (pad.IsPressed(GamepadButtonInputId.LeftShoulder) ? 1f : 0f) - (pad.IsPressed(GamepadButtonInputId.RightShoulder) ? 1f : 0f);

            // Stick up = pitch forward, stick right = roll right.
            return new Vector3(stick.Y, yaw, stick.X);
        }
    }
}
