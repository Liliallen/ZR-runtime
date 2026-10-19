using Ryujinx.Ava.Input;
using Ryujinx.Input;
using System;
using System.Collections.Generic;

namespace Ryujinx.Headless
{
    /// <summary>
    /// Cemu-Switch: keyboard fed by the host window's key events (macOS virtual key codes), so the keyboard profiles of
    /// the configuration drive the emulated controller as in the BOTW-PC app (keyboard id "0").
    /// </summary>
    sealed class HostKeyboardDriver : IGamepadDriver, IKeyStateSource
    {
        private static readonly string[] _ids = ["0"];
        private readonly HashSet<Key> _pressed = [];

        public string DriverName => "HostKeyboardDriver";
        public ReadOnlySpan<string> GamepadsIds => _ids;

        public event Action<string> OnGamepadConnected { add { } remove { } }
        public event Action<string> OnGamepadDisconnected { add { } remove { } }

        public IGamepad GetGamepad(string id) => id == _ids[0] ? new AvaloniaKeyboard(this, _ids[0], "Keyboard") : null;

        public IEnumerable<IGamepad> GetGamepads() => [GetGamepad(_ids[0])];

        public bool IsPressed(Key key)
        {
            lock (_pressed)
            {
                return _pressed.Contains(key);
            }
        }

        public void Clear()
        {
            lock (_pressed)
            {
                _pressed.Clear();
            }
        }

        /// <summary>A key went down or up in the host window (macOS virtual key code, NSEvent.keyCode).</summary>
        public void OnHostKey(int macKeyCode, bool down)
        {
            if (!_macKeys.TryGetValue(macKeyCode, out Key key))
            {
                return;
            }

            lock (_pressed)
            {
                if (down)
                {
                    _pressed.Add(key);
                }
                else
                {
                    _pressed.Remove(key);
                }
            }
        }

        public void Dispose() { }

        // macOS virtual key codes (Carbon kVK_*), physical positions on an ANSI layout.
        private static readonly Dictionary<int, Key> _macKeys = new()
        {
            [0x00] = Key.A, [0x01] = Key.S, [0x02] = Key.D, [0x03] = Key.F, [0x04] = Key.H, [0x05] = Key.G, [0x06] = Key.Z,
            [0x07] = Key.X, [0x08] = Key.C, [0x09] = Key.V, [0x0B] = Key.B, [0x0C] = Key.Q, [0x0D] = Key.W, [0x0E] = Key.E,
            [0x0F] = Key.R, [0x10] = Key.Y, [0x11] = Key.T, [0x12] = Key.Number1, [0x13] = Key.Number2, [0x14] = Key.Number3,
            [0x15] = Key.Number4, [0x16] = Key.Number6, [0x17] = Key.Number5, [0x18] = Key.Plus, [0x19] = Key.Number9,
            [0x1A] = Key.Number7, [0x1B] = Key.Minus, [0x1C] = Key.Number8, [0x1D] = Key.Number0, [0x1E] = Key.BracketRight,
            [0x1F] = Key.O, [0x20] = Key.U, [0x21] = Key.BracketLeft, [0x22] = Key.I, [0x23] = Key.P, [0x24] = Key.Enter,
            [0x25] = Key.L, [0x26] = Key.J, [0x27] = Key.Quote, [0x28] = Key.K, [0x29] = Key.Semicolon, [0x2A] = Key.BackSlash,
            [0x2B] = Key.Comma, [0x2C] = Key.Slash, [0x2D] = Key.N, [0x2E] = Key.M, [0x2F] = Key.Period, [0x30] = Key.Tab,
            [0x31] = Key.Space, [0x32] = Key.Grave, [0x33] = Key.BackSpace, [0x35] = Key.Escape, [0x36] = Key.WinRight,
            [0x37] = Key.WinLeft, [0x38] = Key.ShiftLeft, [0x39] = Key.CapsLock, [0x3A] = Key.AltLeft, [0x3B] = Key.ControlLeft,
            [0x3C] = Key.ShiftRight, [0x3D] = Key.AltRight, [0x3E] = Key.ControlRight,
            [0x7A] = Key.F1, [0x78] = Key.F2, [0x63] = Key.F3, [0x76] = Key.F4, [0x60] = Key.F5, [0x61] = Key.F6, [0x62] = Key.F7,
            [0x64] = Key.F8, [0x65] = Key.F9, [0x6D] = Key.F10, [0x67] = Key.F11, [0x6F] = Key.F12,
            [0x72] = Key.Insert, [0x73] = Key.Home, [0x74] = Key.PageUp, [0x75] = Key.Delete, [0x77] = Key.End, [0x79] = Key.PageDown,
            [0x7B] = Key.Left, [0x7C] = Key.Right, [0x7D] = Key.Down, [0x7E] = Key.Up,
            [0x52] = Key.Keypad0, [0x53] = Key.Keypad1, [0x54] = Key.Keypad2, [0x55] = Key.Keypad3, [0x56] = Key.Keypad4,
            [0x57] = Key.Keypad5, [0x58] = Key.Keypad6, [0x59] = Key.Keypad7, [0x5B] = Key.Keypad8, [0x5C] = Key.Keypad9,
            [0x41] = Key.KeypadDecimal, [0x43] = Key.KeypadMultiply, [0x45] = Key.KeypadAdd, [0x4B] = Key.KeypadDivide,
            [0x4C] = Key.KeypadEnter, [0x4E] = Key.KeypadSubtract,
        };
    }
}
