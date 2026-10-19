using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Engine
{
    /// <summary>
    /// BOTW-PC GPU front end selection. BOTWPC_GPU_FRONTEND=legacy (default) runs the Ryujinx code paths unchanged;
    /// BOTWPC_GPU_FRONTEND=optimized enables the optimized front-end components, each of which can still be switched off
    /// on its own (its variable set to "0") or compared inside one run (listed in BOTWPC_AB_SWITCHES with
    /// BOTWPC_AB_PERIOD). Every component is an exact transformation: it skips or shortens work only when the result is
    /// provably identical to the legacy path (GPU_FRONTEND_REWRITE.md lists each component's equivalence argument), and
    /// <see cref="FrontendVerify"/> checks that claim at run time against the legacy computation.
    /// </summary>
    static class GpuFrontend
    {
        private static readonly string _mode = SelectMode();

        /// <summary>Round 5 optimized components (also part of GPU V2).</summary>
        public static readonly bool Optimized = _mode is "optimized" or "v2";

        /// <summary>Runtime V2 GPU front end: the optimized components plus the GPU V2 ones (GPU_FRONTEND_V2.md).</summary>
        public static readonly bool V2 = _mode == "v2";

        private static string SelectMode()
        {
            string value = Environment.GetEnvironmentVariable("BOTWPC_GPU_FRONTEND");

            if (string.IsNullOrEmpty(value))
            {
                value = BotwPcSettings.Instance.Graphics.GpuFrontend.ToString();
            }

            string mode = value.ToLowerInvariant() switch
            {
                "optimized" => "optimized",
                "v2" => "v2",
                _ => "legacy",
            };

            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC GPU front end: {mode}{(FrontendVerify.Enabled ? " (differential verification on)" : "")}");

            return mode;
        }


        private static readonly AbPhase.Switch _fastDispatch = new("BOTWPC_FE_FAST_DISPATCH");

        /// <summary>Direct dispatch of 3D register writes and macro calls from the command stream (GPFifoProcessor).</summary>
        public static bool FastDispatch => Optimized && _fastDispatch.IsOn;
    }

    /// <summary>
    /// BOTW-PC differential verification of the optimized GPU front end (BOTWPC_GPU_VERIFY=1). Wherever an optimized
    /// component skips or replaces legacy work, the legacy computation also runs (side-effect free, or with the legacy
    /// side effects, which are then the ones that take effect) and the results are compared at the point of use. Every
    /// comparison is counted; a mismatch is logged with the frame, the draw within the frame and the details the
    /// component supplies (first 50 mismatches in full, then counted). A summary line is logged every 10 s.
    /// Verification makes the optimized paths slower than legacy: never benchmark with it on.
    /// </summary>
    static class FrontendVerify
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BOTWPC_GPU_VERIFY") == "1";

        public enum Check
        {
            FastWrite,
            MacroHle,
            Count,
        }

        private static readonly long[] _checks = new long[(int)Check.Count];
        private static readonly long[] _mismatches = new long[(int)Check.Count];
        private static long _totalMismatches;
        private static long _frame;
        private static long _drawInFrame;
        private static long _nextReport;

        /// <summary>Called once per presented frame.</summary>
        public static void Frame()
        {
            Interlocked.Increment(ref _frame);
            Interlocked.Exchange(ref _drawInFrame, 0);
        }

        /// <summary>Called once per draw (GPU command thread).</summary>
        public static void Draw() => _drawInFrame++;

        /// <summary>Records one comparison. <paramref name="detail"/> is only evaluated for a mismatch.</summary>
        public static void Compare(Check check, bool match, Func<string> detail)
        {
            _checks[(int)check]++;

            if (!match)
            {
                _mismatches[(int)check]++;

                if (Interlocked.Increment(ref _totalMismatches) <= 50)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"BOTW-PC FE verify MISMATCH {check} frame {Interlocked.Read(ref _frame)} draw {_drawInFrame}: {detail()}");
                }
            }

            long now = Stopwatch.GetTimestamp();

            if (now >= _nextReport)
            {
                if (_nextReport != 0)
                {
                    StringBuilder text = new("BOTW-PC FE verify (cumulative):");

                    for (int i = 0; i < _checks.Length; i++)
                    {
                        text.Append($" {(Check)i} {_checks[i]} checks / {_mismatches[i]} mismatches;");
                    }

                    Logger.Info?.Print(LogClass.Gpu, text.ToString());
                }

                _nextReport = now + Stopwatch.Frequency * 10;
            }
        }
    }
}
