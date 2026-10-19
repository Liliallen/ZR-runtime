using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.Input.HLE;
using Silk.NET.Vulkan;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using SPB.Graphics.Vulkan;
using SPB.Platform.Metal;
using SPB.Windowing;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Headless
{
    /// <summary>
    /// Cemu-Switch: a window whose surface is the CAMetalLayer of an NSView owned by the hosting application (Cemu's
    /// game canvas). No SDL window is created; SDL still serves controllers and audio. The host reports size changes.
    /// </summary>
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    sealed class ExternalMetalWindow : VulkanWindow
    {
        private readonly CemuSwitchEntry.HostSurface _surface;

        public ExternalMetalWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            HideCursorMode hideCursorMode,
            bool ignoreControllerApplet,
            CemuSwitchEntry.HostSurface surface)
            : base(inputManager, glLogLevel, aspectRatio, false, hideCursorMode, ignoreControllerApplet)
        {
            _surface = surface;
        }

        protected override void InitializeWindow()
        {
            Width = _surface.Width;
            Height = _surface.Height;
            Logger.Info?.Print(LogClass.Application, $"Cemu-Switch: rendering into the host's CAMetalLayer ({Width}x{Height})");
        }

        protected override void InitializeRenderer()
        {
            Renderer?.Window.SetSize(Width, Height);
            MouseDriver.SetClientSize(Width, Height);
        }

        public void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            Renderer?.Window.SetSize(width, height);
            MouseDriver.SetClientSize(width, height);
        }

        public override nint CreateWindowSurface(nint instance)
        {
            if (OperatingSystem.IsIOS())
            {
                return CreateMetalSurface(instance);
            }

            return (nint)VulkanHelper.CreateWindowSurface(instance, new SimpleMetalWindow(new NativeHandle(_surface.NsView), new NativeHandle(_surface.MetalLayer)));
        }

        public override string[] GetRequiredInstanceExtensions() =>
            OperatingSystem.IsIOS() ? ["VK_KHR_surface", "VK_EXT_metal_surface"] : VulkanHelper.GetRequiredInstanceExtensions();

        // iOS (the iPadOS app): SPB's helpers are macOS-only, so the surface is made with VK_EXT_metal_surface directly.
        private unsafe nint CreateMetalSurface(nint instance)
        {
            Vk api = Ryujinx.Graphics.Vulkan.MoltenVK.MVKInitialization.GetApi();
            Instance vkInstance = new(instance);

            if (!api.TryGetInstanceExtension(vkInstance, ExtMetalSurface.ExtensionName, ctx => new ExtMetalSurface(ctx), out ExtMetalSurface metalSurface))
            {
                throw new System.InvalidOperationException("VK_EXT_metal_surface is not available.");
            }

            MetalSurfaceCreateInfoEXT info = new()
            {
                SType = StructureType.MetalSurfaceCreateInfoExt,
                PLayer = (nint*)_surface.MetalLayer,
            };

            Result result = metalSurface.CreateMetalSurface(vkInstance, &info, null, out SurfaceKHR surface);

            return result == Result.Success ? (nint)surface.Handle : throw new System.InvalidOperationException($"vkCreateMetalSurfaceEXT: {result}");
        }
    }
}
