using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Makes held-button repeat (menu cursors, D-pad lists) run at the console's speed at any frame rate (v1.6.0).
    ///
    /// sead::ControllerBase::updateDerivativeParams_ (main+0x126e3b0) counts how many frames each button has been held
    /// (+0x30, u32[32]) and fires "repeat" when the count reaches the button's delay (+0xb0, u8[32]) and then every
    /// pulse frames (+0xd0, u8[32]). The counts are per frame, so at 90 FPS a held D-pad repeated 3x faster. The patch
    /// rewrites the loop (main+0x126e490..0x126e524, same 38 instructions) to compare against delay * k and pulse * k,
    /// k = round(1 / VFR ratio in use) (VFR +0x3c, 1 at 30 FPS, 1/3 at 90 FPS), at least 1. Hold counts still advance
    /// once per frame, so everything else that reads them is unchanged; at 30/20 FPS k = 1 (console behaviour).
    /// Located by the field layout of open-ead/sead's ControllerBase (delays/pulses strb/ldrb at +0xb0/+0xd0).
    /// </summary>
    public static class BotwInputRepeat
    {
        public const ulong PatchOffset160 = 0x126e490;

        /// <summary>The original loop: ldr w8,[x19,#0x100] … b.lt (repeat check per pad bit, frame-based).</summary>
        private static readonly uint[] _original160 =
        [
            0xb9410268, 0x7100051f, 0xb901166b, 0xb900127f, 0x5400044b, 0x2a1f03e8, 0xaa1f03e9, 0x320003ea, 0x14000002, 0xb941166b,
            0x1ac9214c, 0x6a0c017f, 0x54000180, 0x8b09026e, 0x394341cd, 0x8b090a6b, 0x340001ed, 0xb940316f, 0x3942c1ce, 0x6b0e01ee,
            0x540000e1, 0x2a0c0108, 0xb9001268, 0x14000008, 0x8b090a6b, 0xb900317f, 0x14000008, 0x54000089, 0x1acd09cf, 0x1b0db9ed,
            0x34fffeed, 0xb940316c, 0x1100058c, 0xb900316c, 0xb981026b, 0x91000529, 0xeb0b013f, 0x54fffc8b,
        ];

        /// <summary>The time-scaled loop (assembled by hand, verified with capstone; see the comments).</summary>
        public static readonly uint[] Patch160 =
        [
            0x2a1f03e8, // mov    w8, wzr                 repeat bits
            0xb9410269, // ldr    w9, [x19, #0x100]       pad bit count
            0xb901166b, // str    w11, [x19, #0x114]      mPadHold (as before)
            0x9000d591, // adrp   x17, VFR::sInstance page (main+0x2d1e000)
            0xf940ba31, // ldr    x17, [x17, #0x170]
            0xb4000091, // cbz    x17, 1f                 no VFR yet: k = 1
            0xbd403e20, // ldr    s0, [x17, #0x3c]        VFR ratio in use
            0x5ea1d800, // frecpe s0, s0                  ~1 / ratio
            0x1e200011, // fcvtns w17, s0                 round
            0x7100063f, // 1: cmp w17, #1
            0x1a9fa630, // csinc  w16, w17, wzr, ge       k = max(1, round(1 / ratio))
            0xaa1f03ea, // mov    x10, xzr                i
            0x5280002c, // mov    w12, #1                 1 << i
            0xeb09015f, // loop: cmp x10, x9
            0x540002ea, // b.ge   done
            0x8b0a0a6e, // add    x14, x19, x10, lsl #2
            0xb94031cf, // ldr    w15, [x14, #0x30]       hold count
            0x6a0c017f, // tst    w11, w12                held?
            0x110005e3, // add    w3, w15, #1
            0x1a9f1063, // csel   w3, w3, wzr, ne         count + 1 if held, else 0
            0xb90031c3, // str    w3, [x14, #0x30]
            0x540001a0, // b.eq   next
            0x8b0a026d, // add    x13, x19, x10
            0x394341a0, // ldrb   w0, [x13, #0xd0]        pulse
            0x34000140, // cbz    w0, next                pulse 0: no repeat
            0x3942c1a1, // ldrb   w1, [x13, #0xb0]        delay
            0x1b107c21, // mul    w1, w1, w16
            0x6b0101e1, // subs   w1, w15, w1             count - delay * k
            0x540000c3, // b.lo   next
            0x1b107c00, // mul    w0, w0, w16
            0x1ac00822, // udiv   w2, w1, w0
            0x1b008442, // msub   w2, w2, w0, w1          (count - delay * k) % (pulse * k)
            0x35000042, // cbnz   w2, next
            0x2a0c0108, // orr    w8, w8, w12             repeat
            0x531f798c, // next: lsl w12, w12, #1
            0x9100054a, // add    x10, x10, #1
            0x17ffffe9, // b      loop
            0xb9001268, // done: str w8, [x19, #0x10]    mPadRepeat
        ];

        /// <summary>Applies the patch to loaded 1.6.0 code if the original instructions are found; returns whether it did.</summary>
        public static bool Apply(IVirtualMemoryManager memory, ulong mainBase, BotwProfile.Version version)
        {
            if (version.Name != "1.6.0" || Environment.GetEnvironmentVariable("BOTWPC_INPUT_REPEAT") == "0")
            {
                return false;
            }

            ulong address = mainBase + PatchOffset160;

            for (int i = 0; i < _original160.Length; i++)
            {
                if (memory.Read<uint>(address + (ulong)(i * 4)) != _original160[i])
                {
                    Logger.Warning?.Print(LogClass.Loader, "BOTW-PC input repeat: unexpected code (an ExeFS mod changed it?); held-button repeat stays frame-based.");
                    return false;
                }
            }

            for (int i = 0; i < Patch160.Length; i++)
            {
                memory.Write(address + (ulong)(i * 4), Patch160[i]);
            }

            Logger.Info?.Print(LogClass.Loader, "BOTW-PC input repeat: held-button repeat follows the VFR frame-time ratio.");
            return true;
        }
    }
}
