using ARMeilleure.Memory;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.LightningJit.State;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

[assembly: InternalsVisibleTo("Ryujinx.Tests")]

namespace Ryujinx.Cpu.LightningJit.Interpreter
{
    /// <summary>
    /// ARMv8.0-A (A64) interpreter for guest code that is neither in the ahead-of-time image nor can be compiled (iPadOS
    /// without JIT). It is entered like a translated function: native code calls <see cref="EntryPointer"/> with the
    /// native context in X0 and the guest address in its dispatch address, and gets the next guest address back. Its
    /// control flow is that of a translated function: calls go through the dispatch stub (native or interpreted callee)
    /// and continue when the callee returns to the next instruction; branches to code that has native code tail-call it,
    /// other branches stay in the interpreter; RET returns the target. Guest state lives in the native context; the host
    /// FPCR/FPSR are the guest's while guest code runs (the dispatch loop swaps them), so FP operations done here behave
    /// like the guest's.
    /// </summary>
    unsafe sealed partial class A64Interpreter
    {
        private static readonly int _xOffset = NativeContext.GetXOffset();
        private static readonly int _vOffset = NativeContext.GetVOffset();
        private static readonly int _flagsOffset = NativeContext.GetFlagsOffset();
        private static readonly int _tpidrOffset = NativeContext.GetTpidrEl0Offset();
        private static readonly int _tpidrroOffset = NativeContext.GetTpidrroEl0Offset();
        private static readonly int _counterOffset = NativeContext.GetCounterOffset();
        private static readonly int _dispatchOffset = NativeContext.GetDispatchAddressOffset();

        private const uint FpcrMask = 0x07F79F00;
        private const uint FpsrMask = 0xF800009F;
        private const int DczBlockSize = 64; // DCZID_EL0 = 4, as on the Apple hosts the native code runs on

        /// <summary>Native entry: X0 = native context, guest address in the context's dispatch address.</summary>
        public static nint EntryPointer { get; } = (nint)(delegate* unmanaged<nint, ulong>)&Entry;

        [ThreadStatic]
        private static A64Interpreter _threadInstance;

        private readonly Translator _translator;
        private readonly IMemoryManager _memory;
        private readonly Dictionary<ulong, uint[]> _codePages = new();
        private readonly Dictionary<ulong, nint> _nativeTargets = new();

        private byte* _ctx;
        private int _codeGeneration;
        private int _depth;

        // Tail calls into native code nest (the native code may tail-call back into the interpreter); past this depth they
        // return the target instead, as translated code does when a callee returns elsewhere, and the dispatch loop goes on.
        private const int MaxTailCallDepth = 32;

        // Exclusive monitor (LDXR/LDAXR .. STXR/STLXR).
        private ulong _exclusiveAddress = ulong.MaxValue;
        private ulong _exclusiveValue;
        private int _exclusiveSize;

        private static long _instructions;

        internal A64Interpreter(Translator translator, IMemoryManager memory)
        {
            _translator = translator;
            _memory = memory;
        }

        [UnmanagedCallersOnly]
        private static ulong Entry(nint context)
        {
            A64Interpreter interpreter = _threadInstance;

            if (interpreter == null || interpreter._translator != NativeInterface.GetTranslator())
            {
                interpreter = _threadInstance = new A64Interpreter(NativeInterface.GetTranslator(), NativeInterface.GetMemoryManager());
            }

            byte* previous = interpreter._ctx;
            interpreter._ctx = (byte*)context;
            interpreter._depth++;

            try
            {
                return interpreter.Run(*(ulong*)((byte*)context + _dispatchOffset));
            }
            catch (Exception e)
            {
                // An exception must not unwind into native code.
                Logger.Error?.Print(LogClass.Cpu, $"Interpreter: {e}");
                Environment.FailFast($"Interpreter: {e.Message}", e);
                return 0;
            }
            finally
            {
                interpreter._ctx = previous;
                interpreter._depth--;
            }
        }

        internal static long InstructionCount => System.Threading.Interlocked.Read(ref _instructions);

        // ---- Guest state ----

        private ulong* X => (ulong*)(_ctx + _xOffset);

        private ref uint Nzcv => ref *(uint*)(_ctx + _flagsOffset);

        private ref Vector128<byte> V(int index) => ref *(Vector128<byte>*)(_ctx + _vOffset + index * 16);

        /// <summary>Register read where 31 is the zero register.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong R(uint n) => n == 31 ? 0 : X[n];

