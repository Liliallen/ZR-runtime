using ARMeilleure;
using ARMeilleure.State;
using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using Ryujinx.Memory;
using Ryujinx.Tests.Unicorn;
using System;
using System.Collections.Generic;
using CpuContext = Ryujinx.Tests.Cpu.CpuContext;
using ExecutionContext = ARMeilleure.State.ExecutionContext;
using MemoryPermission = Ryujinx.Tests.Unicorn.MemoryPermission;

namespace Ryujinx.Tests.BotwPc
{
    /// <summary>
    /// Differential tests of the CPU V2 code generator (<see cref="CpuV2Features"/>): random A64 programs (integer
    /// ALU, shifts and bitfields, multiply/divide, conditional select/compare, carry arithmetic, loads/stores and
    /// pairs, forward branches, counted loops that create loop-carried values, and calls to a callee) run on Unicorn,
    /// on the legacy ARMeilleure code generator and on CPU V2, both forced into the high-quality tier (the only tier
    /// CPU V2 changes). The programs use up to 28 registers at once, so register pressure, spills, phi resolution,
    /// value numbering and the context load/store placement around calls are all exercised.
    ///
    /// Exact features must reproduce every register, NZCV and the data memory. With the procedure-call-standard
    /// features the programs follow AAPCS64 (no flag reads after a call or at a callee's entry before flags are set;
    /// no temporary register reads after a call before it is written), and the values the standard leaves undefined at
    /// the final return are excluded from the comparison; everything else must still match exactly.
    /// </summary>
    public class CpuV2DifferentialTests
    {
        private const ulong CodeBase = 0x10000;
        private const ulong CodeSize = 0x8000;
        private const ulong DataBase = 0x20000;
        private const ulong DataSize = 0x1000;

        private const int DataReg = 28;
        private const int LoopReg = 27;
        private const int LinkSaveReg = 26;

        private sealed class Program
        {
            public readonly List<uint> Words = [];
            public int CalleeIndex;
            public int EndIndex; // index of main's final RET
        }

        private sealed class Generator
        {
            private readonly Random _rng;
            private readonly bool _abiFlags;
            private readonly bool _abiTemps;
            private bool _flagsDefined;
            private readonly bool[] _tempsDefined = new bool[32];

            public Generator(int seed, bool abiFlags, bool abiTemps)
            {
                _rng = new Random(seed);
                _abiFlags = abiFlags;
                _abiTemps = abiTemps;
            }

            private static bool IsTemp(int r) => r is >= 8 and <= 17;

            private int SrcReg()
            {
                while (true)
                {
                    int r = _rng.Next(0, 31);

                    if (r is LoopReg or LinkSaveReg or 30)
                    {
                        continue;
                    }

                    if (_abiTemps && IsTemp(r) && !_tempsDefined[r])
                    {
                        continue;
                    }

                    return r;
                }
            }

            private int DstReg()
            {
                while (true)
                {
                    int r = _rng.Next(0, 30);

                    if (r is LoopReg or LinkSaveReg or DataReg)
                    {
                        continue;
                    }

                    _tempsDefined[r] = true;

                    return r;
                }
            }

            /// <summary>A destination that is also read (MOVK, BFM keep the other bits): defined and writable.</summary>
            private int ReadWriteReg()
            {
                while (true)
                {
                    int r = DstRegCandidate();

                    if (!(_abiTemps && IsTemp(r) && !_tempsDefined[r]))
                    {
                        return r;
                    }
                }
            }

            private int DstRegCandidate()
            {
                while (true)
                {
                    int r = _rng.Next(0, 30);

                    if (r is not (LoopReg or LinkSaveReg or DataReg))
                    {
                        return r;
                    }
                }
            }

            private int Sf() => _rng.Next(2);

            private bool CanReadFlags => !_abiFlags || _flagsDefined;

            private void AfterCall()
            {
                _flagsDefined = false;

                for (int r = 8; r <= 17; r++)
                {
                    _tempsDefined[r] = false;
                }
            }

            public void StartFunction()
            {
                AfterCall(); // at a function entry the standard leaves flags (and temporaries) undefined as well
            }

