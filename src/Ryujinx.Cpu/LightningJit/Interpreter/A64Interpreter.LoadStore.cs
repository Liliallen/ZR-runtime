using System;
using System.Runtime.Intrinsics;
using System.Threading;

namespace Ryujinx.Cpu.LightningJit.Interpreter
{
    unsafe partial class A64Interpreter
    {
        // ---- Memory access ----

        private ulong Load(ulong address, int size) => size switch
        {
            0 => _memory.ReadGuest<byte>(address),
            1 => _memory.ReadGuest<ushort>(address),
            2 => _memory.ReadGuest<uint>(address),
            _ => _memory.ReadGuest<ulong>(address),
        };

        private void Store(ulong address, int size, ulong value)
        {
            switch (size)
            {
                case 0:
                    _memory.WriteGuest(address, (byte)value);
                    break;
                case 1:
                    _memory.WriteGuest(address, (ushort)value);
                    break;
                case 2:
                    _memory.WriteGuest(address, (uint)value);
                    break;
                default:
                    _memory.WriteGuest(address, value);
                    break;
            }
        }

        private byte* VPtr(uint index) => _ctx + _vOffset + index * 16;

        /// <summary>Loads a scalar FP/SIMD register (1 &lt;&lt; size bytes, size 4 = 128 bits), clearing the rest.</summary>
        private void LoadV(uint t, ulong address, int size)
        {
            Vector128<byte> value = size == 4
                ? _memory.ReadGuest<Vector128<byte>>(address)
                : Vector128.Create(Load(address, size), 0UL).AsByte();

            V((int)t) = value;
        }

        private void StoreV(uint t, ulong address, int size)
        {
            if (size == 4)
            {
                _memory.WriteGuest(address, V((int)t));
            }
            else
            {
                Store(address, size, V((int)t).AsUInt64().ToScalar());
            }
        }

        private static long SignExtend(ulong value, int bits) => (long)(value << (64 - bits)) >> (64 - bits);

        // ---- Loads and stores ----

        private void ExecuteLoadStore(uint inst, ulong pc)
        {
            uint rt = inst & 31, rn = (inst >> 5) & 31;
            bool simd = (inst & 0x04000000) != 0;

            if ((inst & 0xBF000000) == 0x0C000000 || (inst & 0xBF000000) == 0x0D000000)
            {
                ExecuteSimdStructure(inst, pc);
                return;
            }

            if ((inst & 0x3F000000) == 0x08000000)
            {
                ExecuteExclusive(inst, pc);
                return;
            }

            if ((inst & 0x3B000000) == 0x18000000) // LDR (literal), LDRSW (literal), PRFM (literal)
            {
                ulong address = pc + (ulong)(SignExtend((inst >> 5) & 0x7FFFF, 19) << 2);
                uint opc = inst >> 30;

                if (simd)
                {
                    if (opc == 3)
                    {
                        throw Unsupported(pc, inst);
                    }

                    LoadV(rt, address, (int)opc + 2);
                }
                else if (opc == 0)
                {
                    W(rt, Load(address, 2));
                }
                else if (opc == 1)
                {
                    W(rt, Load(address, 3));
                }
                else if (opc == 2)
                {
                    W(rt, (ulong)(long)(int)Load(address, 2));
                }

                return;
            }

            if ((inst & 0x3A000000) == 0x28000000) // Load/store pair
            {
                ExecutePair(inst, pc, rt, rn, simd);
                return;
            }

            if ((inst & 0x3B000000) == 0x39000000) // Load/store register (unsigned immediate)
            {
                int scale = Scale(inst, simd);
                ulong address = RSp(rn) + (((inst >> 10) & 0xFFF) << scale);

                LoadStoreRegister(inst, pc, rt, address, simd);
                return;
            }

            if ((inst & 0x3B200000) == 0x38000000) // Unscaled immediate, post-index, unprivileged, pre-index
            {
                ulong offset = (ulong)SignExtend((inst >> 12) & 0x1FF, 9);
                ulong address = RSp(rn);

                switch ((inst >> 10) & 3)
                {
                    case 0: // LDUR/STUR
                    case 2: // LDTR/STTR (EL0: as LDR/STR)
                        LoadStoreRegister(inst, pc, rt, address + offset, simd);
                        return;
                    case 1: // post-index
                        LoadStoreRegister(inst, pc, rt, address, simd);
                        WSp(rn, address + offset);
                        return;
                    case 3: // pre-index
                        LoadStoreRegister(inst, pc, rt, address + offset, simd);
                        WSp(rn, address + offset);
                        return;
                }
            }

            if ((inst & 0x3B200C00) == 0x38200800) // Load/store register (register offset)
            {
                uint option = (inst >> 13) & 7;

                if ((option & 2) == 0)
                {
                    throw Unsupported(pc, inst);
                }

                int shift = (inst & 0x1000) != 0 ? Scale(inst, simd) : 0;
                ulong address = RSp(rn) + Extend(R((inst >> 16) & 31), option, shift);

                LoadStoreRegister(inst, pc, rt, address, simd);
                return;
            }

            throw Unsupported(pc, inst);
        }

