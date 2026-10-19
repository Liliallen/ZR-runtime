using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Vulkan.MoltenVK
{
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    public static partial class MVKInitialization
    {
        private const string VulkanLib = "libvulkan.dylib";

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkGetMoltenVKConfigurationMVK(nint unusedInstance, out MVKConfiguration config, in nint configSize);

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkSetMoltenVKConfigurationMVK(nint unusedInstance, in MVKConfiguration config, in nint configSize);

        public static void Initialize()
        {
            IntPtr configSize = (nint)Marshal.SizeOf<MVKConfiguration>();

            vkGetMoltenVKConfigurationMVK(nint.Zero, out MVKConfiguration config, configSize);

            // BOTW-PC: BOTWPC_MVK_ARGUMENT_BUFFERS / BOTWPC_MVK_SYNC_SUBMITS = 0|1 override these two for A/B tests
            // (MACOS_PERFORMANCE.md); every other field already follows MoltenVK's MVK_CONFIG_* variables.
            config.UseMetalArgumentBuffers = Environment.GetEnvironmentVariable("BOTWPC_MVK_ARGUMENT_BUFFERS") != "0";

            config.SemaphoreSupportStyle = MVKVkSemaphoreSupportStyle.MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_SINGLE_QUEUE;
            config.SynchronousQueueSubmits = Environment.GetEnvironmentVariable("BOTWPC_MVK_SYNC_SUBMITS") == "1";

            config.ResumeLostDevice = true;

            vkSetMoltenVKConfigurationMVK(nint.Zero, config, configSize);
        }

        private static string[] Resolver(string path)
        {
            if (path.EndsWith(VulkanLib))
            {
                path = path[..^VulkanLib.Length] + "libMoltenVK.dylib";

                // iOS apps keep their dylibs in Frameworks/.
                return OperatingSystem.IsIOS() ? [System.IO.Path.Combine(AppContext.BaseDirectory, "Frameworks", "libMoltenVK.dylib"), path] : [path];
            }

            return [];
        }

        /// <summary>
        /// The Vulkan API. On iOS Silk.NET expects Vulkan linked into the executable ("__Internal"); the iPadOS app ships
        /// MoltenVK as a dylib in Frameworks instead.
        /// </summary>
        public static Vk GetApi() => OperatingSystem.IsIOS()
            ? _iosApi ??= new Vk(Vk.CreateDefaultContext([System.IO.Path.Combine(AppContext.BaseDirectory, "Frameworks", "libMoltenVK.dylib")]))
            : Vk.GetApi();

        private static Vk _iosApi;

        public static void InitializeResolver()
        {
            ((DefaultPathResolver)PathResolver.Default).Resolvers.Insert(0, Resolver);
        }
    }
}
