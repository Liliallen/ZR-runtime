using NUnit.Framework;
using Ryujinx.Memory;
using Ryujinx.Memory.Tracking;
using System.Collections.Generic;

namespace Ryujinx.Tests.Memory
{
    /// <summary>
    /// BOTW-PC Memory V3 (coalesced re-protection in multi-region dirty scans). Runs only with
    /// BOTWPC_MEMORY_TRACKING=v3 BOTWPC_MEM_COALESCE=1 (read once per process; the component was rejected in measurement). Checks that every range handed to the caller is
    /// already protected when the caller reads it, that adjacent re-armed pages are protected with one call, and that
    /// writes after the scan are still detected.
    /// </summary>
    public class MemoryV3CoalesceTests
    {
        private const ulong PageCount = 8;
        private const int PageSize = 4096;

        private MockVirtualMemoryManager _memoryManager;
        private MemoryTracking _tracking;
        private MemoryPermission[] _pages;
        private readonly List<(ulong Address, ulong Size, MemoryPermission Permission)> _calls = [];

        [SetUp]
        public void Setup()
        {
            if (!MemoryTracking.CoalesceReprotects)
            {
                Assert.Ignore("needs BOTWPC_MEMORY_TRACKING=v3 and BOTWPC_MEM_COALESCE=1");
            }

            _memoryManager = new MockVirtualMemoryManager(PageCount * PageSize, PageSize);
            _tracking = new MemoryTracking(_memoryManager, PageSize);
            _pages = new MemoryPermission[PageCount];

            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i] = MemoryPermission.ReadAndWrite;
            }

            _memoryManager.OnProtect += (va, size, permission) =>
            {
                _calls.Add((va, size, permission));

                for (ulong p = va / PageSize; p < (va + size) / PageSize; p++)
                {
                    _pages[p] = permission;
                }
            };
        }

        private List<(ulong, ulong)> Query(MultiRegionHandle handle, bool checkProtectedOnRead = true)
        {
            List<(ulong, ulong)> ranges = [];

            handle.QueryModified(0, PageCount * PageSize, (address, size) =>
            {
                if (checkProtectedOnRead)
                {
                    for (ulong p = address / PageSize; p < (address + size) / PageSize; p++)
                    {
                        Assert.That(_pages[p], Is.EqualTo(MemoryPermission.Read), $"page {p} handed to the caller before it was write-protected");
                    }
                }

                ranges.Add((address, size));
            });

            return ranges;
        }

        [Test]
        public void ScanProtectsBeforeReadAndCoalesces()
        {
            MultiRegionHandle handle = _tracking.BeginGranularTracking(0, PageCount * PageSize, null, PageSize, 0);

            // New handles are dirty: the first scan re-arms all eight pages with one protection call per region list
            // (the tracking keeps a host-side and a guest-side region list; without coalescing: one call per page each).
            _calls.Clear();
            List<(ulong, ulong)> ranges = Query(handle);
            Assert.That(ranges, Is.EqualTo(new List<(ulong, ulong)> { (0, PageCount * PageSize) }));
            int lists = _calls.Count;
            Assert.That(lists, Is.InRange(1, 2), string.Join("; ", _calls));
            Assert.That(_calls.TrueForAll(c => c.Address == 0 && c.Size == PageCount * PageSize));

            // Guest writes to pages 1-3 and 6 (each unprotects its page immediately).
            foreach (ulong p in new ulong[] { 1, 2, 3, 6 })
            {
                _tracking.VirtualMemoryEvent(p * PageSize, 8, true);
            }

            _calls.Clear();
            ranges = Query(handle);
            Assert.That(ranges, Is.EqualTo(new List<(ulong, ulong)> { (1 * PageSize, 3 * PageSize), (6 * PageSize, PageSize) }));
            // Pages 1-3 are re-armed with one call (per region list that changed), page 6 with another.
            HashSet<(ulong, ulong)> runs = [];
            _calls.ForEach(c => runs.Add((c.Address, c.Size)));
            Assert.That(runs, Is.EquivalentTo(new[] { (1UL * PageSize, 3UL * PageSize), (6UL * PageSize, (ulong)PageSize) }), string.Join("; ", _calls));
            Assert.That(_calls.Count, Is.LessThanOrEqualTo(2 * lists));

            for (int p = 0; p < _pages.Length; p++)
            {
                Assert.That(_pages[p], Is.EqualTo(MemoryPermission.Read));
            }

            // A write after the scan is seen by the next one.
            _tracking.VirtualMemoryEvent(2 * PageSize, 4, true);
            ranges = Query(handle);
            Assert.That(ranges, Is.EqualTo(new List<(ulong, ulong)> { (2 * PageSize, PageSize) }));

            // Nothing written: nothing reported, nothing re-protected.
            _calls.Clear();
            Assert.That(Query(handle), Is.Empty);
            Assert.That(_calls, Is.Empty);
        }

        [Test]
        public void ProtectionChangedByTheCallerIsNotDeferred()
        {
            MultiRegionHandle handle = _tracking.BeginGranularTracking(0, PageCount * PageSize, null, PageSize, 0);
            RegionHandle other = _tracking.BeginTracking(7 * PageSize, PageSize, 0);
            Query(handle);
            other.Reprotect();

            _tracking.VirtualMemoryEvent(0, 8, true);

            handle.QueryModified(0, PageCount * PageSize, (address, size) =>
            {
                // A protection change made inside the callback applies at once (deferral is suspended here).
                _tracking.VirtualMemoryEvent(7 * PageSize, 8, true);
                Assert.That(_pages[7], Is.EqualTo(MemoryPermission.ReadAndWrite));
                other.Reprotect();
                Assert.That(_pages[7], Is.EqualTo(MemoryPermission.Read));
            });
        }
    }
}
