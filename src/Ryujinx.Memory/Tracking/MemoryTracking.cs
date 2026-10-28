using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;

namespace Ryujinx.Memory.Tracking
{
    /// <summary>
    /// Manages memory tracking for a given virutal/physical memory block.
    /// </summary>
    public class MemoryTracking
    {
        private readonly IVirtualMemoryManager _memoryManager;
        private readonly InvalidAccessHandler _invalidAccessHandler;

        // Only use these from within the lock.
        private readonly NonOverlappingRangeList<VirtualRegion> _virtualRegions;
        // Guest virtual regions are a subset of the normal virtual regions, with potentially different protection
        // and expanded area of effect on platforms that don't support misaligned page protection.
        private readonly NonOverlappingRangeList<VirtualRegion> _guestVirtualRegions;

        private readonly int _pageSize;

        private readonly bool _singleByteGuestTracking;

        /// <summary>
        /// This lock must be obtained when traversing or updating the region-handle hierarchy.
        /// It is not required when reading dirty flags.
        /// </summary>
        internal object TrackingLock = new();

        /// <summary>
        /// BOTW-PC Memory V3 (MEMORY_TRACKING_V3.md): while a multi-region dirty scan re-arms handles, the protection
        /// changes are recorded instead of applied one region at a time, and applied just before the scan hands a range
        /// to its caller (which then reads the memory). Adjacent regions that end up with the same permission are
        /// protected with one call. The permissions applied are the regions' current ones, looked up again under
        /// <see cref="TrackingLock"/> at flush time, so a concurrent change of a region (split, removal, signal) is never
        /// overwritten with a stale permission. A write that lands before the flush is still read by the caller.
        /// </summary>
        /// Rejected in measurement (in-run A/B -0.85 FPS): experiment only, BOTWPC_MEM_COALESCE=1 with memory tracking V3.
        private static readonly Ryujinx.Common.BotwPc.AbPhase.Switch _coalesceSwitch = new("BOTWPC_MEM_COALESCE", experimental: true);

        public static bool CoalesceReprotects => Ryujinx.Common.BotwPc.RuntimeV2.MemoryV3 && _coalesceSwitch.IsOn;

        private sealed class DeferredReprotect
        {
            public int Depth;
            public readonly List<(MemoryTracking Tracking, bool Guest, ulong Address, ulong Size)> Ranges = [];
        }

        [ThreadStatic]
        private static DeferredReprotect t_deferred;

        /// <summary>Starts recording protection changes on this thread (Memory V3); returns whether it did.</summary>
        internal static bool BeginDeferredReprotect()
        {
            if (!CoalesceReprotects)
            {
                return false;
            }

            (t_deferred ??= new DeferredReprotect()).Depth++;

            return true;
        }

        /// <summary>Applies the recorded protection changes (call before the protected memory is read).</summary>
        internal static void FlushDeferredReprotect()
        {
            DeferredReprotect deferred = t_deferred;

            if (deferred == null || deferred.Ranges.Count == 0)
            {
                return;
            }

            List<(MemoryTracking Tracking, bool Guest, ulong Address, ulong Size)> ranges = deferred.Ranges;

            if (ranges.Count > 1)
            {
                ranges.Sort((a, b) => a.Guest != b.Guest ? a.Guest.CompareTo(b.Guest) : a.Address.CompareTo(b.Address));
            }

            int i = 0;

            while (i < ranges.Count)
            {
                MemoryTracking tracking = ranges[i].Tracking;
                bool guest = ranges[i].Guest;
                ulong start = ranges[i].Address;
                ulong end = start + ranges[i].Size;
                int j = i + 1;

                while (j < ranges.Count && ranges[j].Tracking == tracking && ranges[j].Guest == guest && ranges[j].Address <= end)
                {
                    end = Math.Max(end, ranges[j].Address + ranges[j].Size);
                    j++;
                }

                tracking.ApplyCurrentProtection(start, end - start, guest, j - i);
                i = j;
            }

            ranges.Clear();
        }

        /// <summary>Applies the recorded changes, then runs the caller's action with recording suspended, so protection
        /// changes made by the action itself take effect immediately as usual.</summary>
        internal static void InvokeFlushed(Action<ulong, ulong> action, ulong address, ulong size)
        {
            DeferredReprotect deferred = t_deferred;

            if (deferred == null || deferred.Depth == 0)
            {
                action(address, size);

                return;
            }

            FlushDeferredReprotect();

            int depth = deferred.Depth;
            deferred.Depth = 0;

            try
            {
                action(address, size);
            }
            finally
            {
                deferred.Depth = depth;
            }
        }

        internal static void EndDeferredReprotect(bool begun)
        {
            if (begun)
            {
                FlushDeferredReprotect();
                t_deferred.Depth--;
            }
        }

