using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Scales BotW values that assume one frame = 1/30 s by the real frame step (v1.6.0).
    ///
    /// Most of the game multiplies per-frame quantities by ksys::VFR's ratio, which BOTW-PC's timing correction
    /// drives (<see cref="BotwTiming"/>). Some code uses fixed 30 FPS values instead (stamina regen, arrow speed,
    /// ragdoll impulses, ...), so above 30 FPS those systems run too fast or too strong. Each <see cref="Site"/> names one
    /// instruction that produces such a value in a float register; it is replaced by a branch to a stub in the unused
    /// tail of main's .text that runs the original instruction and multiplies (or divides) the register by
    ///
    ///     f = clamp(VFR mIntervalRatioA, 1/16, 1)      (1 at 30 FPS and below, 0.25 at 120 FPS)
    ///
    /// (<see cref="Site.Before"/> scales the register before the original instruction instead). The stubs save every
    /// register they use on the stack and leave NZCV alone, so a site needs no dead registers. At 30 FPS and below
    /// f is 1 and the game behaves as on the console. Sites whose original instruction does not match are skipped with
    /// a warning (an ExeFS mod changed them). BOTWPC_FRAME_FIXES=0 disables all; BOTWPC_FRAME_FIX_&lt;NAME&gt;=0 one site.
    /// </summary>
    public static class BotwFrameScale
    {
        /// <param name="Name">Short id, also used in BOTWPC_FRAME_FIX_&lt;NAME&gt;.</param>
        /// <param name="Offset">Main-relative offset of the instruction to replace.</param>
        /// <param name="Original">Expected instruction word (checked before patching; relocated into the stub).</param>
        /// <param name="Register">Float register (s/d number) that holds the value.</param>
        /// <param name="Divide">Divide by f (value grows with the frame rate) instead of multiplying (value shrinks).</param>
        /// <param name="Before">Scale before running the original instruction (the original consumes the register).</param>
        /// <param name="Double">The value is a double (d register).</param>
        public sealed record Site(string Name, ulong Offset, uint Original, int Register, bool Divide, bool Before = false, bool Double = false, string Note = "");

        /// <summary>Unused tail of 1.6.0 main .text (text ends at 0x2129ba4, page end 0x212a000).</summary>
        public const ulong CaveStart160 = 0x2129bc0;
        public const ulong CaveEnd160 = 0x212a000;

        public const int IntervalRatioOffset = 0x3c;
        public const float MinFactor = 1f / 16;

        /// <summary>The sites patched in 1.6.0 (filled in as each one is located and verified).</summary>
        public static readonly IReadOnlyList<Site> Sites160 =
        [
            // Player stamina update (0xf6fdd0): regen adds EnergyAutoRecover / 30 * max / 1000 every frame, unscaled.
            // fmov s1, #30.0 (then fdiv s0, s0, s1): s1 = 30 / f.
            new("stamina_regen", 0xf6ffdc, 0x1e27d001, 1, Divide: true),
            // Bow aiming in the air (0xf34b90): drains EnergyBowSlow * (1/30) every frame. ldr s1, [x8, #0x788] (the
            // shared 1/30 literal), then fmul and the stamina-decrease call: s1 = f / 30.
            new("bow_slow_drain", 0xf354b8, 0xbd478901, 1, Divide: false),
            // Flurry Rush follow-up dash (PlayerCutAfterJust, 0xe619f0): s0 = VFR delta frame from player+0x20f0, compared
            // with 1.0 to scale the root-motion distance a second time. fmov s1, #1.0 here; s0 = s0 / f.
            new("flurry_rush", 0xe61bb8, 0x1e2e1001, 0, Divide: true),
            // KorokTargetWait / InvisibleKorokWait calc_: a wait counter in frames gets += 1.0 per frame (then compared
            // with DynStopTime). fmov s1, #1.0 (then fadd s0, s0, s1): s1 = f.
            new("korok_target_wait", 0x2dd9d0, 0x1e2e1001, 1, Divide: false),
            new("invisible_korok_wait", 0x2c3474, 0x1e2e1001, 1, Divide: false),
            // Shield surfing motion update (0x1740bc0, called once per frame by PlayerShieldRideMove::calc_): two fixed
            // per-frame slow-downs. fmul s5, s5, s6 (s6 = 0.02, then v -= v * s5): s5 = d * f.
            new("surf_drag", 0x1742b70, 0x1e2608a5, 5, Divide: false),
            // fmul s4, s0, s4 (s4 = -0.03, then fadd s4, s4, #1 and speed *= s4): s4 = -0.03 * k * f.
            new("surf_brake", 0x17429b8, 0x1e240804, 4, Divide: false),
            // Map screen cursor (0x114fae0, once per frame): position += speed[zoom] * stick / scale, so it crossed the map
            // ~3x faster at ~100 FPS (measured). ldr s0, [x9, x8, lsl #2] (the per-zoom speed table): s0 = speed * f.
            new("map_cursor", 0x114fb30, 0xbc687920, 0, Divide: false),
        ];

        private const int ScratchX = 16; // x16 (address), saved/restored
        private const int Factor = 16;    // s16 = f, saved/restored (q16)
        private const int Temp = 17;      // s17, saved/restored (q17)

        public static bool Apply(IVirtualMemoryManager memory, ulong mainBase, BotwProfile.Version version) =>
            Apply(memory, mainBase, version, Sites160);

        public static bool Apply(IVirtualMemoryManager memory, ulong mainBase, BotwProfile.Version version, IReadOnlyList<Site> sites)
        {
            if (version.Name != "1.6.0" || sites.Count == 0 || Environment.GetEnvironmentVariable("BOTWPC_FRAME_FIXES") == "0")
            {
                return false;
            }

            if (!memory.IsRangeMapped(mainBase + CaveStart160, CaveEnd160 - CaveStart160))
            {
                Logger.Warning?.Print(LogClass.Loader, "BOTW-PC frame fixes: the code cave after main .text is not mapped; fixed-30 FPS values stay unscaled.");
                return false;
            }

            for (ulong a = CaveStart160; a < CaveEnd160; a += 4)
            {
                if (memory.Read<uint>(mainBase + a) != 0)
                {
                    Logger.Warning?.Print(LogClass.Loader, "BOTW-PC frame fixes: code cave is not empty (an ExeFS mod uses it?); fixed-30 FPS values stay unscaled.");
                    return false;
                }
            }

            List<Site> active = [];

            foreach (Site site in sites)
            {
                if (Environment.GetEnvironmentVariable("BOTWPC_FRAME_FIX_" + site.Name.ToUpperInvariant()) == "0")
                {
                    Logger.Info?.Print(LogClass.Loader, $"BOTW-PC frame fixes: {site.Name} disabled by environment.");
                    continue;
                }

                uint word = memory.Read<uint>(mainBase + site.Offset);

                if (word != site.Original)
                {
                    Logger.Warning?.Print(LogClass.Loader, $"BOTW-PC frame fixes: {site.Name} at main+0x{site.Offset:x} is 0x{word:x8}, expected 0x{site.Original:x8}; skipped.");
                    continue;
                }

                active.Add(site);
            }

            List<(ulong Offset, uint Word)> writes = Build(active, version.VfrInstanceOffset);

            foreach ((ulong offset, uint word) in writes)
            {
                memory.Write(mainBase + offset, word);
            }

            Logger.Info?.Print(LogClass.Loader, $"BOTW-PC frame fixes: {active.Count} of {sites.Count} fixed-30 FPS values follow the frame step ({string.Join(", ", active.ConvertAll(s => s.Name))}).");
            return active.Count != 0;
        }

        /// <summary>
        /// Assembles the factor helper and one stub per site into the cave, and the branches at the sites.
        /// Returns main-relative (offset, instruction) pairs.
        /// </summary>
        public static List<(ulong Offset, uint Word)> Build(IReadOnlyList<Site> sites, ulong vfrInstanceOffset)
        {
            List<(ulong, uint)> writes = [];
            ulong pc = CaveStart160;

            void Emit(uint word)
            {
                if (pc >= CaveEnd160)
                {
                    throw new InvalidOperationException("BOTW-PC frame fixes: code cave full.");
                }

                writes.Add((pc, word));
                pc += 4;
            }

            // Literal 1/16 for the lower clamp (fmov immediates cannot encode it).
            ulong minLiteral = pc;
            Emit(BitConverter.SingleToUInt32Bits(MinFactor));
            Emit(0);

            // helper: s16 = clamp(VFR->mIntervalRatioA, 1/16, 1); uses x16, s17; no flags.
            ulong helper = pc;
            Emit(A64.Adrp(ScratchX, helper, vfrInstanceOffset));
            Emit(A64.LdrX(ScratchX, ScratchX, (int)(vfrInstanceOffset & 0xfff)));
            Emit(A64.FmovOne(Factor));
            Emit(A64.Cbz(ScratchX, pc, pc + 4 * 5));              // no VFR yet: f = 1
            Emit(A64.LdrS(Temp, ScratchX, IntervalRatioOffset));
            Emit(A64.Fmin(Factor, Factor, Temp));
            Emit(A64.LdrSLiteral(Temp, pc, minLiteral));
            Emit(A64.Fmax(Factor, Factor, Temp));
            Emit(A64.Ret);

            foreach (Site site in sites)
            {
                if (site.Register is Factor or Temp)
                {
                    throw new ArgumentException($"BOTW-PC frame fixes: {site.Name} uses v{site.Register}, which the stub needs.");
                }

                if (A64.IsPcRelative(site.Original))
                {
                    throw new ArgumentException($"BOTW-PC frame fixes: {site.Name}'s instruction is PC-relative and cannot be moved.");
                }

                ulong stub = pc;
                writes.Add((site.Offset, A64.B(site.Offset, stub)));

                if (!site.Before)
                {
                    Emit(site.Original);
                }

                Emit(A64.StpXPre(ScratchX, 30, -48));                 // stp x16, x30, [sp, #-48]!
                Emit(A64.StpQ(Factor, Temp, 16));                      // stp q16, q17, [sp, #16]
                Emit(A64.Bl(pc, helper));

                if (site.Double)
                {
                    Emit(A64.FcvtDS(Factor, Factor));                  // fcvt d16, s16
                }

                Emit(site.Divide
                    ? A64.Fdiv(site.Register, site.Register, Factor, site.Double)
                    : A64.Fmul(site.Register, site.Register, Factor, site.Double));
                Emit(A64.LdpQ(Factor, Temp, 16));                      // ldp q16, q17, [sp, #16]
                Emit(A64.LdpXPost(ScratchX, 30, 48));                  // ldp x16, x30, [sp], #48

                if (site.Before)
                {
                    Emit(site.Original);
                }

                Emit(A64.B(pc, site.Offset + 4));
            }

            return writes;
        }

        /// <summary>The few A64 encodings the stubs need (checked against capstone in BotwFrameScaleTests).</summary>
        public static class A64
        {
            public const uint Ret = 0xd65f03c0;

            public static uint B(ulong from, ulong to) => 0x14000000u | Imm26(from, to);

            public static uint Bl(ulong from, ulong to) => 0x94000000u | Imm26(from, to);

            public static uint Cbz(int xt, ulong from, ulong to)
            {
                long words = ((long)to - (long)from) / 4;
                return 0xb4000000u | ((uint)words & 0x7ffff) << 5 | (uint)xt;
            }

            public static uint Adrp(int xd, ulong pc, ulong target)
            {
                long pages = (long)(target >> 12) - (long)(pc >> 12);
                return 0x90000000u | (uint)(pages & 3) << 29 | (uint)((pages >> 2) & 0x7ffff) << 5 | (uint)xd;
            }

            public static uint LdrX(int xt, int xn, int offset) => 0xf9400000u | (uint)(offset / 8) << 10 | (uint)xn << 5 | (uint)xt;

            public static uint LdrS(int st, int xn, int offset) => 0xbd400000u | (uint)(offset / 4) << 10 | (uint)xn << 5 | (uint)st;

            public static uint LdrSLiteral(int st, ulong pc, ulong literal)
            {
                long words = ((long)literal - (long)pc) / 4;
                return 0x1c000000u | ((uint)words & 0x7ffff) << 5 | (uint)st;
            }

            public static uint FmovOne(int sd) => 0x1e2e1000u | (uint)sd;

            public static uint Fmin(int d, int n, int m) => 0x1e205800u | (uint)m << 16 | (uint)n << 5 | (uint)d;

            public static uint Fmax(int d, int n, int m) => 0x1e204800u | (uint)m << 16 | (uint)n << 5 | (uint)d;

            public static uint FcvtDS(int dd, int sn) => 0x1e22c000u | (uint)sn << 5 | (uint)dd;

            /// <summary>fmul Sd, Sn, Sm (or Dd, Dn, Dm with <paramref name="dbl"/>).</summary>
            public static uint Fmul(int d, int n, int m, bool dbl) => (dbl ? 0x1e600800u : 0x1e200800u) | (uint)m << 16 | (uint)n << 5 | (uint)d;

            public static uint Fdiv(int d, int n, int m, bool dbl) => (dbl ? 0x1e601800u : 0x1e201800u) | (uint)m << 16 | (uint)n << 5 | (uint)d;

            public static uint StpXPre(int t1, int t2, int offset) => 0xa9800000u | ((uint)(offset / 8) & 0x7f) << 15 | (uint)t2 << 10 | 31u << 5 | (uint)t1;

            public static uint LdpXPost(int t1, int t2, int offset) => 0xa8c00000u | ((uint)(offset / 8) & 0x7f) << 15 | (uint)t2 << 10 | 31u << 5 | (uint)t1;

            public static uint StpQ(int t1, int t2, int offset) => 0xad000000u | ((uint)(offset / 16) & 0x7f) << 15 | (uint)t2 << 10 | 31u << 5 | (uint)t1;

            public static uint LdpQ(int t1, int t2, int offset) => 0xad400000u | ((uint)(offset / 16) & 0x7f) << 15 | (uint)t2 << 10 | 31u << 5 | (uint)t1;

            /// <summary>adr/adrp, literal loads, branches: instructions whose meaning depends on their address.</summary>
            public static bool IsPcRelative(uint w) =>
                (w & 0x1f000000) == 0x10000000 ||   // adr, adrp
                (w & 0x3b000000) == 0x18000000 ||   // ldr (literal), ldrsw (literal), prfm (literal), SIMD literal
                (w & 0x7c000000) == 0x14000000 ||   // b, bl
                (w & 0x7e000000) == 0x34000000 ||   // cbz, cbnz
                (w & 0x7e000000) == 0x36000000 ||   // tbz, tbnz
                (w & 0xff000010) == 0x54000000;     // b.cond

            private static uint Imm26(ulong from, ulong to)
            {
                long words = ((long)to - (long)from) / 4;

                if (words is < -(1 << 25) or >= (1 << 25))
                {
                    throw new ArgumentOutOfRangeException(nameof(to), "branch out of range");
                }

                return (uint)words & 0x3ffffff;
            }
        }
    }
}