            /// <summary>One straight-line instruction (never a branch).</summary>
            public uint Instruction()
            {
                while (true)
                {
                    int kind = _rng.Next(0, 20);
                    int sf = Sf();
                    int width = sf == 1 ? 64 : 32;

                    switch (kind)
                    {
                        case 0:
                        case 1:
                            {
                                // ADD/SUB(S) immediate
                                int s = _rng.Next(2);
                                uint w = (uint)((sf << 31) | (_rng.Next(2) << 30) | (s << 29) | (0b100010 << 23) | (_rng.Next(2) << 22) |
                                    (_rng.Next(4096) << 10) | (SrcRegNoZr() << 5) | DstReg());
                                _flagsDefined |= s == 1;

                                return w;
                            }
                        case 2:
                        case 3:
                            {
                                // ADD/SUB(S) shifted register
                                int s = _rng.Next(2);
                                uint w = (uint)((sf << 31) | (_rng.Next(2) << 30) | (s << 29) | (0b01011 << 24) | (_rng.Next(3) << 22) |
                                    (SrcReg() << 16) | (_rng.Next(width) << 10) | (SrcReg() << 5) | DstReg());
                                _flagsDefined |= s == 1;

                                return w;
                            }
                        case 4:
                        case 5:
                            {
                                // AND/ORR/EOR/ANDS shifted register (N random)
                                int opc = _rng.Next(4);
                                uint w = (uint)((sf << 31) | (opc << 29) | (0b01010 << 24) | (_rng.Next(4) << 22) | (_rng.Next(2) << 21) |
                                    (SrcReg() << 16) | (_rng.Next(width) << 10) | (SrcReg() << 5) | DstReg());
                                _flagsDefined |= opc == 3;

                                return w;
                            }
                        case 6:
                            {
                                // MOVN/MOVZ/MOVK
                                int[] opcs = [0, 2, 3];
                                int opc = opcs[_rng.Next(3)];
                                int hw = _rng.Next(sf == 1 ? 4 : 2);
                                int rd = opc == 3 ? ReadWriteReg() : DstReg(); // MOVK keeps the other bits

                                return (uint)((sf << 31) | (opc << 29) | (0b100101 << 23) | (hw << 21) | (_rng.Next(65536) << 5) | rd);
                            }
                        case 7:
                            {
                                // SBFM/BFM/UBFM
                                int opc = _rng.Next(3);
                                int rn = SrcReg();
                                int rd = opc == 1 ? ReadWriteReg() : DstReg(); // BFM keeps the other bits

                                return (uint)((sf << 31) | (opc << 29) | (0b100110 << 23) | (sf << 22) | (_rng.Next(width) << 16) |
                                    (_rng.Next(width) << 10) | (rn << 5) | rd);
                            }
                        case 8:
                            {
                                // MADD/MSUB
                                return (uint)((sf << 31) | (0b11011 << 24) | (SrcReg() << 16) | (_rng.Next(2) << 15) | (SrcReg() << 10) |
                                    (SrcReg() << 5) | DstReg());
                            }
                        case 9:
                            {
                                // UDIV/SDIV/LSLV/LSRV/ASRV/RORV
                                int[] ops = [0b000010, 0b000011, 0b001000, 0b001001, 0b001010, 0b001011];

                                return (uint)((sf << 31) | (0b11010110 << 21) | (SrcReg() << 16) | (ops[_rng.Next(ops.Length)] << 10) |
                                    (SrcReg() << 5) | DstReg());
                            }
                        case 10:
                            {
                                // RBIT/REV16/REV(32)/REV/CLZ/CLS
                                int op = _rng.Next(sf == 1 ? 6 : 6);

                                if (sf == 0 && op == 3)
                                {
                                    op = 2;
                                }

                                return (uint)((sf << 31) | (1 << 30) | (0b11010110 << 21) | (op << 10) | (SrcReg() << 5) | DstReg());
                            }
                        case 11:
                        case 12:
                            {
                                // CSEL/CSINC/CSINV/CSNEG
                                if (!CanReadFlags)
                                {
                                    continue;
                                }

                                return (uint)((sf << 31) | (_rng.Next(2) << 30) | (0b11010100 << 21) | (SrcReg() << 16) | (_rng.Next(15) << 12) |
                                    (_rng.Next(2) << 10) | (SrcReg() << 5) | DstReg());
                            }
                        case 13:
                            {
                                // CCMP/CCMN register or immediate
                                if (!CanReadFlags)
                                {
                                    continue;
                                }

                                int imm = _rng.Next(2);
                                int rm = imm == 1 ? _rng.Next(32) : SrcReg();

                                return (uint)((sf << 31) | (_rng.Next(2) << 30) | (1 << 29) | (0b11010010 << 21) | (rm << 16) | (_rng.Next(15) << 12) |
                                    (imm << 11) | (SrcReg() << 5) | _rng.Next(16));
                            }
                        case 14:
                            {
                                // ADC/SBC/ADCS/SBCS
                                if (!CanReadFlags)
                                {
                                    continue;
                                }

                                return (uint)((sf << 31) | (_rng.Next(2) << 30) | (_rng.Next(2) << 29) | (0b11010000 << 21) | (SrcReg() << 16) |
                                    (SrcReg() << 5) | DstReg());
                            }
                        case 15:
                        case 16:
                            {
                                // LDR/STR (unsigned offset) on the data page; LDRSW for 32-bit loads sometimes
                                int size = _rng.Next(4);
                                int maxIndex = (int)(DataSize >> size) - 1;
                                int opc = _rng.Next(2);

                                if (opc == 1 && size == 2 && _rng.Next(4) == 0)
                                {
                                    opc = 2; // LDRSW
                                }

                                int rt = opc == 0 ? SrcReg() : DstReg();

                                return (uint)((size << 30) | (0b111001 << 24) | (opc << 22) | (_rng.Next(maxIndex) << 10) | (DataReg << 5) | rt);
                            }
                        case 17:
                            {
                                // LDP/STP (signed offset, non-negative) on the data page
                                int is64 = _rng.Next(2);
                                int l = _rng.Next(2);
                                int scale = is64 == 1 ? 8 : 4;
                                int imm7 = _rng.Next(Math.Min(63, (int)(DataSize / (ulong)scale) - 2));
                                int rt, rt2;

                                if (l == 1)
                                {
                                    rt = DstReg();

                                    do
                                    {
                                        rt2 = DstReg();
                                    }
                                    while (rt2 == rt);
                                }
                                else
                                {
                                    rt = SrcReg();
                                    rt2 = SrcReg();
                                }

                                return (uint)(((is64 == 1 ? 2 : 0) << 30) | (0b101 << 27) | (0b010 << 23) | (l << 22) | (imm7 << 15) |
                                    (rt2 << 10) | (DataReg << 5) | rt);
                            }
                        default:
                            {
                                // SUBS/ADDS immediate as a compare (sets flags, result to XZR)
                                _flagsDefined = true;

                                return (uint)((sf << 31) | (_rng.Next(2) << 30) | (1 << 29) | (0b100010 << 23) | (_rng.Next(4096) << 10) |
                                    (SrcRegNoZr() << 5) | 31);
                            }
                    }
                }
            }

