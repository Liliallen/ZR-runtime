using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Controller.Motion;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using ConfigGamepadInputId = Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId;
using ConfigStickInputId = Ryujinx.Common.Configuration.Hid.Controller.StickInputId;

namespace Ryujinx.Input.HLE
{
    /// <summary>
    /// BOTW-PC: binds a connected game controller to Player 1 when the saved configuration has none connected
    /// (e.g. the default keyboard config, or a saved pad that is unplugged). The saved configuration is never
    /// modified; when the controller goes away, the saved configuration applies again.
    /// </summary>
    public static class AutoController
    {
        /// <summary>
        /// Picks the controller to use: a name containing <paramref name="preferred"/>, else an Xbox controller,
        /// else a physical controller before Steam's virtual one, else the first.
        /// Only one controller is ever chosen, so a pad exposed both physically and through Steam Input is not read twice.
        /// </summary>
        public static int Choose(IReadOnlyList<string> names, string preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                int match = IndexOf(names, n => n.Contains(preferred, StringComparison.OrdinalIgnoreCase));

                if (match >= 0)
                {
                    return match;
                }
            }

            int xbox = IndexOf(names, n => n.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || n.Contains("X-Box", StringComparison.OrdinalIgnoreCase));

            if (xbox >= 0)
            {
                return xbox;
            }

            int physical = IndexOf(names, n => !n.Contains("Steam", StringComparison.OrdinalIgnoreCase));

            return physical >= 0 ? physical : (names.Count > 0 ? 0 : -1);
        }

