using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// BOTW-PC diagnostics, causal profiling: adds a fixed busy delay per guest frame to one thread, to test whether that
    /// thread is on the frame-time critical path (if the frame rate does not change, the thread has at least that much
    /// slack, and making it faster cannot raise the frame rate either). Off unless set; never for normal play.
    /// BOTWPC_DELAY_GPU_US=n: GPU command thread, once per guest frame. BOTWPC_DELAY_RENDER_US=n: render (backend) thread,
    /// once per present. BOTWPC_DELAY_GUEST_PCT=n: every guest CPU thread whose name starts with one of the comma-separated
    /// prefixes in BOTWPC_DELAY_GUEST_THREADS (default "HLE.GuestThread.") runs n % longer: at each supervisor call it
    /// spins n % of the time it ran since its previous supervisor call returned (a stand-in for slower guest JIT code,
    /// to measure what faster guest code can gain; a fixed delay per thread and frame does not model that, because
    /// threads that barely run would be delayed as much as the busy ones).
    /// With BOTWPC_AB_PERIOD the delay is applied only in every second period (<see cref="AbPhase"/>).
    /// </summary>
    public static class CausalDelay
    {
        public static readonly long GpuTicks = Read("BOTWPC_DELAY_GPU_US");
        public static readonly long RenderTicks = Read("BOTWPC_DELAY_RENDER_US");
        public static readonly int GuestPercent = int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_DELAY_GUEST_PCT"), out int pct) && pct > 0 ? pct : 0;

        private static readonly string[] _guestThreads =
            (Environment.GetEnvironmentVariable("BOTWPC_DELAY_GUEST_THREADS") is string g && g.Length != 0 ? g : "HLE.GuestThread.").Split(',');

        [ThreadStatic]
        private static long _guestRunStart;

        [ThreadStatic]
        private static int _guestSelected; // 0 unknown, 1 yes, 2 no

        private static int _gpuFrame;

        private static long Read(string name)
        {
            return long.TryParse(Environment.GetEnvironmentVariable(name), out long us) && us > 0 ? us * Stopwatch.Frequency / 1_000_000 : 0;
        }

        /// <summary>GPU command thread: delays once per guest frame.</summary>
        public static void OnGpuWork()
        {
            int frame = TimelineTrace.Frame;

            if (frame != _gpuFrame)
            {
                _gpuFrame = frame;

                if (AbPhase.Active)
                {
                    Spin(GpuTicks);
                }
            }
        }

        /// <summary>Guest CPU thread, entering a supervisor call: stretches the run since the last call if selected.</summary>
        public static void OnGuestSyscallEnter()
        {
            if (_guestSelected == 0)
            {
                string name = Thread.CurrentThread.Name ?? "";
                _guestSelected = Array.Exists(_guestThreads, p => name.StartsWith(p, StringComparison.Ordinal)) ? 1 : 2;
            }

            if (_guestSelected == 1 && _guestRunStart != 0)
            {
                Spin((Stopwatch.GetTimestamp() - _guestRunStart) * GuestPercent / 100);
            }
        }

        /// <summary>Guest CPU thread, leaving a supervisor call: guest code runs from here.</summary>
        public static void OnGuestSyscallLeave()
        {
            _guestRunStart = Stopwatch.GetTimestamp();
        }

        public static void Spin(long ticks)
        {
            if (!AbPhase.Active)
            {
                return;
            }

            long end = Stopwatch.GetTimestamp() + ticks;

            while (Stopwatch.GetTimestamp() < end)
            {
                Thread.SpinWait(20);
            }
        }
    }
}
