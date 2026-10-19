using System;
using System.Numerics;

namespace Ryujinx.Cpu.LightningJit.Interpreter
{
    unsafe partial class A64Interpreter
    {
        // ---- Helpers ----

        private static ulong Mask(bool is64) => is64 ? ulong.MaxValue : uint.MaxValue;

        private static ulong Ror(ulong value, int amount, int size)
        {
            amount &= size - 1;

            if (amount == 0)
            {
                return value;
            }

            ulong mask = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
            value &= mask;

            return ((value >> amount) | (value << (size - amount))) & mask;
        }

        private static ulong Replicate(ulong value, int esize)
        {
            for (int size = esize; size < 64; size *= 2)
            {
                value |= value << size;
            }

            return value;
        }

        /// <summary>DecodeBitMasks of the ARM ARM; false for reserved encodings.</summary>
        private static bool DecodeBitMasks(uint n, uint imms, uint immr, bool immediate, bool is64, out ulong wmask, out ulong tmask)
        {
            wmask = tmask = 0;

            int len = BitOperations.Log2((n << 6) | (~imms & 0x3F));

            if (len < 1 || (!is64 && n != 0))
            {
                return false;
            }

            int levels = (1 << len) - 1;

            if (immediate && (imms & levels) == levels)
            {
                return false;
            }

            int s = (int)(imms & levels);
            int r = (int)(immr & levels);
            int d = (s - r) & levels;
            int esize = 1 << len;

            ulong welem = s + 1 == 64 ? ulong.MaxValue : (1UL << (s + 1)) - 1;
            ulong telem = d + 1 == 64 ? ulong.MaxValue : (1UL << (d + 1)) - 1;

            wmask = Replicate(Ror(welem, r, esize), esize);
            tmask = Replicate(telem, esize);

            if (!is64)
            {
                wmask &= uint.MaxValue;
                tmask &= uint.MaxValue;
            }

            return true;
        }

        private static ulong Shift(ulong value, uint type, int amount, bool is64)
        {
            int size = is64 ? 64 : 32;

            if (!is64)
            {
                value &= uint.MaxValue;
            }

            return type switch
            {
                0 => (value << amount) & Mask(is64),
                1 => value >> amount,
                2 => is64 ? (ulong)((long)value >> amount) : (uint)((int)(uint)value >> amount),
                _ => Ror(value, amount, size),
            };
        }

        private static ulong Extend(ulong value, uint option, int shift)
        {
            ulong extended = option switch
            {
                0 => (byte)value,
                1 => (ushort)value,
                2 => (uint)value,
                3 => value,
                4 => (ulong)(sbyte)value,
                5 => (ulong)(short)value,
                6 => (ulong)(int)value,
                _ => value,
            };

            return extended << shift;
        }

        /// <summary>AddWithCarry: result and NZCV.</summary>
        private static ulong AddWithCarry(ulong a, ulong b, bool carryIn, bool is64, out uint nzcv)
        {
            ulong c = carryIn ? 1UL : 0;
            ulong result;
            bool carry, overflow;

            if (is64)
            {
                result = a + b + c;
                carry = result < a || (c != 0 && result == a);
                overflow = ((~(a ^ b) & (a ^ result)) >> 63) != 0;
            }
            else
            {
                ulong wide = (ulong)(uint)a + (uint)b + c;
                result = (uint)wide;
                carry = (wide >> 32) != 0;
                overflow = ((~(a ^ b) & (a ^ result)) & 0x80000000) != 0;
            }

            bool negative = is64 ? (long)result < 0 : (int)(uint)result < 0;

            nzcv = (negative ? 1u << 31 : 0) | (result == 0 ? 1u << 30 : 0) | (carry ? 1u << 29 : 0) | (overflow ? 1u << 28 : 0);

            return result;
        }

