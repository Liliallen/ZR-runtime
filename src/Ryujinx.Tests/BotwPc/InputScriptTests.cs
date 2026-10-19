using NUnit.Framework;
using Ryujinx.HLE.BotwPc;
using Ryujinx.HLE.HOS.Services.Hid;
using System;

namespace Ryujinx.Tests.BotwPc
{
    public class InputScriptTests
    {
        private const string Script = """
            # start dur buttons [lx ly rx ry]
            8    0.25 A
            10   2    -        0 32767 0 0
            11   1    zr+B    # overlaps the stick step
            """;

        [Test]
        public void AppliesButtonsAndSticksInsideTheirWindows()
        {
            InputScript script = InputScript.Parse(Script);
            Assert.That(script.Steps, Has.Count.EqualTo(3));

            GamepadInput input = default;
            script.Apply(ref input, 7.9);
            Assert.That(input.Buttons, Is.EqualTo((ControllerKeys)0));

            script.Apply(ref input, 8.1);
            Assert.That(input.Buttons, Is.EqualTo(ControllerKeys.A));

            input = default;
            script.Apply(ref input, 8.25); // end is exclusive
            Assert.That(input.Buttons, Is.EqualTo((ControllerKeys)0));

            input = default;
            script.Apply(ref input, 11.5);
            Assert.That(input.Buttons, Is.EqualTo(ControllerKeys.Zr | ControllerKeys.B));
            Assert.That(input.LStick.Dy, Is.EqualTo(32767));
        }

        [Test]
        public void ParsesMotionSteps()
        {
            InputScript script = InputScript.Parse("5 1.5 motion 1 -0.5 2\n6 1 A");
            Assert.That(script.Steps, Has.Count.EqualTo(2));
            Assert.That(script.Steps[0].Motion, Is.EqualTo(new System.Numerics.Vector3(1, -0.5f, 1)), "rates are clamped to -1..1");
            Assert.That(script.Steps[0].End, Is.EqualTo(6.5));

            GamepadInput input = default;
            script.Apply(ref input, 5.5);
            Assert.That(input.Buttons, Is.EqualTo((ControllerKeys)0), "motion steps press nothing");
            Assert.That(script.MotionRate(), Is.EqualTo(System.Numerics.Vector3.Zero), "no motion before the script clock starts");
        }

        [Test]
        public void RejectsMalformedLines()
        {
            Assert.Throws<FormatException>(() => InputScript.Parse("1 2"));
            Assert.Throws<ArgumentException>(() => InputScript.Parse("1 2 NotAButton"));
        }
    }
}
