using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Drops BotW to the console's 30 FPS while it plays a video (memories, story movies).
    ///
    /// BotW's movie player (NintendoSDK_movie, nnMedia in subsdk0) decodes H.264 on NVDEC and is paced by the emulated
    /// VBlank, so above 30 FPS the video audio sounds wrong. For each video it opens an mm:u clock session for NvDec
    /// (Initialize ... Finalize; seen in play logs as one session per video), and its decoder work arrives as host1x
    /// submissions on /dev/nvhost-nvdec and /dev/nvhost-vic. While a session is open and the decoder was fed within
    /// <see cref="GuardSeconds"/> (in case a session is never finalized), frame pacing uses stock Switch VSync;
    /// afterwards the configured frame rate returns. The user's settings are never changed.
    /// Disable with BOTWPC_VIDEO_30FPS=0.
    /// </summary>
    public sealed class BotwVideo
    {
        public const double GuardSeconds = 10.0;

        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BOTWPC_VIDEO_30FPS") != "0";

        private static readonly long _guardTicks = (long)(Stopwatch.Frequency * GuardSeconds);

        private long _lastSubmit;
        private int _loggedState;

        public BotwVideo()
        {
            VideoDecodeSessions.Reset();
        }

        /// <summary>Called for every host1x (NVDEC/VIC) command submission.</summary>
        public void OnDecoderSubmit() => Volatile.Write(ref _lastSubmit, Stopwatch.GetTimestamp());

        /// <summary>Whether a video is playing now.</summary>
        public bool IsPlaying
        {
            get
            {
                long last = Volatile.Read(ref _lastSubmit);
                bool playing = Enabled && VideoDecodeSessions.Open > 0 && last != 0 && Stopwatch.GetTimestamp() - last < _guardTicks;
                int state = playing ? 1 : 0;

                if (Interlocked.Exchange(ref _loggedState, state) != state)
                {
                    Logger.Info?.Print(LogClass.Application, playing
                        ? "BOTW-PC video: playback started, frame rate held at 30 FPS."
                        : "BOTW-PC video: playback ended, configured frame rate restored.");
                }

                return playing;
            }
        }
    }
}
