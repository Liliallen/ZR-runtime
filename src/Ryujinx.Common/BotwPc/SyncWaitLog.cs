using Ryujinx.Common.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// BOTW-PC diagnostics (BOTWPC_SYNC_LOG=1): where guest CPU accesses wait for host GPU work. Each wait for a
    /// GPU-written texture or buffer (guest memory flush) is aggregated by source key (resource description and
    /// waiting thread); a summary sorted by total wait time is logged every 5 s.
    /// </summary>
    public static class SyncWaitLog
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("BOTWPC_SYNC_LOG") == "1";

        private static readonly Dictionary<string, (long Count, long Ticks)> _waits = [];
        private static long _nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;

        /// <summary>
        /// Set by the HLE kernel: the calling guest thread's call chain ("main:0x...,main:0x..."), or null on a host
        /// thread. Lets a wait be attributed to the game code whose access caused it.
        /// </summary>
        public static System.Func<string> GuestCallChain;

        public static void Record(string resource, long startTimestamp)
        {
            long now = Stopwatch.GetTimestamp();
            string chain = GuestCallChain?.Invoke();
            string key = chain != null ? $"{resource} on {Thread.CurrentThread.Name} from {chain}" : $"{resource} on {Thread.CurrentThread.Name}";

            lock (_waits)
            {
                _waits.TryGetValue(key, out (long Count, long Ticks) v);
                _waits[key] = (v.Count + 1, v.Ticks + now - startTimestamp);

                if (now < _nextReport)
                {
                    return;
                }

                _nextReport = now + Stopwatch.Frequency * 5;

                foreach ((string k, (long count, long ticks)) in _waits.OrderByDescending(x => x.Value.Ticks).Take(12))
                {
                    Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC sync wait: {count,6} waits {ticks * 1000.0 / Stopwatch.Frequency,9:F1} ms  {k}");
                }

                _waits.Clear();
            }
        }
    }
}
