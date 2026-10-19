using Silk.NET.Vulkan;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class FenceHolder : IDisposable
    {
        private readonly Vk _api;
        private readonly Device _device;
        private Fence _fence;
        private int _referenceCount;
        private int _lock;
        private readonly bool _concurrentWaitUnsupported;
        private readonly FencePool _pool;
        private bool _disposed;

        public unsafe FenceHolder(Vk api, Device device, bool concurrentWaitUnsupported, FencePool pool = null)
        {
            _api = api;
            _device = device;
            _concurrentWaitUnsupported = concurrentWaitUnsupported;
            _pool = pool;

            if (pool != null)
            {
                _fence = pool.Rent();
            }
            else
            {
                FenceCreateInfo fenceCreateInfo = new()
                {
                    SType = StructureType.FenceCreateInfo,
                };

                api.CreateFence(device, in fenceCreateInfo, null, out _fence).ThrowOnError();
            }

            _referenceCount = 1;
        }

        public Fence GetUnsafe()
        {
            return _fence;
        }

        public bool TryGet(out Fence fence)
        {
            int lastValue;
            do
            {
                lastValue = _referenceCount;

                if (lastValue == 0)
                {
                    fence = default;
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            if (_concurrentWaitUnsupported)
            {
                AcquireLock();
            }

            fence = _fence;
            return true;
        }

        public Fence Get()
        {
            Interlocked.Increment(ref _referenceCount);
            return _fence;
        }

        public void PutLock()
        {
            Put();

            if (_concurrentWaitUnsupported)
            {
                ReleaseLock();
            }
        }

        public void Put()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                if (_pool != null)
                {
                    _pool.Return(_fence);
                }
                else
                {
                    FencePool.Destroy(_api, _device, _fence);
                }

                _fence = default;
            }
        }

        private void AcquireLock()
        {
            while (!TryAcquireLock())
            {
                Thread.SpinWait(32);
            }
        }

        private bool TryAcquireLock()
        {
            return Interlocked.Exchange(ref _lock, 1) == 0;
        }

        private void ReleaseLock()
        {
            Interlocked.Exchange(ref _lock, 0);
        }

        public void Wait()
        {
            if (_concurrentWaitUnsupported)
            {
                AcquireLock();

                try
                {
                    FenceHelper.WaitAllIndefinitely(_api, _device, [_fence]);
                }
                finally
                {
                    ReleaseLock();
                }
            }
            else
            {
                FenceHelper.WaitAllIndefinitely(_api, _device, [_fence]);
            }
        }

        public bool IsSignaled()
        {
            long pollStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return IsSignaledImpl();
            }
            finally
            {
                Ryujinx.Common.BotwPc.GpuCounters.AddElapsed(Ryujinx.Common.BotwPc.GpuCounters.Id.FencePolls, Ryujinx.Common.BotwPc.GpuCounters.Id.FencePollTicks, pollStart);
            }
        }

        private bool IsSignaledImpl()
        {
            if (_concurrentWaitUnsupported)
            {
                if (!TryAcquireLock())
                {
                    return false;
                }

                try
                {
                    return FenceHelper.AllSignaled(_api, _device, [_fence]);
                }
                finally
                {
                    ReleaseLock();
                }
            }
            else
            {
                return FenceHelper.AllSignaled(_api, _device, [_fence]);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Put();
                _disposed = true;
            }
        }
    }
}
