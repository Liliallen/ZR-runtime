using NUnit.Framework;
using Ryujinx.Common.BotwPc;
using Ryujinx.Input;
using Ryujinx.Input.Motion;
using System;
using System.Numerics;

namespace Ryujinx.Tests.BotwPc
{
    public class VirtualMotionTests
    {
        private static KeyboardStateSnapshot Keys(params Key[] pressed)
        {
            bool[] state = new bool[(int)Key.Count];

            foreach (Key key in pressed)
            {
                state[(int)key] = true;
            }

            return new KeyboardStateSnapshot(state);
        }

        private static GamepadStateSnapshot Pad(float rx = 0, float ry = 0, params GamepadButtonInputId[] pressed)
        {
            GamepadStateSnapshot state = default;

            foreach (GamepadButtonInputId button in pressed)
            {
                state.SetPressed(button, true);
            }

            state.SetStick(StickInputId.Right, rx, ry);

            return state;
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, float tolerance = 1e-3f)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tolerance), $"{actual} != {expected}");
        }

        [Test]
        public void GravityFollowsOrientation()
        {
            VirtualMotion m = new();
            AssertVector(m.Accelerometer, Vector3.UnitY); // flat, face up: +1 G out of the face

            // Pitch forward 90° (far edge down): the far end (-Z) points at the ground, so gravity reads +1 G on +Z.
            m.Integrate(new Vector3(-90, 0, 0), 1f);
            AssertVector(m.Accelerometer, Vector3.UnitZ);
            Assert.That(m.EulerDegrees.X, Is.EqualTo(90).Within(0.1));

            m.Recenter();
            // Roll right 30°: the right edge (+X) points partly down, so "up" has a negative X component.
            m.Integrate(new Vector3(0, 0, -30), 1f);
            AssertVector(m.Accelerometer, new Vector3(-MathF.Sin(MathF.PI / 6), MathF.Cos(MathF.PI / 6), 0));
            Assert.That(m.EulerDegrees.Y, Is.EqualTo(30).Within(0.1));
            Assert.That(m.Accelerometer.Length(), Is.EqualTo(1).Within(1e-4));
        }

        [Test]
        public void IntegrationIsFrameRateIndependent()
        {
            Quaternion Run(int fps)
            {
                VirtualMotion m = new();
                Vector3 rate = new(-40, 25, -15); // combined pitch, yaw and roll

                for (int i = 0; i < fps; i++)
                {
                    m.Integrate(rate, 1f / fps);
                }

                return m.Orientation;
            }

            Quaternion at30 = Run(30);

            foreach (int fps in new[] { 60, 90, 120 })
            {
                Quaternion q = Run(fps);
                Assert.That(MathF.Abs(Quaternion.Dot(q, at30)), Is.GreaterThan(0.99999f), $"{fps} FPS");
            }
        }

        [Test]
        public void KeyboardTogglesRotatesHoldsAndRecenters()
        {
            VirtualMotionController c = new() { InUse = true };
            MotionSettings s = new() { RateDegreesPerSecond = 90 };

            c.Update(Keys(Key.Up), null, false, false, s, 0.0);
            c.Update(Keys(Key.Up), null, false, false, s, 0.05);
            Assert.That(c.KeyboardEngaged, Is.False);
            Assert.That(c.Motion.EulerDegrees.X, Is.EqualTo(0).Within(1e-3), "keys do nothing until F8 turns motion on");

            c.Update(Keys(Key.F8), null, false, false, s, 0.1);
            Assert.That(c.KeyboardEngaged, Is.True);
            Assert.That(c.SuppressedKeys, Does.Contain(Key.Up).And.Contain(Key.Q));

            // Up for 0.5 s at 90°/s = 45° forward; Shift quarters the speed.
            for (double t = 0.15; t <= 0.651; t += 0.05)
            {
                c.Update(Keys(Key.Up), null, false, false, s, t);
            }

            Assert.That(c.Motion.EulerDegrees.X, Is.EqualTo(45).Within(5));
            Assert.That(c.Motion.Gyroscope.X, Is.LessThan(0));

            c.Update(Keys(), null, false, false, s, 0.7);
            float held = c.Motion.EulerDegrees.X;
            c.Update(Keys(), null, false, false, s, 1.7);
            Assert.That(c.Motion.EulerDegrees.X, Is.EqualTo(held).Within(1e-3), "orientation is kept after release");
            AssertVector(c.Motion.Gyroscope, Vector3.Zero);

            c.Update(Keys(Key.R), null, false, false, s, 1.75);
            AssertVector(c.Motion.Accelerometer, Vector3.UnitY);

            c.Update(Keys(Key.F8), null, false, false, s, 1.8);
            Assert.That(c.KeyboardEngaged, Is.False);
            Assert.That(c.SuppressedKeys, Does.Not.Contain(Key.Up));
        }

        [Test]
        public void ViewTapStillReachesTheGame()
        {
            VirtualMotionController c = new() { InUse = true };
            MotionSettings s = new() { HoldThresholdMs = 300 };

            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 0.0);
            Assert.That(c.SuppressedButtons, Does.Contain(GamepadButtonInputId.Minus), "held View is undecided, not sent");
            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 0.1);
            c.Update(null, Pad(), false, false, s, 0.15);
            Assert.That(c.InjectedButtons, Does.Contain(GamepadButtonInputId.Minus), "a short tap is replayed as View");
            Assert.That(c.ControllerEngaged, Is.False);
            c.Update(null, Pad(), false, false, s, 0.4);
            Assert.That(c.InjectedButtons, Is.Empty);
        }

        [Test]
        public void ViewHoldRotatesAndHidesMotionInputs()
        {
            VirtualMotionController c = new() { InUse = true };
            MotionSettings s = new() { HoldThresholdMs = 300, RateDegreesPerSecond = 90, StickDeadzone = 0.1f };

            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 0.0);
            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 0.35);
            Assert.That(c.ControllerEngaged, Is.True);

            // Full stick right + RB for 0.5 s: roll right 45°, yaw right; the game sees neither.
            for (double t = 0.4; t <= 0.851; t += 0.05)
            {
                c.Update(null, Pad(1f, 0f, GamepadButtonInputId.Minus, GamepadButtonInputId.RightShoulder), false, false, s, t);
                Assert.That(c.SuppressRightStick, Is.True);
                Assert.That(c.SuppressedButtons, Does.Contain(GamepadButtonInputId.RightShoulder).And.Contain(GamepadButtonInputId.Minus));
            }

            Assert.That(c.Motion.EulerDegrees.Y, Is.EqualTo(45).Within(6));
            Assert.That(c.Motion.Gyroscope.Y, Is.LessThan(0), "RB yaws right");

            // Release View while still holding RB and the stick: no stray RB press or camera turn reaches the game.
            c.Update(null, Pad(1f, 0f, GamepadButtonInputId.RightShoulder), false, false, s, 0.9);
            Assert.That(c.ControllerEngaged, Is.False);
            Assert.That(c.InjectedButtons, Is.Empty, "a long hold is not a View tap");
            Assert.That(c.SuppressedButtons, Does.Contain(GamepadButtonInputId.RightShoulder));
            Assert.That(c.SuppressRightStick, Is.True);
            float kept = c.Motion.EulerDegrees.Y;

            c.Update(null, Pad(), false, false, s, 1.0);
            Assert.That(c.SuppressedButtons, Is.Empty);
            Assert.That(c.SuppressRightStick, Is.False);
            Assert.That(c.Motion.EulerDegrees.Y, Is.EqualTo(kept).Within(1e-3));

            // R3 while in motion mode recentres.
            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 2.0);
            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 2.4);
            c.Update(null, Pad(0, 0, GamepadButtonInputId.Minus, GamepadButtonInputId.RightStick), false, false, s, 2.45);
            AssertVector(c.Motion.Accelerometer, Vector3.UnitY);
        }

        [Test]
        public void ToggleModeAndFineAdjustment()
        {
            VirtualMotionController c = new() { InUse = true };
            MotionSettings s = new() { HoldThresholdMs = 200, ControllerToggle = true, RateDegreesPerSecond = 100, FineFactor = 0.25f, StickDeadzone = 0f };

            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 0.0);
            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 0.25);
            c.Update(null, Pad(), false, false, s, 0.3);
            Assert.That(c.ControllerEngaged, Is.True, "stays on after release in toggle mode");

            c.Update(null, Pad(0f, 1f, GamepadButtonInputId.LeftTrigger), false, false, s, 0.35);
            Assert.That(c.Motion.Gyroscope.X, Is.EqualTo(-25).Within(0.01), "stick up = pitch forward at the fine rate");

            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 1.0);
            c.Update(null, Pad(pressed: GamepadButtonInputId.Minus), false, false, s, 1.25);
            Assert.That(c.ControllerEngaged, Is.False);
        }

        [Test]
        public void SuppliedOrientationMatchesRyujinxsSensorFilter()
        {
            // Feed the virtual sensor data through Ryujinx's own filter (as a real pad's data would go) and check that its
            // orientation agrees with the one virtual motion supplies directly: same axes, same conventions.
            VirtualMotion m = new();
            MotionInput filter = new();
            const int Hz = 240;
            ulong micros = 1;

            for (int i = 0; i < 3 * Hz; i++)
            {
                Vector3 rate = i < Hz ? new Vector3(-60, 0, 0) : i < 2 * Hz ? new Vector3(0, 0, -45) : new Vector3(20, 30, 25);
                m.Integrate(rate, 1f / Hz);
                filter.Update(VirtualMotion.ToFilterAxes(m.Accelerometer), VirtualMotion.ToFilterAxes(m.Gyroscope), micros, 100, 0f);
                micros += 1_000_000 / Hz;
            }

            Matrix4x4 expected = filter.GetOrientation();
            filter.OrientationOverride = m.FilterOrientation;
            Matrix4x4 actual = filter.GetOrientation();

            Assert.That(MathF.Abs(expected.M11 - actual.M11) + MathF.Abs(expected.M12 - actual.M12) + MathF.Abs(expected.M13 - actual.M13) +
                        MathF.Abs(expected.M21 - actual.M21) + MathF.Abs(expected.M22 - actual.M22) + MathF.Abs(expected.M23 - actual.M23) +
                        MathF.Abs(expected.M31 - actual.M31) + MathF.Abs(expected.M32 - actual.M32) + MathF.Abs(expected.M33 - actual.M33),
                Is.LessThan(0.15f), $"filter {expected} vs supplied {actual}");
        }

        [Test]
        public void NothingHappensWhenNotInUse()
        {
            VirtualMotionController c = new() { InUse = false };
            MotionSettings s = new();

            c.Update(Keys(Key.F8), null, false, false, s, 0);
            c.Update(Keys(Key.Up), null, false, false, s, 0.5);
            AssertVector(c.Motion.Gyroscope, Vector3.Zero);
        }
    }
}
