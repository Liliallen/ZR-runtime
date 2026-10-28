using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC DLSS Frame Generation at present (DlssG.cs): frame tokens and latency markers, Streamline's common
    /// constants and the tagged depth / motion vectors / hud-less colour of the frame, and turning frame generation on
    /// for frames that have them (gameplay) and off for the others (menus, map, loading, videos, the BOTW-PC menu).
    /// </summary>
    partial class Window
    {
        private uint _fgFrame;
        private bool _fgFrameStarted, _fgOn;
        private int _fgPresents;
        private long _fgPresentedSum;
        private int _fgFrames;
        private uint _fgLimitUs;
        private float _fgShown;
        private uint _fgLastStatus;

        // Debug: BOTWPC_DLSSG_DUMP=<dir> writes the hud-less colour and the presented game image of one frame (raw texels,
        // local files only) to check that they share a colour encoding, as DLSS-G requires.
        private static readonly string _fgDumpDir = Environment.GetEnvironmentVariable("BOTWPC_DLSSG_DUMP");
        private int _fgDumpCountdown = 1200;

        private void FrameGenDump(DlssGInputs inputs, TextureView presented)
        {
            System.IO.Directory.CreateDirectory(_fgDumpDir);

            using (PinnedSpan<byte> h = inputs.Hudless.GetData())
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(_fgDumpDir, "hudless.raw"), h.Get().ToArray());
            }

            using (PinnedSpan<byte> p = presented.GetData())
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(_fgDumpDir, "presented.raw"), p.Get().ToArray());
            }

            System.IO.File.WriteAllText(System.IO.Path.Combine(_fgDumpDir, "info.txt"),
                $"hudless {inputs.Hudless.Width} {inputs.Hudless.Height} {inputs.Hudless.Info.Format} {inputs.Hudless.VkFormat}{Environment.NewLine}" +
                $"presented {presented.Width} {presented.Height} {presented.Info.Format} {presented.VkFormat} swapchain {_format} linear {_isLinear}{Environment.NewLine}");
            Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: dumped hud-less and presented frame to {_fgDumpDir}");
        }

        private unsafe void FrameGenBeforeSubmit(CommandBufferScoped cbs, TextureView presented)
        {
            if (!_fgFrameStarted)
            {
                DlssG.BeginFrame(_fgFrame);
                _fgFrameStarted = true;
            }

            DlssG.Marker(1); // simulation end
            DlssG.Marker(2); // render submit start

            DlssGInputs inputs = _gd.FrameGenInputs;

            if (_fgDumpDir != null && inputs.Valid && _fgDumpCountdown > 0 && --_fgDumpCountdown == 0)
            {
                FrameGenDump(inputs, presented);
            }

            bool menu = _overlayVisible && _overlayTexture != null && _overlayTexture.Width >= _width;
            int frames = Common.BotwPc.Overlay.DlssRuntime.FrameGenFrames; // live: the BOTW-PC menu changes it
            bool on = inputs.Valid && !menu && frames > 0;

            if (on)
            {
                fixed (DlssG.SlConstants* constants = &inputs.Constants)
                {
                    DlssG.SetConstants(constants);
                }

                DlssG.SlImage depth = SlImage(inputs.Depth, cbs, inputs.RenderWidth, inputs.RenderHeight);
                DlssG.SlImage motion = SlImage(inputs.Motion, cbs, inputs.RenderWidth, inputs.RenderHeight);
                DlssG.SlImage hudless = SlImage(inputs.Hudless, cbs, 0, 0);

                // The hud-less colour must match the back buffer (1:1 output, no scaling filter or letterbox).
                bool hudlessUsable = inputs.Hudless.Width == _width && inputs.Hudless.Height == _height && _scalingFilter == null;
                int r = DlssG.Tag(cbs.CommandBuffer, &depth, &motion, hudlessUsable ? &hudless : null);

                if (r != 0 && _fgPresents < 3)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: tagging failed ({DlssG.ResultString(r)}).");
                }
            }
            else if (_fgOn)
            {
                DlssG.Tag(cbs.CommandBuffer, null, null, null);
            }

            if (on != _fgOn || (on && frames != _fgFrames))
            {
                int r = DlssG.SetOptions(on ? 1 : 0, (uint)Math.Max(frames, 1));
                _fgOn = on;
                _fgFrames = frames;
                Common.BotwPc.Overlay.DlssRuntime.FrameGenStatus = on ? $"on ({frames + 1}x)" : frames > 0 ? "paused (no game frame)" : "off";

                if (r != 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: set options {(on ? $"{frames + 1}x" : "off")} failed ({DlssG.ResultString(r)}).");
                }
            }

            // The shown-frame limit applies only while frames are generated (live from the menu).
            uint limitUs = on ? DlssG.FrameLimitUs : 0;

            if (limitUs != _fgLimitUs)
            {
                DlssG.SetReflex(1, limitUs);
                _fgLimitUs = limitUs;
            }

            inputs.Valid = false;
            DlssG.Marker(3); // render submit end
        }

        private unsafe void FrameGenAfterPresent()
        {
            DlssG.Marker(5); // present end

            if (_fgOn)
            {
                // Frames shown per game frame (2 with one generated frame; 1 while Streamline holds interpolation back,
                // e.g. when the window is not in the foreground): a short average for the HUD, 600 game frames for the log.
                uint status, presented, maxFrames, minSize;
                int r = DlssG.GetState(&status, &presented, &maxFrames, &minSize);
                _fgPresentedSum += presented;
                _fgShown = _fgShown == 0 ? presented : _fgShown * 0.95f + presented * 0.05f;
                Common.BotwPc.Overlay.DlssRuntime.FrameGenShownPerFrame = _fgShown;

                if (++_fgPresents % 600 == 0 || status != _fgLastStatus)
                {
                    double average = _fgPresentedSum / (double)(_fgPresents % 600 == 0 ? 600 : Math.Max(1, _fgPresents % 600));
                    Common.BotwPc.Overlay.DlssRuntime.FrameGenStatus = status == 0 ? $"on ({_fgFrames + 1}x)" : $"error 0x{status:x}";
                    Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC DLSS-G: {DlssG.ResultString(r)}, {_fgFrames + 1}x, status 0x{status:x}, {average:0.00} frames shown per game frame (last 600), max generated {maxFrames}.");
                    _fgLastStatus = status;

                    if (_fgPresents % 600 == 0)
                    {
                        _fgPresentedSum = 0;
                    }
                }
            }
            else
            {
                _fgShown = 0;
                Common.BotwPc.Overlay.DlssRuntime.FrameGenShownPerFrame = 0;
            }

            _fgFrame++;
            DlssG.BeginFrame(_fgFrame); // simulation start of the next frame
            _fgFrameStarted = true;
        }

        private unsafe void FrameGenOff()
        {
            if (_fgOn)
            {
                DlssG.SetOptions(0, (uint)Math.Max(_fgFrames, 1));
                DlssG.SetReflex(1, 0);
                _fgLimitUs = 0;
                _fgOn = false;
            }
        }

        private DlssG.SlImage SlImage(TextureView view, CommandBufferScoped cbs, uint width, uint height) => new()
        {
            Image = view.GetImage().Get(cbs).Value,
            View = view.GetIdentityImageView().Get(cbs).Value,
            Format = view.VkFormat,
            Width = (uint)view.Width,
            Height = (uint)view.Height,
            Usage = (uint)TextureStorage.GetImageUsage(view.Info.Format, _gd.Capabilities, true, true),
            Layout = (uint)ImageLayout.General,
            W = width,
            H = height,
        };
    }
}
