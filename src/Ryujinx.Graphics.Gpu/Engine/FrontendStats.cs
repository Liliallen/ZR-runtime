using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine
{
    /// <summary>
    /// BOTW-PC diagnostics (BOTWPC_FE_STATS=1): what the GPU command thread is asked to do per frame, by kind — command
    /// words by destination, 3D register writes by register (and how many change the value), state groups updated per
    /// draw, buffer and texture binding work. Counted on the GPU command thread only (plain increments), reported to the
    /// log every 10 s as per-frame averages. Off by default; the checks compile to a static readonly branch.
    /// </summary>
    static class FrontendStats
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BOTWPC_FE_STATS") == "1";

        public enum Word
        {
            Header,
            ThreedReg,
            ThreedCallbackReg,
            ThreedChanged,
            MacroStart,
            MacroArg,
            Fifo,
            Compute,
            I2m,
            Twod,
            Dma,
            InlineData,
            UboData,
            Count,
        }

        public enum Ev
        {
            Draws,
            DrawsIndexed,
            Clears,
            UpdateCalls,
            StateCallbacks,
            BufCommits,
            IndexSync,
            IndexBind,
            VertexSync,
            VertexBind,
            UniformSync,
            UniformBind,
            StorageSync,
            StorageBind,
            TexCommits,
            TexCommitsSkipped,
            TexBindingsScanned,
            TexHandleReads,
            TexSets,
            ShaderUpdates,
            ShaderLookups,
            ComputeDispatches,
            MacroCalls,
            MacroHleCalls,
            SequenceAdvances,
            Count,
        }

        private static readonly long[] _words = new long[(int)Word.Count];
        private static readonly long[] _events = new long[(int)Ev.Count];
        private static readonly long[] _regWrites = new long[0xe00];
        private static readonly long[] _regChanged = new long[0xe00];
        private static readonly long[] _groups = new long[64];
        private static string[] _groupNames;
        private static long _frames;
        private static long _nextReport;

        public static void W(Word kind, int count = 1) => _words[(int)kind] += count;

        public static void E(Ev ev, int count = 1) => _events[(int)ev] += count;

        public static void Reg(int offset, bool changed)
        {
            int index = offset >> 2;

            if ((uint)index < (uint)_regWrites.Length)
            {
                _regWrites[index]++;

                if (changed)
                {
                    _regChanged[index]++;
                }
            }
        }

        public static void Group(int index) => _groups[index]++;

        public static void SetGroupNames(string[] names) => _groupNames ??= names;

        /// <summary>Called once per presented frame (render thread).</summary>
        public static void Frame() => Interlocked.Increment(ref _frames);

        /// <summary>Called by the GPU command thread at a point where no counter is being updated.</summary>
        public static void MaybeReport()
        {
            long now = Stopwatch.GetTimestamp();

            if (_nextReport == 0)
            {
                _nextReport = now + Stopwatch.Frequency * 10;
                return;
            }

            if (now < _nextReport)
            {
                return;
            }

            _nextReport = now + Stopwatch.Frequency * 10;
            long frames = Math.Max(1, Interlocked.Exchange(ref _frames, 0));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            StringBuilder text = new();
            text.Append(inv, $"BOTW-PC FE stats ({frames} frames, per frame):");

            for (int i = 0; i < _words.Length; i++)
            {
                text.Append(inv, $" {(Word)i}={_words[i] / (double)frames:F0}");
                _words[i] = 0;
            }

            text.Append(" |");

            for (int i = 0; i < _events.Length; i++)
            {
                text.Append(inv, $" {(Ev)i}={_events[i] / (double)frames:F0}");
                _events[i] = 0;
            }

            text.Append(" | groups:");

            for (int i = 0; i < _groups.Length; i++)
            {
                if (_groups[i] != 0)
                {
                    string name = _groupNames != null && i < _groupNames.Length ? _groupNames[i] : i.ToString(inv);
                    text.Append(inv, $" {name}={_groups[i] / (double)frames:F0}");
                    _groups[i] = 0;
                }
            }

            text.Append(" | top regs (offset:writes/changed):");

            foreach (int index in Enumerable.Range(0, _regWrites.Length).OrderByDescending(i => _regWrites[i]).Take(48))
            {
                if (_regWrites[index] == 0)
                {
                    break;
                }

                text.Append(inv, $" {index * 4:x}:{_regWrites[index] / (double)frames:F0}/{_regChanged[index] / (double)frames:F0}");
            }

            Array.Clear(_regWrites);
            Array.Clear(_regChanged);

            Logger.Info?.Print(LogClass.Gpu, text.ToString());
        }
    }
}
