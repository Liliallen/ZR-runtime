using Ryujinx.Common;
using Ryujinx.Common.Collections;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    internal class HostMemoryAllocator
    {
        private readonly struct HostMemoryAllocation
        {
            public readonly Auto<MemoryAllocation> Allocation;
            public readonly nint Pointer;
            public readonly ulong Size;

            public ulong Start => (ulong)Pointer;
            public ulong End => (ulong)Pointer + Size;

            public HostMemoryAllocation(Auto<MemoryAllocation> allocation, nint pointer, ulong size)
            {
                Allocation = allocation;
                Pointer = pointer;
                Size = size;
            }
        }

        private readonly MemoryAllocator _allocator;
        private readonly Vk _api;
        private readonly ExtExternalMemoryHost _hostMemoryApi;
        private readonly Device _device;
        private readonly Lock _lock = new();

        private readonly List<HostMemoryAllocation> _allocations;
        private readonly IntervalTree<ulong, HostMemoryAllocation> _allocationTree;

        public HostMemoryAllocator(MemoryAllocator allocator, Vk api, ExtExternalMemoryHost hostMemoryApi, Device device)
        {
            _allocator = allocator;
            _api = api;
            _hostMemoryApi = hostMemoryApi;
            _device = device;

            _allocations = [];
            _allocationTree = new IntervalTree<ulong, HostMemoryAllocation>();
        }

        public unsafe bool TryImport(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            nint pointer,
            ulong size)
        {
            lock (_lock)
            {
                // Does a compatible allocation exist in the tree?
                HostMemoryAllocation[] allocations = new HostMemoryAllocation[10];

                ulong start = (ulong)pointer;
                ulong end = start + size;

                int count = _allocationTree.Get(start, end, ref allocations);

                // A compatible range is one that where the start and end completely cover the requested range.
                for (int i = 0; i < count; i++)
                {
                    HostMemoryAllocation existing = allocations[i];

                    if (start >= existing.Start && end <= existing.End)
                    {
                        try
                        {
                            existing.Allocation.IncrementReferenceCount();

                            return true;
                        }
                        catch (InvalidOperationException)
                        {
                            // Can throw if the allocation has been disposed.
                            // Just continue the search if this happens.
                        }
                    }
                }

                // BOTW-PC: an import costs ~10–70 ms in the NVIDIA driver whatever its size, and BotW's world load asks for
                // ~30 small ones in a row (a ~2 s GPU-thread stall). Import a whole aligned chunk instead, keep it for the
                // process lifetime (one extra reference) and let later flush buffers in it reuse it. Falls back to the exact
                // range when the chunk import fails. BOTWPC_HOST_IMPORT_CHUNK_MB=0 imports exact ranges only.
                if (_chunkSize != 0)
                {
                    nint chunkStart = BitUtils.AlignDown(pointer, (nint)_chunkSize);
                    nint chunkEnd = BitUtils.AlignUp((nint)((ulong)pointer + size), (nint)_chunkSize);

                    if (TryImportRange(requirements, flags, chunkStart, (ulong)(chunkEnd - chunkStart), out Auto<MemoryAllocation> chunk))
                    {
                        chunk.IncrementReferenceCount(); // the permanent reference

                        return true;
                    }
                }

                return TryImportRange(requirements, flags, BitUtils.AlignDown(pointer, Environment.SystemPageSize),
                    (ulong)(BitUtils.AlignUp((nint)((ulong)pointer + size), Environment.SystemPageSize) - BitUtils.AlignDown(pointer, Environment.SystemPageSize)), out _);
            }
        }

        private static readonly ulong _chunkSize = ReadChunkSize();

        private static ulong ReadChunkSize()
        {
            string value = Environment.GetEnvironmentVariable("BOTWPC_HOST_IMPORT_CHUNK_MB");

            return (ulong)(int.TryParse(value, out int mb) && mb >= 0 ? mb : 16) << 20;
        }

        // Imports [pageAlignedPointer, +pageAlignedSize) and registers it with one reference (the caller's). Holds _lock.
        private unsafe bool TryImportRange(MemoryRequirements requirements, MemoryPropertyFlags flags, nint pageAlignedPointer, ulong pageAlignedSize, out Auto<MemoryAllocation> imported)
        {
            imported = null;

            long importStart = System.Diagnostics.Stopwatch.GetTimestamp();

            Result getResult = _hostMemoryApi.GetMemoryHostPointerProperties(_device, ExternalMemoryHandleTypeFlags.HostAllocationBitExt, (void*)pageAlignedPointer, out MemoryHostPointerPropertiesEXT properties);
            if (getResult < Result.Success)
            {
                return false;
            }

            int memoryTypeIndex = _allocator.FindSuitableMemoryTypeIndex(properties.MemoryTypeBits & requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                return false;
            }

            ImportMemoryHostPointerInfoEXT importInfo = new()
            {
                SType = StructureType.ImportMemoryHostPointerInfoExt,
                HandleType = ExternalMemoryHandleTypeFlags.HostAllocationBitExt,
                PHostPointer = (void*)pageAlignedPointer,
            };

            MemoryAllocateInfo memoryAllocateInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = pageAlignedSize,
                MemoryTypeIndex = (uint)memoryTypeIndex,
                PNext = &importInfo,
            };

            Result result = _api.AllocateMemory(_device, in memoryAllocateInfo, null, out DeviceMemory deviceMemory);

            if (result < Result.Success)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, $"Host mapping import 0x{pageAlignedPointer:x16} 0x{pageAlignedSize:x8} failed.");
                return false;
            }

            MemoryAllocation allocation = new(this, deviceMemory, pageAlignedPointer, 0, pageAlignedSize);
            Auto<MemoryAllocation> allocAuto = new(allocation);
            HostMemoryAllocation hostAlloc = new(allocAuto, pageAlignedPointer, pageAlignedSize);

            allocAuto.IncrementReferenceCount();
            allocAuto.Dispose(); // Kept alive by ref count only.
            imported = allocAuto;

            // BOTW-PC: a slow driver import stalls the GPU thread (seen: 1.5 s at world load, near the 2 s TDR limit).
            double importMs = System.Diagnostics.Stopwatch.GetElapsedTime(importStart).TotalMilliseconds;
            if (importMs > 50 || Environment.GetEnvironmentVariable("BOTWPC_LOG_HOST_IMPORT") == "1")
            {
                Logger.Info?.Print(LogClass.Gpu, $"BOTW-PC host import 0x{pageAlignedPointer:x} size 0x{pageAlignedSize:x} took {importMs:F1} ms ({_allocations.Count + 1} imports).");
            }

            // Register this mapping for future use.

            _allocationTree.Add(hostAlloc.Start, hostAlloc.End, hostAlloc);
            _allocations.Add(hostAlloc);

            return true;
        }

        public (Auto<MemoryAllocation>, ulong) GetExistingAllocation(nint pointer, ulong size)
        {
            lock (_lock)
            {
                // Does a compatible allocation exist in the tree?
                HostMemoryAllocation[] allocations = new HostMemoryAllocation[10];

                ulong start = (ulong)pointer;
                ulong end = start + size;

                int count = _allocationTree.Get(start, end, ref allocations);

                // A compatible range is one that where the start and end completely cover the requested range.
                for (int i = 0; i < count; i++)
                {
                    HostMemoryAllocation existing = allocations[i];

                    if (start >= existing.Start && end <= existing.End)
                    {
                        return (existing.Allocation, start - existing.Start);
                    }
                }

                throw new InvalidOperationException($"No host allocation was prepared for requested range 0x{pointer:x16}:0x{size:x16}.");
            }
        }

        public void Free(DeviceMemory memory, ulong offset, ulong size)
        {
            lock (_lock)
            {
                _allocations.RemoveAll(allocation =>
                {
                    if (allocation.Allocation.GetUnsafe().Memory.Handle == memory.Handle)
                    {
                        _allocationTree.Remove(allocation.Start, allocation);
                        return true;
                    }

                    return false;
                });
            }

            _api.FreeMemory(_device, memory, ReadOnlySpan<AllocationCallbacks>.Empty);
        }
    }
}