        private void SetNzLogical(ulong result, bool is64)
        {
            bool negative = is64 ? (long)result < 0 : (int)(uint)result < 0;

            Nzcv = (negative ? 1u << 31 : 0) | ((is64 ? result : (uint)result) == 0 ? 1u << 30 : 0);
        }

        private bool Carry => (Nzcv & (1u << 29)) != 0;

        // ---- Data processing (immediate) ----

        private void ExecuteDataProcessingImmediate(uint inst, ulong pc)
        {
            uint rd = inst & 31, rn = (inst >> 5) & 31;
            bool is64 = (inst & 0x80000000) != 0;

            switch ((inst >> 23) & 7)
            {
                case 0:
                case 1: // ADR, ADRP
                    {
                        long imm = (long)((((ulong)inst >> 5) & 0x7FFFF) << 2 | ((ulong)inst >> 29) & 3) << 43 >> 43;

                        W(rd, (inst & 0x80000000) != 0 ? (pc & ~0xFFFUL) + (ulong)(imm << 12) : pc + (ulong)imm);
                        return;
                    }
                case 2: // ADD/ADDS/SUB/SUBS (immediate)
                    {
                        ulong imm = ((inst >> 10) & 0xFFF) << ((inst & 0x400000) != 0 ? 12 : 0);
                        bool sub = (inst & 0x40000000) != 0, setFlags = (inst & 0x20000000) != 0;
                        ulong result = AddWithCarry(RSp(rn), sub ? ~imm : imm, sub, is64, out uint nzcv);

                        if (setFlags)
                        {
                            Nzcv = nzcv;
                            W(rd, result, is64);
                        }
                        else
                        {
                            WSp(rd, result, is64);
                        }

                        return;
                    }
                case 4: // AND/ORR/EOR/ANDS (immediate)
                    {
                        if (!DecodeBitMasks((inst >> 22) & 1, (inst >> 10) & 0x3F, (inst >> 16) & 0x3F, true, is64, out ulong imm, out _))
                        {
                            throw Unsupported(pc, inst);
                        }

                        ulong a = R(rn);
                        uint opc = (inst >> 29) & 3;
                        ulong result = (opc switch { 0 => a & imm, 1 => a | imm, 2 => a ^ imm, _ => a & imm }) & Mask(is64);

                        if (opc == 3)
                        {
                            SetNzLogical(result, is64);
                            W(rd, result);
                        }
                        else
                        {
                            WSp(rd, result);
                        }

                        return;
                    }
                case 5: // MOVN/MOVZ/MOVK
                    {
                        int shift = (int)((inst >> 21) & 3) * 16;
                        ulong imm = (ulong)((inst >> 5) & 0xFFFF) << shift;

                        switch ((inst >> 29) & 3)
                        {
                            case 0:
                                W(rd, ~imm, is64);
                                return;
                            case 2:
                                W(rd, imm, is64);
                                return;
                            case 3:
                                W(rd, (R(rd) & ~(0xFFFFUL << shift)) | imm, is64);
                                return;
                        }

                        break;
                    }
                case 6: // SBFM/BFM/UBFM
                    {
                        uint immr = (inst >> 16) & 0x3F, imms = (inst >> 10) & 0x3F;

                        if (!DecodeBitMasks((inst >> 22) & 1, imms, immr, false, is64, out ulong wmask, out ulong tmask) ||
                            ((inst >> 22) & 1) != (is64 ? 1u : 0u))
                        {
                            throw Unsupported(pc, inst);
                        }

                        int size = is64 ? 64 : 32;
                        ulong src = R(rn) & Mask(is64);
                        ulong bot, top;
                        ulong rotated = Ror(src, (int)immr, size);

                        switch ((inst >> 29) & 3)
                        {
                            case 0: // SBFM
                                {
                                    bot = rotated & wmask;
                                    ulong sign = ((src >> (int)imms) & 1) != 0 ? Mask(is64) : 0;
                                    W(rd, ((sign & ~tmask) | (bot & tmask)) & Mask(is64));
                                    return;
                                }
                            case 1: // BFM
                                {
                                    ulong dst = R(rd) & Mask(is64);
                                    bot = (dst & ~wmask) | (rotated & wmask);
                                    W(rd, ((dst & ~tmask) | (bot & tmask)) & Mask(is64));
                                    return;
                                }
                            case 2: // UBFM
                                bot = rotated & wmask;
                                top = 0;
                                W(rd, ((top & ~tmask) | (bot & tmask)) & Mask(is64));
                                return;
                        }

                        break;
                    }
                case 7: // EXTR
                    {
                        if ((inst & 0x60200000) != 0 || ((inst >> 22) & 1) != (is64 ? 1u : 0u))
                        {
                            break;
                        }

                        int lsb = (int)((inst >> 10) & 0x3F);
                        ulong low = R((inst >> 16) & 31) & Mask(is64), high = R(rn) & Mask(is64);

                        // EXTR Rd, Rn, Rm, #lsb: (Rn:Rm) >> lsb.
                        ulong result = lsb == 0 ? low : (low >> lsb) | (high << ((is64 ? 64 : 32) - lsb));
                        W(rd, result, is64);
                        return;
                    }
            }

            throw Unsupported(pc, inst);
        }

