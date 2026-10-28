using ARMeilleure.CodeGen;
using ARMeilleure.CodeGen.Linking;
using ARMeilleure.CodeGen.Unwinding;
using ARMeilleure.CodeGen.X86;
using ARMeilleure.Instructions;
using ARMeilleure.IntermediateRepresentation;
using System.Collections.Generic;
using System.IO;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Translation
{
    /// <summary>
    /// BOTW-PC CPU V3 (<see cref="CpuV2Features.PreservingSyncCall"/>): x86-64 stub for the cold path of the
    /// synchronization check that guest code runs at every function entry and loop back edge. It calls
    /// <see cref="NativeInterface.CheckSynchronization"/> and preserves every caller-saved register except RAX (the
    /// zero-extended result), so the register allocator does not have to treat the check as a call: values live across
    /// a loop back edge no longer have to leave the caller-saved registers on the hot path.
    /// </summary>
    static class SyncCheckStub
    {
        public static CompiledFunction Generate(nint checkSynchronization)
        {
            bool windows = CallingConvention.GetCurrentCallConv() == CallConvName.Windows;

            // Caller-saved registers other than RAX (the return register).
            X86Register[] gprs = windows
                ? [X86Register.Rcx, X86Register.Rdx, X86Register.R8, X86Register.R9, X86Register.R10, X86Register.R11]
                : [X86Register.Rcx, X86Register.Rdx, X86Register.Rsi, X86Register.Rdi, X86Register.R8, X86Register.R9, X86Register.R10, X86Register.R11];
            int xmmCount = windows ? 6 : 16;
            int shadow = windows ? 32 : 0;

            // On entry RSP = 8 (mod 16); keep the call to the managed method 16-byte aligned.
            int alloc = shadow + xmmCount * 16;
            if ((8 + gprs.Length * 8 + alloc) % 16 != 0)
            {
                alloc += 8;
            }

            using MemoryStream stream = new();
            Assembler asm = new(stream, relocatable: false);
            List<UnwindPushEntry> unwind = [];
            Operand rsp = Register(X86Register.Rsp);

            foreach (X86Register reg in gprs)
            {
                asm.Push(Register(reg));
                unwind.Add(new UnwindPushEntry(UnwindPseudoOp.PushReg, (int)stream.Position, regIndex: (int)reg));
            }

            asm.Sub(rsp, Const(alloc), OperandType.I64);
            unwind.Add(new UnwindPushEntry(UnwindPseudoOp.AllocStack, (int)stream.Position, stackOffsetOrAllocSize: alloc));

            for (int i = 0; i < xmmCount; i++)
            {
                int offset = shadow + i * 16;
                asm.Movdqu(MemoryOp(OperandType.V128, rsp, default, Multiplier.x1, offset), Xmm((X86Register)i));
                if (windows)
                {
                    unwind.Add(new UnwindPushEntry(UnwindPseudoOp.SaveXmm128, (int)stream.Position, i, offset));
                }
            }

            int prologSize = (int)stream.Position;

            Operand rax = Register(X86Register.Rax);
            asm.Mov(rax, Const(checkSynchronization), OperandType.I64);
            asm.Call(rax);
            // CheckSynchronization returns a byte; only AL is defined by the managed ABI.
            asm.Movzx8(rax, Register(X86Register.Rax, OperandType.I32), OperandType.I32);

            for (int i = 0; i < xmmCount; i++)
            {
                asm.Movdqu(Xmm((X86Register)i), MemoryOp(OperandType.V128, rsp, default, Multiplier.x1, shadow + i * 16));
            }

            asm.Add(rsp, Const(alloc), OperandType.I64);

            for (int i = gprs.Length - 1; i >= 0; i--)
            {
                asm.Pop(Register(gprs[i]));
            }

            asm.Return();

            (byte[] code, _) = asm.GetCode();

            return new CompiledFunction(code, new UnwindInfo(unwind.ToArray(), prologSize), RelocInfo.Empty);
        }

        private static Operand Register(X86Register register, OperandType type = OperandType.I64)
        {
            return Operand.Factory.Register((int)register, RegisterType.Integer, type);
        }

        private static Operand Xmm(X86Register register)
        {
            return Operand.Factory.Register((int)register, RegisterType.Vector, OperandType.V128);
        }
    }
}