        private void ApplyCurrentProtection(ulong address, ulong size, bool guest, int recorded)
        {
            lock (TrackingLock)
            {
                NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;

                regions.Lock.EnterReadLock();
                RangeItem<VirtualRegion>[] overlaps = regions.FindOverlapsAsArray(address, size);
                regions.Lock.ExitReadLock();

                int k = 0;
                int calls = 0;

                while (k < overlaps.Length)
                {
                    VirtualRegion first = overlaps[k].Value;
                    MemoryPermission permission = first.LastPermission;
                    ulong runStart = first.Address;
                    ulong runEnd = first.EndAddress;
                    k++;

                    while (k < overlaps.Length && overlaps[k].Value.Address == runEnd && overlaps[k].Value.LastPermission == permission)
                    {
                        runEnd = overlaps[k].Value.EndAddress;
                        k++;
                    }

                    if (permission != MemoryPermission.Invalid)
                    {
                        if (Ryujinx.Common.BotwPc.GpuCounters.Detailed)
                        {
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            _memoryManager.TrackingReprotect(runStart, runEnd - runStart, permission, guest);
                            Ryujinx.Common.BotwPc.GpuCounters.AddElapsed(Ryujinx.Common.BotwPc.GpuCounters.Id.Reprotects, Ryujinx.Common.BotwPc.GpuCounters.Id.ReprotectTicks, t0);
                        }
                        else
                        {
                            _memoryManager.TrackingReprotect(runStart, runEnd - runStart, permission, guest);
                        }

                        calls++;
                    }
                }

                if (Ryujinx.Common.BotwPc.GpuCounters.Detailed)
                {
                    Ryujinx.Common.BotwPc.GpuCounters.Add(Ryujinx.Common.BotwPc.GpuCounters.Id.ReprotectsDeferred, recorded);
                    Ryujinx.Common.BotwPc.GpuCounters.Add(Ryujinx.Common.BotwPc.GpuCounters.Id.ReprotectsCoalesced, calls);
                }
            }
        }

        /// <summary>
        /// Create a new tracking structure for the given "physical" memory block,
        /// with a given "virtual" memory manager that will provide mappings and virtual memory protection.
        /// </summary>
        /// <remarks>
        /// If <paramref name="singleByteGuestTracking" /> is true, the memory manager must also support protection on partially
        /// unmapped regions without throwing exceptions or dropping protection on the mapped portion.
        /// </remarks>
        /// <param name="memoryManager">Virtual memory manager</param>
        /// <param name="pageSize">Page size of the virtual memory space</param>
        /// <param name="invalidAccessHandler">Method to call for invalid memory accesses</param>
        /// <param name="singleByteGuestTracking">True if the guest only signals writes for the first byte</param>
        public MemoryTracking(
            IVirtualMemoryManager memoryManager,
            int pageSize,
            InvalidAccessHandler invalidAccessHandler = null,
            bool singleByteGuestTracking = false)
        {
            _memoryManager = memoryManager;
            _pageSize = pageSize;
            _invalidAccessHandler = invalidAccessHandler;
            _singleByteGuestTracking = singleByteGuestTracking;

            _virtualRegions = [];
            _guestVirtualRegions = [];
        }

        private (ulong address, ulong size) PageAlign(ulong address, ulong size)
        {
            ulong pageMask = (ulong)_pageSize - 1;
            ulong rA = address & ~pageMask;
            ulong rS = ((address + size + pageMask) & ~pageMask) - rA;
            return (rA, rS);
        }

