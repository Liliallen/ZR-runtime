using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Input;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Headless
{
    /// <summary>
    /// iPadOS app: the on-screen touch controls (and the device's gyroscope) as a game controller. The host sends the
    /// state in Switch terms (button bit = <see cref="GamepadButtonInputId"/>, sticks -1..1 with +Y up). It is listed
    /// only while no physical controller is connected, so a paired Xbox/PlayStation/MFi pad takes over automatically.
    /// </summary>
    sealed class HostTouchPad : IGamepad
    {
        public const string PadId = "host-touch";

        private ulong _buttons;
        private Vector2 _left, _right;
        private Vector3 _accel = new(0, 0, -1), _gyro;
        private readonly object _lock = new();

        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.Motion;
        public string Id => PadId;
        public string Name => "Touch Controls (Nintendo layout)";
        public bool IsConnected => true;

        public void Set(ulong buttons, float lx, float ly, float rx, float ry)
        {
            lock (_lock)
            {
                _buttons = buttons;
                _left = new Vector2(lx, ly);
                _right = new Vector2(rx, ry);
            }
        }

        /// <summary>Accelerometer in G, gyroscope in degrees per second (the units the HID layer expects).</summary>
        public void SetMotion(Vector3 accel, Vector3 gyro)
        {
            lock (_lock)
            {
                _accel = accel;
                _gyro = gyro;
            }
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            lock (_lock)
            {
                return inputId >= 0 && (int)inputId < 64 && (_buttons & (1UL << (int)inputId)) != 0;
            }
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            lock (_lock)
            {
                return inputId switch
                {
                    StickInputId.Left => (_left.X, _left.Y),
                    StickInputId.Right => (_right.X, _right.Y),
                    _ => (0f, 0f),
                };
            }
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            lock (_lock)
            {
                return inputId == MotionInputId.Accelerometer ? _accel : inputId == MotionInputId.Gyroscope ? _gyro : Vector3.Zero;
            }
        }

        // The host already speaks Switch buttons: the mapping is the identity (AutoController binds A to A for
        // "Nintendo" layouts).
        public GamepadStateSnapshot GetMappedStateSnapshot() => GetStateSnapshot();
        public GamepadStateSnapshot GetStateSnapshot() => IGamepad.GetStateSnapshot(this);

        public void SetTriggerThreshold(float triggerThreshold) { }
        public void SetConfiguration(InputConfig configuration) { }
        public void SetLed(uint packedRgb) { }
        public void Rumble(float lowFrequency, float highFrequency, uint durationMs) => RumbleRequested?.Invoke(lowFrequency, highFrequency, durationMs);
        public void Dispose() { }

        /// <summary>Haptics for the host (UIImpactFeedbackGenerator / CoreHaptics).</summary>
        public static event Action<float, float, uint> RumbleRequested;
    }

    /// <summary>Physical controllers through SDL, plus <see cref="HostTouchPad"/> while none is connected.</summary>
    sealed class HostPadDriver : IGamepadDriver
    {
        private readonly IGamepadDriver _sdl;
        private readonly HostTouchPad _touch;

        public HostPadDriver(IGamepadDriver sdl, HostTouchPad touch)
        {
            _sdl = sdl;
            _touch = touch;
            _sdl.OnGamepadConnected += id => OnGamepadConnected?.Invoke(id);
            _sdl.OnGamepadDisconnected += id => OnGamepadDisconnected?.Invoke(id);
        }

        public string DriverName => "HostPadDriver";

        // The touch pad stays listed after the physical pads: automatic assignment prefers a physical pad, and the touch
        // controls still work when a pad is listed but cannot be opened.
        public ReadOnlySpan<string> GamepadsIds => (string[])[.. _sdl.GamepadsIds, HostTouchPad.PadId];

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public IGamepad GetGamepad(string id) => id == HostTouchPad.PadId ? new NonDisposingPad(_touch) : _sdl.GetGamepad(id);

        public IEnumerable<IGamepad> GetGamepads()
        {
            foreach (string id in GamepadsIds.ToArray())
            {
                yield return GetGamepad(id);
            }
        }

        public void Clear() => _sdl.Clear();

        public void Dispose() => _sdl.Dispose();

        // Callers dispose the pads they get; the touch pad is shared and lives as long as the process.
        private sealed class NonDisposingPad(HostTouchPad pad) : IGamepad
        {
            public GamepadFeaturesFlag Features => pad.Features;
            public string Id => pad.Id;
            public string Name => pad.Name;
            public bool IsConnected => pad.IsConnected;
            public bool IsPressed(GamepadButtonInputId inputId) => pad.IsPressed(inputId);
            public (float, float) GetStick(StickInputId inputId) => pad.GetStick(inputId);
            public Vector3 GetMotionData(MotionInputId inputId) => pad.GetMotionData(inputId);
            public void SetTriggerThreshold(float triggerThreshold) { }
            public void SetConfiguration(InputConfig configuration) { }
            public void SetLed(uint packedRgb) { }
            public void Rumble(float lowFrequency, float highFrequency, uint durationMs) => pad.Rumble(lowFrequency, highFrequency, durationMs);
            public GamepadStateSnapshot GetMappedStateSnapshot() => pad.GetMappedStateSnapshot();
            public GamepadStateSnapshot GetStateSnapshot() => pad.GetStateSnapshot();
            public void Dispose() { }
        }
    }
}
