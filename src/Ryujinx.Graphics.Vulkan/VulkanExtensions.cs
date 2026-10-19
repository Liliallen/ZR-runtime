using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Silk.NET's Vk.TryGet*Extension create the extension object with Activator.CreateInstance, which fails under
    /// NativeAOT on iOS ("Member not found"). These do the same with an explicit constructor.
    /// </summary>
    public static class VulkanExtensions
    {
        public static bool TryGetInstanceExtension<T>(this Vk api, Instance instance, string name, Func<INativeContext, T> create, out T ext) where T : class
        {
            ext = api.IsInstanceExtensionPresent(name) ? create(new LamdaNativeContext(x => api.GetInstanceProcAddr(instance, x))) : null;

            return ext != null;
        }

        public static bool TryGetDeviceExtension<T>(this Vk api, Instance instance, Device device, string name, Func<INativeContext, T> create, out T ext) where T : class
        {
            ext = api.IsDeviceExtensionPresent(instance, name) ? create(new LamdaNativeContext(x => api.GetDeviceProcAddr(device, x))) : null;

            return ext != null;
        }
    }
}