        private static int Scale(uint inst, bool simd)
        {
            int size = (int)(inst >> 30);

            return simd && (inst & 0x00800000) != 0 ? 4 : size;
        }

        private void LoadStoreRegister(uint inst, ulong pc, uint rt, ulong address, bool simd)
        {
            int size = (int)(inst >> 30);
            uint opc = (inst >> 22) & 3;

            if (simd)
            {
                int vsize = (opc & 2) != 0 ? (size == 0 ? 4 : -1) : size;

                if (vsize < 0)
                {
                    throw Unsupported(pc, inst);
                }

                if ((opc & 1) != 0)
                {
                    LoadV(rt, address, vsize);
                }
                else
                {
                    StoreV(rt, address, vsize);
                }

                return;
            }

            switch (opc)
            {
                case 0: // STR
                    Store(address, size, R(rt));
                    return;
                case 1: // LDR
                    W(rt, Load(address, size));
                    return;
                case 2: // LDRS (64-bit result) / PRFM
                    if (size == 3)
                    {
                        return; // PRFM
                    }

                    W(rt, (ulong)SignExtend(Load(address, size), 8 << size));
                    return;
                default: // LDRS (32-bit result)
                    if (size >= 2)
                    {
                        throw Unsupported(pc, inst);
                    }

                    W(rt, (uint)SignExtend(Load(address, size), 8 << size));
                    return;
            }
        }

        private void ExecutePair(uint inst, ulong pc, uint rt, uint rn, bool simd)
        {
            uint opc = inst >> 30;
            uint index = (inst >> 23) & 3;
            bool load = (inst & 0x00400000) != 0;
            uint rt2 = (inst >> 10) & 31;
            int size;

            if (simd)
            {
                if (opc == 3)
                {
                    throw Unsupported(pc, inst);
                }

                size = (int)opc + 2;
            }
            else
            {
                if (opc == 3 || (opc == 1 && (!load || index == 0)))
                {
                    throw Unsupported(pc, inst);
                }

                size = opc == 2 ? 3 : 2;
            }

            ulong offset = (ulong)(SignExtend((inst >> 15) & 0x7F, 7) << size);
            ulong baseAddress = RSp(rn);
            ulong address = index == 1 ? baseAddress : baseAddress + offset;
            ulong bytes = 1UL << size;

            if (simd)
            {
                if (load)
                {
                    LoadV(rt, address, size);
                    LoadV(rt2, address + bytes, size);
                }
                else
                {
                    StoreV(rt, address, size);
                    StoreV(rt2, address + bytes, size);
                }
            }
            else if (load)
            {
                ulong first = Load(address, size), second = Load(address + bytes, size);

                if (opc == 1) // LDPSW
                {
                    first = (ulong)(long)(int)first;
                    second = (ulong)(long)(int)second;
                }

                W(rt, first);
                W(rt2, second);
            }
            else
            {
                ulong first = R(rt), second = R(rt2);

                Store(address, size, first);
                Store(address + bytes, size, second);
            }

            if (index == 1 || index == 3)
            {
                WSp(rn, baseAddress + offset);
            }
        }

