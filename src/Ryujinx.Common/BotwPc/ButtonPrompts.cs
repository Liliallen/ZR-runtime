using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>Logical Switch buttons that BotW shows as on-screen prompt glyphs.</summary>
    public enum SwitchButton
    {
        A, B, X, Y, L, R, ZL, ZR, Plus, Minus, LeftStick, RightStick, DpadUp, DpadDown, DpadLeft, DpadRight,
    }

    /// <summary>Which glyphs the game shows for button prompts.</summary>
    public enum PromptStyle
    {
        /// <summary>Xbox glyphs when Player 1 is a non-Nintendo, non-PlayStation game controller; otherwise the game's own glyphs.</summary>
        Auto,

        /// <summary>Xbox glyphs whenever Player 1 is a game controller.</summary>
        Xbox,

        /// <summary>Never change the game's glyphs.</summary>
        Original,
    }

    /// <summary>Who wins when a RomFS mod also replaces the button glyph textures.</summary>
    public enum PromptPriority
    {
        /// <summary>BOTW-PC draws its glyphs over the mod's files (the mod's other changes are kept).</summary>
        BuiltIn,

        /// <summary>The mod's glyph textures are left untouched (BOTW-PC prompts are not applied to that file).</summary>
        Mod,
    }

    /// <summary>
    /// The single source of truth for button prompts: for each logical Switch button, the physical controller input
    /// that produces it in the active Player 1 configuration (after layout, remapping and auto-assignment).
    /// The prompt shown for a logical button is the glyph of that physical input, so what is on screen is what to press.
    /// </summary>
    public sealed class PromptMapping
    {
        public string ControllerName { get; }
        public IReadOnlyDictionary<SwitchButton, GamepadInputId> Physical { get; }

        public PromptMapping(string controllerName, IReadOnlyDictionary<SwitchButton, GamepadInputId> physical)
        {
            ControllerName = controllerName ?? "";
            Physical = physical;
        }

        /// <summary>Stable text form ("A=B,B=A,...") used to compare mappings and as part of the overlay cache key.</summary>
        public string Key => string.Join(",", Enum.GetValues<SwitchButton>().Select(b => $"{b}={Get(b)}"));

        public GamepadInputId Get(SwitchButton button) => Physical.TryGetValue(button, out GamepadInputId id) ? id : GamepadInputId.Unbound;

        /// <summary>Logical buttons whose glyph changes (all of them except stick directions, which are the same on Xbox).</summary>
        public static IEnumerable<SwitchButton> All => Enum.GetValues<SwitchButton>();
    }

    public static class ButtonPrompts
    {
        /// <summary>Mapping the RomFS prompt overlay is generated from; set by the frontend right before a game loads.</summary>
        public static PromptMapping LaunchMapping { get; set; }

        /// <summary>True when the running game's prompts were generated for a different mapping than the active one.</summary>
        public static bool Stale { get; set; }

        /// <summary>
        /// Reads the logical → physical mapping from a Player 1 configuration. Returns null when the game's own glyphs
        /// should stay: keyboard input, <see cref="PromptStyle.Original"/>, or (Auto) a Nintendo/PlayStation controller.
        /// </summary>
        public static PromptMapping FromConfig(InputConfig config, PromptStyle style, out string reason)
        {
            if (style == PromptStyle.Original)
            {
                reason = "prompt style is Original";
                return null;
            }

            if (config is not StandardControllerInputConfig pad)
            {
                reason = config == null ? "no Player 1 input" : "Player 1 is not a game controller";
                return null;
            }

            string name = pad.Name ?? "";

            if (style == PromptStyle.Auto && IsNonXboxBrand(name))
            {
                reason = $"\"{name}\" has its own button labels";
                return null;
            }

            Dictionary<SwitchButton, GamepadInputId> map = new()
            {
                [SwitchButton.A] = pad.RightJoycon?.ButtonA ?? GamepadInputId.Unbound,
                [SwitchButton.B] = pad.RightJoycon?.ButtonB ?? GamepadInputId.Unbound,
                [SwitchButton.X] = pad.RightJoycon?.ButtonX ?? GamepadInputId.Unbound,
                [SwitchButton.Y] = pad.RightJoycon?.ButtonY ?? GamepadInputId.Unbound,
                [SwitchButton.R] = pad.RightJoycon?.ButtonR ?? GamepadInputId.Unbound,
                [SwitchButton.ZR] = pad.RightJoycon?.ButtonZr ?? GamepadInputId.Unbound,
                [SwitchButton.Plus] = pad.RightJoycon?.ButtonPlus ?? GamepadInputId.Unbound,
                [SwitchButton.L] = pad.LeftJoycon?.ButtonL ?? GamepadInputId.Unbound,
                [SwitchButton.ZL] = pad.LeftJoycon?.ButtonZl ?? GamepadInputId.Unbound,
                [SwitchButton.Minus] = pad.LeftJoycon?.ButtonMinus ?? GamepadInputId.Unbound,
                [SwitchButton.DpadUp] = pad.LeftJoycon?.DpadUp ?? GamepadInputId.Unbound,
                [SwitchButton.DpadDown] = pad.LeftJoycon?.DpadDown ?? GamepadInputId.Unbound,
                [SwitchButton.DpadLeft] = pad.LeftJoycon?.DpadLeft ?? GamepadInputId.Unbound,
                [SwitchButton.DpadRight] = pad.LeftJoycon?.DpadRight ?? GamepadInputId.Unbound,
                [SwitchButton.LeftStick] = pad.LeftJoyconStick?.StickButton ?? GamepadInputId.Unbound,
                [SwitchButton.RightStick] = pad.RightJoyconStick?.StickButton ?? GamepadInputId.Unbound,
            };

            reason = $"Xbox prompts for \"{name}\"";
            return new PromptMapping(name, map);
        }

        /// <summary>Controllers whose own labels are not Xbox-style; Auto keeps the game's glyphs for them.</summary>
        public static bool IsNonXboxBrand(string name) =>
            name.Contains("Nintendo", StringComparison.OrdinalIgnoreCase) || name.Contains("Joy-Con", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Pro Controller", StringComparison.OrdinalIgnoreCase) || name.Contains("PS3", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PS4", StringComparison.OrdinalIgnoreCase) || name.Contains("PS5", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DualShock", StringComparison.OrdinalIgnoreCase) || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PlayStation", StringComparison.OrdinalIgnoreCase);

        /// <summary>Xbox name of a physical input, as printed on the controller.</summary>
        public static string XboxLabel(GamepadInputId id) => id switch
        {
            GamepadInputId.A => "A",
            GamepadInputId.B => "B",
            GamepadInputId.X => "X",
            GamepadInputId.Y => "Y",
            GamepadInputId.LeftShoulder => "LB",
            GamepadInputId.RightShoulder => "RB",
            GamepadInputId.LeftTrigger => "LT",
            GamepadInputId.RightTrigger => "RT",
            GamepadInputId.Minus => "View",
            GamepadInputId.Plus => "Menu",
            GamepadInputId.LeftStick => "LS (click)",
            GamepadInputId.RightStick => "RS (click)",
            GamepadInputId.DpadUp => "D-pad Up",
            GamepadInputId.DpadDown => "D-pad Down",
            GamepadInputId.DpadLeft => "D-pad Left",
            GamepadInputId.DpadRight => "D-pad Right",
            GamepadInputId.Guide => "Guide",
            GamepadInputId.Misc1 => "Share",
            GamepadInputId.Paddle1 => "P1",
            GamepadInputId.Paddle2 => "P2",
            GamepadInputId.Paddle3 => "P3",
            GamepadInputId.Paddle4 => "P4",
            GamepadInputId.Unbound => "(unbound)",
            _ => id.ToString(),
        };

        /// <summary>Human-readable table "Switch B -> Xbox A" for logs and the diagnostics screen.</summary>
        public static string Describe(PromptMapping mapping) =>
            mapping == null ? "original game prompts" :
            string.Join(", ", PromptMapping.All.Select(b => $"{b}→{XboxLabel(mapping.Get(b))}"));
    }
}