        /// <summary>Register read where 31 is the stack pointer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong RSp(uint n) => X[n];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void W(uint n, ulong value)
        {
            if (n != 31)
            {
                X[n] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WSp(uint n, ulong value) => X[n] = value;

        /// <summary>Writes a 64-bit, or zero-extended 32-bit, result.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void W(uint n, ulong value, bool is64) => W(n, is64 ? value : (uint)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WSp(uint n, ulong value, bool is64) => WSp(n, is64 ? value : (uint)value);

        private bool ConditionHolds(uint cond)
        {
            uint f = Nzcv;
            bool n = (f & (1u << 31)) != 0, z = (f & (1u << 30)) != 0, c = (f & (1u << 29)) != 0, v = (f & (1u << 28)) != 0;

            bool result = (cond >> 1) switch
            {
                0 => z,
                1 => c,
                2 => n,
                3 => v,
                4 => c && !z,
                5 => n == v,
                6 => n == v && !z,
                _ => true,
            };

            return (cond & 1) != 0 && cond != 15 ? !result : result;
        }

        private void SetNzcv(bool n, bool z, bool c, bool v)
        {
            Nzcv = (n ? 1u << 31 : 0) | (z ? 1u << 30 : 0) | (c ? 1u << 29 : 0) | (v ? 1u << 28 : 0);
        }

        // ---- Execution ----

        /// <summary>An instruction the interpreter does not implement.</summary>
        private sealed class UnsupportedInstructionException(ulong pc, uint encoding)
            : Exception($"Interpreter: unsupported instruction 0x{encoding:x8} at 0x{pc:x}")
        {
            public ulong Pc { get; } = pc;
            public uint Encoding { get; } = encoding;
        }

        private static UnsupportedInstructionException Unsupported(ulong pc, uint encoding) => new(pc, encoding);

        private uint Fetch(ulong pc)
        {
            ulong page = pc & ~0xFFFUL;

            if (!_codePages.TryGetValue(page, out uint[] words))
            {
                words = MemoryMarshal.Cast<byte, uint>(_memory.GetSpan(page, 0x1000)).ToArray();
                _codePages[page] = words;
            }

            return words[(pc & 0xFFF) >> 2];
        }

        /// <summary>Runs guest code from <paramref name="pc"/> until it leaves this "function"; returns the next address.</summary>
        internal ulong Run(ulong pc)
        {
            long count = 0;

            if (_codeGeneration != Translator.CodeGeneration)
            {
                _codeGeneration = Translator.CodeGeneration;
                _codePages.Clear();
                _nativeTargets.Clear();
            }

            try
            {
                while (true)
                {
                    uint inst = Fetch(pc);
                    count++;

                    // Branches, exception generation and system: op0 = 101x.
                    if ((inst & 0x1C000000) == 0x14000000 || (inst & 0xFFFF0000) == 0)
                    {
                        if (ExecuteBranchOrSystem(inst, ref pc, out ulong exit))
                        {
                            return exit;
                        }

                        continue;
                    }

                    Execute(inst, pc);
                    pc += 4;
                }
            }
            catch (UnsupportedInstructionException e)
            {
                return FallBackToJit(e);
            }
            finally
            {
                long total = System.Threading.Interlocked.Add(ref _instructions, count);

                if ((total >> 28) != ((total - count) >> 28))
                {
                    Logger.Info?.Print(LogClass.Cpu, $"Interpreter: {total >> 20} M instructions so far");
                }
            }
        }

        private void Execute(uint inst, ulong pc)
        {
            switch ((inst >> 25) & 0xF)
            {
                case 0b1000:
                case 0b1001:
                    ExecuteDataProcessingImmediate(inst, pc);
                    break;
                case 0b0100:
                case 0b0110:
                case 0b1100:
                case 0b1110:
                    ExecuteLoadStore(inst, pc);
                    break;
                case 0b0101:
                case 0b1101:
                    ExecuteDataProcessingRegister(inst, pc);
                    break;
                case 0b0111:
                case 0b1111:
                    ExecuteSimdFp(inst, pc);
                    break;
                default:
                    throw Unsupported(pc, inst);
            }
        }

        /// <summary>
        /// An instruction the interpreter lacks: the old behaviour, ask the host for JIT (the game pauses until it is
        /// available), then continue in a compiled function from that instruction.
        /// </summary>
        private ulong FallBackToJit(UnsupportedInstructionException e)
        {
            Logger.Warning?.Print(LogClass.Cpu, e.Message);

            if (!AotImage.JitAvailable && !AotImage.TryAcquireJit(e.Pc))
            {
                Environment.FailFast(e.Message);
            }

            // Not through the dispatch stub: the address may be an interpreted entry.
            *(ulong*)(_ctx + _dispatchOffset) = e.Pc;

            return ((delegate* unmanaged<nint, ulong>)_translator.GetCompiledFunction(e.Pc))((nint)_ctx);
        }

        // ---- Calls into native code ----

        /// <summary>Calls the guest function at an address through the dispatch stub; returns the next guest address.</summary>
        private ulong Call(ulong target)
        {
            *(ulong*)(_ctx + _dispatchOffset) = target;

            return ((delegate* unmanaged<nint, ulong>)_translator.Stubs.DispatchStub)((nint)_ctx);
        }

        /// <summary>Native code for a branch target (image or translated), or 0 when the branch stays interpreted.</summary>
        private nint NativeTarget(ulong target)
        {
            if (_translator.ForceInterpreter)
            {
                return 0;
            }

            if (!_nativeTargets.TryGetValue(target, out nint pointer))
            {
                pointer = _translator.TryGetNativeFunction(target);
                _nativeTargets[target] = pointer;
            }

            return pointer;
        }

        /// <summary>A taken branch: tail call when the target has native code, else continue here.</summary>
        private bool Branch(ulong target, ref ulong pc, out ulong exit)
        {
            if (NativeTarget(target) != 0)
            {
                exit = _depth > MaxTailCallDepth ? target : Call(target);
                return true;
            }

            if (target <= pc && !SyncPoint())
            {
                exit = 0;
                return true;
            }

            pc = target;
            exit = 0;
            return false;
        }

        /// <summary>The translated code's sync point (interrupts, stop requests); false when the guest must stop.</summary>
        private bool SyncPoint()
        {
            ref int counter = ref *(int*)(_ctx + _counterOffset);

            if (counter == 0 && !NativeInterface.CheckSynchronization())
            {
                return false;
            }

            counter--;
            return true;
        }

        private uint GetFpFlags() => ((delegate* unmanaged<uint>)_translator.Stubs.GetFpFlagsStub)();

        private void SetFpFlags(uint value) => ((delegate* unmanaged<uint, void>)_translator.Stubs.SetFpFlagsStub)(value);

        // ---- Branches, exception generation, system ----

        /// <returns>True when the function returns <paramref name="exit"/>.</returns>
        private bool ExecuteBranchOrSystem(uint inst, ref ulong pc, out ulong exit)
        {
            exit = 0;

            if ((inst & 0xFFFF0000) == 0) // UDF
            {
                NativeInterface.Undefined(pc, (int)inst);

                return After(ref pc, out exit);
            }

            if ((inst & 0x7C000000) == 0x14000000) // B, BL
            {
                ulong target = pc + (ulong)((long)((ulong)inst << 38) >> 36);

                if ((inst & 0x80000000) != 0)
                {
                    return CallAndContinue(target, ref pc, out exit);
                }

                return Branch(target, ref pc, out exit);
            }

            if ((inst & 0x7E000000) == 0x34000000) // CBZ, CBNZ
            {
                ulong value = (inst & 0x80000000) != 0 ? R(inst & 31) : (uint)R(inst & 31);
                bool taken = ((inst & 0x01000000) != 0) ? value != 0 : value == 0;

                return Conditional(taken, pc + (ulong)(((long)((ulong)inst << 40) >> 45) << 2), ref pc, out exit);
            }

            if ((inst & 0x7E000000) == 0x36000000) // TBZ, TBNZ
            {
                int bit = (int)(((inst >> 26) & 0x20) | ((inst >> 19) & 0x1F));
                bool set = ((R(inst & 31) >> bit) & 1) != 0;
                bool taken = (inst & 0x01000000) != 0 ? set : !set;

                return Conditional(taken, pc + (ulong)(((long)((ulong)inst << 45) >> 50) << 2), ref pc, out exit);
            }

            if ((inst & 0xFF000010) == 0x54000000) // B.cond
            {
                return Conditional(ConditionHolds(inst & 0xF), pc + (ulong)(((long)((ulong)inst << 40) >> 45) << 2), ref pc, out exit);
            }

            if ((inst & 0xFE1FFC1F) == 0xD61F0000) // BR, BLR, RET (no pointer authentication)
            {
                ulong target = R((inst >> 5) & 31);

                switch ((inst >> 21) & 0xF)
                {
                    case 0: // BR
                        return Branch(target, ref pc, out exit);
                    case 1: // BLR
                        return CallAndContinue(target, ref pc, out exit);
                    case 2: // RET
                        exit = target;
                        return true;
                }
            }

            if ((inst & 0xFF000000) == 0xD4000000) // Exception generation
            {
                uint opc = (inst >> 21) & 7, ll = inst & 0x1F;

                if (opc == 0 && ll == 1) // SVC
                {
                    NativeInterface.SupervisorCall(pc, (int)((inst >> 5) & 0xFFFF));
                }
                else if (opc == 1 && ll == 0) // BRK
                {
                    NativeInterface.Break(pc, (int)inst);
                }
                else
                {
                    throw Unsupported(pc, inst);
                }

                return After(ref pc, out exit);
            }

            if ((inst & 0xFFC00000) == 0xD5000000) // System
            {
                ExecuteSystem(inst, pc);
                pc += 4;
                return false;
            }

            throw Unsupported(pc, inst);
        }

        private bool Conditional(bool taken, ulong target, ref ulong pc, out ulong exit)
        {
            if (taken)
            {
                return Branch(target, ref pc, out exit);
            }

            pc += 4;
            exit = 0;
            return false;
        }

        private bool CallAndContinue(ulong target, ref ulong pc, out ulong exit)
        {
            ulong next = pc + 4;

            X[30] = next;
            exit = Call(target);

            if (exit != next)
            {
                return true;
            }

            pc = next;
            return false;
        }

        /// <summary>After SVC/BRK/UDF: the translated code's sync point, then the next instruction.</summary>
        private bool After(ref ulong pc, out ulong exit)
        {
            exit = 0;

            if (!SyncPoint())
            {
                return true;
            }

            pc += 4;
            return false;
        }

        private void ExecuteSystem(uint inst, ulong pc)
        {
            uint rt = inst & 31;

            if ((inst & 0xFFFFF01F) == 0xD503201F) // HINT (NOP, YIELD, ...)
            {
                return;
            }

            if ((inst & 0xFFFFF01F) == 0xD503301F) // CLREX, DSB, DMB, ISB
            {
                if (((inst >> 5) & 7) == 2)
                {
                    _exclusiveAddress = ulong.MaxValue;
                }

                System.Threading.Thread.MemoryBarrier();
                return;
            }

            if ((inst & 0xFFF80000) == 0xD5080000) // SYS (DC, IC)
            {
                uint op = (inst >> 5) & 0x3FFF; // op1:CRn:CRm:op2

                if ((op & 0x3FFF) == 0x1BA1) // DC ZVA (op1 3, CRn 7, CRm 4, op2 1)
                {
                    ulong address = R(rt) & ~(ulong)(DczBlockSize - 1);

                    for (int i = 0; i < DczBlockSize; i += 8)
                    {
                        _memory.WriteGuest(address + (ulong)i, 0UL);
                    }
                }

                // Cache maintenance: the guest code cache is read-only here.
                return;
            }

            bool read = (inst & 0x00200000) != 0;
            uint sysreg = (inst >> 5) & 0x7FFF; // o0:op1:CRn:CRm:op2 (op0 is 3 for all of these)

            if ((inst & 0xFFD00000) == 0xD5100000) // MRS / MSR (register)
            {
                switch (sysreg)
                {
                    case 0x5E82: // TPIDR_EL0
                        if (read)
                        {
                            W(rt, *(ulong*)(_ctx + _tpidrOffset));
                        }
                        else
                        {
                            *(ulong*)(_ctx + _tpidrOffset) = R(rt);
                        }

                        return;
                    case 0x5E83: // TPIDRRO_EL0
                        if (read)
                        {
                            W(rt, *(ulong*)(_ctx + _tpidrroOffset));
                            return;
                        }

                        break;
                    case 0x5A10: // NZCV
                        if (read)
                        {
                            W(rt, Nzcv);
                        }
                        else
                        {
                            Nzcv = (uint)R(rt) & 0xF0000000;
                        }

                        return;
                    case 0x5A20: // FPCR
                        if (read)
                        {
                            W(rt, GetFpFlags() & FpcrMask);
                        }
                        else
                        {
                            SetFpFlags((GetFpFlags() & FpsrMask) | ((uint)R(rt) & FpcrMask));
                        }

                        return;
                    case 0x5A21: // FPSR
                        if (read)
                        {
                            W(rt, GetFpFlags() & FpsrMask);
                        }
                        else
                        {
                            SetFpFlags((GetFpFlags() & FpcrMask) | ((uint)R(rt) & FpsrMask));
                        }

                        return;
                    case 0x5F01: // CNTPCT_EL0
                        if (read)
                        {
                            W(rt, NativeInterface.GetCntpctEl0());
                            return;
                        }

                        break;
                    case 0x5F00: // CNTFRQ_EL0
                        if (read)
                        {
                            W(rt, NativeInterface.GetCntfrqEl0());
                            return;
                        }

                        break;
                    case 0x5801: // CTR_EL0 (the value the translated code uses)
                        if (read)
                        {
                            W(rt, 0x8444c004);
                            return;
                        }

                        break;
                    case 0x5807: // DCZID_EL0
                        if (read)
                        {
                            W(rt, 4);
                            return;
                        }

                        break;
                }
            }

            throw Unsupported(pc, inst);
        }
    }
}
