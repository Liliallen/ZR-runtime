using Ryujinx.Common.BotwPc;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// BOTW-PC: recycles VkFence objects. On NVIDIA 615.x/Linux vkCreateFence and vkDestroyFence cost
    /// ~0.3-0.6 ms each, and the command buffer pool replaces a fence on every reuse.
    /// Only fences that are signaled when released are reset and reused; others are destroyed as before.
    /// Disable with BOTWPC_FENCE_POOL=0.
    /// </summary>
    class FencePool : IDisposable
    {
        private const int MaxPooled = 64;

        public static readonly bool Enabled = Environment.GetEnvironmentVariable("BOTWPC_FENCE_POOL") != "0";

        private readonly Vk _api;
        private readonly Device _device;
        private readonly ConcurrentQueue<Fence> _free = new();
        private volatile bool _disposed;

        public FencePool(Vk api, Device device)
        {
            _api = api;
            _device = device;
        }

        public unsafe Fence Rent()
        {
            if (_free.TryDequeue(out Fence fence))
            {
                return fence;
            }

            long start = Stopwatch.GetTimestamp();

            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
            };

            _api.CreateFence(_device, in fenceCreateInfo, null, out fence).ThrowOnError();
            GpuCounters.AddElapsed(GpuCounters.Id.FenceCreates, GpuCounters.Id.FenceCreateTicks, start);

            return fence;
        }

        public void Return(Fence fence)
        {
            long start = Stopwatch.GetTimestamp();

            // A fence may only be reset once it is no longer pending; unsignaled ones may still be in flight.
            if (!_disposed && _free.Count < MaxPooled && _api.GetFenceStatus(_device, fence) == Result.Success &&
                _api.ResetFences(_device, 1, in fence) == Result.Success)
            {
                _free.Enqueue(fence);
                GpuCounters.AddElapsed(GpuCounters.Id.FenceResets, GpuCounters.Id.FenceResetTicks, start);

                return;
            }

            Destroy(_api, _device, fence);
        }

        public static void Destroy(Vk api, Device device, Fence fence)
        {
            long start = Stopwatch.GetTimestamp();
            api.DestroyFence(device, fence, Span<AllocationCallbacks>.Empty);
            GpuCounters.AddElapsed(GpuCounters.Id.FenceDestroys, GpuCounters.Id.FenceDestroyTicks, start);
        }

        public void Dispose()
        {
            _disposed = true;

            while (_free.TryDequeue(out Fence fence))
            {
                _api.DestroyFence(_device, fence, Span<AllocationCallbacks>.Empty);
            }
        }
    }
}