        /// <summary>
        /// Indicate that a virtual region has been mapped, and which physical region it has been mapped to.
        /// Should be called after the mapping is complete.
        /// </summary>
        /// <param name="va">Virtual memory address</param>
        /// <param name="size">Size to be mapped</param>
        public void Map(ulong va, ulong size)
        {
            // A mapping may mean we need to re-evaluate each VirtualRegion's affected area.
            // Find all handles that overlap with the range, we need to recalculate their physical regions

            lock (TrackingLock)
            {
                for (int type = 0; type < 2; type++)
                {
                    NonOverlappingRangeList<VirtualRegion> regions = type == 0 ? _virtualRegions : _guestVirtualRegions;
                    regions.Lock.EnterReadLock();
                    Span<RangeItem<VirtualRegion>> overlaps = regions.FindOverlapsAsSpan(va, size);
                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        VirtualRegion region = overlaps[i].Value;

                        // If the region has been fully remapped, signal that it has been mapped again.
                        bool remapped = _memoryManager.IsRangeMapped(region.Address, region.Size);
                        if (remapped)
                        {
                            region.SignalMappingChanged(true);
                        }

                        region.UpdateProtection();
                    }
                    regions.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Indicate that a virtual region has been unmapped.
        /// Should be called before the unmapping is complete.
        /// </summary>
        /// <param name="va">Virtual memory address</param>
        /// <param name="size">Size to be unmapped</param>
        public void Unmap(ulong va, ulong size)
        {
            // An unmapping may mean we need to re-evaluate each VirtualRegion's affected area.
            // Find all handles that overlap with the range, we need to notify them that the region was unmapped.

            lock (TrackingLock)
            {
                for (int type = 0; type < 2; type++)
                {
                    NonOverlappingRangeList<VirtualRegion> regions = type == 0 ? _virtualRegions : _guestVirtualRegions;
                    regions.Lock.EnterReadLock();
                    Span<RangeItem<VirtualRegion>> overlaps = regions.FindOverlapsAsSpan(va, size);
                    
                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        overlaps[i].Value.SignalMappingChanged(false);
                    }
                    regions.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Alter a tracked memory region to properly capture unaligned accesses.
        /// For most memory manager modes, this does nothing.
        /// </summary>
        /// <param name="address">Original region address</param>
        /// <param name="size">Original region size</param>
        /// <returns>A new address and size for tracking unaligned accesses</returns>
        internal (ulong newAddress, ulong newSize) GetUnalignedSafeRegion(ulong address, ulong size)
        {
            if (_singleByteGuestTracking)
            {
                // The guest only signals the first byte of each memory access with the current memory manager.
                // To catch unaligned access properly, we need to also protect the page before the address.

                // Assume that the address and size are already aligned.

                return (address - (ulong)_pageSize, size + (ulong)_pageSize);
            }
            else
            {
                return (address, size);
            }
        }

        /// <summary>
        /// Get a list of virtual regions that a handle covers.
        /// </summary>
        /// <param name="va">Starting virtual memory address of the handle</param>
        /// <param name="size">Size of the handle's memory region</param>
        /// <param name="guest">True if getting handles for guest protection, false otherwise</param>
        /// <returns>A list of virtual regions within the given range</returns>
        internal List<VirtualRegion> GetVirtualRegionsForHandle(ulong va, ulong size, bool guest)
        {
            NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
            regions.Lock.EnterUpgradeableReadLock();
            regions.GetOrAddRegions(out List<VirtualRegion> result, va, size, (va, size) => new VirtualRegion(this, va, size, guest));
            regions.Lock.ExitUpgradeableReadLock();
            
            return result;
        }

        /// <summary>
        /// Remove a virtual region from the range list. This assumes that the lock has been acquired.
        /// </summary>
        /// <param name="region">Region to remove</param>
        internal void RemoveVirtual(VirtualRegion region)
        {
            if (region.Guest)
            {
                _guestVirtualRegions.Lock.EnterWriteLock();
                _guestVirtualRegions.Remove(region);
                _guestVirtualRegions.Lock.ExitWriteLock();
            }
            else
            {
                _virtualRegions.Lock.EnterWriteLock();
                _virtualRegions.Remove(region);
                _virtualRegions.Lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="handles">Handles to inherit state from or reuse. When none are present, provide null</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <param name="id">Handle ID</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags = RegionFlags.None)
        {
            return new MultiRegionHandle(this, address, size, handles, granularity, id, flags);
        }

        /// <summary>
        /// Obtains a smart memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <param name="id">Handle ID</param>
        /// <returns>The memory tracking handle</returns>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            (address, size) = PageAlign(address, size);

            return new SmartMultiRegionHandle(this, address, size, granularity, id);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="id">Handle ID</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags = RegionFlags.None)
        {
            (ulong paAddress, ulong paSize) = PageAlign(address, size);

            lock (TrackingLock)
            {
                bool mapped = _memoryManager.IsRangeMapped(address, size);
                RegionHandle handle = new(this, paAddress, paSize, address, size, id, flags, mapped);

                return handle;
            }
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="bitmap">The bitmap owning the dirty flag for this handle</param>
        /// <param name="bit">The bit of this handle within the dirty flag</param>
        /// <param name="id">Handle ID</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        internal RegionHandle BeginTrackingBitmap(ulong address, ulong size, ConcurrentBitmap bitmap, int bit, int id, RegionFlags flags = RegionFlags.None)
        {
            (ulong paAddress, ulong paSize) = PageAlign(address, size);

            lock (TrackingLock)
            {
                bool mapped = _memoryManager.IsRangeMapped(address, size);
                RegionHandle handle = new(this, paAddress, paSize, address, size, bitmap, bit, id, flags, mapped);

                return handle;
            }
        }

        /// <summary>
        /// Signal that a virtual memory event happened at the given location.
        /// The memory event is assumed to be triggered by guest code.
        /// </summary>
        /// <param name="address">Virtual address accessed</param>
        /// <param name="size">Size of the region affected in bytes</param>
        /// <param name="write">Whether the region was written to or read</param>
        /// <returns>True if the event triggered any tracking regions, false otherwise</returns>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            if (Ryujinx.Common.BotwPc.GpuCounters.Detailed)
            {
                Ryujinx.Common.BotwPc.GpuCounters.Add(Ryujinx.Common.BotwPc.GpuCounters.Id.TrackFaults);
            }

            return VirtualMemoryEvent(address, size, write, precise: false, exemptId: null, guest: true);
        }

        /// <summary>
        /// Signal that a virtual memory event happened at the given location.
        /// This can be flagged as a precise event, which will avoid reprotection and call special handlers if possible.
        /// A precise event has an exact address and size, rather than triggering on page granularity.
        /// </summary>
        /// <param name="address">Virtual address accessed</param>
        /// <param name="size">Size of the region affected in bytes</param>
        /// <param name="write">Whether the region was written to or read</param>
        /// <param name="precise">True if the access is precise, false otherwise</param>
        /// <param name="exemptId">Optional ID that of the handles that should not be signalled</param>
        /// <param name="guest">True if the access is from the guest, false otherwise</param>
        /// <returns>True if the event triggered any tracking regions, false otherwise</returns>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write, bool precise, int? exemptId = null, bool guest = false)
        {
            // Look up the virtual region using the region list.
            // Signal up the chain to relevant handles.

            bool shouldThrow = false;

            lock (TrackingLock)
            {
                NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
                
                // We use the non-span method here because keeping the lock will cause a deadlock.
                regions.Lock.EnterReadLock();
                RangeItem<VirtualRegion>[] overlaps = regions.FindOverlapsAsArray(address, size);
                regions.Lock.ExitReadLock();

                if (overlaps.Length == 0 && !precise)
                {
                    if (_memoryManager.IsRangeMapped(address, size))
                    {
                        // TODO: There is currently the possibility that a page can be protected after its virtual region is removed.
                        // This code handles that case when it happens, but it would be better to find out how this happens.
                        _memoryManager.TrackingReprotect(address & ~(ulong)(_pageSize - 1), (ulong)_pageSize, MemoryPermission.ReadAndWrite, guest);
                        
                        return true; // This memory _should_ be mapped, so we need to try again.
                    }
                    
                    shouldThrow = true;
                }
                else
                {
                    if (guest && _singleByteGuestTracking)
                    {
                        // Increase the access size to trigger handles with misaligned accesses.
                        size += (ulong)_pageSize;
                    }

                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        VirtualRegion region = overlaps[i].Value;

                        if (precise)
                        {
                            region.SignalPrecise(address, size, write, exemptId);
                        }
                        else
                        {
                            region.Signal(address, size, write, exemptId);
                        }
                    }
                }
            }

            if (shouldThrow)
            {
                _invalidAccessHandler?.Invoke(address);

                // We can't continue - it's impossible to remove protection from the page.
                // Even if the access handler wants us to continue, we wouldn't be able to.
                throw new InvalidMemoryRegionException();
            }

            return true;
        }

        /// <summary>
        /// Reprotect a given virtual region. The virtual memory manager will handle this.
        /// </summary>
        /// <param name="region">Region to reprotect</param>
        /// <param name="permission">Memory permission to protect with</param>
        /// <param name="guest">True if the protection is for guest access, false otherwise</param>
        internal void ProtectVirtualRegion(VirtualRegion region, MemoryPermission permission, bool guest)
        {
            DeferredReprotect deferred = t_deferred;

            if (deferred != null && deferred.Depth > 0)
            {
                // Memory V3: applied by FlushDeferredReprotect, before the caller reads the memory.
                deferred.Ranges.Add((this, guest, region.Address, region.Size));

                return;
            }

            if (Ryujinx.Common.BotwPc.GpuCounters.Detailed)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                _memoryManager.TrackingReprotect(region.Address, region.Size, permission, guest);
                Ryujinx.Common.BotwPc.GpuCounters.AddElapsed(Ryujinx.Common.BotwPc.GpuCounters.Id.Reprotects, Ryujinx.Common.BotwPc.GpuCounters.Id.ReprotectTicks, start);
                return;
            }

            _memoryManager.TrackingReprotect(region.Address, region.Size, permission, guest);
        }

        /// <summary>
        /// Returns the number of virtual regions currently being tracked.
        /// Useful for tests and metrics.
        /// </summary>
        /// <returns>The number of virtual regions</returns>
        public int GetRegionCount()
        {
            lock (TrackingLock)
            {
                return _virtualRegions.Count;
            }
        }
    }
}
