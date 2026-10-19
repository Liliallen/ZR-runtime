using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Threading;

namespace Ryujinx.HLE.HOS.Kernel.SupervisorCall
{
    partial class SyscallHandler
    {
        private readonly KernelContext _context;

        public SyscallHandler(KernelContext context)
        {
            _context = context;
        }

        public void SvcCall(IExecutionContext context, ulong address, int id)
        {
            KThread currentThread = KernelStatic.GetCurrentThread();

            if (currentThread.Owner != null &&
                currentThread.GetUserDisableCount() != 0 &&
                currentThread.Owner.PinnedThreads[currentThread.CurrentCore] == null)
            {
                _context.CriticalSection.Enter();

                currentThread.Owner.PinThread(currentThread);

                currentThread.SetUserInterruptFlag();

                _context.CriticalSection.Leave();
            }

            if (Ryujinx.Common.BotwPc.CausalDelay.GuestPercent != 0)
            {
                Ryujinx.Common.BotwPc.CausalDelay.OnGuestSyscallEnter();
            }

            long svcStart = Ryujinx.Common.BotwPc.StageLog.SvcEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            if (context.IsAarch32)
            {
                SyscallDispatch.Dispatch32(_context.Syscall, context, id);
            }
            else
            {
                SyscallDispatch.Dispatch64(_context.Syscall, context, id);
            }

            if (Ryujinx.Common.BotwPc.StageLog.SvcEnabled)
            {
                // BOTW-PC diagnostics: time per syscall (waits included) and thread.
                Ryujinx.Common.BotwPc.StageLog.RecordShared($"svc 0x{id:x2} on {System.Threading.Thread.CurrentThread.Name}", svcStart);
            }

            currentThread.HandlePostSyscall();

            if (Ryujinx.Common.BotwPc.CausalDelay.GuestPercent != 0)
            {
                Ryujinx.Common.BotwPc.CausalDelay.OnGuestSyscallLeave();
            }
        }
    }
}
