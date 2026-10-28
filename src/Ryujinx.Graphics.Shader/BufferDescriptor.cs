namespace Ryujinx.Graphics.Shader
{
    public readonly struct BufferDescriptor
    {
        // New fields should be added to the end of the struct to keep disk shader cache compatibility.

        public readonly int Set;
        public readonly int Binding;
        public readonly byte Slot;
        public readonly byte SbCbSlot;
        public readonly ushort SbCbOffset;
        public readonly BufferUsageFlags Flags;

        /// <summary>
        /// Constant buffers: bytes from the start of the binding that the shader can read (end of the highest vec4 read at a
        /// constant offset), or 0 when unknown (dynamic offsets, or descriptors from an older shader cache).
        /// </summary>
        public readonly int UsedSize;

        public BufferDescriptor(int set, int binding, int slot)
        {
            Set = set;
            Binding = binding;
            Slot = (byte)slot;
            SbCbSlot = 0;
            SbCbOffset = 0;
            Flags = BufferUsageFlags.None;
            UsedSize = 0;
        }

        public BufferDescriptor(int set, int binding, int slot, int usedSize) : this(set, binding, slot)
        {
            UsedSize = usedSize;
        }

        public BufferDescriptor(int set, int binding, int slot, int sbCbSlot, int sbCbOffset, BufferUsageFlags flags)
        {
            Set = set;
            Binding = binding;
            Slot = (byte)slot;
            SbCbSlot = (byte)sbCbSlot;
            SbCbOffset = (ushort)sbCbOffset;
            Flags = flags;
            UsedSize = 0;
        }
    }
}