            // ADD/SUB immediate read SP for register 31: avoid it.
            private int SrcRegNoZr()
            {
                int r;

                do
                {
                    r = SrcReg();
                }
                while (r == 31);

                return r;
            }

            /// <summary>A conditional branch to <paramref name="offsetWords"/> ahead (B.cond, CBZ/CBNZ or TBZ/TBNZ).</summary>
            public uint ForwardBranch(int offsetWords)
            {
                int kind = _rng.Next(3);

                if (kind == 0 && CanReadFlags)
                {
                    return 0x54000000u | ((uint)(offsetWords & 0x7FFFF) << 5) | (uint)_rng.Next(15);
                }

                if (kind == 1)
                {
                    return (uint)((Sf() << 31) | (0b011010 << 25) | (_rng.Next(2) << 24) | ((offsetWords & 0x7FFFF) << 5) | SrcReg());
                }

                int bit = _rng.Next(64);

                return (uint)(((bit >> 5) << 31) | (0b011011 << 25) | (_rng.Next(2) << 24) | ((bit & 31) << 19) | ((offsetWords & 0x3FFF) << 5) | SrcReg());
            }

            /// <summary>
            /// After a call: under AAPCS64 rules a compare redefines the flags at once (so they are defined on every
            /// path, including branches over later flag setters), and with the temporary-register rule X8-X17 are
            /// rewritten before any use.
            /// </summary>
            public void NoteCall(List<uint> words)
            {
                AfterCall();

                if (_abiFlags)
                {
                    words.Add((uint)((1 << 31) | (1 << 30) | (1 << 29) | (0b100010 << 23) | (_rng.Next(4096) << 10) | (SrcRegNoZr() << 5) | 31));
                    _flagsDefined = true;
                }

                if (_abiTemps)
                {
                    for (int r = 8; r <= 17; r++)
                    {
                        words.Add(0xD2800000u | ((uint)_rng.Next(65536) << 5) | (uint)r);
                        _tempsDefined[r] = true;
                    }
                }
            }

            /// <summary>Main's entry: the flags and registers are the test's initial values, all defined.</summary>
            public void StartMain()
            {
                _flagsDefined = true;
                Array.Fill(_tempsDefined, true);
            }

