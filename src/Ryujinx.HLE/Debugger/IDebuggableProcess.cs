using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;

namespace Ryujinx.HLE.Debugger
{
    internal interface IDebuggableProcess
    {
        void DebugStop();
        void DebugContinue();
        void DebugContinue(KThread thread);
        bool DebugStep(KThread thread);
        KThread GetThread(ulong threadUid);
        DebugState GetDebugState();
        bool IsThreadPaused(KThread thread);
        ulong[] GetThreadUids();
        public void DebugInterruptHandler(IExecutionContext ctx);
        IVirtualMemoryManager CpuMemory { get; }
        void InvalidateCacheRegion(ulong address, ulong size);
    }
}
