using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ryujinx.Ava.Systems;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.Motion;
using Ryujinx.Input.SDL2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Button = Avalonia.Controls.Button;
using StickInputId = Ryujinx.Input.StickInputId;

namespace Ryujinx.Ava.UI.Windows
{
    /// <summary>
    /// BOTW-PC controller test: live physical inputs with the Switch button each produces and the glyph the game shows
    /// for it, sticks, triggers, rumble, and the virtual motion state. Uses the same mapping and prompt resolution as
    /// the running game (AutoController + ButtonPrompts), and the running game's own virtual motion when one runs.
    /// </summary>
    public class BotwControllerTestWindow : Window
    {
        private static readonly (GamepadButtonInputId Physical, GamepadInputId Config)[] _physical =
        [
            (GamepadButtonInputId.A, GamepadInputId.A), (GamepadButtonInputId.B, GamepadInputId.B), (GamepadButtonInputId.X, GamepadInputId.X),
            (GamepadButtonInputId.Y, GamepadInputId.Y), (GamepadButtonInputId.LeftShoulder, GamepadInputId.LeftShoulder),
            (GamepadButtonInputId.RightShoulder, GamepadInputId.RightShoulder), (GamepadButtonInputId.LeftTrigger, GamepadInputId.LeftTrigger),
            (GamepadButtonInputId.RightTrigger, GamepadInputId.RightTrigger), (GamepadButtonInputId.Minus, GamepadInputId.Minus),
            (GamepadButtonInputId.Plus, GamepadInputId.Plus), (GamepadButtonInputId.LeftStick, GamepadInputId.LeftStick),
            (GamepadButtonInputId.RightStick, GamepadInputId.RightStick), (GamepadButtonInputId.DpadUp, GamepadInputId.DpadUp),
            (GamepadButtonInputId.DpadDown, GamepadInputId.DpadDown), (GamepadButtonInputId.DpadLeft, GamepadInputId.DpadLeft),
            (GamepadButtonInputId.DpadRight, GamepadInputId.DpadRight), (GamepadButtonInputId.Misc1, GamepadInputId.Misc1),
            (GamepadButtonInputId.Guide, GamepadInputId.Guide), (GamepadButtonInputId.Paddle1, GamepadInputId.Paddle1),
            (GamepadButtonInputId.Paddle2, GamepadInputId.Paddle2), (GamepadButtonInputId.Paddle3, GamepadInputId.Paddle3),
            (GamepadButtonInputId.Paddle4, GamepadInputId.Paddle4),
        ];

        private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _live = new() { FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _motion = new() { FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _mapping = new() { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly VirtualMotionController _localMotion = new() { InUse = true };
        private IGamepad _gamepad;
        private string _gamepadId;
        private StandardControllerInputConfig _config;

        public BotwControllerTestWindow()
        {
            Title = "BOTW-PC — controller test";
            Width = 760;
            Height = 720;

            Button rumble = new() { Content = "Rumble test (0.5 s)" };
            rumble.Click += (_, _) => _gamepad?.Rumble(1f, 1f, 500);

            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        _header,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { rumble } },
                        new TextBlock { Text = "Live input (press buttons on the controller):", FontWeight = FontWeight.Bold },
                        _live,
                        new TextBlock { Text = "Motion:", FontWeight = FontWeight.Bold },
                        _motion,
                        new TextBlock { Text = "Active mapping (Switch button → physical input):", FontWeight = FontWeight.Bold },
                        _mapping,
                    },
                },
            };

            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Closed += (_, _) =>
            {
                _timer.Stop();
                _gamepad?.Rumble(0f, 0f, 1);
                _gamepad?.Dispose();
            };
        }