            public int Next(int max) => _rng.Next(max);
        }

        private static Program Generate(int seed, int length, bool abiFlags, bool abiTemps, bool calls)
        {
            Generator g = new(seed, abiFlags, abiTemps);
            Program p = new();
            List<(int Index, int Target)> fixups = []; // branch at Index to instruction Target (filled later)
            List<int> callSites = [];

            // main: save LR (the test ends when main returns to 0).
            g.StartMain();
            p.Words.Add(0xAA1E03E0u | LinkSaveReg); // MOV x26, x30

            int i = 0;

            while (i < length)
            {
                int choice = g.Next(20);

                if (choice == 0 && i + 12 < length)
                {
                    // Counted loop: MOVZ x27, #k; body; SUB x27, x27, #1; CBNZ x27, body
                    int k = 1 + g.Next(4);
                    p.Words.Add(0xD2800000u | ((uint)k << 5) | LoopReg);
                    int bodyStart = p.Words.Count;
                    int bodyLength = 2 + g.Next(8);

                    for (int b = 0; b < bodyLength; b++)
                    {
                        p.Words.Add(g.Instruction());
                    }

                    p.Words.Add(0xD1000400u | (LoopReg << 5) | LoopReg); // SUB x27, x27, #1
                    int back = bodyStart - p.Words.Count;
                    p.Words.Add(0xB5000000u | ((uint)(back & 0x7FFFF) << 5) | LoopReg); // CBNZ x27, body
                    i += bodyLength + 3;
                }
                else if (choice == 1)
                {
                    // Forward branch to a later top-level instruction (resolved below).
                    fixups.Add((p.Words.Count, -1));
                    p.Words.Add(0);
                    i++;
                }
                else if (choice == 2 && calls)
                {
                    callSites.Add(p.Words.Count);
                    p.Words.Add(0); // BL callee
                    g.NoteCall(p.Words);
                    i++;
                }
                else
                {
                    p.Words.Add(g.Instruction());
                    i++;
                }
            }

            p.Words.Add(0xAA0003FEu | (LinkSaveReg << 16)); // MOV x30, x26
            p.EndIndex = p.Words.Count;
            p.Words.Add(0xD65F03C0); // RET

            // Callee: a few instructions and RET (does not touch x26-x28, x30).
            p.CalleeIndex = p.Words.Count;
            g.StartFunction();
            int calleeLength = 3 + g.Next(10);

            for (int c = 0; c < calleeLength; c++)
            {
                p.Words.Add(g.Instruction());
            }

            p.Words.Add(0xD65F03C0);

            foreach (int site in callSites)
            {
                p.Words[site] = 0x94000000u | (uint)((p.CalleeIndex - site) & 0x3FFFFFF);
            }

            // Forward branches: target a later instruction of main that is not inside a loop body (loop bodies are
            // entered only through their MOVZ), or main's epilogue.
            HashSet<int> loopInterior = [];

            for (int w = 0; w < p.EndIndex; w++)
            {
                uint word = p.Words[w];

                if ((word & 0xFF000000u) == 0xB5000000u && (word & 31) == LoopReg)
                {
                    int back = (int)((word >> 5) & 0x7FFFF);
                    back = (back << 13) >> 13;

                    for (int t = w + back; t <= w; t++)
                    {
                        loopInterior.Add(t);
                    }
                }
            }

            // Flags are defined at every top-level position (initial values, or the compare after each call), so
            // the branch generator may read them; it never reads an undefined temporary either (main starts with all
            // registers defined, and each call is followed by the redefinitions above).
            Generator branchGen = new(seed ^ 0x5bd1e995, abiFlags, abiTemps);
            branchGen.StartMain();

            foreach ((int index, _) in fixups)
            {
                List<int> targets = [];

                for (int t = index + 1; t <= p.EndIndex - 1; t++)
                {
                    if (!loopInterior.Contains(t))
                    {
                        targets.Add(t);
                    }
                }

                int target = targets.Count == 0 ? index + 1 : targets[branchGen.Next(Math.Min(targets.Count, 24))];

                uint branch;

                branch = branchGen.ForwardBranch(target - index);

                p.Words[index] = loopInterior.Contains(index) ? 0xD503201Fu /* NOP */ : branch;
            }

            return p;
        }

        private readonly struct State
        {
            public readonly ulong[] X;
            public readonly bool N, Z, C, V;
            public readonly byte[] Data;

            public State(ulong[] x, bool n, bool z, bool c, bool v, byte[] data)
            {
                X = x;
                N = n;
                Z = z;
                C = c;
                V = v;
                Data = data;
            }
        }

