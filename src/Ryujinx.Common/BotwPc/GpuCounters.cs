using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// Always-on counters for diagnosing CPU/GPU serialization in the Vulkan backend.
    /// One Interlocked.Add per event; written per 750 ms window to counters.csv when BOTWPC_FRAMELOG is set.
    /// Names ending in "Ticks" hold Stopwatch ticks and are written as milliseconds.
    /// </summary>
    public static class GpuCounters
    {
        public enum Id
        {
            Submits,
            FlushQuery,
            FlushQueryBatch,
            FlushAttachment,
            FlushDraw,
            FlushWeight,
            FlushSync,
            FlushCpuAccess,
            FlushPresent,
            FlushOther,
            CbWaits,
            CbWaitTicks,
            FenceCreates,
            FenceCreateTicks,
            FenceDestroys,
            FenceDestroyTicks,
            FenceResets,
            FenceResetTicks,
            FencePolls,
            FencePollTicks,
            SyncWaits,
            SyncWaitTicks,

            // GPU command thread (CPU wall time on GPU.MainThread).
            GpuFifoTicks,
            CommitGfx,
            CommitGfxTicks,
            BufSyncs,
            BufSyncBytes,
            BufSyncCopyTicks,
            SpanPoolSpills,
            GalQueueFullTicks,
            BufLookupHits,
            BufLookupMisses,
            GalWakes,
            GalWakeTicks,
            BindSets,
            BindSkips,

            // Binding memo (BufferManager, BOTWPC_PROFILE=1): sets skipped / done in full, and buffer-cache changes that
            // invalidate every memo (buffers merged or unmapped).
            MemoHits,
            MemoMisses,
            BufCacheChanges,

            // GPU command thread, per draw (BOTWPC_PROFILE=1): StateUpdater.Update (all state groups and bindings),
            // its texture binding commit, and the backend draw call (threaded command queueing).
            DrawUpdates,
            DrawUpdateTicks,
            TexCommitTicks,
            BackendDrawTicks,

            // GPU command thread outside draws (BOTWPC_PROFILE=1): GPFIFO command words processed, uniform buffer data
            // written back to guest memory, inline-to-memory transfers.
            Methods,
            UboFlushes,
            UboFlushTicks,
            I2mTransfers,
            I2mTicks,

            // Macro calls (HLE draws included) and register write callbacks; callbacks run inside macros count in both.
            MacroCalls,
            MacroTicks,
            RegCallbacks,
            RegCallbackTicks,

            // Memory write tracking (any thread): guest write faults and page reprotection (mprotect/VirtualProtect).
            TrackFaults,
            Reprotects,
            ReprotectTicks,

            // Per resource kind (Ryujinx.Graphics.Gpu ResourceKind: 1 buffer, 2 texture, 3 pool): a handle made dirty by
            // a guest write, a handle re-armed (its region's protection changed back), and a check of a handle that
            // has become volatile (never re-armed, reported dirty at every check).
            DirtyBuffer,
            DirtyTexture,
            DirtyPool,
            RearmBuffer,
            RearmTexture,
            RearmPool,
            VolatileBuffer,
            VolatileTexture,
            VolatilePool,
            StreamBuffer,
            StreamTexture,
            StreamPool,

            // HLE (guest threads): svcSleepThread yields (timeout 0 or -1), and those HLE V2 answered without work.
            HleYields,
            HleYieldsSkipped,

            // Render (backend) thread.
            RenderIdles,
            RenderIdleTicks,
            UpDirect,
            UpDirectBytes,
            UpMirror,
            UpMirrorBytes,
            UpInline,
            UpInlineBytes,
            UpStaging,
            UpStagingBytes,
            UpTicks,
            QueueSubmits,
            QueueSubmitTicks,
            AcquireTicks,
            PresentTicks,
            ShaderCompiles,
            ShaderCompileTicks,
            PipelineCreates,
            PipelineCreateTicks,

            // GPU time domain: union of command-buffer execution intervals from Vulkan timestamps.
            GpuBusyTicks,
            Count,
        }

        private static readonly long[] _values = new long[(int)Id.Count];

        /// <summary>Per-draw/per-command timers (BOTWPC_PROFILE=1); they cost a few percent, so off by default.</summary>
        public static readonly bool Detailed = Environment.GetEnvironmentVariable("BOTWPC_PROFILE") == "1";

        private static readonly int[] _lastGcCounts = new int[3];
        private static long _lastAllocated;
        private static TimeSpan _lastGcPause;

        public static readonly string CsvHeader = string.Join(',', Enum.GetNames<Id>().Take((int)Id.Count)
            .Select(n => n.EndsWith("Ticks") ? n[..^5] + "Ms" : n)) + ",Gen0,Gen1,Gen2,AllocMiB,GcPauseMs" + ScopeProfile.CsvHeader();

        /// <summary>Current value without resetting it (the overlay HUD takes differences).</summary>
        public static long Peek(Id id) => Interlocked.Read(ref _values[(int)id]);

        public static void Add(Id id, long value = 1)
        {
            Interlocked.Add(ref _values[(int)id], value);
        }

        private static readonly string[] _regionNames = Enum.GetNames<Id>().Select(n => n.EndsWith("Ticks") ? n[..^5] : n).ToArray();

        /// <summary>Adds the elapsed time since <paramref name="startTimestamp"/> and counts one event.</summary>
        public static void AddElapsed(Id countId, Id ticksId, long startTimestamp)
        {
            Interlocked.Increment(ref _values[(int)countId]);
            Interlocked.Add(ref _values[(int)ticksId], Stopwatch.GetTimestamp() - startTimestamp);
            TimelineTrace.Region(_regionNames[(int)ticksId], startTimestamp);
        }

        /// <summary>Adds the elapsed time since <paramref name="startTimestamp"/>.</summary>
        public static void AddTicks(Id ticksId, long startTimestamp)
        {
            Interlocked.Add(ref _values[(int)ticksId], Stopwatch.GetTimestamp() - startTimestamp);
            TimelineTrace.Region(_regionNames[(int)ticksId], startTimestamp);
        }

        public static string SnapshotAndResetCsv()
        {
            string[] cells = new string[(int)Id.Count];

            for (int i = 0; i < cells.Length; i++)
            {
                long value = Interlocked.Exchange(ref _values[i], 0);
                cells[i] = ((Id)i).ToString().EndsWith("Ticks")
                    ? (value * 1000.0 / Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                    : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // Managed GC activity in the same window (process-wide).
            int[] gc = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            long allocated = GC.GetTotalAllocatedBytes();
            TimeSpan pause = GC.GetTotalPauseDuration();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string gcCells = string.Create(inv, $"{gc[0] - _lastGcCounts[0]},{gc[1] - _lastGcCounts[1]},{gc[2] - _lastGcCounts[2]},{(allocated - _lastAllocated) / 1048576.0:F3},{(pause - _lastGcPause).TotalMilliseconds:F3}");
            gc.CopyTo(_lastGcCounts, 0);
            _lastAllocated = allocated;
            _lastGcPause = pause;

            return string.Join(',', cells) + "," + gcCells + ScopeProfile.SnapshotAndResetCsv();
        }
    }
}
