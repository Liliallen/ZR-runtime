using NUnit.Framework;
using Ryujinx.HLE.BotwPc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using A64 = Ryujinx.HLE.BotwPc.BotwFrameScale.A64;

namespace Ryujinx.Tests.BotwPc
{
    public class BotwFrameScaleTests
    {
        [Test]
        public void EncodingsMatchCapstone()
        {
            // Expected words disassembled with capstone 5 (see the comments).
            Assert.That(A64.Adrp(16, 0x2129bc8, 0x2D1E170), Is.EqualTo(0xb0005fb0u));      // adrp x16, #0x2d1e000
            Assert.That(A64.LdrX(16, 16, 0x170), Is.EqualTo(0xf940ba10u));                  // ldr x16, [x16, #0x170]
            Assert.That(A64.FmovOne(16), Is.EqualTo(0x1e2e1010u));                          // fmov s16, #1.0
            Assert.That(A64.Cbz(16, 0x100, 0x114), Is.EqualTo(0xb40000b0u));                // cbz x16, #0x114
            Assert.That(A64.LdrS(17, 16, 0x3c), Is.EqualTo(0xbd403e11u));                   // ldr s17, [x16, #0x3c]
            Assert.That(A64.Fmin(16, 16, 17), Is.EqualTo(0x1e315a10u));                     // fmin s16, s16, s17
            Assert.That(A64.LdrSLiteral(17, 0x200, 0x1e8), Is.EqualTo(0x1cffff51u));        // ldr s17, #0x1e8
            Assert.That(A64.Fmax(16, 16, 17), Is.EqualTo(0x1e314a10u));                     // fmax s16, s16, s17
            Assert.That(A64.StpXPre(16, 30, -48), Is.EqualTo(0xa9bd7bf0u));                 // stp x16, x30, [sp, #-0x30]!
            Assert.That(A64.StpQ(16, 17, 16), Is.EqualTo(0xad00c7f0u));                     // stp q16, q17, [sp, #0x10]
            Assert.That(A64.LdpQ(16, 17, 16), Is.EqualTo(0xad40c7f0u));                     // ldp q16, q17, [sp, #0x10]
            Assert.That(A64.LdpXPost(16, 30, 48), Is.EqualTo(0xa8c37bf0u));                 // ldp x16, x30, [sp], #0x30
            Assert.That(A64.Fmul(0, 0, 16, false), Is.EqualTo(0x1e300800u));                // fmul s0, s0, s16
            Assert.That(A64.Fdiv(3, 3, 16, false), Is.EqualTo(0x1e301863u));                // fdiv s3, s3, s16
            Assert.That(A64.Fmul(2, 2, 16, true), Is.EqualTo(0x1e700842u));                 // fmul d2, d2, d16
            Assert.That(A64.FcvtDS(16, 16), Is.EqualTo(0x1e22c210u));                       // fcvt d16, s16
            Assert.That(A64.Bl(0x2129c00, 0x2129bc8), Is.EqualTo(0x97fffff2u));             // bl #0x2129bc8
            Assert.That(A64.B(0x1000000, 0x2129c00), Is.EqualTo(0x1444a700u));              // b #0x2129c00
        }

        [Test]
        public void PcRelativeInstructionsAreRecognised()
        {
            Assert.That(A64.IsPcRelative(0xb0005fb0), Is.True);  // adrp
            Assert.That(A64.IsPcRelative(0x1cffff51), Is.True);  // ldr s17, literal
            Assert.That(A64.IsPcRelative(0x97fffff2), Is.True);  // bl
            Assert.That(A64.IsPcRelative(0xb40000b0), Is.True);  // cbz
            Assert.That(A64.IsPcRelative(0x54000080), Is.True);  // b.eq
            Assert.That(A64.IsPcRelative(0xbd403e11), Is.False); // ldr s17, [x16, #0x3c]
            Assert.That(A64.IsPcRelative(0x1e2e1010), Is.False); // fmov
            Assert.That(A64.IsPcRelative(0x1e300800), Is.False); // fmul
        }

        [Test]
        public void BuildBranchesEachSiteToItsStubAndBack()
        {
            BotwFrameScale.Site[] sites =
            [
                new("a", 0x100000, 0x1e260000, 0, Divide: false),          // fmov s0, #?? (any non-PC-relative word)
                new("b", 0x200000, 0xbd400261, 1, Divide: true, Before: true),
            ];

            List<(ulong Offset, uint Word)> writes = BotwFrameScale.Build(sites, 0x2D1E170);
            Dictionary<ulong, uint> code = writes.ToDictionary(w => w.Offset, w => w.Word);

            Assert.That(writes.All(w => w.Offset is 0x100000 or 0x200000 || (w.Offset >= BotwFrameScale.CaveStart160 && w.Offset < BotwFrameScale.CaveEnd160)));
            Assert.That(code[0x100000] >> 26, Is.EqualTo(0x05u)); // b
            Assert.That(code[0x200000] >> 26, Is.EqualTo(0x05u));

            // Site a: original first, the multiply, branch back to the next instruction.
            ulong stubA = Target(0x100000, code[0x100000]);
            Assert.That(code[stubA], Is.EqualTo(0x1e260000u));
            Assert.That(code.Values.Contains(A64.Fmul(0, 0, 16, false)));
            Assert.That(code.Values.Contains(A64.Fdiv(1, 1, 16, false)));

            ulong last = stubA;
            while (code.ContainsKey(last + 4) && last + 4 != Target(0x200000, code[0x200000]))
            {
                last += 4;
            }

            Assert.That(Target(last, code[last]), Is.EqualTo(0x100004UL));

            // BOTWPC_FRAME_SCALE_DUMP=<file>: writes the assembled cave for an emulator check (unicorn) outside the tests.
            string dump = Environment.GetEnvironmentVariable("BOTWPC_FRAME_SCALE_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                File.WriteAllLines(dump, writes.Select(w => $"{w.Offset:x} {w.Word:x8}"));
            }
        }

        [Test]
        public void RejectsSitesTheStubCannotHandle()
        {
            Assert.Throws<ArgumentException>(() => BotwFrameScale.Build([new("x", 0x1000, 0x1e260010, 16, false)], 0x2D1E170));
            Assert.Throws<ArgumentException>(() => BotwFrameScale.Build([new("y", 0x1000, 0x1cffff51, 0, false)], 0x2D1E170));
        }

        private static ulong Target(ulong pc, uint branch)
        {
            long imm = (branch & 0x3ffffff) << 6;
            return (ulong)((long)pc + ((int)imm >> 4));
        }
    }
}