        private static (ulong[] X, bool N, bool Z, bool C, bool V, byte[] Data) InitialState(int seed)
        {
            Random rng = new(seed * 7919 + 13);
            ulong[] x = new ulong[31];

            for (int r = 0; r < 31; r++)
            {
                x[r] = rng.Next(4) switch
                {
                    0 => (ulong)rng.Next(16),
                    1 => (ulong)rng.NextInt64() | (rng.Next(2) == 0 ? 0x8000_0000_0000_0000UL : 0),
                    2 => 0xFFFF_FFFF_0000_0000UL | (uint)rng.Next(),
                    _ => (uint)rng.Next() | ((ulong)rng.Next(2) << 31),
                };
            }

            x[DataReg] = DataBase;
            x[LoopReg] = 0;
            x[LinkSaveReg] = 0;
            x[30] = 0;

            byte[] data = new byte[DataSize];
            rng.NextBytes(data);

            return (x, rng.Next(2) == 1, rng.Next(2) == 1, rng.Next(2) == 1, rng.Next(2) == 1, data);
        }

        private static State RunArmeilleure(Program p, int seed, CpuV2Features features)
        {
            CpuV2Features saved = CpuV2.Features;
            bool savedHighCq = Optimizations.ForceHighCq;

            CpuV2.Features = features;
            Optimizations.ForceHighCq = true;
            Optimizations.AllowLcqInFunctionTable = false;
            Optimizations.UseUnmanagedDispatchLoop = false;

            ulong pageSize = MemoryBlock.GetPageSize();
            MemoryBlock ram = new(CodeSize + DataSize + pageSize);
            MemoryManager memory = new(ram, 1ul << 24);
            memory.IncrementReferenceCount();

            try
            {
                memory.Map(CodeBase, 0, CodeSize, MemoryMapFlags.Private);
                memory.Map(DataBase, CodeSize, DataSize, MemoryMapFlags.Private);

                for (int w = 0; w < p.Words.Count; w++)
                {
                    memory.Write(CodeBase + (ulong)w * 4, p.Words[w]);
                }

                (ulong[] x, bool n, bool z, bool c, bool v, byte[] data) = InitialState(seed);
                memory.Write(DataBase, data);

                ExecutionContext context = CpuContext.CreateExecutionContext();

                for (int r = 0; r < 31; r++)
                {
                    context.SetX(r, x[r]);
                }

                context.SetPstateFlag(PState.NFlag, n);
                context.SetPstateFlag(PState.ZFlag, z);
                context.SetPstateFlag(PState.CFlag, c);
                context.SetPstateFlag(PState.VFlag, v);

                CpuContext cpu = new(memory, for64Bit: true);
                cpu.Execute(context, CodeBase);

                ulong[] result = new ulong[31];

                for (int r = 0; r < 31; r++)
                {
                    result[r] = context.GetX(r);
                }

                State state = new(result,
                    context.GetPstateFlag(PState.NFlag), context.GetPstateFlag(PState.ZFlag),
                    context.GetPstateFlag(PState.CFlag), context.GetPstateFlag(PState.VFlag),
                    memory.GetSpan(DataBase, (int)DataSize).ToArray());

                context.Dispose();

                return state;
            }
            finally
            {
                memory.DecrementReferenceCount();
                ram.Dispose();
                CpuV2.Features = saved;
                Optimizations.ForceHighCq = savedHighCq;
            }
        }

        private static State RunUnicorn(Program p, int seed)
        {
            using UnicornAArch64 uc = new();
            uc.MemoryMap(CodeBase, CodeSize, MemoryPermission.Read | MemoryPermission.Exec);
            uc.MemoryMap(DataBase, DataSize, MemoryPermission.Read | MemoryPermission.Write);

            byte[] code = new byte[p.Words.Count * 4];

            for (int w = 0; w < p.Words.Count; w++)
            {
                BitConverter.GetBytes(p.Words[w]).CopyTo(code, w * 4);
            }

            uc.MemoryWrite(CodeBase, code);

            (ulong[] x, bool n, bool z, bool c, bool v, byte[] data) = InitialState(seed);
            uc.MemoryWrite(DataBase, data);

            for (int r = 0; r < 31; r++)
            {
                uc.X[r] = x[r];
            }

            uc.NegativeFlag = n;
            uc.ZeroFlag = z;
            uc.CarryFlag = c;
            uc.OverflowFlag = v;
            uc.PC = CodeBase;

            // Stop at main's final RET (x30 = 0 there); ARMeilleure executes it and returns to address 0.
            uc.RunUntil(CodeBase + (ulong)p.EndIndex * 4);

            ulong[] result = new ulong[31];

            for (int r = 0; r < 31; r++)
            {
                result[r] = uc.X[r];
            }

            return new State(result, uc.NegativeFlag, uc.ZeroFlag, uc.CarryFlag, uc.OverflowFlag, uc.MemoryRead(DataBase, DataSize));
        }

