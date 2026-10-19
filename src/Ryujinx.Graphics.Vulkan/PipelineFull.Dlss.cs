using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System.Diagnostics;
using System.Linq;
using Format = Ryujinx.Graphics.GAL.Format;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC: replaces Breath of the Wild's 900p → 1080p upscale draw (programs <see cref="UpscaleProgramIds"/>, a
    /// full-screen triangle sampling the tonemapped scene into the output-size target, verified in DLSS.md) with
    /// NVIDIA DLSS. The HUD is drawn afterwards into the same target, so it stays sharp and is never reconstructed.
    /// Any mismatch keeps the game's own upscale.
    /// </summary>
    partial class PipelineFull
    {
        // The upscale has two variants: 973785cd7b40 (Shrine of Resurrection) and b164ac0219a2 (outdoors, Great Plateau);
        // same inputs (1600x900 R10G10B10A2 + D32), same output, same colours as the game's upscale (DLSS.md).
        private static readonly string[] UpscaleProgramIds =
            Environment.GetEnvironmentVariable("BOTWPC_DLSS_PROGRAM")?.Split(',') ?? ["973785cd7b40", "b164ac0219a2"];
        private static readonly bool DepthInverted = Environment.GetEnvironmentVariable("BOTWPC_DLSS_DEPTH_INVERTED") == "1";

        // DLSS gets reversed depth (1 − depth, written by the motion pass) with the DepthInverted flag: BotW's standard
        // depth puts the whole scene in [0.95, 1.0]. No visible change in the ghosting test by itself (DLSS.md).
        // BOTWPC_DLSS_REVERSED_DEPTH=0 passes the game's depth as before.
        private static readonly bool ReversedDepth = Environment.GetEnvironmentVariable("BOTWPC_DLSS_REVERSED_DEPTH") != "0";

        // Disocclusion: a pixel whose reprojected position was covered by something nearer in the previous frame (relative
        // reversed-depth difference above the threshold, 3x3 neighbourhood) has no valid history. DLSS's colour clamp kept
        // that history in low-contrast scenes: a trail of copies of Link's head on camera turns in the shrine (DLSS.md).
        // Such pixels get an off-screen motion vector. BOTWPC_DLSS_DISOCCLUSION_MASK=0 disables the detection;
        // BOTWPC_DLSS_BIAS_MASK=1 also passes it as DLSS's bias-current-colour mask (no visible effect with presets J/K/M).
        private static readonly bool DisocclusionMask = Environment.GetEnvironmentVariable("BOTWPC_DLSS_DISOCCLUSION_MASK") != "0";
        private static readonly bool BiasMask = DisocclusionMask && Environment.GetEnvironmentVariable("BOTWPC_DLSS_BIAS_MASK") == "1";
        private static readonly bool DisocclusionOffscreen = Environment.GetEnvironmentVariable("BOTWPC_DLSS_DISOCCLUSION_MV") != "0";
        private static readonly float DisocclusionThreshold =
            float.TryParse(Environment.GetEnvironmentVariable("BOTWPC_DLSS_DISOCCLUSION_THRESHOLD"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dt) ? dt : 0.2f;

        private static readonly bool JitterEnabled = Environment.GetEnvironmentVariable("BOTWPC_DLSS_JITTER") != "0";

        // Reported sample offset = sign * applied viewport offset (the image moves by +offset, so each pixel samples
        // the scene at -offset). Overridable for verification: BOTWPC_DLSS_JITTER_SIGN="x,y".
        private static readonly System.Numerics.Vector2 JitterSign = ParseSign(Environment.GetEnvironmentVariable("BOTWPC_DLSS_JITTER_SIGN"));

        private static System.Numerics.Vector2 ParseSign(string value)
        {
            string[] p = value?.Split(',') ?? [];
            return p.Length == 2 && float.TryParse(p[0], out float x) && float.TryParse(p[1], out float y) ? new(x, y) : new(-1, -1);
        }

        private static float Halton(int index, int b)
        {
            float f = 1, r = 0;

            for (int i = index; i > 0; i /= b)
            {
                f /= b;
                r += f * (i % b);
            }

            return r;
        }

        private int _dlssJitterIndex;

        // Camera (SetBotwCamera, in command order): current and previous world → clip rows, scene size.
        // BotW: OpenGL-style clip space (depth buffer = NDC z * 0.5 + 0.5) with NDC y up relative to the render target
        // (flags 1 | 2), found by reprojection error on dumped frames (DLSS.md).
        private static readonly int MotionFlags = int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_DLSS_MV_FLAGS"), out int mf) ? mf : 3;
        private static readonly bool MotionEnabled = Environment.GetEnvironmentVariable("BOTWPC_DLSS_MV") != "0";
        private readonly float[] _camCur = new float[16], _camPrev = new float[16];
        private int _camFrames;
        private static readonly bool CamLog = Environment.GetEnvironmentVariable("BOTWPC_DLSS_CAMLOG") == "1";
        private uint _camWidth, _camHeight;
        private ShaderCollection _motionProgram;
        private PipelineHelperShader _motionPipeline;
        private ISampler _motionSampler;

        // Debug: BOTWPC_DLSS_DUMP=<dir> writes two consecutive frames (colour, depth, motion vectors, matrices) once the
        // camera moves, for offline verification of the motion-vector conventions (local files only — game imagery).
        private static readonly string DumpDir = Environment.GetEnvironmentVariable("BOTWPC_DLSS_DUMP");
        private int _dumpStage;

        private void DumpFrame(TextureView color, TextureView depth)
        {
            if (DumpDir == null || _dumpStage >= 2 || _dlssEvaluations < 600)
            {
                return;
            }

            float change = 0;

            for (int i = 0; i < 16; i++)
            {
                change += Math.Abs(_camCur[i] - _camPrev[i]);
            }

            if (_dumpStage == 0 && (change < 100f || change > 1000f))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(DumpDir);
            string stem = System.IO.Path.Combine(DumpDir, $"f{_dumpStage}");

            using (PinnedSpan<byte> c = color.GetData())
            {
                System.IO.File.WriteAllBytes(stem + ".color", c.Get().ToArray());
            }

            using (PinnedSpan<byte> d = depth.GetData())
            {
                System.IO.File.WriteAllBytes(stem + ".depth", d.Get().ToArray());
            }

            using (PinnedSpan<byte> m = _dlssMotion.GetData())
            {
                System.IO.File.WriteAllBytes(stem + ".mv", m.Get().ToArray());
            }

            if (_dlssMask != null)
            {
                using PinnedSpan<byte> k = _dlssMask.GetData();
                System.IO.File.WriteAllBytes(stem + ".mask", k.Get().ToArray());
            }

            System.IO.File.WriteAllText(stem + ".txt", $"{color.Width} {color.Height} {color.Info.Format} {depth.Info.Format}\n" +
                string.Join(" ", _camCur) + "\n" + string.Join(" ", _camPrev) + "\n");
            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS: dumped frame {_dumpStage} (camera change {change:0.###}) to {DumpDir}");
            _dumpStage++;
        }

        public void SetBotwCamera(ReadOnlySpan<float> data)
        {
            if (!Dlss.Available || data.Length < 18)
            {
                return;
            }

            Array.Copy(_camCur, _camPrev, 16);
            data[..16].CopyTo(_camCur);
            (_camWidth, _camHeight) = ((uint)data[16], (uint)data[17]);
            _camFrames++;

            if (CamLog)
            {
                float change = 0;

                for (int i = 0; i < 16; i++)
                {
                    change += Math.Abs(_camCur[i] - _camPrev[i]);
                }

                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS camlog #{_camFrames} eval {_dlssEvaluations} change {change:0.####}");
            }

            if (_camFrames <= 3 || _camFrames % 600 == 0)
            {
                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS camera #{_camFrames} {_camWidth}x{_camHeight}: rows " +
                    string.Join(" | ", Enumerable.Range(0, 4).Select(r => string.Join(" ", Enumerable.Range(0, 4).Select(c => _camCur[r * 4 + c].ToString("0.###"))))));
            }
        }

        /// <summary>prev * inverse(cur) for row-major 4x4 matrices, in double precision; null if cur is singular.</summary>
        internal static double[] Reprojection(float[] prev, float[] cur)
        {
            double[] a = new double[16], inv = new double[16];

            for (int i = 0; i < 16; i++)
            {
                a[i] = cur[i];
                inv[i] = i % 5 == 0 ? 1 : 0;
            }

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

            double[] m = new double[16];

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        m[r * 4 + c] += prev[r * 4 + k] * inv[k * 4 + c];
                    }
                }
            }

            return m;
        }

        /// <summary>
        /// Writes camera motion vectors (or zeros when no camera history) into <see cref="_dlssMotion"/>, and the DLSS
        /// depth (reversed) into <see cref="_dlssDepth"/>.
        /// </summary>
        private unsafe void UpdateMotionVectors(TextureView depth, uint width, uint height)
        {
            bool valid = MotionEnabled && _camFrames >= 2 && _camWidth == width && _camHeight == height;
            double[] reproject = valid ? Reprojection(_camPrev, _camCur) : null;

            // Camera cut / teleport: the screen centre at mid depth jumps by more than a quarter of the screen.
            if (reproject != null)
            {
                double x = reproject[2] * 0.9 + reproject[3], y = reproject[6] * 0.9 + reproject[7], w = reproject[14] * 0.9 + reproject[15];

                if (w <= 0 || Math.Abs(x / w) > 0.5 || Math.Abs(y / w) > 0.5)
                {
                    _dlssReset = true;

                    if (++_dlssCuts <= 20)
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS: camera cut detected (centre moved to {x / w:0.00},{y / w:0.00}), history reset");
                    }
                }
            }

            if (reproject == null)
            {
                // No camera history: identity reprojection gives zero motion; the pass still writes the depth.
                reproject = new double[16];
                reproject[0] = reproject[5] = reproject[10] = reproject[15] = 1;
            }

            if (_motionProgram == null)
            {
                ResourceLayout layout = new ResourceLayoutBuilder()
                    .Add(ResourceStages.Compute, ResourceType.UniformBuffer, 0)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 0)
                    .Add(ResourceStages.Compute, ResourceType.TextureAndSampler, 1)
                    .Add(ResourceStages.Compute, ResourceType.Image, 0, true)
                    .Add(ResourceStages.Compute, ResourceType.Image, 1, true)
                    .Add(ResourceStages.Compute, ResourceType.Image, 2, true).Build();
                _motionProgram = Gd.CreateProgramWithMinimalLayout([new ShaderSource(
                    Ryujinx.Common.EmbeddedResources.Read("Ryujinx.Graphics.Vulkan/Shaders/SpirvBinaries/BotwDlssMotion.spv"), ShaderStage.Compute, TargetLanguage.Spirv)], layout);
                _motionPipeline = new PipelineHelperShader(Gd, Device);
                _motionSampler = Gd.CreateSampler(GAL.SamplerCreateInfo.Create(MinFilter.Nearest, MagFilter.Nearest));
            }

            Span<float> p = stackalloc float[20];

            for (int i = 0; i < 16; i++)
            {
                p[i] = (float)reproject[i];
            }

            p[16] = width;
            p[17] = height;
            p[18] = MotionFlags | (_dlssDepthValid && DisocclusionMask ? 8 : 0) | (DisocclusionOffscreen ? 16 : 0);
            p[19] = DisocclusionThreshold;

            using ScopedTemporaryBuffer buffer = Gd.BufferManager.ReserveOrCreate(Gd, Cbs, 20 * sizeof(float));
            buffer.Holder.SetDataUnchecked<float>(buffer.Offset, p);

            _motionPipeline.SetCommandBuffer(Cbs);
            _motionPipeline.SetProgram(_motionProgram);
            _motionPipeline.SetUniformBuffers([new BufferAssignment(0, buffer.Range)]);
            _dlssDepthIndex ^= 1;
            _motionPipeline.SetTextureAndSamplerIdentitySwizzle(ShaderStage.Compute, 0, depth, _motionSampler);
            _motionPipeline.SetTextureAndSamplerIdentitySwizzle(ShaderStage.Compute, 1, _dlssDepth[_dlssDepthIndex ^ 1], _motionSampler);
            _motionPipeline.SetImage(ShaderStage.Compute, 0, _dlssMotion);
            _motionPipeline.SetImage(ShaderStage.Compute, 1, _dlssDepth[_dlssDepthIndex]);
            _motionPipeline.SetImage(ShaderStage.Compute, 2, _dlssMask);
            _motionPipeline.DispatchCompute((int)(width + 15) / 16, (int)(height + 15) / 16, 1);
            _motionPipeline.Finish(Gd, Cbs);
            _dlssDepthValid = true;
        }

        private uint _dlssRenderWidth, _dlssRenderHeight, _dlssOutWidth, _dlssOutHeight;
        private bool _dlssCreated, _dlssFailed, _dlssReset = true;
        private int _dlssQuality = -1, _dlssFailedQuality = -1;
        private TextureView _dlssMotion, _dlssMask;
        private readonly TextureView[] _dlssDepth = new TextureView[2]; // reversed depth, current / previous frame
        private int _dlssDepthIndex;
        private bool _dlssDepthValid;
        private long _dlssLastTimestamp;
        private int _dlssEvaluations, _dlssCuts;
        // BOTWPC_DLSS_DEBUG=1 logs the first 40 candidate draws, =<n> the first n.
        private int _debugLeft = int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_DLSS_DEBUG"), out int n) ? (n == 1 ? 40 : n) : 0;

        protected override bool BotwDlssReplaceDraw(int vertexCount)
        {
            if (_debugLeft > 0 && vertexCount == 3 && FramebufferParams?.GetColorView(0) is TextureView dbgOut && dbgOut.Width >= 1920)
            {
                _debugLeft--;
                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS debug: 3-vertex draw program {BotwProgram?.BotwId} into {dbgOut.Width}x{dbgOut.Height} {dbgOut.Info.Format}");
            }

            int quality = Dlss.Quality;

            // Off (live): the game's own upscale. A failed mode stays off until another mode is selected.
            if (quality < 0 || (_dlssFailed && quality == _dlssFailedQuality))
            {
                if (quality < 0 && _dlssCreated && vertexCount == 3 && UpscaleProgramIds.Contains(BotwProgram?.BotwId))
                {
                    _dlssCreated = false; // recreated (with reset history) if it is switched on again
                    Common.BotwPc.Overlay.DlssRuntime.Status = "off";
                    Logger.Info?.Print(LogClass.Gpu, "BOTW-PC DLSS: switched off; the game's own upscale is used.");
                }

                return false;
            }

            _dlssFailed = false;

            if (vertexCount != 3 || !UpscaleProgramIds.Contains(BotwProgram?.BotwId))
            {
                return false;
            }

            TextureView color = null;

            foreach (ResourceBindingSegment[] set in BotwProgram.BindingSegments)
            {
                foreach (ResourceBindingSegment segment in set)
                {
                    if (color == null && segment.Type is ResourceType.TextureAndSampler or ResourceType.Texture)
                    {
                        BotwFragmentTextures.TryGetValue(segment.Binding, out color);
                    }
                }
            }

            TextureView output = FramebufferParams.GetColorView(0);
            TextureView depth = BotwLastDepth;

            if (color == null || output == null || depth == null || depth.Width != color.Width || depth.Height != color.Height ||
                output.Width < color.Width || output.Height < color.Height)
            {
                if (_debugLeft > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS debug: inputs color={color?.Width}x{color?.Height} {color?.Info.Format} depth={depth?.Width}x{depth?.Height} out={output?.Width}x{output?.Height} textures=[{string.Join(",", BotwFragmentTextures.Keys)}]");
                }

                return false;
            }

            uint rw = (uint)color.Width, rh = (uint)color.Height, ow = (uint)output.Width, oh = (uint)output.Height;

            EndRenderPass();

            if (!_dlssCreated || quality != _dlssQuality || rw != _dlssRenderWidth || rh != _dlssRenderHeight || ow != _dlssOutWidth || oh != _dlssOutHeight)
            {
                CreateDlss(rw, rh, ow, oh);
                return false; // This frame keeps the game's upscale; DLSS runs from the next one.
            }

            long now = Stopwatch.GetTimestamp();
            float frameMs = _dlssLastTimestamp == 0 ? 16.7f : (float)((now - _dlssLastTimestamp) * 1000.0 / Stopwatch.Frequency);
            _dlssLastTimestamp = now;

            UpdateMotionVectors(depth, rw, rh);
            Barrier();
            DumpFrame(color, depth);

            unsafe
            {
                Dlss.NgxImage c = Image(color), d = Image(ReversedDepth ? _dlssDepth[_dlssDepthIndex] : depth), m = Image(_dlssMotion), o = Image(output);
                Dlss.NgxImage mask = BiasMask ? Image(_dlssMask) : default;
                System.Numerics.Vector2 sample = BotwJitter * JitterSign;
                int r = Dlss.Evaluate(CommandBuffer, &c, &d, &m, &o, BiasMask ? &mask : null, sample.X, sample.Y, 1f, 1f, _dlssReset ? 1 : 0, frameMs);

                if (!Dlss.Succeeded(r))
                {
                    _dlssFailed = true;
                    _dlssFailedQuality = quality;
                    Common.BotwPc.Overlay.DlssRuntime.Status = $"evaluate failed ({Dlss.ResultString(r)}); game upscale in use";
                    Logger.Error?.Print(LogClass.Gpu, $"BOTW-PC DLSS: evaluate failed ({Dlss.ResultString(r)}); using the game's upscale.");
                    BotwSignalCommandBufferChange();
                    return false;
                }

                System.Threading.Interlocked.Exchange(ref Common.BotwPc.Overlay.DlssRuntime.LastEvaluation, now);
            }

            _dlssReset = false;
            Barrier();
            BotwSignalCommandBufferChange();
            NextJitter(rw, rh);

            if (++_dlssEvaluations == 1)
            {
                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS: active, {rw}x{rh} ({color.Info.Format}) → {ow}x{oh} ({output.Info.Format}), depth {depth.Info.Format}");
            }

            return true;
        }

        /// <summary>Jitter for the next frame's scene geometry: Halton(2,3), 16-frame cycle, in [-0.5, 0.5) render pixels.</summary>
        private void NextJitter(uint sceneWidth, uint sceneHeight)
        {
            (BotwSceneWidth, BotwSceneHeight) = JitterEnabled ? (sceneWidth, sceneHeight) : (0u, 0u);
            int i = _dlssJitterIndex++ % 16 + 1;
            BotwJitter = JitterEnabled ? new System.Numerics.Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f) : default;
        }

        private Dlss.NgxImage Image(TextureView view) => new()
        {
            Image = view.GetImage().Get(Cbs).Value,
            View = view.GetIdentityImageView().Get(Cbs).Value,
            Format = view.VkFormat,
            Width = (uint)view.Width,
            Height = (uint)view.Height,
        };

        private unsafe void Barrier()
        {
            MemoryBarrier barrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            Gd.Api.CmdPipelineBarrier(CommandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 1, in barrier, 0, null, 0, null);
        }

        private unsafe void CreateDlss(uint rw, uint rh, uint ow, uint oh)
        {
            _dlssMotion?.Release();
            _dlssMotion = Gd.CreateTextureView(new TextureCreateInfo((int)rw, (int)rh, 1, 1, 1, 1, 1, 4, Format.R16G16Float,
                DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));
            for (int i = 0; i < _dlssDepth.Length; i++)
            {
                _dlssDepth[i]?.Release();
                _dlssDepth[i] = Gd.CreateTextureView(new TextureCreateInfo((int)rw, (int)rh, 1, 1, 1, 1, 1, 4, Format.R32Float,
                    DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));
            }

            _dlssDepthValid = false;
            _dlssMask?.Release();
            _dlssMask = Gd.CreateTextureView(new TextureCreateInfo((int)rw, (int)rh, 1, 1, 1, 1, 1, 1, Format.R8Unorm,
                DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));

            // Zero motion vectors until camera motion is reconstructed.
            ClearColorValue zero = default;
            ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            Gd.Api.CmdClearColorImage(CommandBuffer, _dlssMotion.GetImage().Get(Cbs).Value, ImageLayout.General, &zero, 1, &range);

            int flags = 1 << 1 /* MV low res */ | (DepthInverted || ReversedDepth ? 1 << 3 : 0);
            int quality = Dlss.Quality;
            int r = Dlss.Create(CommandBuffer, rw, rh, ow, oh, quality, flags);

            if (!Dlss.Succeeded(r))
            {
                _dlssFailed = true;
                _dlssFailedQuality = quality;
                Common.BotwPc.Overlay.DlssRuntime.Status = $"create failed ({Dlss.ResultString(r)}); game upscale in use";
                Logger.Error?.Print(LogClass.Gpu, $"BOTW-PC DLSS: create {rw}x{rh}→{ow}x{oh} failed ({Dlss.ResultString(r)}); using the game's upscale.");
                return;
            }

            // NGX needs the creation commands executed before the first evaluation.
            FlushCommandsImpl();

            lock (Gd.QueueLock)
            {
                Gd.Api.QueueWaitIdle(Gd.Queue);
            }

            (_dlssRenderWidth, _dlssRenderHeight, _dlssOutWidth, _dlssOutHeight) = (rw, rh, ow, oh);
            _dlssCreated = true;
            _dlssQuality = quality;
            _dlssReset = true;
            Common.BotwPc.Overlay.DlssRuntime.Status = "running";
            Common.BotwPc.Overlay.DlssRuntime.Sizes = $"{rw}×{rh} → {ow}×{oh}";
            NextJitter(rw, rh);
            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS: feature created {rw}x{rh} → {ow}x{oh}, quality {quality}, flags 0x{flags:x}");
        }
    }
}
