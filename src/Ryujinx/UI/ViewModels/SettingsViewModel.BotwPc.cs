using Ryujinx.Ava.Common.Locale;
using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Ava.UI.ViewModels
{
    /// <summary>BOTW-PC settings page: frame-rate presets over the existing VSync settings, and BotwPcSettings.</summary>
    public partial class SettingsViewModel
    {
        // Edited copy, written to <data>/botw/settings.json when the settings window saves.
        public BotwPcSettings BotwPc { get; } = BotwPcSettings.Load(BotwPcSettings.FilePath);

        // BotW presents every 2nd emulated VBlank, so a custom refresh of 120/180/240 Hz gives 60/90/120 FPS.
        private static readonly int[] _botwRefresh = [0, 120, 180, 240, 0];

        /// <summary>0 = stock 30 (Switch VSync), 1/2/3 = 60/90/120 FPS, 4 = uncapped, -1 = other custom value.</summary>
        public int BotwFrameRate
        {
            get => VSyncMode switch
            {
                VSyncMode.Switch => 0,
                VSyncMode.Unbounded => 4,
                _ => System.Array.IndexOf(_botwRefresh, CustomVSyncInterval, 1, 3),
            };
            set
            {
                switch (value)
                {
                    case 0:
                        EnableCustomVSyncInterval = false;
                        VSyncMode = VSyncMode.Switch;
                        break;
                    case 4:
                        EnableCustomVSyncInterval = false;
                        VSyncMode = VSyncMode.Unbounded;
                        break;
                    case >= 1 and <= 3:
                        CustomVSyncInterval = _botwRefresh[value];
                        EnableCustomVSyncInterval = true;
                        break;
                }

                OnPropertyChanged();
                OnPropertiesChanged(nameof(EnableCustomVSyncInterval), nameof(CustomVSyncInterval));
            }
        }

        public int BotwLayoutIndex
        {
            get => (int)BotwPc.Controller.Layout;
            set => BotwPc.Controller.Layout = (ButtonLayout)value;
        }

        public int BotwPromptStyleIndex
        {
            get => (int)BotwPc.Controller.Prompts;
            set => BotwPc.Controller.Prompts = (PromptStyle)value;
        }

        public int BotwDlssIndex
        {
            get => (int)BotwPc.Graphics.Dlss;
            set => BotwPc.Graphics.Dlss = (DlssMode)value;
        }

        public int BotwPromptPriorityIndex
        {
            get => (int)BotwPc.Controller.PromptPriority;
            set => BotwPc.Controller.PromptPriority = (PromptPriority)value;
        }

        public int BotwMotionSourceIndex
        {
            get => (int)BotwPc.Motion.Source;
            set => BotwPc.Motion.Source = (MotionSource)value;
        }

        public float BotwHoldThresholdSeconds
        {
            get => BotwPc.Motion.HoldThresholdMs / 1000f;
            set => BotwPc.Motion.HoldThresholdMs = (int)(value * 1000);
        }

        private List<BotwBindingRow> _botwBindings;

        /// <summary>One row per Switch button; "Layout default" removes the custom binding.</summary>
        public List<BotwBindingRow> BotwBindings => _botwBindings ??=
            [.. System.Enum.GetValues<SwitchButton>().Select(b => new BotwBindingRow(BotwPc.Controller.Bindings, b))];

        public string BotwPromptRebuildStatus { get; private set; }

        public void RebuildBotwPrompts()
        {
            Ryujinx.HLE.BotwPc.Prompts.PromptOverlay.Invalidate(System.IO.Path.Combine(BotwPcSettings.DirectoryPath, "prompts"));
            BotwPromptRebuildStatus = LocaleManager.Instance[LocaleKeys.BotwSettingsPromptsRebuildDone];
            OnPropertyChanged(nameof(BotwPromptRebuildStatus));
        }

        public void ResetBotwBindings()
        {
            BotwPc.Controller.Bindings.Clear();
            _botwBindings = null;
            OnPropertyChanged(nameof(BotwBindings));
        }
    }

    /// <summary>A custom binding row of Settings > BotW-PC: Switch button → physical Xbox input.</summary>
    public class BotwBindingRow
    {
        /// <summary>SDL input names stored in settings.json, with the Xbox label shown in the list.</summary>
        public static readonly (string Sdl, string Label)[] Inputs =
        [
            ("", "Layout default"), ("A", "A (bottom)"), ("B", "B (right)"), ("X", "X (left)"), ("Y", "Y (top)"),
            ("LeftShoulder", "LB"), ("RightShoulder", "RB"), ("LeftTrigger", "LT"), ("RightTrigger", "RT"),
            ("Back", "View"), ("Start", "Menu"), ("LeftStick", "LS click"), ("RightStick", "RS click"),
            ("DpadUp", "D-pad Up"), ("DpadDown", "D-pad Down"), ("DpadLeft", "D-pad Left"), ("DpadRight", "D-pad Right"),
            ("Misc1", "Share"), ("Paddle1", "Paddle 1"), ("Paddle2", "Paddle 2"), ("Paddle3", "Paddle 3"), ("Paddle4", "Paddle 4"),
            ("Guide", "Guide"), ("Unbound", "Unbound"),
        ];

        public static List<string> Labels { get; } = [.. Inputs.Select(i => i.Label)];

        private readonly Dictionary<string, string> _bindings;
        private readonly SwitchButton _button;

        public BotwBindingRow(Dictionary<string, string> bindings, SwitchButton button)
        {
            _bindings = bindings;
            _button = button;
        }

        public string Name => $"Switch {_button}";

        public int Selected
        {
            get
            {
                if (!_bindings.TryGetValue(_button.ToString(), out string sdl))
                {
                    return 0;
                }

                int index = System.Array.FindIndex(Inputs, i => string.Equals(i.Sdl, sdl, System.StringComparison.OrdinalIgnoreCase) ||
                                                                (sdl is "Minus" && i.Sdl == "Back") || (sdl is "Plus" && i.Sdl == "Start"));
                return index < 0 ? 0 : index;
            }
            set
            {
                if (value <= 0 || value >= Inputs.Length)
                {
                    _bindings.Remove(_button.ToString());
                }
                else
                {
                    _bindings[_button.ToString()] = Inputs[value].Sdl;
                }
            }
        }
    }
}