        private static string Compare(State expected, State actual, bool flags, bool temps)
        {
            for (int r = 0; r < 31; r++)
            {
                if (!temps && r is >= 8 and <= 17)
                {
                    continue;
                }

                if (expected.X[r] != actual.X[r])
                {
                    return $"X{r}: expected {expected.X[r]:x16}, got {actual.X[r]:x16}";
                }
            }

            if (flags && (expected.N != actual.N || expected.Z != actual.Z || expected.C != actual.C || expected.V != actual.V))
            {
                return $"NZCV: expected {(expected.N ? 1 : 0)}{(expected.Z ? 1 : 0)}{(expected.C ? 1 : 0)}{(expected.V ? 1 : 0)}, " +
                    $"got {(actual.N ? 1 : 0)}{(actual.Z ? 1 : 0)}{(actual.C ? 1 : 0)}{(actual.V ? 1 : 0)}";
            }

            for (int b = 0; b < expected.Data.Length; b++)
            {
                if (expected.Data[b] != actual.Data[b])
                {
                    return $"data[{b:x}]: expected {expected.Data[b]:x2}, got {actual.Data[b]:x2}";
                }
            }

            return null;
        }

        private static void RunCampaign(int firstSeed, int count, CpuV2Features features, bool abiFlags, bool abiTemps)
        {
            int failures = 0;
            List<string> messages = [];

            for (int seed = firstSeed; seed < firstSeed + count; seed++)
            {
                int length = 20 + (seed % 7) * 25;
                Program p = Generate(seed, length, abiFlags, abiTemps, calls: true);

                if (Environment.GetEnvironmentVariable("BOTWPC_FUZZ_TRACE") is string trace && trace.Length != 0)
                {
                    System.IO.File.WriteAllText(trace, $"{seed} {(int)features:x}");
                }

                State reference = RunUnicorn(p, seed);
                State v2 = RunArmeilleure(p, seed, features);
                string v2Error = Compare(reference, v2, flags: !abiFlags, temps: !abiTemps);

                // Unicorn is the reference. The legacy code generator is run only on request
                // (BOTWPC_FUZZ_LEGACY=1): it has latent defects that the random programs hit (wrong values, and a
                // native crash of the test host on seed 100915), see docs/RUNTIME_V2_DIFFERENTIAL_TESTS.md.
                string legacyError = null;

                if (Environment.GetEnvironmentVariable("BOTWPC_FUZZ_LEGACY") == "1")
                {
                    legacyError = Compare(reference, RunArmeilleure(p, seed, CpuV2Features.None), flags: true, temps: true);
                }

                if (legacyError != null)
                {
                    // A legacy/Unicorn difference is not a CPU V2 defect, but makes the program useless as a reference.
                    messages.Add($"seed {seed}: legacy vs Unicorn: {legacyError}");
                }

                if (v2Error != null)
                {
                    failures++;
                    messages.Add($"seed {seed}: CPU V2 0x{(int)features:x} vs Unicorn: {v2Error}");
                }
            }

            foreach (string m in messages)
            {
                TestContext.Progress.WriteLine(m);
            }

            TestContext.Progress.WriteLine($"CPU V2 0x{(int)features:x}: {count} random programs, {failures} mismatches");
            Assert.That(failures, Is.Zero, string.Join("\n", messages));
        }

