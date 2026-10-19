using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// BOTW-PC diagnostics (BOTWPC_PROFILE=1): inclusive and exclusive time of nested regions of the GPU command thread.
    /// The older per-region timers (<see cref="GpuCounters"/> "...Ticks") are inclusive and nest (a draw state update runs
    /// inside a register callback, which may run inside a macro), so adding them double counts. Here every region is
    /// pushed on a per-thread stack: exclusive time = inclusive time minus the inclusive time of its direct children, so
    /// the exclusive times of all regions add up to the root (<see cref="Scope.GpuFifo"/>).
    ///
    /// The timers themselves cost time (two timestamps per region, ~45 000 regions per frame). At start-up the cost is
    /// calibrated: an empty region measures <c>e</c> ticks itself and adds <c>p - e</c> ticks to its parent. Corrected
    /// exclusive time = raw exclusive - count * e - direct children * (p - e); the correction is written as
    /// <c>XOverheadMs</c>. Corrected exclusive times are the "X...Ms" columns of counters.csv.
    /// </summary>
    public static class ScopeProfile
    {
        public enum Scope
        {
            /// <summary>Root: GPFifoDevice.DispatchCalls (exclusive = entry fetch and everything outside Process).</summary>
            GpuFifo,
            /// <summary>GPFifoProcessor.Process (exclusive = command word decoding and register writes).</summary>
            Process,
            /// <summary>BOTWPC_PARSE_PROBE=1: decoding the same command words again without executing them.</summary>
            ParseProbe,
            /// <summary>Macro execution (MME interpreter/JIT, register writes it makes, HLE macros).</summary>
            Macro,
            /// <summary>Register write callbacks (exclusive = method handlers outside the regions below).</summary>
            RegCallback,
            /// <summary>StateUpdater.Update (exclusive = state groups other than the two binding commits).</summary>
            DrawUpdate,
            TexCommit,
            BufCommit,
            BackendDraw,
            UboFlush,
            I2m,
            /// <summary>Render (backend) thread root: one batch of threaded-GAL commands per wake-up.</summary>
            RenderBatch,
            /// <summary>Backend draw (exclusive = the draw call and state outside the regions below).</summary>
            RDraw,
            RPipeline,
            RDescriptors,
            RBarriers,
            RRenderPass,
            RBufferData,
            /// <summary>Command buffer submission (FlushCommandsImpl).</summary>
            RFlush,
            RPresent,
            /// <summary>Threaded-GAL buffer handle mapping (BufferMap).</summary>
            RMap,
            /// <summary>Timer calibration only (not written).</summary>
            CalibParent,
            CalibChild,
            Count,
        }

        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BOTWPC_PROFILE") == "1";

        private const int MaxDepth = 64;

        private sealed class Stack
        {
            public readonly long[] Start = new long[MaxDepth];
            public readonly long[] ChildTicks = new long[MaxDepth];
            public readonly int[] Children = new int[MaxDepth];
            public readonly Scope[] Ids = new Scope[MaxDepth];
            public int Depth;
        }

        [ThreadStatic]
        private static Stack _stack;

        private const int Fields = 4; // inclusive ticks, exclusive ticks, count, direct children
        private static readonly long[] _values = new long[(int)Scope.Count * Fields];

        // Inclusive timers of GpuCounters fed from the same timestamps (old columns keep their meaning).
        private static readonly (GpuCounters.Id Count, GpuCounters.Id Ticks)[] _legacy =
        [
            (GpuCounters.Id.Count, GpuCounters.Id.Count), // GpuFifo: GpuFifoTicks is always on and timed separately
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.MacroCalls, GpuCounters.Id.MacroTicks),
            (GpuCounters.Id.RegCallbacks, GpuCounters.Id.RegCallbackTicks),
            (GpuCounters.Id.DrawUpdates, GpuCounters.Id.DrawUpdateTicks),
            (GpuCounters.Id.Count, GpuCounters.Id.TexCommitTicks),
            (GpuCounters.Id.CommitGfx, GpuCounters.Id.CommitGfxTicks),
            (GpuCounters.Id.Count, GpuCounters.Id.BackendDrawTicks),
            (GpuCounters.Id.UboFlushes, GpuCounters.Id.UboFlushTicks),
            (GpuCounters.Id.I2mTransfers, GpuCounters.Id.I2mTicks),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
            (GpuCounters.Id.Count, GpuCounters.Id.Count),
        ];

        private static readonly string[] _names = Enum.GetNames<Scope>();

        /// <summary>Calibrated timer cost in ticks: measured by an empty region itself, and added to its parent.</summary>
        private static double _emptySelf, _emptyParent;

        private static readonly long _calibrateAfter = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        private static bool _calibrated;
        private static long _calibrationSink;

        /// <summary>
        /// Measures the timer cost once the code is warm (tiered JIT, 30 s after start; a calibration at start-up ran on
        /// unoptimized code and measured several times the real cost). Runs on the counters thread with its own two
        /// scopes; the empty child does the same counter updates as a real region's exit.
        /// </summary>
        private static void Calibrate()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                const int N = 200_000;
                Enter(Scope.CalibParent);
                for (int i = 0; i < N; i++)
                {
                    Enter(Scope.CalibChild);
                    Exit(Scope.CalibChild);
                }
                Exit(Scope.CalibParent);

                long childIncl = _values[(int)Scope.CalibChild * Fields];
                long parentExcl = _values[((int)Scope.CalibParent * Fields) + 1];
                Array.Clear(_values, (int)Scope.CalibParent * Fields, 2 * Fields);
                _emptySelf = childIncl / (double)N;
                _emptyParent = (parentExcl / (double)N) + _emptySelf;
            }

            Logging.Logger.Info?.Print(Logging.LogClass.Gpu, string.Create(CultureInfo.InvariantCulture,
                $"BOTW-PC scope profile: timer cost {_emptySelf * 1e9 / Stopwatch.Frequency:F1} ns in a region, {_emptyParent * 1e9 / Stopwatch.Frequency:F1} ns per region in its parent (Stopwatch {Stopwatch.Frequency} Hz)"));
        }

        public static void Enter(Scope scope)
        {
            Stack s = _stack ??= new Stack();
            int d = s.Depth++;

            if (d >= MaxDepth)
            {
                return;
            }

            s.Ids[d] = scope;
            s.ChildTicks[d] = 0;
            s.Children[d] = 0;
            s.Start[d] = Stopwatch.GetTimestamp();
        }

        public static void Exit(Scope scope)
        {
            long now = Stopwatch.GetTimestamp();
            Stack s = _stack;
            int d = --s.Depth;

            if (d >= MaxDepth)
            {
                return;
            }

            Debug.Assert(s.Ids[d] == scope);

            long start = s.Start[d];
            long inclusive = now - start;
            int b = (int)scope * Fields;
            _values[b] += inclusive;
            _values[b + 1] += inclusive - s.ChildTicks[d];
            _values[b + 2]++;
            _values[b + 3] += s.Children[d];

            if (d > 0)
            {
                s.ChildTicks[d - 1] += inclusive;
                s.Children[d - 1]++;
            }

            if (scope == Scope.CalibChild)
            {
                // What a real region's exit does below: two counter updates and the timeline check.
                Interlocked.Increment(ref _calibrationSink);
                Interlocked.Add(ref _calibrationSink, inclusive);
                _calibrationSink += TimelineTrace.Configured ? 1 : 0;
            }
            else if (scope < Scope.CalibParent)
            {
                (GpuCounters.Id countId, GpuCounters.Id ticksId) = _legacy[(int)scope];

                if (countId != GpuCounters.Id.Count)
                {
                    GpuCounters.Add(countId);
                }

                if (ticksId != GpuCounters.Id.Count)
                {
                    GpuCounters.Add(ticksId, inclusive);
                }

                TimelineTrace.Region(_names[(int)scope], start);
            }
        }

        /// <summary>CSV header cells appended to counters.csv (one window per line, same order as <see cref="SnapshotAndResetCsv"/>).</summary>
        public static string CsvHeader()
        {
            StringBuilder sb = new();

            for (int i = 0; i < (int)Scope.CalibParent; i++)
            {
                sb.Append(",X").Append(_names[i]).Append("Ms");
            }

            return sb.Append(",XOverheadMs,XRegions").ToString();
        }

        /// <summary>Corrected exclusive milliseconds per scope since the last call (GPU thread values are read without a lock;
        /// a region finishing during the snapshot may land in either window).</summary>
        public static string SnapshotAndResetCsv()
        {
            if (Enabled && !_calibrated && Stopwatch.GetTimestamp() > _calibrateAfter)
            {
                _calibrated = true;
                Calibrate();
            }

            StringBuilder sb = new();
            double overhead = 0;
            long regions = 0;
            double toMs = 1000.0 / Stopwatch.Frequency;

            for (int i = 0; i < (int)Scope.CalibParent; i++)
            {
                int b = i * Fields;
                Interlocked.Exchange(ref _values[b], 0);
                long excl = Interlocked.Exchange(ref _values[b + 1], 0);
                long count = Interlocked.Exchange(ref _values[b + 2], 0);
                long children = Interlocked.Exchange(ref _values[b + 3], 0);
                double correction = (count * _emptySelf) + (children * (_emptyParent - _emptySelf));
                overhead += correction;
                regions += count;
                sb.Append(',').Append(((excl - correction) * toMs).ToString("F3", CultureInfo.InvariantCulture));
            }

            return sb.Append(',').Append((overhead * toMs).ToString("F3", CultureInfo.InvariantCulture))
                .Append(',').Append(regions.ToString(CultureInfo.InvariantCulture)).ToString();
        }
    }
}
