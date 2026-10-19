using Ryujinx.Common.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// BOTW-PC diagnostics (BOTWPC_STAGE_LOG=1): time spent per named stage (e.g. each state-update group of the GPU
    /// command thread), aggregated and logged every 5 s as calls and milliseconds per second of wall time. Single-thread
    /// callers only (the GPU command thread); off by default.
    /// </summary>
    public static class StageLog
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("BOTWPC_STAGE_LOG") == "1";

        private static readonly Dictionary<string, (long Count, long Ticks)> _stages = [];
        private static long _windowStart = Stopwatch.GetTimestamp();

        /// <summary>BOTWPC_SVC_LOG=1: guest syscall time per syscall and thread (any thread; see <see cref="RecordShared"/>).</summary>
        public static readonly bool SvcEnabled = System.Environment.GetEnvironmentVariable("BOTWPC_SVC_LOG") == "1";

        /// <summary>BOTWPC_WAKE_LOG=1: host wake-up latency of guest threads (scheduler hand-over to the thread running).</summary>
        public static readonly bool WakeEnabled = System.Environment.GetEnvironmentVariable("BOTWPC_WAKE_LOG") == "1";

        private static readonly object _sharedLock = new();

        /// <summary>Thread-safe <see cref="Record"/> for stages measured on several threads.</summary>
        public static void RecordShared(string stage, long startTimestamp)
        {
            lock (_sharedLock)
            {
                Record(stage, startTimestamp);
            }
        }

        public static void Record(string stage, long startTimestamp)
        {
            long now = Stopwatch.GetTimestamp();

            _stages.TryGetValue(stage, out (long Count, long Ticks) v);
            _stages[stage] = (v.Count + 1, v.Ticks + now - startTimestamp);

            if (now - _windowStart < Stopwatch.Frequency * 5)
            {
                return;
            }

            double seconds = (now - _windowStart) / (double)Stopwatch.Frequency;

            foreach ((string name, (long count, long ticks)) in _stages.OrderByDescending(x => x.Value.Ticks).Take(24))
            {
                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC stage: {count / seconds,9:F0} calls/s {ticks * 1000.0 / Stopwatch.Frequency / seconds,8:F2} ms/s  {name}");
            }

            _stages.Clear();
            _windowStart = now;
        }
    }
}
