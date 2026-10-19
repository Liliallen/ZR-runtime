using Ryujinx.Cpu.LightningJit.Arm64.Target.Arm64;
using Ryujinx.Cpu.LightningJit.CodeGen;
using Ryujinx.Cpu.LightningJit.CodeGen.Arm64;
using Ryujinx.Cpu.LightningJit.State;
using System;

namespace Ryujinx.Cpu.LightningJit
{
    /// <summary>
    /// Host addresses the generated code needs (page table, function table, stubs, helper functions). With
    /// <see cref="Enabled"/> they are kept in the native context and loaded from there instead of being embedded in the
    /// code as immediates: the code is then position- and process-independent, so it can be compiled ahead of time into
    /// a signed binary (iPadOS without JIT, see botw/docs/IPADOS.md). BOTWPC_PIC=1 forces it on other hosts.
    /// </summary>
    static class HostPointers
    {
        public enum Id
        {
            PageTable,
            FunctionTable,
            DispatchStub,
            GetFunctionAddress,
            Break,
            SupervisorCall,
            Undefined,
            GetCntpctEl0,
            CheckSynchronization,
        }

        private static readonly bool _forced = OperatingSystem.IsIOS() || Environment.GetEnvironmentVariable("BOTWPC_PIC") == "1" || AotImage.Recording;

        // Also whenever an ahead-of-time image is loaded: its code reads the context.
        public static bool Enabled => _forced || AotImage.Loaded;

        public static int Offset(Id id) => NativeContext.GetHostOffset((int)id);

        /// <summary>Loads a host address into <paramref name="rd"/>: from the context (PIC) or as an immediate.</summary>
        public static void Load(ref Assembler asm, Operand rd, Operand context, Id id, nint value)
        {
            if (Enabled)
            {
                asm.LdrRiUn(rd, context, Offset(id));
            }
            else
            {
                asm.Mov(rd, (ulong)value);
            }
        }

        public static unsafe void Fill(nint nativeContext, Translator translator)
        {
            if (!Enabled)
            {
                return;
            }

            void Set(Id id, nint value) => *(ulong*)(nativeContext + Offset(id)) = (ulong)value;

            Set(Id.PageTable, translator.Memory.PageTablePointer);
            Set(Id.FunctionTable, translator.FunctionTable.Base);
            Set(Id.DispatchStub, translator.Stubs.DispatchStub);
            Set(Id.GetFunctionAddress, translator.Stubs.GetFunctionAddressPointer);
            Set(Id.Break, InstEmitSystem.GetBrkHandlerPtr());
            Set(Id.SupervisorCall, InstEmitSystem.GetSvcHandlerPtr());
            Set(Id.Undefined, InstEmitSystem.GetUdfHandlerPtr());
            Set(Id.GetCntpctEl0, InstEmitSystem.GetCntpctEl0Ptr());
            Set(Id.CheckSynchronization, InstEmitSystem.CheckSynchronizationPtr());
        }
    }
}
