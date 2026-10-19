namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// BOTW-PC: maps every 4 KiB page of guest memory to the cached buffer that covers it, so finding the buffer of a
    /// range is one table read instead of a binary search over all buffers (under the range list's write lock in
    /// <see cref="BufferCache"/>). Same idea as the page table of the yuzu/Eden buffer cache (re-implemented here from
    /// the algorithm; their code is GPL). Buffers never overlap and start and end on 4 KiB boundaries, so a page
    /// belongs to at most one buffer. Two levels with leaves allocated on first use; addresses above 2^40 are not
    /// covered (lookups there miss and fall back to the search).
    /// </summary>
    /// <remarks>Used only on the GPU thread, which is also the only thread that adds or removes buffers.</remarks>
    sealed class BufferPageTable
    {
        private const int PageBits = 12;
        private const int LeafBits = 14;
        private const int AddressBits = 40;
        private const ulong LeafMask = (1UL << LeafBits) - 1;

        private readonly Buffer[][] _leaves = new Buffer[1 << (AddressBits - PageBits - LeafBits)][];

        /// <summary>
        /// Gets the buffer covering the page of an address, or null.
        /// </summary>
        public Buffer Get(ulong address)
        {
            ulong page = address >> PageBits;
            ulong leafIndex = page >> LeafBits;

            if (leafIndex >= (ulong)_leaves.Length)
            {
                return null;
            }

            return _leaves[leafIndex]?[page & LeafMask];
        }

        /// <summary>
        /// Records a buffer that was added to the cache.
        /// </summary>
        public void Add(Buffer buffer)
        {
            ulong last = (buffer.EndAddress - 1) >> PageBits;

            for (ulong page = buffer.Address >> PageBits; page <= last; page++)
            {
                ulong leafIndex = page >> LeafBits;

                if (leafIndex >= (ulong)_leaves.Length)
                {
                    break;
                }

                (_leaves[leafIndex] ??= new Buffer[1 << LeafBits])[page & LeafMask] = buffer;
            }
        }

        /// <summary>
        /// Forgets a buffer that was removed from the cache (only the pages that still point to it).
        /// </summary>
        public void Remove(Buffer buffer)
        {
            ulong last = (buffer.EndAddress - 1) >> PageBits;

            for (ulong page = buffer.Address >> PageBits; page <= last; page++)
            {
                ulong leafIndex = page >> LeafBits;

                if (leafIndex >= (ulong)_leaves.Length)
                {
                    break;
                }

                Buffer[] leaf = _leaves[leafIndex];

                if (leaf != null && leaf[page & LeafMask] == buffer)
                {
                    leaf[page & LeafMask] = null;
                }
            }
        }
    }
}