        /// <summary>
        /// Debugging aid: BOTWPC_FUZZ_SEED, BOTWPC_FUZZ_FEATURES (hex), BOTWPC_FUZZ_ABI (0 none, 1 flags, 2 flags +
        /// temporaries) and BOTWPC_FUZZ_OUT (directory): reruns one program and writes its words and the IR of every
        /// compiled function (legacy and CPU V2).
        /// </summary>
        [Test]
        public void SingleProgram()
        {
            string seedText = Environment.GetEnvironmentVariable("BOTWPC_FUZZ_SEED");

            if (string.IsNullOrEmpty(seedText))
            {
                Assert.Ignore("BOTWPC_FUZZ_SEED not set");
            }

            int seed = int.Parse(seedText);
            ARMeilleure.CodeGen.Optimizations.ValueNumbering.DebugMode = int.Parse(Environment.GetEnvironmentVariable("BOTWPC_FUZZ_VN_DEBUG") ?? "0");
            CpuV2Features features = (CpuV2Features)int.Parse(Environment.GetEnvironmentVariable("BOTWPC_FUZZ_FEATURES") ?? "7", System.Globalization.NumberStyles.HexNumber);
            CpuV2Features baseline = (CpuV2Features)int.Parse(Environment.GetEnvironmentVariable("BOTWPC_FUZZ_BASELINE") ?? "0", System.Globalization.NumberStyles.HexNumber);
            int abi = int.Parse(Environment.GetEnvironmentVariable("BOTWPC_FUZZ_ABI") ?? "0");
            string outDir = Environment.GetEnvironmentVariable("BOTWPC_FUZZ_OUT");
            Program p = Generate(seed, 20 + (seed % 7) * 25, abi >= 1, abi >= 2, calls: true);

            if (Environment.GetEnvironmentVariable("BOTWPC_FUZZ_REDUCE") == "1")
            {
                Reduce(p, seed, features, abi);
            }

            System.Text.StringBuilder ir = new();
            if (Environment.GetEnvironmentVariable("BOTWPC_FUZZ_IR") == "1")
            {
                ARMeilleure.Translation.Compiler.Observer = (stage, cfg) => ir.AppendLine($"===== {stage}").AppendLine(ARMeilleure.Diagnostics.IRDumper.GetDump(cfg));
            }

            try
            {
                State reference = RunUnicorn(p, seed);
                string order = Environment.GetEnvironmentVariable("BOTWPC_FUZZ_ORDER") ?? "lv";
                State legacy = reference, v2 = reference;

                foreach (char c in order)
                {
                    ir.AppendLine(c == 'l' ? "################ legacy" : "################ v2");

                    if (c == 'l')
                    {
                        legacy = RunArmeilleure(p, seed, baseline);
                    }
                    else
                    {
                        v2 = RunArmeilleure(p, seed, features);
                    }
                }

                string report = $"legacy: {Compare(reference, legacy, true, true) ?? "ok"}; v2: {Compare(reference, v2, abi < 1, abi < 2) ?? "ok"}";

                if (!string.IsNullOrEmpty(outDir))
                {
                    System.IO.Directory.CreateDirectory(outDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"seed{seed}.ir.txt"), ir.ToString());
                    System.IO.File.WriteAllLines(System.IO.Path.Combine(outDir, $"seed{seed}.words.txt"), p.Words.ConvertAll(w => w.ToString("x8")));
                    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, $"seed{seed}.result.txt"), report + Environment.NewLine + ReduceLog + Environment.NewLine +
                        string.Join(Environment.NewLine, ARMeilleure.CodeGen.Optimizations.ValueNumbering.DebugLog));
                }
            }
            finally
            {
                ARMeilleure.Translation.Compiler.Observer = null;
            }
        }

        /// <summary>
        /// Greedy reduction: replaces instructions by NOP (never the program skeleton: LR save/restore, RET, loop
        /// control, calls and branches) as long as CPU V2 still differs from Unicorn while the legacy code generator
        /// matches it. Changes <paramref name="p"/> in place.
        /// </summary>
        private static string ReduceLog = "";

        private static void Reduce(Program p, int seed, CpuV2Features features, int abi)
        {
            bool Fails()
            {
                State reference = RunUnicorn(p, seed);

                return Compare(reference, RunArmeilleure(p, seed, CpuV2Features.None), true, true) == null &&
                    Compare(reference, RunArmeilleure(p, seed, features), abi < 1, abi < 2) != null;
            }

            if (!Fails())
            {
                ReduceLog = "initial program does not fail";

                return;
            }

            int attempts = 0, kept = 0;

            bool changed = true;

            while (changed)
            {
                changed = false;

                for (int i = 1; i < p.Words.Count; i++)
                {
                    uint w = p.Words[i];
                    bool skeleton = w == 0xD503201Fu || w == 0xD65F03C0u || (w & 0xFC000000u) == 0x94000000u ||
                        (w & 0xFF000000u) == 0x54000000u || (w & 0x7E000000u) == 0x34000000u || (w & 0x7E000000u) == 0x36000000u ||
                        (w & 0xFFE0001Fu) == (0xD2800000u | LoopReg) || w == (0xD1000400u | (LoopReg << 5) | LoopReg) ||
                        w == (0xAA0003FEu | (LinkSaveReg << 16)) || w == (0xAA1E03E0u | LinkSaveReg);

                    if (skeleton)
                    {
                        continue;
                    }

                    p.Words[i] = 0xD503201Fu;
                    attempts++;

                    if (Fails())
                    {
                        changed = true;
                        kept++;
                    }
                    else
                    {
                        p.Words[i] = w;
                    }
                }
            }

            ReduceLog = $"reduce: {attempts} attempts, {kept} instructions removed";
        }

        [Test]
        public void ExactFeaturesMatchUnicorn([Values(0, 1, 2, 3)] int batch)
        {
            RunCampaign(1 + batch * 250, 250, CpuV2Features.Exact, abiFlags: false, abiTemps: false);
        }

        [Test]
        public void FlagLivenessMatchesUnicornForConformingPrograms([Values(0, 1, 2, 3)] int batch)
        {
            RunCampaign(100_001 + batch * 250, 250, CpuV2Features.Exact | CpuV2Features.AbiFlagLiveness, abiFlags: true, abiTemps: false);
        }

        [Test]
        public void TemporaryLivenessMatchesUnicornForConformingPrograms([Values(0, 1)] int batch)
        {
            RunCampaign(200_001 + batch * 250, 250, CpuV2Features.Exact | CpuV2Features.AbiCallLiveness, abiFlags: true, abiTemps: true);
        }

        /// <summary>CPU V3: the register-preserving synchronization stub, called at almost every function entry and
        /// loop back edge (interval 1), must leave every register and the data memory as Unicorn does.</summary>
        [Test]
        public void PreservingSyncCallMatchesUnicorn([Values(0, 1)] int batch)
        {
            int saved = ExecutionContext.SyncCheckInterval;
            ExecutionContext.SyncCheckInterval = 1;

            try
            {
                RunCampaign(300_001 + batch * 250, 250, CpuV2Features.Exact | CpuV2Features.PreservingSyncCall, abiFlags: false, abiTemps: false);
                RunCampaign(310_001 + batch * 250, 250, CpuV2Features.Exact | CpuV2Features.AbiFlagLiveness | CpuV2Features.PreservingSyncCall, abiFlags: true, abiTemps: false);
            }
            finally
            {
                ExecutionContext.SyncCheckInterval = saved;
            }
        }

        /// <summary>CPU V3: floating-point values held in caller-saved XMM registers across a loop back edge survive
        /// the synchronization stub (the random programs are integer only).</summary>
        [Test]
        public void PreservingSyncCallKeepsVectorRegisters([Values(1, 2, 7)] int interval)
        {
            Program p = new();
            List<uint> w = p.Words;

            for (int d = 0; d < 12; d++)
            {
                w.Add(0x9E620000u | ((uint)(d + 1) << 5) | (uint)d); // scvtf d<d>, x<d+1> (finite values)
            }

            w.Add(0xD2800000u); // movz x0, #0
            int loop = w.Count;

            for (int d = 0; d < 12; d += 2)
            {
                w.Add(0x1E602800u | ((uint)(d + 1) << 16) | ((uint)d << 5) | (uint)d); // fadd d<d>, d<d>, d<d+1>
            }

            w.Add(0x8B020021u); // add x1, x1, x2 (an integer value carried around the loop too)
            w.Add(0x91000400u); // add x0, x0, #1
            w.Add(0xF103201Fu); // cmp x0, #200
            int back = loop - w.Count;
            w.Add(0x5400000Bu | (((uint)back & 0x7FFFF) << 5)); // b.lt loop

            for (int d = 0; d < 12; d++)
            {
                w.Add(0x9E660000u | ((uint)d << 5) | (uint)(d + 3)); // fmov x<d+3>, d<d>
            }

            p.EndIndex = w.Count;
            w.Add(0xD65F03C0u); // ret

            int saved = ExecutionContext.SyncCheckInterval;
            ExecutionContext.SyncCheckInterval = interval;

            try
            {
                for (int seed = 1; seed <= 20; seed++)
                {
                    State reference = RunUnicorn(p, seed);
                    State v3 = RunArmeilleure(p, seed, CpuV2Features.Exact | CpuV2Features.PreservingSyncCall);
                    string error = Compare(reference, v3, flags: true, temps: true);
                    Assert.That(error, Is.Null, $"seed {seed}, interval {interval}: {error}");
                }
            }
            finally
            {
                ExecutionContext.SyncCheckInterval = saved;
            }
        }
    }
}