        private void ExecuteExclusive(uint inst, ulong pc)
        {
            uint rt = inst & 31, rn = (inst >> 5) & 31, rs = (inst >> 16) & 31;
            int size = (int)(inst >> 30);
            bool o2 = (inst & 0x00800000) != 0, load = (inst & 0x00400000) != 0, o1 = (inst & 0x00200000) != 0, o0 = (inst & 0x8000) != 0;
            ulong address = RSp(rn);

            if (o1)
            {
                throw Unsupported(pc, inst); // LDXP/STXP, CAS, CASP
            }

            if (o2)
            {
                // LDAR / STLR (o0 = 0: LDLAR/STLLR, same effect here).
                if (load)
                {
                    W(rt, Load(address, size));
                    Thread.MemoryBarrier();
                }
                else
                {
                    Thread.MemoryBarrier();
                    Store(address, size, R(rt));
                }

                return;
            }

            if (load) // LDXR, LDAXR
            {
                ulong value = Load(address, size);

                _exclusiveAddress = address;
                _exclusiveSize = size;
                _exclusiveValue = value;

                W(rt, value);

                if (o0)
                {
                    Thread.MemoryBarrier();
                }

                return;
            }

            // STXR, STLXR: succeeds when the monitor holds this address and memory still has the loaded value.
            bool success = false;

            if (_exclusiveAddress == address && _exclusiveSize == size)
            {
                ulong newValue = R(rt);

                success = size switch
                {
                    0 => Interlocked.CompareExchange(ref _memory.GetRef<byte>(address), (byte)newValue, (byte)_exclusiveValue) == (byte)_exclusiveValue,
                    1 => Interlocked.CompareExchange(ref _memory.GetRef<ushort>(address), (ushort)newValue, (ushort)_exclusiveValue) == (ushort)_exclusiveValue,
                    2 => Interlocked.CompareExchange(ref _memory.GetRef<uint>(address), (uint)newValue, (uint)_exclusiveValue) == (uint)_exclusiveValue,
                    _ => Interlocked.CompareExchange(ref _memory.GetRef<ulong>(address), newValue, _exclusiveValue) == _exclusiveValue,
                };
            }

            _exclusiveAddress = ulong.MaxValue;
            W(rs, success ? 0UL : 1UL);
        }

        // ---- Advanced SIMD load/store structures ----