        // ---- Data processing (register) ----

        private void ExecuteDataProcessingRegister(uint inst, ulong pc)
        {
            uint rd = inst & 31, rn = (inst >> 5) & 31, rm = (inst >> 16) & 31;
            bool is64 = (inst & 0x80000000) != 0;

            if ((inst & 0x10000000) == 0)
            {
                if ((inst & 0x01000000) == 0) // Logical (shifted register)
                {
                    int amount = (int)((inst >> 10) & 0x3F);

                    if (!is64 && amount >= 32)
                    {
                        throw Unsupported(pc, inst);
                    }

                    ulong b = Shift(R(rm), (inst >> 22) & 3, amount, is64);

                    if ((inst & 0x00200000) != 0)
                    {
                        b = ~b;
                    }

                    ulong a = R(rn);
                    uint opc = (inst >> 29) & 3;
                    ulong result = (opc switch { 0 => a & b, 1 => a | b, 2 => a ^ b, _ => a & b }) & Mask(is64);

                    if (opc == 3)
                    {
                        SetNzLogical(result, is64);
                    }

                    W(rd, result);
                    return;
                }

                bool sub = (inst & 0x40000000) != 0, setFlags = (inst & 0x20000000) != 0;

                if ((inst & 0x00200000) == 0) // Add/subtract (shifted register)
                {
                    uint type = (inst >> 22) & 3;
                    int amount = (int)((inst >> 10) & 0x3F);

                    if (type == 3 || (!is64 && amount >= 32))
                    {
                        throw Unsupported(pc, inst);
                    }

                    ulong b = Shift(R(rm), type, amount, is64);
                    ulong result = AddWithCarry(R(rn), sub ? ~b : b, sub, is64, out uint nzcv);

                    if (setFlags)
                    {
                        Nzcv = nzcv;
                    }

                    W(rd, result, is64);
                    return;
                }
                else // Add/subtract (extended register)
                {
                    int shift = (int)((inst >> 10) & 7);

                    if (((inst >> 22) & 3) != 0 || shift > 4)
                    {
                        throw Unsupported(pc, inst);
                    }

                    ulong b = Extend(R(rm), (inst >> 13) & 7, shift);
                    ulong result = AddWithCarry(RSp(rn), sub ? ~b : b, sub, is64, out uint nzcv);

                    if (setFlags)
                    {
                        Nzcv = nzcv;
                        W(rd, result, is64);
                    }
                    else
                    {
                        WSp(rd, result, is64);
                    }

                    return;
                }
            }

            if ((inst & 0x01000000) != 0) // Data processing (3 source)
            {
                uint ra = (inst >> 10) & 31;
                bool o0 = (inst & 0x8000) != 0;
                ulong a = R(rn), b = R(rm), acc = R(ra);

                switch ((inst >> 21) & 7)
                {
                    case 0: // MADD, MSUB
                        W(rd, o0 ? acc - a * b : acc + a * b, is64);
                        return;
                    case 1 when is64: // SMADDL, SMSUBL
                        {
                            long product = (long)(int)a * (int)b;
                            W(rd, o0 ? acc - (ulong)product : acc + (ulong)product);
                            return;
                        }
                    case 2 when is64 && !o0: // SMULH
                        W(rd, (ulong)Math.BigMul((long)a, (long)b, out _));
                        return;
                    case 5 when is64: // UMADDL, UMSUBL
                        {
                            ulong product = (ulong)(uint)a * (uint)b;
                            W(rd, o0 ? acc - product : acc + product);
                            return;
                        }
                    case 6 when is64 && !o0: // UMULH
                        W(rd, Math.BigMul(a, b, out _));
                        return;
                }

                throw Unsupported(pc, inst);
            }

            switch ((inst >> 21) & 0xF)
            {
                case 0: // ADC, ADCS, SBC, SBCS
                    {
                        if ((inst & 0xFC00) != 0)
                        {
                            break;
                        }

                        bool sub = (inst & 0x40000000) != 0;
                        ulong b = R(rm);
                        ulong result = AddWithCarry(R(rn), sub ? ~b : b, Carry, is64, out uint nzcv);

                        if ((inst & 0x20000000) != 0)
                        {
                            Nzcv = nzcv;
                        }

                        W(rd, result, is64);
                        return;
                    }
                case 2: // CCMN, CCMP (register / immediate)
                    {
                        if ((inst & 0x20000000) == 0 || (inst & 0x410) != 0)
                        {
                            break;
                        }

                        if (ConditionHolds((inst >> 12) & 0xF))
                        {
                            ulong b = (inst & 0x800) != 0 ? rm : R(rm);
                            bool sub = (inst & 0x40000000) != 0;

                            AddWithCarry(R(rn), sub ? ~b : b, sub, is64, out uint nzcv);
                            Nzcv = nzcv;
                        }
                        else
                        {
                            Nzcv = (inst & 0xF) << 28;
                        }

                        return;
                    }
                case 4: // CSEL, CSINC, CSINV, CSNEG
                    {
                        if ((inst & 0x20000800) != 0)
                        {
                            break;
                        }

                        ulong result;

                        if (ConditionHolds((inst >> 12) & 0xF))
                        {
                            result = R(rn);
                        }
                        else
                        {
                            result = R(rm);

                            if ((inst & 0x40000000) != 0)
                            {
                                result = ~result;
                            }

                            if ((inst & 0x400) != 0)
                            {
                                result++;
                            }
                        }

                        W(rd, result, is64);
                        return;
                    }
                case 6:
                    if ((inst & 0x20000000) != 0)
                    {
                        break;
                    }

                    if ((inst & 0x40000000) == 0)
                    {
                        ExecuteDataProcessing2Source(inst, pc, rd, rn, rm, is64);
                    }
                    else
                    {
                        ExecuteDataProcessing1Source(inst, pc, rd, rn, is64);
                    }

                    return;
            }

            throw Unsupported(pc, inst);
        }

