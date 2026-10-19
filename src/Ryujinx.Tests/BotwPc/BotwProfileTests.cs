using NUnit.Framework;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.BotwPc;
using Ryujinx.Memory;

namespace Ryujinx.Tests.BotwPc
{
    public class BotwProfileTests
    {
        private const string Main160 = "8E9978D50BDD20B4C8395A106C27FFDE00000000000000000000000000000000";
        private const string Rtld = "39E10C07096696C9C4E228496C4BC44D6B9E0041000000000000000000000000";

        [Test]
        public void DetectsSupportedBuildAndMainBase()
        {
            BotwProfile.Version v = BotwProfile.Detect(BotwProfile.TitleId, [Rtld, Main160], [0x8500000UL, 0x8504000UL], out ulong mainBase, out _);

            Assert.That(v?.Name, Is.EqualTo("1.6.0"));
            Assert.That(mainBase, Is.EqualTo(0x8504000UL));
        }

        [Test]
        public void RejectsOtherTitlesAndUnknownBuilds()
        {
            Assert.That(BotwProfile.Detect(0x0100000000010000, [Main160], [0x8504000UL], out _, out string reason), Is.Null);
            Assert.That(reason, Is.EqualTo("not Breath of the Wild"));

            Assert.That(BotwProfile.Detect(BotwProfile.TitleId, [Rtld, "CD57B23F" + new string('0', 56)], [0UL, 1UL], out _, out reason), Is.Null);
            Assert.That(reason, Does.StartWith("unsupported BotW build"));
        }

        [TestCase(1 / 30.0, 1.0f)]   // console speed
        [TestCase(1 / 60.0, 0.5f)]   // 60 FPS
        [TestCase(1 / 90.0, 1f / 3)] // 90 FPS
        [TestCase(1 / 120.0, 0.25f)] // 120 FPS
        public void RatioMakesEachFrameAdvanceItsRealDuration(double frameSeconds, float expected)
        {
            float ratio = BotwTiming.ComputeRatio(frameSeconds, interval: 2, baseInterval: 2);
            Assert.That(ratio, Is.EqualTo(expected).Within(1e-6));

            // Game step = ratio * (1 / 30 s) must equal the real frame time.
            Assert.That(ratio / 30.0, Is.EqualTo(frameSeconds).Within(1e-7));
        }

        [Test]
        public void RatioIsClampedToConsoleBehaviour()
        {
            // Below 30 FPS the game slows down like on the console instead of taking bigger steps.
            Assert.That(BotwTiming.ComputeRatio(0.1, 2, 2), Is.EqualTo(1.0f));
            // If the game itself switches to interval 1, its own (lower) ratio is the cap.
            Assert.That(BotwTiming.ComputeRatio(1 / 30.0, 1, 2), Is.EqualTo(0.5f));
            // Absurdly short frames are bounded.
            Assert.That(BotwTiming.ComputeRatio(1e-6, 2, 2), Is.EqualTo(1f / 16));
        }

        [Test]
        public void VfrLayoutCheck()
        {
            Assert.That(BotwTiming.LooksLikeVfr(2, 30, 1f / 30), Is.True);
            Assert.That(BotwTiming.LooksLikeVfr(2, 60, 1f / 60), Is.False);
            Assert.That(BotwTiming.LooksLikeVfr(0, 0, 0), Is.False);
            Assert.That(BotwTiming.LooksLikeVfr(0x3F800000, 30, 1f / 30), Is.False);
        }

        [Test]
        public void SwitchingBackToConsoleTimingRestoresTheGameRatio()
        {
            // VSync mode changed at runtime from a high frame rate back to Switch: the ratio last written must not stay.
            const ulong Main = 0x10000, Vfr = 0x18000;
            using MemoryBlock ram = new(0x10000);
            Ryujinx.Cpu.Jit.MemoryManager memory = new(ram, 1ul << 20);
            memory.Map(Main, 0, 0x10000, MemoryMapFlags.Private);
            BotwProfile.Version version = new("test", 0x100, (0, 0));
            memory.Write(Main + 0x100, Vfr);
            memory.Write(Vfr + BotwTiming.BaseIntervalOffset, 2u);
            memory.Write(Vfr + BotwTiming.FrameRateOffset, 30u);
            memory.Write(Vfr + BotwTiming.FrameTimeOffset, 1f / 30);
            memory.Write(Vfr + BotwTiming.IntervalAOffset, 2u);
            memory.Write(Vfr + BotwTiming.NewIntervalRatioOffset, 1f);

            BotwTiming timing = new(memory, Main, version);
            for (int i = 0; i < 5; i++)
            {
                timing.OnFrameQueued(VSyncMode.Custom);
                System.Threading.Thread.Sleep(5);
            }
            Assert.That(memory.Read<float>(Vfr + BotwTiming.NewIntervalRatioOffset), Is.LessThan(1f));

            timing.OnFrameQueued(VSyncMode.Switch);
            Assert.That(memory.Read<float>(Vfr + BotwTiming.NewIntervalRatioOffset), Is.EqualTo(1f));
        }

        [Test]
        public void UiTimingPatchLoadsTheVfrRatio()
        {
            // Encodings checked with a disassembler: adrp x8, #0x2d1e000; ldr x8, [x8, #0x170]; ldr s0, [x8, #0x3c]; nop x4.
            uint[] patch = BotwUiTiming.BuildPatch(BotwUiTiming.PatchOffset160, BotwUiTiming.VfrInstanceOffset160);
            Assert.That(patch, Is.EqualTo(new uint[] { 0x9000a668, 0xf940b908, 0xbd403d00, 0xd503201f, 0xd503201f, 0xd503201f, 0xd503201f }));
            Assert.That(BotwUiTiming.VfrInstanceOffset160, Is.EqualTo(BotwProfile.Versions["8E9978D50BDD20B4C8395A106C27FFDE"].VfrInstanceOffset));
        }
    }
}
