using Ryujinx.Cpu.LightningJit.Arm64;
using Ryujinx.Cpu.LightningJit.Arm64.Target.Arm64;
using Ryujinx.Cpu.LightningJit.Cache;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Ryujinx.Cpu.LightningJit.Interpreter
{
    /// <summary>
    /// FP and Advanced SIMD data processing runs on the host's own unit: the instruction, with its registers renamed to
    /// V0-V7 / X0-X3 ("normalized"), is a two-instruction stub (instruction, RET) that the trampoline stub calls between
    /// loading and storing those registers. Stubs come from the ahead-of-time image (every normalized FP/SIMD encoding of
    /// the game's code) or, where the process can run generated code, are made on demand. The results are exactly the
    /// hardware's, with the guest's FPCR (the host FPCR while guest code runs) and FPSR flags.
    /// </summary>
    unsafe partial class A64Interpreter
    {
        // Trampoline block: V0-V7, X0-X3, NZCV.
        private const int BlockX = 128, BlockNzcv = 160, BlockSize = 176;

        private static readonly CpuPreset _preset = CpuPresets.CortexA57;
        private static readonly ConcurrentDictionary<uint, nint> _generatedStubs = new();
        private static readonly bool _logStubs = Environment.GetEnvironmentVariable("BOTWPC_INTERP_LOG") == "1";

        private readonly byte[] _block = GC.AllocateArray<byte>(BlockSize, pinned: true);

        /// <summary>How an FP/SIMD instruction maps to the trampoline's registers.</summary>
        internal readonly struct SimdForm
        {
            public readonly uint Normalized;
            public readonly int Rd, Rn, Rm, Ra, RnCount;
            public readonly bool RdGpr, RnGpr;

            public SimdForm(uint normalized, int rd, int rn, int rm, int ra, int rnCount, bool rdGpr, bool rnGpr)
            {
                Normalized = normalized;
                Rd = rd;
                Rn = rn;
                Rm = rm;
                Ra = ra;
                RnCount = rnCount;
                RdGpr = rdGpr;
                RnGpr = rnGpr;
            }
        }

        /// <summary>
        /// Decodes an FP/SIMD data processing instruction (op0 x111) into its normalized form; false for encodings the
        /// decoder does not know (they stay unsupported). Register fields not used by the instruction are -1.
        /// </summary>
        internal static bool TryGetSimdForm(uint inst, out SimdForm form)
        {
            form = default;

            (InstName name, InstFlags flags, _) = InstTable.GetInstNameAndFlags(inst, _preset.Version, _preset.Features);

            if (name == InstName.UdfPermUndef || !flags.HasFlag(InstFlags.FpSimd) || flags.HasFlag(InstFlags.Memory))
            {
                return false;
            }

            uint normalized = inst;
            int rd = -1, rn = -1, rm = -1, ra = -1, rnCount = 1;
            bool rdGpr = false, rnGpr = false;

            if (flags.HasFlag(InstFlags.Rd))
            {
                rd = (int)(inst & 31);
                rdGpr = IsFpToGpr(flags, inst);
                normalized &= ~31u; // V0 / X0
            }

            if (flags.HasFlag(InstFlags.Rn))
            {
                rn = (int)((inst >> 5) & 31);
                rnGpr = IsFpFromGpr(flags, inst);

                if (flags.HasFlag(InstFlags.RnSeq) && !rnGpr)
                {
                    rnCount = (int)((inst >> 13) & 3) + 1; // TBL, TBX
                    normalized = (normalized & ~(31u << 5)) | (4u << 5); // V4..V7
                }
                else
                {
                    normalized = (normalized & ~(31u << 5)) | (1u << 5); // V1 / X1
                }
            }

            if (flags.HasFlag(InstFlags.Rm))
            {
                // By element with 16-bit elements: Rm is 4 bits and bit 20 is part of the index.
                uint mask = IsElement(name) && (inst & (1u << 23)) == 0 ? 15u : 31u;

                rm = (int)((inst >> 16) & mask);
                normalized = (normalized & ~(mask << 16)) | (2u << 16); // V2
            }

            if (flags.HasFlag(InstFlags.Ra))
            {
                ra = (int)((inst >> 10) & 31);
                normalized = (normalized & ~(31u << 10)) | (3u << 10); // V3
            }

            form = new SimdForm(normalized, rd, rn, rm, ra, rnCount, rdGpr, rnGpr);
            return true;
        }

        private static bool IsElement(InstName name) => name.ToString().EndsWith("Elt", StringComparison.Ordinal);

        // As RegisterUtils (FMOV general is both; bit 16 tells the direction).
        private static bool IsFpFromGpr(InstFlags flags, uint encoding)
        {
            InstFlags both = InstFlags.FpSimdFromGpr | InstFlags.FpSimdToGpr;

            return (flags & both) == both ? (encoding & (1u << 16)) != 0 : flags.HasFlag(InstFlags.FpSimdFromGpr);
        }

        private static bool IsFpToGpr(InstFlags flags, uint encoding)
        {
            InstFlags both = InstFlags.FpSimdFromGpr | InstFlags.FpSimdToGpr;

            return (flags & both) == both ? (encoding & (1u << 16)) == 0 : flags.HasFlag(InstFlags.FpSimdToGpr);
        }

        /// <summary>The stub of a normalized encoding: from the image, else generated when the process can run it.</summary>
        internal static nint GetSimdStub(uint normalized)
        {
            nint stub = AotImage.GetSimdStub(normalized);

            if (stub != 0 || !AotImage.JitAvailable)
            {
                return stub;
            }

            return _generatedStubs.GetOrAdd(normalized, static encoding =>
            {
                uint[] code = [encoding, 0xD65F03C0]; // instruction; RET

                AotImage.RecordSimd(encoding);

                if (_logStubs)
                {
                    Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Cpu, $"Interpreter: FP/SIMD stub 0x{encoding:x8}");
                }

                return JitCache.Map(MemoryMarshal.AsBytes(code.AsSpan()));
            });
        }

        private void ExecuteSimdFp(uint inst, ulong pc)
        {
            if (!TryGetSimdForm(inst, out SimdForm form))
            {
                throw Unsupported(pc, inst);
            }

            nint stub = GetSimdStub(form.Normalized);

            if (stub == 0)
            {
                AotImage.NoteSimdMiss(form.Normalized);
                throw Unsupported(pc, inst);
            }

            fixed (byte* block = _block)
            {
                Vector128<byte>* v = (Vector128<byte>*)block;
                ulong* x = (ulong*)(block + BlockX);

                if (form.Rd >= 0)
                {
                    if (form.RdGpr)
                    {
                        x[0] = R((uint)form.Rd);
                    }
                    else
                    {
                        v[0] = V(form.Rd);
                    }
                }

                if (form.Rn >= 0)
                {
                    if (form.RnGpr)
                    {
                        x[1] = R((uint)form.Rn);
                    }
                    else if (form.RnCount > 1 || (form.Normalized & (31u << 5)) == 4u << 5)
                    {
                        for (int i = 0; i < form.RnCount; i++)
                        {
                            v[4 + i] = V((form.Rn + i) & 31);
                        }
                    }
                    else
                    {
                        v[1] = V(form.Rn);
                    }
                }

                if (form.Rm >= 0)
                {
                    v[2] = V(form.Rm);
                }

                if (form.Ra >= 0)
                {
                    v[3] = V(form.Ra);
                }

                *(ulong*)(block + BlockNzcv) = Nzcv;

                ((delegate* unmanaged<byte*, nint, void>)_translator.Stubs.SimdTrampolineStub)(block, stub);

                Nzcv = (uint)*(ulong*)(block + BlockNzcv);

                if (form.Rd >= 0)
                {
                    if (form.RdGpr)
                    {
                        W((uint)form.Rd, x[0]);
                    }
                    else
                    {
                        V(form.Rd) = v[0];
                    }
                }
            }
        }
    }
}
