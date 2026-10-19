using System;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// Spin count of the events guest threads and the GAL threads block on. .NET spins 35 rounds (yields) before sleeping;
    /// on iOS they sleep at once: 9 % less CPU time at the same frame rate on the M1 iPad (2.53 -> 2.31 cores, 30 FPS,
    /// nominal thermal state), which matters when the device runs hot. BOTWPC_WAIT_SPIN overrides it.
    /// </summary>
    public static class WaitSpin
    {
        public static readonly int Count = int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_WAIT_SPIN"), out int n) ? n : OperatingSystem.IsIOS() ? 0 : -1;

        public static ManualResetEventSlim CreateEvent(bool initialState = false)
        {
            return Count >= 0 ? new ManualResetEventSlim(initialState, Count) : new ManualResetEventSlim(initialState);
        }
    }
}
