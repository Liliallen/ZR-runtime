using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Keeps BotW's game time real-time when it renders faster than the console's 30 FPS.
    ///
    /// BotW (ksys::VFR) never reads wall-clock time: each frame it assumes `interval` 60 Hz VBlanks
    /// elapsed and scales all per-frame deltas by ratio = interval / baseInterval (baseInterval = 2,
    /// i.e. 30 FPS). Raising the emulated VBlank rate (VSync "Custom") or unbounding it raises the
    /// frame rate, which would speed the whole game up. VFR::updateInterval reloads the ratio from
    /// mNewIntervalRatio whenever the interval is unchanged, so on every queued frame we write
    /// ratio = measuredFrameSeconds / (baseInterval / 60). The game still simulates once per
    /// rendered frame, so at 120 FPS it truly steps at ~120 Hz (no interpolation).
    ///
    /// The ratio is clamped to the game's own ratio, so below 30 FPS the game slows down exactly as on
    /// the console instead of taking oversized physics steps. With the stock 60 Hz VSync nothing is written.
    /// Disable the correction with BOTWPC_TIMING=0 (game speed is still measured).
    /// </summary>
    public sealed class BotwTiming
    {
        // ksys::VFR field offsets, verified against VFR::updateInterval in 1.6.0 (main+0x1c01180).
        public const int NewIntervalRatioOffset = 0x30;
        public const int IntervalAOffset = 0x34;
        public const int IntervalRatioAOffset = 0x3C;
        public const int BaseIntervalOffset = 0x150;
        public const int FrameRateOffset = 0x154;
        public const int FrameTimeOffset = 0x158;

        // Smoothing of the measured frame time; also bounds how fast the step size can change.
        private const double SmoothingFactor = 0.2;
        private const float MinRatio = 1f / 16; // 480 FPS

        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BOTWPC_TIMING") != "0";

        private readonly IVirtualMemoryManager _memory;
        private readonly ulong _instancePointerAddress;

        private ulong _validatedInstance;
        private bool _disabled;
        private bool _loggedActive;
        private long _lastFrameTimestamp;
        private double _smoothedFrameSeconds;
        private bool _corrected;

        // Game seconds the game believes elapsed (sum of ratio-in-use * baseInterval / 60), for diagnostics.
        private double _gameSeconds;

        /// <summary>Game seconds since start (never reset; the overlay derives game speed from it).</summary>
        public double TotalGameSeconds { get; private set; }
        private readonly Lock _gameSecondsLock = new();

        public BotwTiming(IVirtualMemoryManager memory, ulong mainBase, BotwProfile.Version version)
        {
            _memory = memory;
            _mainBase = mainBase;
            _instancePointerAddress = mainBase + version.VfrInstanceOffset;
        }

        /// <summary>Ratio that makes the next frame advance the game by <paramref name="frameSeconds"/>.</summary>
        public static float ComputeRatio(double frameSeconds, uint interval, uint baseInterval)
        {
            float nativeRatio = (float)interval / baseInterval;
            float ratio = (float)(frameSeconds * 60.0 / baseInterval);

            return Math.Clamp(ratio, MinRatio, nativeRatio);
        }

        /// <summary>Checks that the object really is a ksys::VFR with the expected baseline layout.</summary>
        public static bool LooksLikeVfr(uint baseInterval, uint frameRate, float frameTime)
        {
            return baseInterval is >= 1 and <= 4 &&
                   frameRate == 60 / baseInterval &&
                   MathF.Abs(frameTime - 1f / frameRate) < 1e-6f;
        }

        /// <summary>Returns game seconds advanced since the last call (diagnostics; 1 per real second is correct).</summary>
        public double TakeGameSeconds()
        {
            lock (_gameSecondsLock)
            {
                double value = _gameSeconds;
                _gameSeconds = 0;
                return value;
            }
        }

        /// <summary>Called whenever the game queues a frame for presentation.</summary>
        public void OnFrameQueued(VSyncMode vSyncMode)
        {
            if (_disabled || !TryGetVfr(out ulong vfr))
            {
                return;
            }

            uint baseInterval = _memory.Read<uint>(vfr + BaseIntervalOffset);
            float ratioInUse = _memory.Read<float>(vfr + IntervalRatioAOffset);

            lock (_gameSecondsLock)
            {
                _gameSeconds += ratioInUse * baseInterval / 60.0;
                TotalGameSeconds += ratioInUse * baseInterval / 60.0;
            }

            long now = Stopwatch.GetTimestamp();
            double frameSeconds = _lastFrameTimestamp == 0 ? 0 : (now - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
            _lastFrameTimestamp = now;

            uint interval = _memory.Read<uint>(vfr + IntervalAOffset);

            // Stock console timing (or correction disabled): leave the game's own ratio alone, only measure.
            if (!Enabled || vSyncMode == VSyncMode.Switch)
            {
                // Switched back at runtime: the game reloads the last written ratio until its interval changes, so a
                // ratio left from a high frame rate would run the game in slow motion. Restore the game's own ratio.
                if (_corrected && interval != 0)
                {
                    _memory.Write(vfr + NewIntervalRatioOffset, (float)interval / baseInterval);
                    _corrected = false;
                    _smoothedFrameSeconds = 0;
                }

                return;
            }

            if (frameSeconds <= 0 || interval == 0)
            {
                return;
            }

            // Long gaps (loading, pauses) are not frames the game should catch up on.
            double maxFrameSeconds = (double)interval / 60;
            frameSeconds = Math.Min(frameSeconds, maxFrameSeconds);
            _smoothedFrameSeconds = _smoothedFrameSeconds == 0 ? frameSeconds : _smoothedFrameSeconds + SmoothingFactor * (frameSeconds - _smoothedFrameSeconds);

            float ratio = ComputeRatio(_smoothedFrameSeconds, interval, baseInterval);
            _memory.Write(vfr + NewIntervalRatioOffset, ratio);
            _corrected = true;

            if (!_loggedActive)
            {
                _loggedActive = true;
                Logger.Info?.Print(LogClass.Application, $"BOTW-PC timing: active ({vSyncMode} VSync); VFR ratio follows measured frame time (first ratio {ratio:F4}).");
            }
        }

        private readonly ulong _mainBase;

        /// <summary>
        /// BOTWPC_DUMP_RUNTIME=&lt;file&gt;: writes the main module as it is in guest memory once the game runs (relocations
        /// applied, so vtables hold real addresses), for local analysis only — never commit the output.
        /// </summary>
        private void DumpRelocatedMain()
        {
            string path = Environment.GetEnvironmentVariable("BOTWPC_DUMP_RUNTIME");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            const int Size = 0x2D66000; // text + ro + data + bss of 1.6.0 main
            byte[] image = new byte[Size];

            for (int offset = 0; offset < Size; offset += 0x10000)
            {
                _memory.Read(_mainBase + (ulong)offset, image.AsSpan(offset, Math.Min(0x10000, Size - offset)));
            }

            System.IO.File.WriteAllBytes(path, image);
            Logger.Info?.Print(LogClass.Application, $"BOTW-PC: relocated main (base 0x{_mainBase:x}) dumped to {path}");
        }

        private bool TryGetVfr(out ulong vfr)
        {
            vfr = _memory.Read<ulong>(_instancePointerAddress);

            if (vfr == 0)
            {
                return false; // Not constructed yet (early boot).
            }

            if (vfr == _validatedInstance)
            {
                return true;
            }

            uint baseInterval = _memory.Read<uint>(vfr + BaseIntervalOffset);
            uint frameRate = _memory.Read<uint>(vfr + FrameRateOffset);
            float frameTime = _memory.Read<float>(vfr + FrameTimeOffset);

            if (!LooksLikeVfr(baseInterval, frameRate, frameTime))
            {
                _disabled = true;
                Logger.Error?.Print(LogClass.Application, $"BOTW-PC timing: VFR at 0x{vfr:x} has unexpected layout (interval={baseInterval}, rate={frameRate}, time={frameTime}); timing correction disabled.");
                return false;
            }

            _validatedInstance = vfr;
            Logger.Info?.Print(LogClass.Application, $"BOTW-PC timing: VFR at 0x{vfr:x} (base interval {baseInterval}, {frameRate} FPS baseline).");
            DumpRelocatedMain();
            return true;
        }
    }
}
