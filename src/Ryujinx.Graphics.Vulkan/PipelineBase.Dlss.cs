using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.Vulkan
{
    partial class PipelineBase
    {
        // BOTW-PC DLSS: state the DLSS pass needs (tracked only when DLSS is available).
        protected readonly Dictionary<int, TextureView> BotwFragmentTextures = [];
        protected TextureView BotwLastDepth;
        protected ShaderCollection BotwProgram => _program;

        /// <summary>Sub-pixel offset (render pixels) applied to scene geometry this frame; set by the DLSS pass.</summary>
        protected Vector2 BotwJitter;

        /// <summary>Scene render-target size whose geometry is jittered (0 = no jitter).</summary>
        protected uint BotwSceneWidth, BotwSceneHeight;

        private Vector2 _botwJitterApplied;

        /// <summary>Replaces the draw with DLSS when it is the game's upscale; returns true if it did.</summary>
        protected virtual bool BotwDlssReplaceDraw(int vertexCount) => false;

        protected void BotwSignalCommandBufferChange() => SignalCommandBufferChange();

        /// <summary>
        /// Offsets the viewports of scene geometry (scene-size target with depth, not a full-screen triangle) by the
        /// current jitter, and removes the offset for every other draw.
        /// </summary>
        private void BotwApplyJitter(int count)
        {
            bool scene = BotwSceneWidth != 0 && FramebufferParams != null && FramebufferParams.HasDepthStencil &&
                         FramebufferParams.Width == BotwSceneWidth && FramebufferParams.Height == BotwSceneHeight && count != 3;
            Vector2 target = scene ? BotwJitter : Vector2.Zero;

            if (target == _botwJitterApplied)
            {
                return;
            }

            Vector2 delta = target - _botwJitterApplied;

            for (int i = 0; i < DynamicState.ViewportsCount; i++)
            {
                Silk.NET.Vulkan.Viewport v = DynamicState.Viewports[i];
                v.X += delta.X;
                v.Y += delta.Y;
                DynamicState.SetViewport(i, v);
            }

            _botwJitterApplied = target;
        }
    }
}
