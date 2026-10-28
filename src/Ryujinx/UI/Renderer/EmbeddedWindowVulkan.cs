using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using SPB.Graphics.Vulkan;
using SPB.Platform.Metal;
using SPB.Platform.Win32;
using SPB.Platform.X11;
using SPB.Windowing;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Ava.UI.Renderer
{
    public class EmbeddedWindowVulkan : EmbeddedWindow
    {
        public SurfaceKHR CreateSurface(Instance instance)
        {
            NativeWindowBase nativeWindowBase;

            if (OperatingSystem.IsWindows())
            {
                nativeWindowBase = new SimpleWin32Window(new NativeHandle(WindowHandle));
            }
            else if (OperatingSystem.IsLinux())
            {
                nativeWindowBase = new SimpleX11Window(new NativeHandle(X11Display), new NativeHandle(WindowHandle));
            }
            else if (OperatingSystem.IsMacOS())
            {
                nativeWindowBase = new SimpleMetalWindow(new NativeHandle(NsView), new NativeHandle(MetalLayer));
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            return new SurfaceKHR((ulong?)VulkanHelper.CreateWindowSurface(instance.Handle, nativeWindowBase));
        }

        public SurfaceKHR CreateSurface(Instance instance, Vk vk)
        {
            // BOTW-PC: on Windows the surface is created through the renderer's Vulkan API rather than SPB's own
            // vulkan-1.dll, so a Vulkan interposer in use (Streamline for DLSS Frame Generation) sees the window.
            if (OperatingSystem.IsWindows() && vk.TryGetInstanceExtension(instance, out KhrWin32Surface win32Surface))
            {
                Win32SurfaceCreateInfoKHR info = new()
                {
                    SType = StructureType.Win32SurfaceCreateInfoKhr,
                    Hinstance = GetModuleHandleW(null),
                    Hwnd = WindowHandle,
                };

                unsafe
                {
                    if (win32Surface.CreateWin32Surface(instance, in info, null, out SurfaceKHR surface) == Result.Success)
                    {
                        return surface;
                    }
                }
            }

            return CreateSurface(instance);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string moduleName);
    }
}
