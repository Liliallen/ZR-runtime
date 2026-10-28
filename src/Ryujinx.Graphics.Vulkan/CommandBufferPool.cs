using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Ryujinx.Graphics.Vulkan
{
    class CommandBufferPool : IDisposable
    {
        public const int MaxCommandBuffers = 16;

        private readonly int _totalCommandBuffers;
        private readonly int _totalCommandBuffersMask;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly Queue _queue;
        private readonly Lock _queueLock;
        private readonly bool _concurrentFenceWaitUnsupported;
        private readonly CommandPool _pool;
        private readonly FencePool _fencePool;
        private readonly Thread _owner;

        public bool OwnedByCurrentThread => _owner == Thread.CurrentThread;

        private struct ReservedCommandBuffer
        {
            public bool InUse;
            public bool InConsumption;
            public int SubmissionCount;
            public CommandBuffer CommandBuffer;
            public FenceHolder Fence;
            public long SubmitTicks;

            public List<IAuto> Dependants;
            public List<MultiFenceHolder> Waitables;

            public void Initialize(Vk api, Device device, CommandPool pool)
            {
                CommandBufferAllocateInfo allocateInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandBufferCount = 1,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Primary,
                };

                api.AllocateCommandBuffers(device, in allocateInfo, out CommandBuffer);

                Dependants = [];
                Waitables = [];
            }
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private readonly int[] _queuedIndexes;
        private int _queuedIndexesPtr;
        private int _queuedCount;
        private int _inUseCount;

        // Optional GPU timestamps (2 per command buffer) for GpuCounters.GpuBusyTicks.
        private QueryPool _timestampPool;
        private double _timestampTicksPerUnit;
        private ulong _lastTimestampEnd;
        // Timeline only: GPU ticks -> Stopwatch ticks offset, estimated as the smallest (GPU start - CPU submit) seen
        // (a command buffer cannot start before it was submitted; the error is the shortest queue latency).
        // The two clocks drift (tens of ppm), so the minimum is taken per 1 s window (the previous window's value is
        // used until the current one has samples).
        private double _gpuToCpuOffset = double.MaxValue;
        private double _gpuToCpuOffsetNext = double.MaxValue;
        private long _gpuToCpuWindowEnd;

        public unsafe CommandBufferPool(
            Vk api,
            Device device,
            Queue queue,
            Lock queueLock,
            uint queueFamilyIndex,
            bool concurrentFenceWaitUnsupported,
            bool isLight = false)
        {
            _api = api;
            _device = device;
            _queue = queue;
            _queueLock = queueLock;
            _concurrentFenceWaitUnsupported = concurrentFenceWaitUnsupported;
            _owner = Thread.CurrentThread;
            _fencePool = FencePool.Enabled ? new FencePool(api, device) : null;

            CommandPoolCreateInfo commandPoolCreateInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit |
                        CommandPoolCreateFlags.ResetCommandBufferBit,
            };

            api.CreateCommandPool(device, in commandPoolCreateInfo, null, out _pool).ThrowOnError();

            // We need at least 2 command buffers to get texture data in some cases.
            _totalCommandBuffers = isLight ? 2 : MaxCommandBuffers;
            _totalCommandBuffersMask = _totalCommandBuffers - 1;

            _commandBuffers = new ReservedCommandBuffer[_totalCommandBuffers];

            _queuedIndexes = new int[_totalCommandBuffers];
            _queuedIndexesPtr = 0;
            _queuedCount = 0;

            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                _commandBuffers[i].Initialize(api, device, _pool);
                WaitAndDecrementRef(i);
            }
        }

        public unsafe void EnableGpuTimestamps(float timestampPeriodNs)
        {
            QueryPoolCreateInfo info = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = (uint)_totalCommandBuffers * 2,
            };

            _api.CreateQueryPool(_device, in info, null, out _timestampPool).ThrowOnError();
            _timestampTicksPerUnit = timestampPeriodNs * Stopwatch.Frequency / 1e9;
        }

        private unsafe void AccumulateGpuTime(int cbIndex)
        {
            ulong* times = stackalloc ulong[2];

            if (_api.GetQueryPoolResults(_device, _timestampPool, (uint)cbIndex * 2, 2, 16, times, 8, QueryResultFlags.Result64Bit) != Result.Success)
            {
                return;
            }

            // Union of intervals: overlapping command buffers are not counted twice.
            ulong start = Math.Max(times[0], _lastTimestampEnd);

            if (times[1] > start)
            {
                Ryujinx.Common.BotwPc.GpuCounters.Add(Ryujinx.Common.BotwPc.GpuCounters.Id.GpuBusyTicks, (long)((times[1] - start) * _timestampTicksPerUnit));
            }

            _lastTimestampEnd = Math.Max(_lastTimestampEnd, times[1]);

            if (Ryujinx.Common.BotwPc.TimelineTrace.Configured && times[1] >= times[0] && _commandBuffers[cbIndex].SubmitTicks != 0)
            {
                double gpuStart = times[0] * _timestampTicksPerUnit;
                long submit = _commandBuffers[cbIndex].SubmitTicks;
                _gpuToCpuOffsetNext = Math.Min(_gpuToCpuOffsetNext, gpuStart - submit);

                if (submit >= _gpuToCpuWindowEnd)
                {
                    _gpuToCpuOffset = _gpuToCpuOffsetNext;
                    _gpuToCpuOffsetNext = double.MaxValue;
                    _gpuToCpuWindowEnd = submit + Stopwatch.Frequency;
                }

                _gpuToCpuOffset = Math.Min(_gpuToCpuOffset, gpuStart - submit);
                long cpuStart = (long)(gpuStart - _gpuToCpuOffset);
                long cpuEnd = (long)(times[1] * _timestampTicksPerUnit - _gpuToCpuOffset);
                Ryujinx.Common.BotwPc.TimelineTrace.Span("GpuExec", cpuStart, cpuEnd, (cpuStart - _commandBuffers[cbIndex].SubmitTicks).ToString());
            }
        }

        public void AddDependant(int cbIndex, IAuto dependant)
        {
            dependant.IncrementReferenceCount();
            _commandBuffers[cbIndex].Dependants.Add(dependant);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddInUseWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];
            if (waitable.AddFence(cbIndex, entry.Fence))
            {
                entry.Waitables.Add(waitable);
            }
        }

        public bool HasWaitableOnRentedCommandBuffer(MultiFenceHolder waitable, int offset, int size)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse &&
                        waitable.HasFence(i) &&
                        waitable.IsBufferRangeInUse(i, offset, size))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsFenceOnRentedCommandBuffer(FenceHolder fence)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse && entry.Fence == fence)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public FenceHolder GetFence(int cbIndex)
        {
            return _commandBuffers[cbIndex].Fence;
        }

        public int GetSubmissionCount(int cbIndex)
        {
            return _commandBuffers[cbIndex].SubmissionCount;
        }

        private int FreeConsumed(bool wait)
        {
            int freeEntry = 0;

            while (_queuedCount > 0)
            {
                int index = _queuedIndexes[_queuedIndexesPtr];

                ref ReservedCommandBuffer entry = ref _commandBuffers[index];

                if (wait || !entry.InConsumption || entry.Fence.IsSignaled())
                {
                    WaitAndDecrementRef(index);

                    wait = false;
                    freeEntry = index;

                    _queuedCount--;
                    _queuedIndexesPtr = (_queuedIndexesPtr + 1) % _totalCommandBuffers;
                }
                else
                {
                    break;
                }
            }

            return freeEntry;
        }

        public CommandBufferScoped ReturnAndRent(CommandBufferScoped cbs)
        {
            Return(cbs);
            return Rent();
        }

        public CommandBufferScoped Rent()
        {
            lock (_commandBuffers)
            {
                int cursor = FreeConsumed(_inUseCount + _queuedCount == _totalCommandBuffers);

                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[cursor];

                    if (!entry.InUse && !entry.InConsumption)
                    {
                        entry.InUse = true;

                        _inUseCount++;

                        CommandBufferBeginInfo commandBufferBeginInfo = new()
                        {
                            SType = StructureType.CommandBufferBeginInfo,
                        };

                        _api.BeginCommandBuffer(entry.CommandBuffer, in commandBufferBeginInfo).ThrowOnError();

                        if (_timestampPool.Handle != 0)
                        {
                            _api.CmdResetQueryPool(entry.CommandBuffer, _timestampPool, (uint)cursor * 2, 2);
                            _api.CmdWriteTimestamp(entry.CommandBuffer, PipelineStageFlags.TopOfPipeBit, _timestampPool, (uint)cursor * 2);
                        }

                        return new CommandBufferScoped(this, entry.CommandBuffer, cursor);
                    }

                    cursor = (cursor + 1) & _totalCommandBuffersMask;
                }
            }

            throw new InvalidOperationException($"Out of command buffers (In use: {_inUseCount}, queued: {_queuedCount}, total: {_totalCommandBuffers})");
        }

        public void Return(CommandBufferScoped cbs)
        {
            Return(cbs, null, null, null);
        }

        public unsafe void Return(
            CommandBufferScoped cbs,
            ReadOnlySpan<Semaphore> waitSemaphores,
            ReadOnlySpan<PipelineStageFlags> waitDstStageMask,
            ReadOnlySpan<Semaphore> signalSemaphores)
        {
            lock (_commandBuffers)
            {
                int cbIndex = cbs.CommandBufferIndex;

                ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

                Debug.Assert(entry.InUse);
                Debug.Assert(entry.CommandBuffer.Handle == cbs.CommandBuffer.Handle);
                entry.InUse = false;
                entry.InConsumption = true;
                entry.SubmissionCount++;
                _inUseCount--;

                CommandBuffer commandBuffer = entry.CommandBuffer;

                if (_timestampPool.Handle != 0)
                {
                    _api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, _timestampPool, (uint)cbIndex * 2 + 1);
                }

                _api.EndCommandBuffer(commandBuffer).ThrowOnError();

                fixed (Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
                {
                    fixed (PipelineStageFlags* pWaitDstStageMask = waitDstStageMask)
                    {
                        SubmitInfo sInfo = new()
                        {
                            SType = StructureType.SubmitInfo,
                            WaitSemaphoreCount = !waitSemaphores.IsEmpty ? (uint)waitSemaphores.Length : 0,
                            PWaitSemaphores = pWaitSemaphores,
                            PWaitDstStageMask = pWaitDstStageMask,
                            CommandBufferCount = 1,
                            PCommandBuffers = &commandBuffer,
                            SignalSemaphoreCount = !signalSemaphores.IsEmpty ? (uint)signalSemaphores.Length : 0,
                            PSignalSemaphores = pSignalSemaphores,
                        };

                        lock (_queueLock)
                        {
                            long submitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            _api.QueueSubmit(_queue, 1, in sInfo, entry.Fence.GetUnsafe()).ThrowOnError();
                            entry.SubmitTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            Ryujinx.Common.BotwPc.GpuCounters.AddElapsed(Ryujinx.Common.BotwPc.GpuCounters.Id.QueueSubmits, Ryujinx.Common.BotwPc.GpuCounters.Id.QueueSubmitTicks, submitStart);
                        }
                    }
                }

                int ptr = (_queuedIndexesPtr + _queuedCount) % _totalCommandBuffers;
                _queuedIndexes[ptr] = cbIndex;
                _queuedCount++;
            }
        }

        private void WaitAndDecrementRef(int cbIndex, bool refreshFence = true)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

            if (entry.InConsumption)
            {
                long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                entry.Fence.Wait();
                Ryujinx.Common.BotwPc.GpuCounters.AddElapsed(Ryujinx.Common.BotwPc.GpuCounters.Id.CbWaits, Ryujinx.Common.BotwPc.GpuCounters.Id.CbWaitTicks, waitStart);
                entry.InConsumption = false;

                if (_timestampPool.Handle != 0)
                {
                    AccumulateGpuTime(cbIndex);
                }
            }

            foreach (IAuto dependant in entry.Dependants)
            {
                dependant.DecrementReferenceCount(cbIndex);
            }

            foreach (MultiFenceHolder waitable in entry.Waitables)
            {
                waitable.RemoveFence(cbIndex);
                waitable.RemoveBufferUses(cbIndex);
            }

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.Fence?.Dispose();

            if (refreshFence)
            {
                entry.Fence = new FenceHolder(_api, _device, _concurrentFenceWaitUnsupported, _fencePool);
            }
            else
            {
                entry.Fence = null;
            }
        }

        public unsafe void Dispose()
        {
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i, refreshFence: false);
            }

            // shortcut: a fence returned concurrently with this drain is leaked until device destruction; only at shutdown.
            _fencePool?.Dispose();

            if (_timestampPool.Handle != 0)
            {
                _api.DestroyQueryPool(_device, _timestampPool, null);
            }

            _api.DestroyCommandPool(_device, _pool, null);
        }
    }
}