        private static string Connection(string id)
        {
            // Ryujinx ids embed SDL's joystick GUID; its first field is the bus type (USB 0x03, Bluetooth 0x05).
            string[] parts = id?.Split('-') ?? [];
            return parts.Length > 1 && parts[1].Length == 8 ? parts[1][^2..] switch { "03" => "USB", "05" => "Bluetooth", _ => $"bus 0x{parts[1][^2..]}" } : "unknown";
        }

        private void Refresh()
        {
            ControllerSettings settings = BotwPcSettings.Instance.Controller;
            IGamepadDriver driver = RyujinxApp.MainWindow.InputManager?.GamepadDriver;
            List<InputConfig> saved = ConfigurationState.Instance.Hid.InputConfig.Value;
            InputConfig player1 = AutoController.Apply(saved ?? [], driver, settings).Find(c => c.PlayerIndex == PlayerIndex.Player1);
            _config = player1 as StandardControllerInputConfig;

            if (_config?.Id != _gamepadId)
            {
                _gamepad?.Dispose();
                _gamepad = _config != null ? driver?.GetGamepad(_config.Id) : null;
                _gamepad?.SetConfiguration(_config);
                _gamepadId = _config?.Id;
            }

            AppHost host = RyujinxApp.MainWindow.ViewModel.AppHost;
            bool running = host != null && RyujinxApp.MainWindow.ViewModel.IsGameRunning;
            PromptMapping next = ButtonPrompts.FromConfig(player1, settings.Prompts, out string reason);
            PromptMapping shown = running ? ButtonPrompts.LaunchMapping : next;

            _header.Text = _gamepad == null
                ? $"No game controller is Player 1 ({reason}). Connect an Xbox controller; the saved Player 1 configuration is " +
                  $"{(player1 == null ? "empty" : player1.Backend.ToString())}."
                : $"Controller: {_gamepad.Name}   Connection: {Connection(_gamepad.Id)}   Id: {_gamepad.Id}\n" +
                  $"Rumble: {(_gamepad.Features.HasFlag(GamepadFeaturesFlag.Rumble) ? "yes" : "no")}   " +
                  $"Gyroscope: {(_gamepad.Features.HasFlag(GamepadFeaturesFlag.Motion) ? "yes" : "no (use virtual motion)")}   " +
                  $"Layout: {settings.Layout}   Custom bindings: {settings.Bindings.Count}\n" +
                  $"Prompts: {reason}{(running ? (ButtonPrompts.Stale ? " — the running game was started with another mapping: restart it" : " — as loaded by the running game") : " — will be generated at the next game start")}";

            _mapping.Text = ButtonPrompts.Describe(next);
            _live.Text = _gamepad == null ? "" : LiveText(_gamepad, _config, shown, running);
            _motion.Text = MotionText(running ? host.NpadManager.VirtualMotion : null);
        }

