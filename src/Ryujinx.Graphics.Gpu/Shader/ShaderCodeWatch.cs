using Ryujinx.Graphics.Gpu.Memory;

namespace Ryujinx.Graphics.Gpu.Shader
{
    /// <summary>
    /// GPU V3: memory tracking of one cached shader stage's code (ShaderCache.IsShaderEqualWatched).
    /// </summary>
    sealed class ShaderCodeWatch
    {
        public readonly GpuRegionHandle Handle;
        public readonly MemoryManager MemoryManager;
        public readonly ulong GpuVa;
        public readonly int Sequence;

        public ShaderCodeWatch(GpuRegionHandle handle, MemoryManager memoryManager, ulong gpuVa, int sequence)
        {
            Handle = handle;
            MemoryManager = memoryManager;
            GpuVa = gpuVa;
            Sequence = sequence;
        }
    }
}
