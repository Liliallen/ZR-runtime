using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Makes BotW's 2D UI animations follow real time at any frame rate (v1.6.0).
    ///
    /// Every frame the game sets the UI step (UI manager +0xb20, read by eui::LayoutEx and passed as "progress" to
    /// eui::Animator::UpdateFrame, frame += speed * progress) from the integer VFR interval: step = interval / 2.
    /// BOTW-PC's timing correction only changes VFR's fractional ratio, so the UI kept advancing a whole
    /// interval per frame (4x too fast at 120 FPS). The patch loads VFR's ratio in use (mIntervalRatioA, +0x3c)
    /// instead. On stock 30/20 FPS that ratio is exactly interval / 2, so console behaviour is unchanged.
    /// Found at runtime: GDB stub breakpoints on eui::Animator::UpdateFrame (main+0x136b000) → its caller's
    /// step getter (main+0x1a35d20) → the only per-frame writer of +0xb20 (main+0x18522a0).
    /// </summary>
    public static class BotwUiTiming
    {
        /// <summary>main+0x18522e8: ldr x8,[x8]; ldr w8,[x8]; cmp w8,#0; csinc w8,w8,wzr,ne; ucvtf s0,w8; fmov s1,#0.5; fmul s0,s0,s1</summary>
        private static readonly uint[] _original160 = [0xf9400108, 0xb9400108, 0x7100011f, 0x1a9f1508, 0x1e230100, 0x1e2c1001, 0x1e210800];

        public const ulong PatchOffset160 = 0x18522e8;
        public const ulong VfrInstanceOffset160 = 0x2d1e170;
        public const int IntervalRatioOffset = 0x3c;

        /// <summary>adrp x8, VFR::sInstance page; ldr x8, [x8, #lo]; ldr s0, [x8, #0x3c]; nop x4.</summary>
        public static uint[] BuildPatch(ulong patchOffset, ulong vfrInstanceOffset)
        {
            long pages = (long)(vfrInstanceOffset & ~0xfffUL) / 0x1000 - (long)(patchOffset & ~0xfffUL) / 0x1000;
            uint adrp = 0x90000000u | (uint)((pages & 3) << 29) | (uint)(((pages >> 2) & 0x7ffff) << 5) | 8u;
            uint ldrX = 0xf9400000u | (uint)((vfrInstanceOffset & 0xfff) / 8) << 10 | 8u << 5 | 8u;
            uint ldrS = 0xbd400000u | (uint)(IntervalRatioOffset / 4) << 10 | 8u << 5;
            const uint Nop = 0xd503201f;

            return [adrp, ldrX, ldrS, Nop, Nop, Nop, Nop];
        }

        /// <summary>Applies the patch to loaded 1.6.0 code if the original instructions are found; returns whether it did.</summary>
        public static bool Apply(IVirtualMemoryManager memory, ulong mainBase, BotwProfile.Version version)
        {
            if (version.Name != "1.6.0" || Environment.GetEnvironmentVariable("BOTWPC_UI_TIMING") == "0")
            {
                return false;
            }

            ulong address = mainBase + PatchOffset160;

            for (int i = 0; i < _original160.Length; i++)
            {
                if (memory.Read<uint>(address + (ulong)(i * 4)) != _original160[i])
                {
                    Logger.Warning?.Print(LogClass.Loader, "BOTW-PC UI timing: unexpected code (an ExeFS mod changed it?); UI animations stay frame-based.");
                    return false;
                }
            }

            uint[] patch = BuildPatch(PatchOffset160, VfrInstanceOffset160);

            for (int i = 0; i < patch.Length; i++)
            {
                memory.Write(address + (ulong)(i * 4), patch[i]);
            }

            Logger.Info?.Print(LogClass.Loader, "BOTW-PC UI timing: UI animation step follows the VFR frame-time ratio.");
            return true;
        }
    }
}
