using Silk.NET.Core;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC: VK_EXT_present_mode_fifo_latest_ready, missing from Silk.NET 2.22.
    /// Tear-free like FIFO but presents the newest ready image at each refresh without blocking the
    /// application, i.e. mailbox behaviour. NVIDIA on Wayland offers this but no MAILBOX.
    /// </summary>
    static class PresentModeFifoLatestReady
    {
        public const string ExtensionName = "VK_EXT_present_mode_fifo_latest_ready";
        public const PresentModeKHR PresentMode = (PresentModeKHR)1000361000;
        public const StructureType FeaturesStructureType = (StructureType)1000361000;

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct Features
        {
            public StructureType SType;
            public void* PNext;
            public Bool32 PresentModeFifoLatestReady;
        }
    }
}
