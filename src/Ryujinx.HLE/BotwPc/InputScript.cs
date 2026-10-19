using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Timed controller input for reproducible benchmark runs, enabled with BOTWPC_INPUT_SCRIPT=&lt;file&gt;.
    /// Line format: <c>start_s duration_s buttons [lx ly rx ry]</c>; '#' starts a comment.
    /// Buttons are <see cref="ControllerKeys"/> names joined with '+', or '-' for none.
    /// Sticks range from -32767 to 32767 and replace the real stick values while the step is active.
    /// <c>start_s duration_s motion pitch roll yaw</c> turns the virtual motion controller (fractions of the configured
    /// rate, -1..1: pitch forward, roll right, yaw left positive) when virtual motion feeds the game.
    /// <c>start_s duration_s pad A+Down</c> holds physical controller buttons (<see cref="Common.BotwPc.Overlay.PadButtons"/>,
    /// Xbox positions) as seen by the overlay shortcut and menu; <c>start_s duration_s key Insert</c> holds keyboard keys
    /// for the overlay (names of Ryujinx.Input.Key); <c>start_s duration_s mouse x y [click]</c> places the mouse for the
    /// overlay (x, y as fractions of the render area, click = left button held). Overlay tests only; the game does not see them.
    /// Time starts at the first input poll, i.e. when the game starts running.
    /// </summary>
    public sealed class InputScript
    {
        public readonly record struct Step(double Start, double End, ControllerKeys Buttons, JoystickPosition? LStick, JoystickPosition? RStick, System.Numerics.Vector3? Motion = null,
            Common.BotwPc.Overlay.PadButtons Pad = 0, string[] Keys = null, (float X, float Y, bool Click)? Mouse = null);

        public IReadOnlyList<Step> Steps { get; }

        private readonly Stopwatch _clock = new();

        private InputScript(List<Step> steps)
        {
            Steps = steps;
        }

        public static InputScript FromEnvironment()
        {
            string path = Environment.GetEnvironmentVariable("BOTWPC_INPUT_SCRIPT");

            return string.IsNullOrEmpty(path) ? null : Parse(File.ReadAllText(path));
        }

        public static InputScript Parse(string text)
        {
            List<Step> steps = [];
            int lineNumber = 0;

            foreach (string rawLine in text.Split('\n'))
            {
                lineNumber++;
                string line = rawLine.Split('#')[0].Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 6 && parts[2].Equals("motion", StringComparison.OrdinalIgnoreCase))
                {
                    float Rate(string v) => Math.Clamp(float.Parse(v, CultureInfo.InvariantCulture), -1f, 1f);
                    double motionStart = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    steps.Add(new Step(motionStart, motionStart + double.Parse(parts[1], CultureInfo.InvariantCulture), 0, null, null,
                        new System.Numerics.Vector3(Rate(parts[3]), Rate(parts[4]), Rate(parts[5]))));
                    continue;
                }

                if (parts.Length == 4 && parts[2].Equals("pad", StringComparison.OrdinalIgnoreCase))
                {
                    double padStart = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    Common.BotwPc.Overlay.PadButtons pad = 0;

                    foreach (string name in parts[3].Split('+'))
                    {
                        pad |= Enum.Parse<Common.BotwPc.Overlay.PadButtons>(name, ignoreCase: true);
                    }

                    steps.Add(new Step(padStart, padStart + double.Parse(parts[1], CultureInfo.InvariantCulture), 0, null, null, Pad: pad));
                    continue;
                }

                if (parts.Length is 5 or 6 && parts[2].Equals("mouse", StringComparison.OrdinalIgnoreCase))
                {
                    double mouseStart = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    steps.Add(new Step(mouseStart, mouseStart + double.Parse(parts[1], CultureInfo.InvariantCulture), 0, null, null,
                        Mouse: (float.Parse(parts[3], CultureInfo.InvariantCulture), float.Parse(parts[4], CultureInfo.InvariantCulture),
                                parts.Length == 6 && parts[5].Equals("click", StringComparison.OrdinalIgnoreCase))));
                    continue;
                }

                if (parts.Length == 4 && parts[2].Equals("key", StringComparison.OrdinalIgnoreCase))
                {
                    double keyStart = double.Parse(parts[0], CultureInfo.InvariantCulture);
                    steps.Add(new Step(keyStart, keyStart + double.Parse(parts[1], CultureInfo.InvariantCulture), 0, null, null, Keys: parts[3].Split('+')));
                    continue;
                }

                if (parts.Length is not 3 and not 7)
                {
                    throw new FormatException($"Input script line {lineNumber}: expected 3 or 7 fields, got {parts.Length}.");
                }

                double start = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double duration = double.Parse(parts[1], CultureInfo.InvariantCulture);
                ControllerKeys buttons = 0;

                if (parts[2] != "-")
                {
                    foreach (string name in parts[2].Split('+'))
                    {
                        buttons |= Enum.Parse<ControllerKeys>(name, ignoreCase: true);
                    }
                }

                JoystickPosition? left = null, right = null;

                if (parts.Length == 7)
                {
                    left = new JoystickPosition { Dx = ParseAxis(parts[3]), Dy = ParseAxis(parts[4]) };
                    right = new JoystickPosition { Dx = ParseAxis(parts[5]), Dy = ParseAxis(parts[6]) };
                }

                steps.Add(new Step(start, start + duration, buttons, left, right));
            }

            return new InputScript(steps);
        }

        private static int ParseAxis(string value)
        {
            return Math.Clamp(int.Parse(value, CultureInfo.InvariantCulture), -32767, 32767);
        }

        public void Apply(ref GamepadInput input)
        {
            if (!_clock.IsRunning)
            {
                _clock.Start();

                // The script's time 0 on the Stopwatch clock (framelog.csv, BOTWPC_CAMERA_LOG), for aligning runs.
                Common.Logging.Logger.Info?.Print(Common.Logging.LogClass.Application,
                    $"BOTW-PC input script clock started at {Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency:F6} s");
            }

            Apply(ref input, _clock.Elapsed.TotalSeconds);
        }

        /// <summary>Scripted virtual-motion rate (pitch, roll, yaw; -1..1) at the script's current time.</summary>
        public System.Numerics.Vector3 MotionRate()
        {
            System.Numerics.Vector3 rate = System.Numerics.Vector3.Zero;
            double time = _clock.Elapsed.TotalSeconds;

            foreach (Step step in Steps)
            {
                if (step.Motion.HasValue && _clock.IsRunning && time >= step.Start && time < step.End)
                {
                    rate += step.Motion.Value;
                }
            }

            return rate;
        }

        /// <summary>Scripted physical buttons for the overlay at the script's current time.</summary>
        public Common.BotwPc.Overlay.PadButtons PadButtons()
        {
            Common.BotwPc.Overlay.PadButtons held = 0;
            double time = _clock.Elapsed.TotalSeconds;

            foreach (Step step in Steps)
            {
                if (_clock.IsRunning && time >= step.Start && time < step.End)
                {
                    held |= step.Pad;
                }
            }

            return held;
        }

        /// <summary>Scripted mouse position (fractions of the render area, 0..1) and left button for the overlay, if a step is active.</summary>
        public (float X, float Y, bool Click)? Mouse()
        {
            double time = _clock.Elapsed.TotalSeconds;

            foreach (Step step in Steps)
            {
                if (step.Mouse != null && _clock.IsRunning && time >= step.Start && time < step.End)
                {
                    return step.Mouse;
                }
            }

            return null;
        }

        /// <summary>Scripted keyboard keys for the overlay at the script's current time.</summary>
        public IEnumerable<string> Keys()
        {
            double time = _clock.Elapsed.TotalSeconds;

            foreach (Step step in Steps)
            {
                if (step.Keys != null && _clock.IsRunning && time >= step.Start && time < step.End)
                {
                    foreach (string key in step.Keys)
                    {
                        yield return key;
                    }
                }
            }
        }

        public void Apply(ref GamepadInput input, double time)
        {
            foreach (Step step in Steps)
            {
                if (time < step.Start || time >= step.End)
                {
                    continue;
                }

                input.Buttons |= step.Buttons;

                if (step.LStick.HasValue)
                {
                    input.LStick = step.LStick.Value;
                    input.RStick = step.RStick.Value;
                }
            }
        }
    }
}