        private string LiveText(IGamepad gamepad, StandardControllerInputConfig config, PromptMapping shown, bool running)
        {
            GamepadStateSnapshot raw = gamepad.GetStateSnapshot();
            PromptMapping bound = ButtonPrompts.FromConfig(config, PromptStyle.Xbox, out _);
            StringBuilder text = new();

            foreach ((GamepadButtonInputId physical, GamepadInputId id) in _physical)
            {
                if (!raw.IsPressed(physical))
                {
                    continue;
                }

                SwitchButton[] logical = [.. PromptMapping.All.Where(b => bound.Get(b) == id)];
                text.AppendLine($"Physical Button:       Xbox {ButtonPrompts.XboxLabel(id)}");

                if (logical.Length == 0)
                {
                    text.AppendLine($"Logical Switch Button: (none){(id == GamepadInputId.Misc1 ? " — Share takes a screenshot (Capture)" : "")}");
                    text.AppendLine();
                    continue;
                }

                foreach (SwitchButton b in logical)
                {
                    string prompt = shown == null ? $"Switch {b} (game's own glyph)" : $"Xbox {ButtonPrompts.XboxLabel(shown.Get(b))}";
                    bool matched = shown != null && shown.Get(b) == id;
                    text.AppendLine($"Logical Switch Button: {b}");
                    text.AppendLine($"Displayed Game Prompt: {prompt}{(running ? "" : " (at next game start)")}");
                    text.AppendLine($"Status:                {(matched ? "Matched" : "MISMATCH — the screen shows a different button")}");
                }

                if (id == GamepadInputId.Minus && BotwPcSettings.Instance.Motion.Controller)
                {
                    text.AppendLine("                       (hold View for virtual motion; a short press is Switch −)");
                }

                text.AppendLine();
            }

            (float lx, float ly) = raw.GetStick(StickInputId.Left);
            (float rx, float ry) = raw.GetStick(StickInputId.Right);
            float lt = gamepad is SDL2Gamepad sdl ? sdl.TriggerValue(GamepadButtonInputId.LeftTrigger) : float.NaN;
            float rt = gamepad is SDL2Gamepad sdl2 ? sdl2.TriggerValue(GamepadButtonInputId.RightTrigger) : float.NaN;
            string dpad = string.Concat(
                raw.IsPressed(GamepadButtonInputId.DpadUp) ? "↑" : "", raw.IsPressed(GamepadButtonInputId.DpadDown) ? "↓" : "",
                raw.IsPressed(GamepadButtonInputId.DpadLeft) ? "←" : "", raw.IsPressed(GamepadButtonInputId.DpadRight) ? "→" : "");

            text.AppendLine($"Left stick  x {lx,6:0.00}  y {ly,6:0.00}   (deadzone {config.DeadzoneLeft:0.00})");
            text.AppendLine($"Right stick x {rx,6:0.00}  y {ry,6:0.00}   (deadzone {config.DeadzoneRight:0.00})");
            text.AppendLine($"LT {lt:0.00} {(raw.IsPressed(GamepadButtonInputId.LeftTrigger) ? "pressed" : "       ")}   " +
                            $"RT {rt:0.00} {(raw.IsPressed(GamepadButtonInputId.RightTrigger) ? "pressed" : "       ")}   " +
                            $"(threshold {config.TriggerThreshold:0.00}, release below {config.TriggerThreshold - BotwPcSettings.Instance.Controller.TriggerHysteresis:0.00})");
            text.Append($"D-pad {(dpad.Length == 0 ? "-" : dpad)}");

            return text.ToString();
        }

        private string MotionText(VirtualMotionController game)
        {
            MotionSettings settings = BotwPcSettings.Instance.Motion;
            VirtualMotionController vm = game;

            if (vm == null)
            {
                // No game running: drive a local copy from the controller so the View-hold controls can be tried here.
                GamepadStateSnapshot? raw = _gamepad?.GetStateSnapshot();
                _localMotion.Update(null, raw, false, false, settings, System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
                vm = _localMotion;
            }

            Vector3 euler = vm.Motion.EulerDegrees;
            Vector3 g = vm.Motion.Accelerometer;
            Vector3 w = vm.Motion.Gyroscope;

            return $"Source: {settings.Source}{(game == null ? " (no game running: preview driven by this window)" : vm.InUse ? " — virtual motion feeds the game" : " — the game uses the controller's own sensors or none")}\n" +
                   $"Virtual motion active: keyboard {(vm.KeyboardEngaged ? "ON" : "off")} ({settings.KeyToggle} toggles)   controller {(vm.ControllerEngaged ? "ON" : "off")} (hold View)\n" +
                   $"Pitch {euler.X,7:0.0}°   Roll {euler.Y,7:0.0}°   Yaw {euler.Z,7:0.0}°\n" +
                   $"Gravity (accelerometer, G)  x {g.X,6:0.00}  y {g.Y,6:0.00}  z {g.Z,6:0.00}\n" +
                   $"Gyroscope (°/s)             x {w.X,6:0.0}  y {w.Y,6:0.0}  z {w.Z,6:0.0}";
        }
    }
}
