using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC: NVIDIA DLSS Frame Generation through Streamline (botw/native/sl_bridge, DLSS_FRAME_GENERATION.md).
    /// Streamline works as a Vulkan interposer: when frame generation is selected (Settings graphics.frame_generation or
    /// BOTWPC_DLSSG=2x|3x|4x) Vulkan is loaded from sl.interposer.dll instead of vulkan-1.dll, after slInit. The scene
    /// depth, camera motion vectors and the hud-less colour come from BotW's upscale pass (<see cref="PipelineFull"/>),
    /// and are tagged at present (<see cref="Window"/>). Any failure leaves Vulkan and presentation untouched.
    /// </summary>
    static unsafe class DlssG
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SlImage
        {
            public Image Image;
            public ImageView View;
            public Silk.NET.Vulkan.Format Format;
            public uint Width, Height, Usage, Layout, X, Y, W, H;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SlConstants
        {
            public fixed float ViewToClip[16];
            public fixed float ClipToView[16];
            public fixed float ClipToPrevClip[16];
            public fixed float PrevClipToClip[16];
            public float JitterX, JitterY, MvecScaleX, MvecScaleY;
            public fixed float CameraPos[3];
            public fixed float CameraUp[3];
            public fixed float CameraRight[3];
            public fixed float CameraFwd[3];
            public float Near, Far, Fov, Aspect;
            public int DepthInverted, Reset;
        }

        /// <summary>Generated frames per game frame (1..3), 0 = frame generation not selected.</summary>
        public static int FramesToGenerate { get; }

        /// <summary>slInit succeeded: Vulkan must be loaded from <see cref="InterposerPath"/>.</summary>
        public static bool Initialised { get; }

        /// <summary>DLSS-G supported and loaded on the device in use.</summary>
        public static bool Available { get; private set; }

        public static string InterposerPath { get; }

        /// <summary>
        /// Reflex frame-rate limit while frame generation is on (µs per shown frame, 0 = none), from the live shown-frame
        /// limit (overlay, setting or BOTWPC_DLSSG_FPS), e.g. 160 on a 165 Hz display, since DLSS-G on Vulkan has no VSync
        /// (it would tear). With DLSS-G on, Reflex applies the limit to the shown frames (measured: 12.5 ms gave 40 game
        /// FPS at 2x).
        /// </summary>
        public static uint FrameLimitUs => Common.BotwPc.Overlay.DlssRuntime.FrameGenFpsLimit is int fps and > 0 ? (uint)(1_000_000.0 / fps) : 0;

        private static readonly delegate* unmanaged<int> _shutdown;
        private static readonly delegate* unmanaged<PhysicalDevice, int> _check;
        internal static readonly delegate* unmanaged<int, uint, int> SetOptions;
        internal static readonly delegate* unmanaged<int, uint, int> SetReflex;
        internal static readonly delegate* unmanaged<uint, int> BeginFrame;
        internal static readonly delegate* unmanaged<int, int> Marker;
        internal static readonly delegate* unmanaged<SlConstants*, int> SetConstants;
        internal static readonly delegate* unmanaged<CommandBuffer, SlImage*, SlImage*, SlImage*, int> Tag;
        internal static readonly delegate* unmanaged<uint*, uint*, uint*, uint*, int> GetState;
        private static readonly delegate* unmanaged<int, nint> _resultString;

        private static readonly bool _verbose = Environment.GetEnvironmentVariable("BOTWPC_SL_LOG") == "1";

        static DlssG()
        {
            FramesToGenerate = Common.BotwPc.GraphicsSettings.EffectiveFrameGeneration;

            if (FramesToGenerate == 0 || !OperatingSystem.IsWindows())
            {
                return;
            }

            string dir = Environment.GetEnvironmentVariable("BOTWPC_SL_DIR") ?? Path.Combine(AppContext.BaseDirectory, "streamline");
            InterposerPath = Path.Combine(dir, "sl.interposer.dll");

            // The bridge imports sl.interposer.dll by name: load that one first so the bridge binds to it.
            if (!NativeLibrary.TryLoad(InterposerPath, out _) || !NativeLibrary.TryLoad(Path.Combine(dir, "botw_sl_bridge.dll"), out nint library))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: Streamline not found in {dir}; frame generation off.");
                Common.BotwPc.Overlay.DlssRuntime.FrameGenStatus = "Streamline not installed";
                return;
            }

            nint F(string name) => NativeLibrary.GetExport(library, name);
            delegate* unmanaged<char*, char*, int, delegate* unmanaged<int, byte*, void>, int> init =
                (delegate* unmanaged<char*, char*, int, delegate* unmanaged<int, byte*, void>, int>)F("botw_sl_init");
            _shutdown = (delegate* unmanaged<int>)F("botw_sl_shutdown");
            _check = (delegate* unmanaged<PhysicalDevice, int>)F("botw_sl_check");
            SetOptions = (delegate* unmanaged<int, uint, int>)F("botw_sl_set_options");
            SetReflex = (delegate* unmanaged<int, uint, int>)F("botw_sl_set_reflex");
            BeginFrame = (delegate* unmanaged<uint, int>)F("botw_sl_begin_frame");
            Marker = (delegate* unmanaged<int, int>)F("botw_sl_marker");
            SetConstants = (delegate* unmanaged<SlConstants*, int>)F("botw_sl_set_constants");
            Tag = (delegate* unmanaged<CommandBuffer, SlImage*, SlImage*, SlImage*, int>)F("botw_sl_tag");
            GetState = (delegate* unmanaged<uint*, uint*, uint*, uint*, int>)F("botw_sl_get_state");
            _resultString = (delegate* unmanaged<int, nint>)F("botw_sl_result_string");

            string logs = Path.Combine(Common.Configuration.AppDataManager.BaseDirPath ?? ".", "botw", "streamline");
            Directory.CreateDirectory(logs);

            int r;

            fixed (char* pluginDir = dir, logDir = logs)
            {
                r = init(pluginDir, logDir, _verbose ? 1 : 0, &Log);
            }

            Initialised = r == 0;
            Common.BotwPc.Overlay.DlssRuntime.FrameGenStatus = Initialised ? "loaded" : $"init failed ({ResultString(r)})";
            int limit = Common.BotwPc.GraphicsSettings.EffectiveFrameGenerationFpsLimit;
            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: Streamline init {ResultString(r)} ({dir}), {FramesToGenerate + 1}x requested" +
                (limit > 0 ? $", output limited to {limit} FPS while on." : "."));
        }

        [UnmanagedCallersOnly]
        private static void Log(int type, byte* message)
        {
            string text = Marshal.PtrToStringUTF8((nint)message)?.TrimEnd();

            if (type == 2)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Streamline: {text}");
            }
            else if (type == 1)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Streamline: {text}");
            }
            else if (_verbose)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Streamline: {text}");
            }
        }

        public static string ResultString(int result) => _resultString == null ? result.ToString() : Marshal.PtrToStringAnsi(_resultString(result));

        /// <summary>The Vulkan API to use: through Streamline's interposer when it is initialised.</summary>
        public static Vk CreateApi() => Initialised ? new Vk(Vk.CreateDefaultContext([InterposerPath])) : Vk.GetApi();

        /// <summary>Checks DLSS-G on the created device and turns Reflex on (DLSS-G requires it).</summary>
        public static void Init(PhysicalDevice physicalDevice)
        {
            if (!Initialised)
            {
                return;
            }

            int r = _check(physicalDevice);
            Available = r == 0;

            if (Available)
            {
                r = SetReflex(1, 0);
            }

            Common.BotwPc.Overlay.DlssRuntime.FrameGenStatus = Available ? "ready" : $"not available ({ResultString(r)})";
            Common.BotwPc.Overlay.DlssRuntime.FrameGenFrames = Available ? FramesToGenerate : 0;
            Common.BotwPc.Overlay.DlssRuntime.FrameGenFpsLimit = Common.BotwPc.GraphicsSettings.EffectiveFrameGenerationFpsLimit;
            Common.BotwPc.Overlay.DlssRuntime.FrameGenAvailable = Available;
            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: {(Available ? "available" : "NOT available")} ({ResultString(r)}).");
        }

        public static void Shutdown()
        {
            if (Initialised)
            {
                _shutdown();
            }
        }

        // Row-major 4x4 matrix helpers (column-vector convention: v' = M v), double precision.
        internal static double[] Multiply(double[] a, double[] b)
        {
            double[] m = new double[16];

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        m[r * 4 + c] += a[r * 4 + k] * b[k * 4 + c];
                    }
                }
            }

            return m;
        }

        /// <summary>Inverse by Gauss-Jordan elimination with partial pivoting; null if singular.</summary>
        internal static double[] Invert(double[] m)
        {
            double[] a = (double[])m.Clone(), inv = Identity();

            for (int col = 0; col < 4; col++)
            {
                int pivot = col;

                for (int r = col + 1; r < 4; r++)
                {
                    if (Math.Abs(a[r * 4 + col]) > Math.Abs(a[pivot * 4 + col]))
                    {
                        pivot = r;
                    }
                }

                if (Math.Abs(a[pivot * 4 + col]) < 1e-12)
                {
                    return null;
                }

                for (int k = 0; k < 4; k++)
                {
                    (a[col * 4 + k], a[pivot * 4 + k]) = (a[pivot * 4 + k], a[col * 4 + k]);
                    (inv[col * 4 + k], inv[pivot * 4 + k]) = (inv[pivot * 4 + k], inv[col * 4 + k]);
                }

                double d = a[col * 4 + col];

                for (int k = 0; k < 4; k++)
                {
                    a[col * 4 + k] /= d;
                    inv[col * 4 + k] /= d;
                }

                for (int r = 0; r < 4; r++)
                {
                    if (r != col)
                    {
                        double f = a[r * 4 + col];

                        for (int k = 0; k < 4; k++)
                        {
                            a[r * 4 + k] -= f * a[col * 4 + k];
                            inv[r * 4 + k] -= f * inv[col * 4 + k];
                        }
                    }
                }
            }

            return inv;
        }

        internal static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

        /// <summary>
        /// Streamline constants from BotW's camera (DLSS.md: vertex cb3 rows 3..6 world → clip, 7..10 projection, 11..13
        /// inverse view, 14 near/far). BotW clip space is OpenGL's (z in [-1, 1]); the tagged depth is reversed
        /// (1 − (z * 0.5 + 0.5)), so Streamline's clip space is BotW's with z' = (w − z) / 2. Streamline matrices are
        /// row-vector (v' = v M), i.e. the transpose of ours.
        /// </summary>
        internal static void BuildConstants(ref SlConstants c, double[] reproject, float[] projection, float[] inverseView, float near, float far,
            uint renderWidth, uint renderHeight, System.Numerics.Vector2 jitter, bool reset)
        {
            double[] s = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -0.5, 0.5, 0, 0, 0, 1];
            double[] sInv = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -2, 1, 0, 0, 0, 1];
            double[] proj = Array.ConvertAll(projection, x => (double)x);
            double[] viewToClip = Multiply(s, proj);
            double[] clipToView = Invert(viewToClip) ?? Identity();
            double[] clipToPrevClip = reproject != null ? Multiply(Multiply(s, reproject), sInv) : Identity();
            double[] prevClipToClip = Invert(clipToPrevClip) ?? Identity();

            fixed (float* v = c.ViewToClip, cv = c.ClipToView, cp = c.ClipToPrevClip, pc = c.PrevClipToClip)
            {
                for (int r = 0; r < 4; r++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        v[r * 4 + k] = (float)viewToClip[k * 4 + r];
                        cv[r * 4 + k] = (float)clipToView[k * 4 + r];
                        cp[r * 4 + k] = (float)clipToPrevClip[k * 4 + r];
                        pc[r * 4 + k] = (float)prevClipToClip[k * 4 + r];
                    }
                }
            }

            c.JitterX = jitter.X;
            c.JitterY = jitter.Y;
            c.MvecScaleX = 1f / renderWidth;
            c.MvecScaleY = 1f / renderHeight;

            // Inverse view (view → world, 3x4 rows): columns are right, up, back (OpenGL looks down −z) and the position.
            for (int i = 0; i < 3; i++)
            {
                c.CameraRight[i] = inverseView[i * 4 + 0];
                c.CameraUp[i] = inverseView[i * 4 + 1];
                c.CameraFwd[i] = -inverseView[i * 4 + 2];
                c.CameraPos[i] = inverseView[i * 4 + 3];
            }

            c.Near = near;
            c.Far = far;
            c.Fov = 2f * MathF.Atan(1f / projection[5]);
            c.Aspect = projection[5] / projection[0];
            c.DepthInverted = 1;
            c.Reset = reset ? 1 : 0;
        }
    }
}

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>DLSS-G inputs of the current frame (render thread only): set by the upscale pass, consumed at present.</summary>
    internal sealed class DlssGInputs
    {
        public bool Valid;
        public TextureView Depth, Motion, Hudless;
        public uint RenderWidth, RenderHeight;
        public DlssG.SlConstants Constants;
    }
}
