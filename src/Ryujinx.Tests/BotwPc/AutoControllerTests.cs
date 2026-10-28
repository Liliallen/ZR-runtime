using NUnit.Framework;
using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.Input.HLE;
using System.IO;

namespace Ryujinx.Tests.BotwPc
{
    public class AutoControllerTests
    {
        [Test]
        public void ChoosesPreferredThenXboxThenPhysical()
        {
            string[] pads = ["Steam Virtual Gamepad", "PS5 Controller", "Xbox Series X Controller"];

            Assert.That(AutoController.Choose(pads, ""), Is.EqualTo(2));
            Assert.That(AutoController.Choose(pads, "ps5"), Is.EqualTo(1));
            Assert.That(AutoController.Choose(pads, "missing"), Is.EqualTo(2));
            Assert.That(AutoController.Choose(["Steam Virtual Gamepad", "8BitDo Pro 2"], ""), Is.EqualTo(1));
            Assert.That(AutoController.Choose(["Steam Virtual Gamepad"], ""), Is.EqualTo(0));
            Assert.That(AutoController.Choose([], ""), Is.EqualTo(-1));
        }

        [Test]
        public void NintendoLayoutIsPositional()
        {
            var config = AutoController.Build("0-xbox", "Xbox Series X Controller", new ControllerSettings(), hasMotion: false);

            // SDL names are positional: A = bottom. Switch A sits on the right button like a Pro Controller.
            Assert.That(config.RightJoycon.ButtonA, Is.EqualTo(GamepadInputId.B));
            Assert.That(config.RightJoycon.ButtonB, Is.EqualTo(GamepadInputId.A));
            Assert.That(config.RightJoycon.ButtonX, Is.EqualTo(GamepadInputId.Y));
            Assert.That(config.RightJoycon.ButtonY, Is.EqualTo(GamepadInputId.X));
            Assert.That(config.LeftJoycon.ButtonZl, Is.EqualTo(GamepadInputId.LeftTrigger));
            Assert.That(config.RightJoycon.ButtonZr, Is.EqualTo(GamepadInputId.RightTrigger));
            Assert.That(config.LeftJoycon.ButtonMinus, Is.EqualTo(GamepadInputId.Minus));
            Assert.That(config.RightJoycon.ButtonPlus, Is.EqualTo(GamepadInputId.Plus));
            Assert.That(config.Motion.EnableMotion, Is.False);
            Assert.That(config.Rumble.EnableRumble, Is.True);
        }

        [Test]
        public void XboxLayoutIsLabelTrueAndNotSwappedTwice()
        {
            var settings = new ControllerSettings { Layout = ButtonLayout.Xbox };

            var xbox = AutoController.Build("0-xbox", "Xbox One Controller", settings, hasMotion: false);
            Assert.That(xbox.RightJoycon.ButtonA, Is.EqualTo(GamepadInputId.A));
            Assert.That(xbox.RightJoycon.ButtonB, Is.EqualTo(GamepadInputId.B));

            // Nintendo pads report Nintendo labels: never swapped, whatever the layout.
            var pro = AutoController.Build("0-pro", "Nintendo Switch Pro Controller", new ControllerSettings(), hasMotion: true);
            Assert.That(pro.RightJoycon.ButtonA, Is.EqualTo(GamepadInputId.A));
            Assert.That(pro.Motion.EnableMotion, Is.True);
        }

        [Test]
        public void PromptsFollowTheLastControllerBeforeItConnects()
        {
            var settings = new ControllerSettings { LastController = "Xbox Series X Controller" };

            // No pad connected at game start (no driver, no saved pad): the prompts are made for the last auto-assigned pad.
            var predicted = AutoController.ResolvePrompts([], null, settings, out string reason);
            Assert.That(predicted, Is.Not.Null, reason);
            Assert.That(predicted.Get(SwitchButton.A), Is.EqualTo(Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId.B));
            Assert.That(predicted.Get(SwitchButton.X), Is.EqualTo(Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId.Y));

            // ...and match the automatic config that runs once it connects, so the prompts are not stale.
            var connected = ButtonPrompts.FromConfig(AutoController.Build("0-xbox", settings.LastController, settings, false), settings.Prompts, out _);
            Assert.That(predicted.Key, Is.EqualTo(connected.Key));

            Assert.That(AutoController.ResolvePrompts([], null, new ControllerSettings(), out _), Is.Null);
            Assert.That(AutoController.ResolvePrompts([], null, new ControllerSettings { LastController = "Xbox", Prompts = PromptStyle.Original }, out _), Is.Null);
            Assert.That(AutoController.ResolvePrompts([], null, new ControllerSettings { LastController = "Xbox", AutoAssign = false }, out _), Is.Null);
            Assert.That(AutoController.ResolvePrompts([], null, new ControllerSettings { LastController = "Nintendo Switch Pro Controller" }, out _), Is.Null);
        }

        [Test]
        public void SettingsAreClamped()
        {
            var config = AutoController.Build("0", "Xbox", new ControllerSettings { DeadzoneLeft = 5f, RumbleStrength = -1f, TriggerThreshold = 0f }, false);

            Assert.That(config.DeadzoneLeft, Is.EqualTo(0.9f));
            Assert.That(config.Rumble.StrongRumble, Is.EqualTo(0f));
            Assert.That(config.TriggerThreshold, Is.EqualTo(0.05f));
        }

        [Test]
        public void CameraCurveKeepsDirectionAndEnds()
        {
            var half = new JoystickPosition { Dx = short.MaxValue / 2, Dy = 0 };

            Assert.That(NpadController.ApplyCurve(half, 1f).Dx, Is.EqualTo(half.Dx));
            Assert.That(NpadController.ApplyCurve(half, 2f).Dx, Is.InRange(8190, 8193)); // 0.5^2 = 0.25
            Assert.That(NpadController.ApplyCurve(new JoystickPosition { Dx = 0, Dy = short.MaxValue }, 2f).Dy, Is.EqualTo(short.MaxValue));
            Assert.That(NpadController.ApplyCurve(new JoystickPosition { Dx = -16384, Dy = 0 }, 2f).Dx, Is.LessThan(0));
        }

        [Test]
        public void SettingsRoundTripAndSurviveCorruptFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "botwpc-settings-" + Path.GetRandomFileName());
            string path = Path.Combine(dir, "settings.json");

            try
            {
                var s = new BotwPcSettings();
                s.Controller.Layout = ButtonLayout.Xbox;
                s.Mods.SafeMode = true;
                s.Save(path);

                Assert.That(File.ReadAllText(path), Does.Contain("\"layout\": \"Xbox\""));
                var loaded = BotwPcSettings.Load(path);
                Assert.That(loaded.Controller.Layout, Is.EqualTo(ButtonLayout.Xbox));
                Assert.That(loaded.Mods.SafeMode, Is.True);

                File.WriteAllText(path, "{ not json");
                Assert.That(BotwPcSettings.Load(path).Controller.AutoAssign, Is.True);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
