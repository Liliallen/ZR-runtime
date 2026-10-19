using Ryujinx.Common;
using Ryujinx.Common.BotwPc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Timers;

namespace Ryujinx.HLE
{
    public class PerformanceStatistics
    {
        private readonly Switch _device;

        private const int FrameTypeGame = 0;
        private const int PercentTypeFifo = 0;

        private readonly double[] _frameRate;
        private readonly double[] _accumulatedFrameTime;
        private readonly double[] _previousFrameTime;

        private readonly double[] _averagePercent;
        private readonly double[] _accumulatedActiveTime;
        private readonly double[] _percentLastEndTime;
        private readonly double[] _percentStartTime;

        private readonly long[] _framesRendered;
        private readonly double[] _percentTime;

        private readonly Lock[] _frameLock = [new()];
        private readonly Lock[] _percentLock = [new()];

        private readonly double _ticksToSeconds;

        private readonly System.Timers.Timer _resetTimer;

        // BOTW-PC: optional per-frame log for benchmarks, enabled with BOTWPC_FRAMELOG=<csv path>.
        private readonly StreamWriter _frameLog;
        private readonly StreamWriter _counterLog;
        private List<(double Time, double Delta)> _pendingFrames = [];

        public PerformanceStatistics(Switch device)
        {
            _device = device;

            _frameRate = new double[1];
            _accumulatedFrameTime = new double[1];
            _previousFrameTime = new double[1];

            _averagePercent = new double[1];
            _accumulatedActiveTime = new double[1];
            _percentLastEndTime = new double[1];
            _percentStartTime = new double[1];

            _framesRendered = new long[1];
            _percentTime = new double[1];

            _resetTimer = new(750);

            _resetTimer.Elapsed += ResetTimerElapsed;
            _resetTimer.AutoReset = true;

            _resetTimer.Start();

            _ticksToSeconds = 1.0 / PerformanceCounter.TicksPerSecond;

            string frameLogPath = Environment.GetEnvironmentVariable("BOTWPC_FRAMELOG");

            if (!string.IsNullOrEmpty(frameLogPath))
            {
                _frameLog = new StreamWriter(frameLogPath, false);
                _frameLog.WriteLine("time_s,frame_ms,fifo_pct");

                _counterLog = new StreamWriter(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(frameLogPath)), "counters.csv"), false);
                _counterLog.WriteLine("time_s,frames,game_s," + GpuCounters.CsvHeader);
            }
        }

        private void ResetTimerElapsed(object sender, ElapsedEventArgs e)
        {
            CalculateFrameRate(FrameTypeGame);
            CalculateAveragePercent(PercentTypeFifo);
            FlushFrameLog();
        }

        private void FlushFrameLog()
        {
            if (_frameLog == null)
            {
                return;
            }

            // System.Timers.Timer may run a late Elapsed handler concurrently with the next one; StreamWriter is not
            // thread-safe (interleaved lines were seen in framelog.csv), so the whole flush is serialized.
            lock (_frameLog)
            {
                List<(double Time, double Delta)> frames;

                lock (_frameLock[FrameTypeGame])
                {
                    frames = _pendingFrames;
                    _pendingFrames = [];
                }

                // FIFO busy percent is only known per 750 ms window, so every frame in the window shares it.
                double fifo = _averagePercent[PercentTypeFifo];

                foreach ((double time, double delta) in frames)
                {
                    _frameLog.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{time:F6},{delta * 1000:F4},{fifo:F2}"));
                }

                _frameLog.Flush();

                double now = PerformanceCounter.ElapsedTicks * _ticksToSeconds;
                // game_s: game time BotW advanced in this window (equals the wall time when timing is correct).
                double gameSeconds = _device.BotwTiming?.TakeGameSeconds() ?? 0;
                _counterLog.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{now:F3},{frames.Count},{gameSeconds:F4},") + GpuCounters.SnapshotAndResetCsv());
                _counterLog.Flush();
            }
        }

        private void CalculateFrameRate(int frameType)
        {
            double frameRate = 0;

            lock (_frameLock[frameType])
            {
                if (_accumulatedFrameTime[frameType] > 0)
                {
                    frameRate = _framesRendered[frameType] / _accumulatedFrameTime[frameType];
                }

                _frameRate[frameType] = frameRate;
                _framesRendered[frameType] = 0;
                _accumulatedFrameTime[frameType] = 0;
            }
        }

        private void CalculateAveragePercent(int percentType)
        {
            // If start time is non-zero, a percent reading is still being measured.
            // If there aren't any readings, the default should be 100% if still being measured, or 0% if not.
            double percent = (_percentStartTime[percentType] == 0) ? 0 : 100;

            lock (_percentLock[percentType])
            {
                if (_percentTime[percentType] > 0)
                {
                    percent = (_accumulatedActiveTime[percentType] / _percentTime[percentType]) * 100;
                }

                _averagePercent[percentType] = percent;
                _percentTime[percentType] = 0;
                _accumulatedActiveTime[percentType] = 0;
            }
        }

        public void RecordGameFrameTime()
        {
            RecordFrameTime(FrameTypeGame);
        }

        public void RecordFifoStart()
        {
            StartPercentTime(PercentTypeFifo);
        }

        public void RecordFifoEnd()
        {
            EndPercentTime(PercentTypeFifo);
        }

        private void StartPercentTime(int percentType)
        {
            double currentTime = PerformanceCounter.ElapsedTicks * _ticksToSeconds;

            _percentStartTime[percentType] = currentTime;
        }

        private void EndPercentTime(int percentType)
        {
            double currentTime = PerformanceCounter.ElapsedTicks * _ticksToSeconds;
            double elapsedTime = currentTime - _percentLastEndTime[percentType];
            double elapsedActiveTime = currentTime - _percentStartTime[percentType];

            lock (_percentLock[percentType])
            {
                _accumulatedActiveTime[percentType] += elapsedActiveTime;
                _percentTime[percentType] += elapsedTime;
            }

            _percentLastEndTime[percentType] = currentTime;
            _percentStartTime[percentType] = 0;
        }

        private void RecordFrameTime(int frameType)
        {
            double currentFrameTime = PerformanceCounter.ElapsedTicks * _ticksToSeconds;
            double elapsedFrameTime = currentFrameTime - _previousFrameTime[frameType];

            _previousFrameTime[frameType] = currentFrameTime;

            lock (_frameLock[frameType])
            {
                _accumulatedFrameTime[frameType] += elapsedFrameTime;

                _framesRendered[frameType]++;

                if (_frameLog != null)
                {
                    _pendingFrames.Add((currentFrameTime, elapsedFrameTime));
                }
            }
        }

        public double GetGameFrameRate()
        {
            return _frameRate[FrameTypeGame];
        }

        public double GetFifoPercent()
        {
            return _averagePercent[PercentTypeFifo];
        }

        public double GetGameFrameTime()
        {
            return 1000 / _frameRate[FrameTypeGame];
        }

        public string FormatFifoPercent()
        {
            double fifoPercent = GetFifoPercent();

            return $"FIFO: {fifoPercent:00.00}%";
        }
    }
}