        private void ExecuteDataProcessing2Source(uint inst, ulong pc, uint rd, uint rn, uint rm, bool is64)
        {
            uint opcode = (inst >> 10) & 0x3F;
            ulong a = R(rn) & Mask(is64), b = R(rm) & Mask(is64);
            int size = is64 ? 64 : 32;

            switch (opcode)
            {
                case 2: // UDIV
                    W(rd, b == 0 ? 0 : a / b, is64);
                    return;
                case 3: // SDIV
                    if (b == 0)
                    {
                        W(rd, 0);
                    }
                    else if (is64)
                    {
                        long n = (long)a, m = (long)b;
                        W(rd, n == long.MinValue && m == -1 ? (ulong)n : (ulong)(n / m));
                    }
                    else
                    {
                        int n = (int)(uint)a, m = (int)(uint)b;
                        W(rd, n == int.MinValue && m == -1 ? (uint)n : (uint)(n / m));
                    }

                    return;
                case 8: // LSLV
                case 9: // LSRV
                case 10: // ASRV
                case 11: // RORV
                    W(rd, Shift(a, opcode - 8, (int)(b & (ulong)(size - 1)), is64), is64);
                    return;
            }

            if ((opcode & 0x38) == 0x10) // CRC32{B,H,W,X}, CRC32C{B,H,W,X}
            {
                uint sz = opcode & 3;

                if ((sz == 3) != is64)
                {
                    throw Unsupported(pc, inst);
                }

                W(rd, ComputeCrc32((uint)a, b, sz, (opcode & 4) != 0));
                return;
            }

            throw Unsupported(pc, inst);
        }

