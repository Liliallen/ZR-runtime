using Ryujinx.Common.BotwPc;
using Ryujinx.Common.BotwPc.Overlay;
using Ryujinx.HLE.BotwPc.Overlay;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ryujinx.Ava.Systems
{
    /// <summary>
    /// Performance HUD numbers from the counters BOTW-PC already keeps (GpuCounters, Ryujinx statistics, present
    /// timestamps, BotW timing); differences over the sampling interval, no new instrumentation.
    /// </summary>
    internal sealed class HudSampler
    {
        private long _lastTicks;
        private int _lastPresents;
        private readonly long[] _last = new long[(int)GpuCounters.Id.Count];
        private TimeSpan _lastCpu;
        private double _lastGameSeconds;
        private float _gameSpeed = float.NaN;
        private readonly Queue<(long Ticks, long Compiles)> _compiles = new();

        public HudData Last { get; private set; } = new();

        /// <summary>The game is paused: the numbers shown are the last ones measured before the pause.</summary>
        public bool Paused { get; private set; }

        private long Delta(GpuCounters.Id id)
        {
            long now = GpuCounters.Peek(id), d = now - _last[(int)id];
            _last[(int)id] = now;

            return d < 0 ? now : d; // the bench logger resets counters; then count from zero
        }

        public HudData Sample(HLE.Switch device, HudMode mode, float targetFps)
        {
            long ticks = Stopwatch.GetTimestamp();
            double seconds = _lastTicks == 0 ? 0 : (ticks - _lastTicks) / (double)Stopwatch.Frequency;
            int presents = OverlayFrame.PresentCount;
            int frames = Math.Max(1, presents - _lastPresents);
            double ms(long t) => t * 1000.0 / Stopwatch.Frequency / frames;

            long gpuBusy = Delta(GpuCounters.Id.GpuBusyTicks), fifo = Delta(GpuCounters.Id.GpuFifoTicks);
            long syncWait = Delta(GpuCounters.Id.SyncWaitTicks) + Delta(GpuCounters.Id.CbWaitTicks);
            long submits = Delta(GpuCounters.Id.Submits), compiles = Delta(GpuCounters.Id.ShaderCompiles) + Delta(GpuCounters.Id.PipelineCreates);
            long upload = Delta(GpuCounters.Id.UpStagingBytes) + Delta(GpuCounters.Id.UpDirectBytes) + Delta(GpuCounters.Id.UpMirrorBytes) + Delta(GpuCounters.Id.UpInlineBytes);
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            double cpuCores = seconds > 0 ? (cpu - _lastCpu).TotalSeconds / seconds : 0;
            _lastCpu = cpu;

            double gameSeconds = device?.BotwTiming?.TotalGameSeconds ?? 0;

            if (seconds > 0 && device?.System?.IsPaused == false)
            {
                _gameSpeed = (float)((gameSeconds - _lastGameSeconds) / seconds);
            }

            _lastGameSeconds = gameSeconds;
            _compiles.Enqueue((ticks, compiles));

            while (_compiles.Count > 0 && ticks - _compiles.Peek().Ticks > 5 * Stopwatch.Frequency)
            {
                _compiles.Dequeue();
            }

            bool paused = device?.System?.IsPaused == true;
            float fps = paused ? Last.Fps : (float)(device?.Statistics.GetGameFrameRate() ?? 0);
            float frameMs = paused ? Last.FrameMs : (float)(device?.Statistics.GetGameFrameTime() ?? 0);

            // Before the game's first frame the statistics measure from timestamp 0 (hours); there is no frame time yet.
            if (fps <= 0)
            {
                frameMs = 0;
            }
            Paused = paused;
            List<(string, string)> lines = [];

            // DLSS Frame Generation: shown frames = game frames × frames shown per game frame (measured by Streamline).
            float shown = DlssRuntime.FrameGenShownPerFrame;
            // The BOTW-PC menu pauses the game (and generation): keep the values from before it opened.
            float outputFps = paused ? Last.OutputFps : shown > 0 && fps > 0 ? fps * shown : 0;

            if (mode >= HudMode.Detailed && shown > 0)
            {
                lines.Add(("Frame generation", $"{DlssRuntime.FrameGenFrames + 1}x · {shown:0.00} shown/frame"));
            }

            if (mode >= HudMode.Detailed && seconds > 0)
            {
                lines.Add(("GPU execution", gpuBusy > 0 ? $"{ms(gpuBusy):0.00} ms" : "n/a"));
                lines.Add(("GPU command thread", $"{ms(fifo):0.00} ms"));
                lines.Add(("GPU sync waits", $"{ms(syncWait):0.00} ms"));
                lines.Add(("CPU (process)", $"{cpuCores:0.0} cores"));
                lines.Add(("Shader compiles", $"{compiles}"));
            }

            if (mode == HudMode.Developer && seconds > 0)
            {
                lines.Add(("Submits / frame", $"{submits / (double)frames:0.0}"));
                lines.Add(("Uploads", $"{upload / seconds / 1048576:0.0} MiB/s"));
                lines.Add(("Game speed", float.IsNaN(_gameSpeed) ? "n/a" : $"{_gameSpeed:0.00}"));
                lines.Add(("Managed heap", $"{GC.GetTotalMemory(false) / 1048576} MiB"));
                lines.Add(("VRAM", "not measured"));
            }

            _lastTicks = ticks;
            _lastPresents = presents;

            return Last = new HudData
            {
                Mode = mode,
                Fps = fps,
                OutputFps = outputFps,
                FrameMs = frameMs,
                TargetFps = targetFps,
                FrameTimes = mode >= HudMode.Detailed ? OverlayFrame.FrameTimes(120) : [],
                Lines = lines,
                Compiling = compiles > 0,
                Paused = paused,
            };
        }

        public string MeasuredText(Func<string, string, string> t) =>
            (Paused ? t("paused · before: ", "已暫停 · 暫停前：") : "") + $"{Last.Fps:0.0} FPS  ({Last.FrameMs:0.0} ms)" +
            (Last.OutputFps > 0 ? t($" → {Last.OutputFps:0} shown", $" → 畫面 {Last.OutputFps:0}") : "");

        public bool BelowTarget(float target) => target > 0 && Last.Fps > 0 && Last.Fps < target * 0.9f;

        public string FrameGenerationText(Func<string, string, string> t) =>
            (Paused ? t("before the menu: ", "開選單前：") : "") + (Last.OutputFps > 0
                ? t($"game {Last.Fps:0} → shown {Last.OutputFps:0} FPS", $"遊戲 {Last.Fps:0} → 畫面 {Last.OutputFps:0} FPS")
                : t($"game {Last.Fps:0} FPS, no generated frames", $"遊戲 {Last.Fps:0} FPS，沒有補幀"));

        public string GameSpeedText() => float.IsNaN(_gameSpeed) ? "n/a" : $"{_gameSpeed:0.00}";

        public string ShaderActivity(Func<string, string, string> t, bool _)
        {
            long total = 0;

            foreach ((long _, long c) in _compiles)
            {
                total += c;
            }

            return t($"{total} in the last 5 s", $"最近 5 秒 {total} 個");
        }
    }
}