        private void ExecuteSimdStructure(uint inst, ulong pc)
        {
            uint rt = inst & 31, rn = (inst >> 5) & 31, rm = (inst >> 16) & 31;
            bool q = (inst & 0x40000000) != 0, load = (inst & 0x00400000) != 0, post = (inst & 0x00800000) != 0;
            bool single = (inst & 0x01000000) != 0;
            ulong address = RSp(rn);
            ulong transferred;

            if (!single)
            {
                if ((inst & 0x003F0000) != 0 && !post)
                {
                    throw Unsupported(pc, inst);
                }

                uint opcode = (inst >> 12) & 0xF;
                int size = (int)((inst >> 10) & 3);
                int esize = 1 << size;
                int elements = (q ? 16 : 8) >> size;
                int regs, selem;

                switch (opcode)
                {
                    case 0b0000: regs = 1; selem = 4; break; // LD4/ST4
                    case 0b0010: regs = 4; selem = 1; break; // LD1/ST1 (4 registers)
                    case 0b0100: regs = 1; selem = 3; break; // LD3/ST3
                    case 0b0110: regs = 3; selem = 1; break; // LD1/ST1 (3 registers)
                    case 0b0111: regs = 1; selem = 1; break; // LD1/ST1 (1 register)
                    case 0b1000: regs = 1; selem = 2; break; // LD2/ST2
                    case 0b1010: regs = 2; selem = 1; break; // LD1/ST1 (2 registers)
                    default: throw Unsupported(pc, inst);
                }

                if (size == 3 && !q && selem != 1)
                {
                    throw Unsupported(pc, inst);
                }

                ulong offset = 0;

                for (int r = 0; r < regs; r++)
                {
                    for (int e = 0; e < elements; e++)
                    {
                        for (int s = 0; s < selem; s++)
                        {
                            uint t = (uint)((rt + r + s) & 31);
                            byte* element = VPtr(t) + e * esize;

                            if (load)
                            {
                                ulong value = Load(address + offset, size);
                                Buffer.MemoryCopy(&value, element, esize, esize);
                            }
                            else
                            {
                                ulong value = 0;
                                Buffer.MemoryCopy(element, &value, esize, esize);
                                Store(address + offset, size, value);
                            }

                            offset += (ulong)esize;
                        }
                    }
                }

                if (load && !q)
                {
                    for (int r = 0; r < regs * selem; r++)
                    {
                        ((ulong*)VPtr((uint)((rt + r) & 31)))[1] = 0;
                    }
                }

                transferred = offset;
            }
            else
            {
                uint opcode = (inst >> 13) & 7;
                bool s = (inst & 0x1000) != 0;
                uint sizeField = (inst >> 10) & 3;
                int selem = (int)((((opcode & 1) << 1) | ((inst >> 21) & 1)) + 1);
                int scale = (int)(opcode >> 1);
                int index;
                bool replicate = false;

                if ((inst & 0x001F0000) != 0 && !post)
                {
                    throw Unsupported(pc, inst);
                }

                switch (scale)
                {
                    case 3: // LDnR
                        if (!load || s)
                        {
                            throw Unsupported(pc, inst);
                        }

                        scale = (int)sizeField;
                        replicate = true;
                        index = 0;
                        break;
                    case 0:
                        index = (int)(((q ? 1u : 0) << 3) | (s ? 1u << 2 : 0) | sizeField);
                        break;
                    case 1:
                        if ((sizeField & 1) != 0)
                        {
                            throw Unsupported(pc, inst);
                        }

                        index = (int)(((q ? 1u : 0) << 2) | (s ? 1u << 1 : 0) | (sizeField >> 1));
                        break;
                    default:
                        if ((sizeField & 1) == 0)
                        {
                            if ((sizeField & 2) != 0)
                            {
                                throw Unsupported(pc, inst);
                            }

                            index = (int)(((q ? 1u : 0) << 1) | (s ? 1u : 0));
                        }
                        else
                        {
                            if (s || sizeField != 1)
                            {
                                throw Unsupported(pc, inst);
                            }

                            index = q ? 1 : 0;
                            scale = 3;
                        }

                        break;
                }

                int esize = 1 << scale;
                ulong offset = 0;

                for (int e = 0; e < selem; e++)
                {
                    uint t = (uint)((rt + e) & 31);

                    if (replicate)
                    {
                        ulong value = Load(address + offset, scale);
                        int count = (q ? 16 : 8) / esize;
                        byte* v = VPtr(t);

                        for (int i = 0; i < count; i++)
                        {
                            Buffer.MemoryCopy(&value, v + i * esize, esize, esize);
                        }

                        if (!q)
                        {
                            ((ulong*)v)[1] = 0;
                        }
                    }
                    else if (load)
                    {
                        ulong value = Load(address + offset, scale);
                        Buffer.MemoryCopy(&value, VPtr(t) + index * esize, esize, esize);
                    }
                    else
                    {
                        ulong value = 0;
                        Buffer.MemoryCopy(VPtr(t) + index * esize, &value, esize, esize);
                        Store(address + offset, scale, value);
                    }

                    offset += (ulong)esize;
                }

                transferred = offset;
            }

            if (post)
            {
                WSp(rn, address + (rm == 31 ? transferred : R(rm)));
            }
        }
    }
}