        private static int IndexOf(IReadOnlyList<string> names, Func<string, bool> predicate)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] != null && predicate(names[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Builds a Pro Controller config for Player 1. SDL button names are positional (A = bottom, B = right).
        /// Nintendo layout keeps positions (Switch A on the right button, as on a Pro Controller);
        /// Xbox layout keeps labels (Switch A on Xbox A). Nintendo-made pads always use their own labels.
        /// </summary>
        public static StandardControllerInputConfig Build(string id, string name, ControllerSettings settings, bool hasMotion)
        {
            bool labelTrue = settings.Layout == ButtonLayout.Xbox || name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase);

            StandardControllerInputConfig config = new()
            {
                Version = InputConfig.CurrentVersion,
                Backend = InputBackendType.GamepadSDL2,
                Id = id,
                Name = name,
                PlayerIndex = PlayerIndex.Player1,
                ControllerType = ControllerType.ProController,
                DeadzoneLeft = Math.Clamp(settings.DeadzoneLeft, 0f, 0.9f),
                DeadzoneRight = Math.Clamp(settings.DeadzoneRight, 0f, 0.9f),
                RangeLeft = Math.Clamp(settings.RangeLeft, 0.5f, 2f),
                RangeRight = Math.Clamp(settings.RangeRight, 0.5f, 2f),
                TriggerThreshold = Math.Clamp(settings.TriggerThreshold, 0.05f, 0.95f),
                LeftJoycon = new LeftJoyconCommonConfig<ConfigGamepadInputId>
                {
                    DpadUp = ConfigGamepadInputId.DpadUp,
                    DpadDown = ConfigGamepadInputId.DpadDown,
                    DpadLeft = ConfigGamepadInputId.DpadLeft,
                    DpadRight = ConfigGamepadInputId.DpadRight,
                    ButtonMinus = ConfigGamepadInputId.Minus,
                    ButtonL = ConfigGamepadInputId.LeftShoulder,
                    ButtonZl = ConfigGamepadInputId.LeftTrigger,
                    ButtonSl = ConfigGamepadInputId.Unbound,
                    ButtonSr = ConfigGamepadInputId.Unbound,
                },
                LeftJoyconStick = new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>
                {
                    Joystick = ConfigStickInputId.Left,
                    StickButton = ConfigGamepadInputId.LeftStick,
                },
                RightJoycon = new RightJoyconCommonConfig<ConfigGamepadInputId>
                {
                    ButtonA = labelTrue ? ConfigGamepadInputId.A : ConfigGamepadInputId.B,
                    ButtonB = labelTrue ? ConfigGamepadInputId.B : ConfigGamepadInputId.A,
                    ButtonX = labelTrue ? ConfigGamepadInputId.X : ConfigGamepadInputId.Y,
                    ButtonY = labelTrue ? ConfigGamepadInputId.Y : ConfigGamepadInputId.X,
                    ButtonPlus = ConfigGamepadInputId.Plus,
                    ButtonR = ConfigGamepadInputId.RightShoulder,
                    ButtonZr = ConfigGamepadInputId.RightTrigger,
                    ButtonSl = ConfigGamepadInputId.Unbound,
                    ButtonSr = ConfigGamepadInputId.Unbound,
                },
                RightJoyconStick = new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>
                {
                    Joystick = ConfigStickInputId.Right,
                    StickButton = ConfigGamepadInputId.RightStick,
                    InvertStickX = settings.InvertCameraX,
                    InvertStickY = settings.InvertCameraY,
                },
                Motion = new StandardMotionConfigController
                {
                    MotionBackend = MotionInputBackendType.GamepadDriver,
                    EnableMotion = hasMotion,
                    Sensitivity = 100,
                    GyroDeadzone = 1,
                },
                Rumble = new RumbleConfigController
                {
                    StrongRumble = Math.Clamp(settings.RumbleStrength, 0f, 2f),
                    WeakRumble = Math.Clamp(settings.RumbleStrength, 0f, 2f),
                    EnableRumble = settings.Rumble,
                },
                Led = new LedConfigController(),
            };

            ApplyBindings(config, settings.Bindings);

            return config;
        }

        /// <summary>Applies custom "Switch button → SDL input" bindings; unknown names are skipped with a warning.</summary>
        public static void ApplyBindings(StandardControllerInputConfig config, IReadOnlyDictionary<string, string> bindings)
        {
            foreach ((string logical, string physical) in bindings ?? new Dictionary<string, string>())
            {
                if (!Enum.TryParse(logical, true, out SwitchButton button) || !Enum.TryParse(physical, true, out ConfigGamepadInputId id) ||
                    !Enum.IsDefined(id) || id == ConfigGamepadInputId.Count)
                {
                    Logger.Warning?.Print(LogClass.Hid, $"BOTW-PC: ignoring custom binding \"{logical}\" = \"{physical}\"");
                    continue;
                }

                switch (button)
                {
                    case SwitchButton.A: config.RightJoycon.ButtonA = id; break;
                    case SwitchButton.B: config.RightJoycon.ButtonB = id; break;
                    case SwitchButton.X: config.RightJoycon.ButtonX = id; break;
                    case SwitchButton.Y: config.RightJoycon.ButtonY = id; break;
                    case SwitchButton.R: config.RightJoycon.ButtonR = id; break;
                    case SwitchButton.ZR: config.RightJoycon.ButtonZr = id; break;
                    case SwitchButton.Plus: config.RightJoycon.ButtonPlus = id; break;
                    case SwitchButton.L: config.LeftJoycon.ButtonL = id; break;
                    case SwitchButton.ZL: config.LeftJoycon.ButtonZl = id; break;
                    case SwitchButton.Minus: config.LeftJoycon.ButtonMinus = id; break;
                    case SwitchButton.DpadUp: config.LeftJoycon.DpadUp = id; break;
                    case SwitchButton.DpadDown: config.LeftJoycon.DpadDown = id; break;
                    case SwitchButton.DpadLeft: config.LeftJoycon.DpadLeft = id; break;
                    case SwitchButton.DpadRight: config.LeftJoycon.DpadRight = id; break;
                    case SwitchButton.LeftStick: config.LeftJoyconStick.StickButton = id; break;
                    case SwitchButton.RightStick: config.RightJoyconStick.StickButton = id; break;
                }
            }
        }

        /// <summary>
        /// The button-prompt mapping of the configuration that will run (saved config or automatic Player 1), or null
        /// when the game's own glyphs stay.
        /// </summary>
        public static PromptMapping ResolvePrompts(List<InputConfig> saved, IGamepadDriver driver, ControllerSettings settings, out string reason)
        {
            List<InputConfig> effective = Apply(saved ?? [], driver, settings);

            return ButtonPrompts.FromConfig(effective.Find(c => c.PlayerIndex == PlayerIndex.Player1), settings.Prompts, out reason);
        }

        /// <summary>
        /// Returns the configuration to run with: <paramref name="saved"/>, with Player 1 replaced by an automatic
        /// controller config when Player 1 has no connected controller and one is available.
        /// </summary>
        public static List<InputConfig> Apply(List<InputConfig> saved, IGamepadDriver driver, ControllerSettings settings)
        {
            // BOTWPC_AUTO_CONTROLLER=0 keeps benchmark runs free of a physical pad's stick noise.
            if (!settings.AutoAssign || driver == null || Environment.GetEnvironmentVariable("BOTWPC_AUTO_CONTROLLER") == "0")
            {
                return saved;
            }

            ReadOnlySpan<string> ids = driver.GamepadsIds;
            InputConfig player1 = saved.Find(c => c.PlayerIndex == PlayerIndex.Player1);

            if (player1 is StandardControllerInputConfig && ids.Contains(player1.Id))
            {
                return saved;
            }

            List<string> candidateIds = [];
            List<string> names = [];
            List<bool> motion = [];

            foreach (string id in ids)
            {
                using IGamepad gamepad = driver.GetGamepad(id);

                if (gamepad != null)
                {
                    candidateIds.Add(id);
                    names.Add(gamepad.Name ?? "");
                    motion.Add(gamepad.Features.HasFlag(GamepadFeaturesFlag.Motion));
                }
            }

            int pick = Choose(names, settings.Preferred);

            if (pick < 0)
            {
                return saved;
            }

            List<InputConfig> result = saved.FindAll(c => c.PlayerIndex != PlayerIndex.Player1);
            result.Insert(0, Build(candidateIds[pick], names[pick], settings, motion[pick]));

            return result;
        }
    }
}
