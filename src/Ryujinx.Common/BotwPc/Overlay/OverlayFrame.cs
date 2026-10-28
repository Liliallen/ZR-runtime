using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.BotwPc.Overlay
{
    /// <summary>
    /// Hand-off between the overlay painter (host input thread) and the presenter (render thread): the latest overlay
    /// image (premultiplied BGRA8, top-down) and a ring of present timestamps for the frame-time graph. The presenter
    /// draws the image over the finished frame at output resolution, after any upscaling, so it is never part of DLSS
    /// or the game's own frame.
    /// </summary>
    public static class OverlayFrame
    {
        private static readonly object _lock = new();
        private static byte[] _pixels;
        private static int _width, _height, _x, _y, _version;

        /// <summary>Output size the painter should use (set by the presenter, in pixels).</summary>
        public static int TargetWidth { get; set; } = 1920;
        public static int TargetHeight { get; set; } = 1080;

        /// <summary>Anything to draw (menu open or performance HUD on).</summary>
        public static bool Visible { get; private set; }

        /// <summary>Set when a new image was published and not presented yet (used to refresh while the game is paused).</summary>
        public static bool PendingPresent;

        /// <summary>Publishes an image placed at (<paramref name="x"/>, <paramref name="y"/>) in output pixels.</summary>
        public static void Publish(byte[] pixels, int width, int height, int x = 0, int y = 0)
        {
            lock (_lock)
            {
                _pixels = pixels;
                _width = width;
                _height = height;
                _x = x;
                _y = y;
                _version++;
                Visible = pixels != null;
                Volatile.Write(ref PendingPresent, true);
            }
        }

        public static void Hide()
        {
            if (Visible)
            {
                Publish(null, 0, 0);
            }
        }

        /// <summary>Presenter: the current image and its version (null when nothing is shown).</summary>
        public static byte[] Take(out int width, out int height, out int x, out int y, out int version)
        {
            lock (_lock)
            {
                width = _width;
                height = _height;
                x = _x;
                y = _y;
                version = _version;
                Volatile.Write(ref PendingPresent, false);

                return _pixels;
            }
        }

        // ---- frame timing (presenter writes, painter reads) ----

        public const int HistoryLength = 240;
        private static readonly long[] _presentTicks = new long[HistoryLength];
        private static int _presentCount;

        public static int PresentCount => Volatile.Read(ref _presentCount);

        public static void OnPresent()
        {
            int index = Interlocked.Increment(ref _presentCount) - 1;
            _presentTicks[index % HistoryLength] = Stopwatch.GetTimestamp();
        }

        /// <summary>Frame intervals in ms, oldest first, at most <paramref name="max"/>.</summary>
        public static float[] FrameTimes(int max)
        {
            int count = Volatile.Read(ref _presentCount);
            int n = System.Math.Min(System.Math.Min(max, count - 1), HistoryLength - 1);

            if (n <= 0)
            {
                return [];
            }

            float[] result = new float[n];

            for (int i = 0; i < n; i++)
            {
                long a = _presentTicks[(count - n - 1 + i) % HistoryLength], b = _presentTicks[(count - n + i) % HistoryLength];
                result[i] = (float)((b - a) * 1000.0 / Stopwatch.Frequency);
            }

            return result;
        }
    }

    /// <summary>
    /// Input gate between the controller poll (NpadManager) and the overlay host: the poll reports the physical buttons
    /// and asks what the game may see; the host reads the presses it collected.
    /// </summary>
    public static class OverlayInputGate
    {
        private static readonly StickChord _chord = new();
        private static long _toggleRequests;
        private static PadButtons _held;
        private static (float X, float Y) _rightStick;

        /// <summary>The overlay has focus: the game gets neutral controller and keyboard input.</summary>
        public static volatile bool Captures;

        /// <summary>Shortcut enabled (Settings: overlay.enabled).</summary>
        public static volatile bool Enabled = true;

        public static double ChordWindowMs
        {
            get => _chord.WindowMs;
            set => _chord.WindowMs = value;
        }

        /// <summary>
        /// Controller poll (Player 1, physical buttons). Returns which stick clicks the game may see; everything else is
        /// passed unless <see cref="Captures"/>.
        /// </summary>
        public static PadButtons Process(PadButtons held, (float X, float Y) rightStick, double nowMs)
        {
            _held = held;
            _rightStick = rightStick;

            if (!Enabled)
            {
                return held & (PadButtons.LS | PadButtons.RS);
            }

            (bool fired, PadButtons sticks) = _chord.Update(held, nowMs);

            if (fired)
            {
                Interlocked.Increment(ref _toggleRequests);
            }

            return sticks;
        }

        public static void RequestToggle() => Interlocked.Increment(ref _toggleRequests);

        /// <summary>Overlay host: number of toggle requests since the last call.</summary>
        public static long TakeToggleRequests() => Interlocked.Exchange(ref _toggleRequests, 0);

        public static PadButtons Held => _held;
        public static (float X, float Y) RightStick => _rightStick;
    }
}

namespace Ryujinx.Common.BotwPc.Overlay
{
    /// <summary>What the renderer reports about DLSS, for the overlay (written by the Vulkan backend).</summary>
    public static class DlssRuntime
    {
        /// <summary>NGX initialised and DLSS Super Resolution supported on this device.</summary>
        public static volatile bool Available;

        /// <summary>Last state change, e.g. "NGX not available" or "create failed (…)".</summary>
        public static volatile string Status = "not initialised";

        /// <summary>Stopwatch timestamp of the last successful DLSS evaluation (0 = never).</summary>
        public static long LastEvaluation;

        /// <summary>Render → output size of the current feature.</summary>
        public static volatile string Sizes = "";

        public static bool RanRecently => System.Diagnostics.Stopwatch.GetTimestamp() - System.Threading.Interlocked.Read(ref LastEvaluation) < System.Diagnostics.Stopwatch.Frequency;

        /// <summary>DLSS Frame Generation state, e.g. "Streamline not installed", "ready", "on (2x)".</summary>
        public static volatile string FrameGenStatus = "off";

        /// <summary>Streamline and DLSS-G loaded at start: frame generation can be changed live.</summary>
        public static volatile bool FrameGenAvailable;

        /// <summary>Generated frames per game frame wanted now (0 = off, 1..3 = 2x..4x); the overlay changes it live.</summary>
        public static volatile int FrameGenFrames;

        /// <summary>Shown-frame limit while frame generation runs (0 = none); the overlay changes it live.</summary>
        public static volatile int FrameGenFpsLimit;

        /// <summary>Frames shown per game frame, recent average (1 when not interpolating, 0 when frame generation is off).</summary>
        public static volatile float FrameGenShownPerFrame;
    }
}