        private static uint ComputeCrc32(uint crc, ulong value, uint size, bool castagnoli)
        {
            if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported)
            {
                return (size, castagnoli) switch
                {
                    (0, false) => System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(crc, (byte)value),
                    (1, false) => System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(crc, (ushort)value),
                    (2, false) => System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(crc, (uint)value),
                    (3, false) => System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32(crc, value),
                    (0, true) => System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(crc, (byte)value),
                    (1, true) => System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(crc, (ushort)value),
                    (2, true) => System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(crc, (uint)value),
                    _ => System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(crc, value),
                };
            }

            uint poly = castagnoli ? 0x82F63B78u : 0xEDB88320u;
            int bytes = 1 << (int)size;

            for (int i = 0; i < bytes; i++)
            {
                crc ^= (byte)(value >> (i * 8));

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                }
            }

            return crc;
        }

        private void ExecuteDataProcessing1Source(uint inst, ulong pc, uint rd, uint rn, bool is64)
        {
            if (((inst >> 16) & 0x1F) != 0)
            {
                throw Unsupported(pc, inst);
            }

            ulong a = R(rn) & Mask(is64);

            switch ((inst >> 10) & 0x3F)
            {
                case 0: // RBIT
                    {
                        ulong r = ReverseBits(a);
                        W(rd, is64 ? r : r >> 32);
                        return;
                    }
                case 1: // REV16
                    {
                        ulong r = ((a & 0x00FF00FF00FF00FFUL) << 8) | ((a >> 8) & 0x00FF00FF00FF00FFUL);
                        W(rd, r, is64);
                        return;
                    }
                case 2: // REV32 (64-bit) / REV (32-bit)
                    {
                        ulong r = (ulong)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)a) |
                                  ((ulong)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)(a >> 32)) << 32);
                        W(rd, r, is64);
                        return;
                    }
                case 3 when is64: // REV
                    W(rd, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(a));
                    return;
                case 4: // CLZ
                    W(rd, is64 ? (ulong)BitOperations.LeadingZeroCount(a) : (ulong)BitOperations.LeadingZeroCount((uint)a));
                    return;
                case 5: // CLS
                    {
                        ulong x = is64 ? a ^ (a << 1) : (uint)(a ^ (a << 1));
                        int count = is64 ? BitOperations.LeadingZeroCount(x | 1) : BitOperations.LeadingZeroCount((uint)x | 1);
                        W(rd, (ulong)count);
                        return;
                    }
            }

            throw Unsupported(pc, inst);
        }

        private static ulong ReverseBits(ulong value)
        {
            value = ((value >> 1) & 0x5555555555555555UL) | ((value & 0x5555555555555555UL) << 1);
            value = ((value >> 2) & 0x3333333333333333UL) | ((value & 0x3333333333333333UL) << 2);
            value = ((value >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((value & 0x0F0F0F0F0F0F0F0FUL) << 4);

            return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
        }
    }
}
