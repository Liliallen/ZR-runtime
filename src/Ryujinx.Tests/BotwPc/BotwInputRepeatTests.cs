using NUnit.Framework;
using Ryujinx.HLE.BotwPc;
using Ryujinx.Tests.Cpu;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.BotwPc
{
    /// <summary>Runs BotwInputRepeat's patched loop on the ARM64 JIT, called once per simulated game frame.</summary>
    public class BotwInputRepeatTests : CpuTest
    {
        private const ulong Vfr = 0x400, Controller = 0x800;
        private const int Button = 3;

        /// <summary>Frames (1 = first held frame) on which "repeat" fires for a button held <paramref name="frames"/> frames (≤ 63).</summary>
        private List<int> RepeatFrames(float vfrRatio, byte delay, byte pulse, int frames, bool vfrPresent = true)
        {
            // The JIT translator is single-use per Execute, so a small driver calls the loop once per frame and collects
            // the frames that set the button's repeat bit into x24.
            uint[] driver =
            [
                0xAA1E03F7,                                         // mov  x23, x30
                0xAA1F03F5,                                         // mov  x21, xzr        frame - 1
                0xAA1F03F8,                                         // mov  x24, xzr        result mask
                0xD2800013u | (uint)((DataBaseAddress + Controller) << 5), // loop: movz x19, #controller
                0x52800000u | (1u << Button) << 5 | 11,             // movz w11, #(1 << Button)  mPadHold
                0x9400000B,                                         // bl   body
                0x721D011F,                                         // tst  w8, #8          repeat bit of Button
                0x54000080,                                         // b.eq skip
                0xD2800020,                                         // mov  x0, #1
                0x9AD52000,                                         // lsl  x0, x0, x21
                0xAA000318,                                         // orr  x24, x24, x0
                0x910006B5,                                         // skip: add x21, x21, #1
                0xF10002BFu | (uint)frames << 10,                   // cmp  x21, #frames
                0x54FFFEcB,                                         // b.lt loop
                0xAA1703FE,                                         // mov  x30, x23
                0xD65F03C0,                                         // ret
            ];

            uint[] body = (uint[])BotwInputRepeat.Patch160.Clone();
            // movz x17, #data in place of adrp x17, VFR::sInstance page (the test data page stands in for main's .bss).
            body[3] = 0xD2800011u | (uint)((DataBaseAddress & 0xffff) << 5);

            foreach (uint op in driver)
            {
                Opcode(op);
            }

            foreach (uint op in body)
            {
                Opcode(op);
            }

            Opcode(0xD65F03C0); // RET

            SetWorkingMemory(0x170, BitConverter.GetBytes(vfrPresent ? DataBaseAddress + Vfr : 0));
            SetWorkingMemory(Vfr + 0x3c, BitConverter.GetBytes(vfrRatio));
            SetWorkingMemory(Controller + 0x100, BitConverter.GetBytes(32));

            for (int i = 0; i < 32; i++)
            {
                SetWorkingMemory(Controller + 0xb0 + (ulong)i, delay);
                SetWorkingMemory(Controller + 0xd0 + (ulong)i, pulse);
            }

            SetContext();
            ExecuteOpcodes(runUnicorn: false);

            ulong mask = GetContext().GetX(24);
            List<int> repeats = [];

            for (int frame = 1; frame <= frames; frame++)
            {
                if ((mask & (1ul << (frame - 1))) != 0)
                {
                    repeats.Add(frame);
                }
            }

            return repeats;
        }

        [Test]
        public void ConsoleTimingAt30Fps()
        {
            // Delay 6, pulse 2 (values BotW sets for its menu cursor): first repeat on the 7th held frame, then every 2nd.
            Assert.That(RepeatFrames(1f, 6, 2, 15), Is.EqualTo(new[] { 7, 9, 11, 13, 15 }));
        }

        [Test]
        public void SameRealTimeAt90Fps()
        {
            // Ratio 1/3: delay and pulse in frames are tripled, i.e. the same 0.2 s / 0.067 s as at 30 FPS.
            Assert.That(RepeatFrames(1f / 3, 6, 2, 45), Is.EqualTo(new[] { 19, 25, 31, 37, 43 }));
        }

        // One RepeatFrames call per test: the test JIT translator is disposed after its first Execute.
        [Test]
        public void SlowerThan30FpsKeepsConsoleBehaviour()
        {
            Assert.That(RepeatFrames(1.5f, 6, 2, 15), Is.EqualTo(new[] { 7, 9, 11, 13, 15 }), "20 FPS");
        }

        [Test]
        public void MissingVfrKeepsConsoleBehaviour()
        {
            Assert.That(RepeatFrames(1f / 3, 6, 2, 15, vfrPresent: false), Is.EqualTo(new[] { 7, 9, 11, 13, 15 }));
        }

        [Test]
        public void PulseZeroNeverRepeats()
        {
            Assert.That(RepeatFrames(1f / 4, 6, 0, 60), Is.Empty);
        }
    }
}
