using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Buffer manager.
    /// </summary>
    class BufferManager
    {
        private readonly GpuContext _context;
        private readonly GpuChannel _channel;

        private int _unalignedStorageBuffers;
        public bool HasUnalignedStorageBuffers => _unalignedStorageBuffers > 0;

        public bool HasTransformFeedbackOutputs { get; set; }

        private IndexBuffer _indexBuffer;
        private readonly VertexBuffer[] _vertexBuffers;
        private readonly BufferBounds[] _transformFeedbackBuffers;
        private readonly List<BufferTextureBinding> _bufferTextures;
        private readonly List<BufferTextureArrayBinding<ITextureArray>> _bufferTextureArrays;
        private readonly List<BufferTextureArrayBinding<IImageArray>> _bufferImageArrays;
        private readonly BufferAssignment[] _ranges;

        /// <summary>
        /// Holds shader stage buffer state and binding information.
        /// </summary>
        private class BuffersPerStage
        {
            /// <summary>
            /// Shader buffer binding information.
            /// </summary>
            public BufferDescriptor[] Bindings { get; private set; }

            /// <summary>
            /// Buffer regions.
            /// </summary>
            public BufferBounds[] Buffers { get; }

            /// <summary>
            /// Flag indicating if this binding is unaligned.
            /// </summary>
            public bool[] Unaligned { get; }

            /// <summary>
            /// Total amount of buffers used on the shader.
            /// </summary>
            public int Count { get; private set; }

            /// <summary>
            /// Creates a new instance of the shader stage buffer information.
            /// </summary>
            /// <param name="count">Maximum amount of buffers that the shader stage can use</param>
            public BuffersPerStage(int count)
            {
                Bindings = new BufferDescriptor[count];
                Buffers = new BufferBounds[count];
                Unaligned = new bool[count];

                Buffers.AsSpan().Fill(new BufferBounds(new MultiRange(MemoryManager.PteUnmapped, 0UL)));
            }

            /// <summary>
            /// Sets the region of a buffer at a given slot.
            /// </summary>
            /// <param name="index">Buffer slot</param>
            /// <param name="range">Physical memory regions where the buffer is mapped</param>
            /// <param name="flags">Buffer usage flags</param>
            public void SetBounds(int index, MultiRange range, BufferUsageFlags flags = BufferUsageFlags.None)
            {
                Buffers[index] = new BufferBounds(range, flags);
            }

            /// <summary>
            /// Sets shader buffer binding information.
            /// </summary>
            /// <param name="descriptors">Buffer binding information</param>
            public void SetBindings(BufferDescriptor[] descriptors)
            {
                if (descriptors == null)
                {
                    Count = 0;
                    return;
                }

                if ((Count = descriptors.Length) != 0)
                {
                    Bindings = descriptors;
                }
            }
        }

        private readonly BuffersPerStage _cpStorageBuffers;
        private readonly BuffersPerStage _cpUniformBuffers;
        private readonly BuffersPerStage[] _gpStorageBuffers;
        private readonly BuffersPerStage[] _gpUniformBuffers;

        private bool _gpStorageBuffersDirty;
        private bool _gpUniformBuffersDirty;

        private bool _indexBufferDirty;
        private bool _vertexBuffersDirty;
        private uint _vertexBuffersEnableMask;
        private bool _transformFeedbackBuffersDirty;

        private bool _rebind;

        // BOTW-PC: a vertex or uniform buffer set to exactly the binding it already has (same physical range, stride,
        // divisor) does not mark the bindings dirty, like storage buffers upstream. A dirty flag makes the next draw look
        // up every buffer of that kind again and re-send all of them to the backend. Host buffer changes still rebind
        // everything through Rebind() (NotifyBuffersModified, compute work, ForceAllDirty). The index buffer keeps the
        // unconditional flag: VtgAsComputeState overrides it on the backend and relies on the next set to restore it.
        // BOTWPC_SKIP_REDUNDANT_BINDS=0 restores the old behaviour.
        private static readonly bool _skipRedundantBinds = Environment.GetEnvironmentVariable("BOTWPC_SKIP_REDUNDANT_BINDS") != "0";

        // BOTW-PC: binding memo. BotW sets most vertex/uniform buffer bindings to the GPU range they already have (72 % of
        // the uniform binds), and indexed draws with 8/16-bit indices derive the vertex and index buffer sizes from the
        // base vertex and index count, which change on almost every draw. Each set translated the GPU VA, made sure a
        // buffer exists for the range (lookup, often a locked range-list search) and compared ranges. A memo per slot
        // remembers the last (GPU VA, size) whose range is bound, with the page-table and buffer-cache sequences of
        // that moment; while both are unchanged the translation is identical and the buffer still exists, so a set of
        // the same range (or, for vertex and index buffers, of a range contained in the bound one: binding more than a
        // draw reads is what upstream already does for most draws) changes nothing and is skipped. The memo is always
        // recorded, also while the switch is off. BOTWPC_BIND_MEMO=0 disables the skip.
        private static readonly Ryujinx.Common.BotwPc.AbPhase.Switch _bindMemo = new("BOTWPC_BIND_MEMO");

        private struct BindingMemo
        {
            public ulong GpuVa;
            public ulong Size;
            public int MappingSequence;
            public int BufferSequence;
            public bool Valid;
        }

        private readonly BindingMemo[] _uniformMemo = new BindingMemo[Constants.ShaderStages * Constants.TotalGpUniformBuffers];
        private readonly BindingMemo[] _vertexMemo = new BindingMemo[Constants.TotalVertexBuffers];
        private BindingMemo _indexMemo;
        private MemoryManager _memoMemoryManager;

        /// <summary>
        /// The channel memory manager; every memo is dropped if it differs from the one they were recorded with.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MemoryManager MemoMemoryManager()
        {
            MemoryManager memoryManager = _channel.MemoryManager;

            if (!ReferenceEquals(memoryManager, _memoMemoryManager))
            {
                Array.Clear(_uniformMemo);
                Array.Clear(_vertexMemo);
                _indexMemo = default;
                _memoMemoryManager = memoryManager;
            }

            return memoryManager;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool MemoMatches(in BindingMemo memo, ulong gpuVa, ulong size, bool contained, MemoryManager memoryManager)
        {
            return _bindMemo.IsOn &&
                memo.Valid &&
                memo.GpuVa == gpuVa &&
                (contained ? size <= memo.Size : size == memo.Size) &&
                memo.MappingSequence == memoryManager.MappingSequence &&
                memo.BufferSequence == memoryManager.Physical.BufferCache.ModificationSequence;
        }

        /// <param name="mappingSequence">Page-table sequence read before the translation</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static BindingMemo NewMemo(ulong gpuVa, ulong size, int mappingSequence, MemoryManager memoryManager)
        {
            return new BindingMemo
            {
                GpuVa = gpuVa,
                Size = size,
                MappingSequence = mappingSequence,
                BufferSequence = memoryManager.Physical.BufferCache.ModificationSequence,
                Valid = true,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool BindingChanged(bool changed)
        {
            if (Ryujinx.Common.BotwPc.GpuCounters.Detailed)
            {
                Ryujinx.Common.BotwPc.GpuCounters.Add(changed ? Ryujinx.Common.BotwPc.GpuCounters.Id.BindSets : Ryujinx.Common.BotwPc.GpuCounters.Id.BindSkips);
            }

            return changed || !_skipRedundantBinds;
        }

        /// <summary>
        /// Creates a new instance of the buffer manager.
        /// </summary>
        /// <param name="context">GPU context that the buffer manager belongs to</param>
        /// <param name="channel">GPU channel that the buffer manager belongs to</param>
        public BufferManager(GpuContext context, GpuChannel channel)
        {
            _context = context;
            _channel = channel;

            _indexBuffer.Range = new MultiRange(MemoryManager.PteUnmapped, 0UL);
            _vertexBuffers = new VertexBuffer[Constants.TotalVertexBuffers];

            _transformFeedbackBuffers = new BufferBounds[Constants.TotalTransformFeedbackBuffers];

            _cpStorageBuffers = new BuffersPerStage(Constants.TotalCpStorageBuffers);
            _cpUniformBuffers = new BuffersPerStage(Constants.TotalCpUniformBuffers);

            _gpStorageBuffers = new BuffersPerStage[Constants.ShaderStages];
            _gpUniformBuffers = new BuffersPerStage[Constants.ShaderStages];

            for (int index = 0; index < Constants.ShaderStages; index++)
            {
                _gpStorageBuffers[index] = new BuffersPerStage(Constants.TotalGpStorageBuffers);
                _gpUniformBuffers[index] = new BuffersPerStage(Constants.TotalGpUniformBuffers);
            }

            _bufferTextures = [];
            _bufferTextureArrays = [];
            _bufferImageArrays = [];

            _ranges = new BufferAssignment[Constants.TotalGpUniformBuffers * Constants.ShaderStages];
        }

        /// <summary>
        /// Sets the memory range with the index buffer data, to be used for subsequent draw calls.
        /// </summary>
        /// <param name="gpuVa">Start GPU virtual address of the index buffer</param>
        /// <param name="size">Size, in bytes, of the index buffer</param>
        /// <param name="type">Type of each index buffer element</param>
        public void SetIndexBuffer(ulong gpuVa, ulong size, IndexType type)
        {
            MemoryManager memoryManager = MemoMemoryManager();

            if (_indexBuffer.Type == type && MemoMatches(_indexMemo, gpuVa, size, contained: true, memoryManager))
            {
                return;
            }

            int mappingSequence = memoryManager.MappingSequence;
            MultiRange range = memoryManager.Physical.BufferCache.TranslateAndCreateBuffer(memoryManager, gpuVa, size, BufferStage.IndexBuffer);

            _indexBuffer.Range = range;
            _indexBuffer.Type = type;

            _indexBufferDirty = true;
            _indexMemo = NewMemo(gpuVa, size, mappingSequence, memoryManager);
        }

        /// <summary>
        /// Sets a new index buffer that overrides the one set on the call to <see cref="CommitGraphicsBindings"/>.
        /// </summary>
        /// <param name="buffer">Buffer to be used as index buffer</param>
        /// <param name="type">Type of each index buffer element</param>
        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _context.Renderer.Pipeline.SetIndexBuffer(buffer, type);

            _indexBufferDirty = true;

            // The next guest index buffer set must not be skipped: it restores the binding.
            _indexMemo.Valid = false;
        }

        /// <summary>
        /// Sets the memory range with vertex buffer data, to be used for subsequent draw calls.
        /// </summary>
        /// <param name="index">Index of the vertex buffer (up to 16)</param>
        /// <param name="gpuVa">GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stride">Stride of the buffer, defined as the number of bytes of each vertex</param>
        /// <param name="divisor">Vertex divisor of the buffer, for instanced draws</param>
        public void SetVertexBuffer(int index, ulong gpuVa, ulong size, int stride, int divisor)
        {
            MemoryManager memoryManager = MemoMemoryManager();
            ref VertexBuffer vb = ref _vertexBuffers[index];
            ref BindingMemo memo = ref _vertexMemo[index];

            if (vb.Stride == stride && vb.Divisor == divisor && MemoMatches(memo, gpuVa, size, contained: true, memoryManager))
            {
                BindingChanged(false);
                return;
            }

            int mappingSequence = memoryManager.MappingSequence;
            MultiRange range = memoryManager.Physical.BufferCache.TranslateAndCreateBuffer(memoryManager, gpuVa, size, BufferStage.VertexBuffer);

            if (BindingChanged(vb.Stride != stride || vb.Divisor != divisor || !vb.Range.Equals(range)))
            {
                _vertexBuffersDirty = true;
            }

            vb.Range = range;
            vb.Stride = stride;
            vb.Divisor = divisor;
            memo = NewMemo(gpuVa, size, mappingSequence, memoryManager);

            if (!range.IsUnmapped)
            {
                _vertexBuffersEnableMask |= 1u << index;
            }
            else
            {
                _vertexBuffersEnableMask &= ~(1u << index);
            }
        }

        /// <summary>
        /// Sets a transform feedback buffer on the graphics pipeline.
        /// The output from the vertex transformation stages are written into the feedback buffer.
        /// </summary>
        /// <param name="index">Index of the transform feedback buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the transform feedback buffer</param>
        public void SetTransformFeedbackBuffer(int index, ulong gpuVa, ulong size)
        {
            MultiRange range = _channel.MemoryManager.Physical.BufferCache.TranslateAndCreateMultiBuffers(_channel.MemoryManager, gpuVa, size, BufferStage.TransformFeedback);

            _transformFeedbackBuffers[index] = new BufferBounds(range);
            _transformFeedbackBuffersDirty = true;
        }

        /// <summary>
        /// Records the alignment of a storage buffer.
        /// Unaligned storage buffers disable some optimizations on the shader.
        /// </summary>
        /// <param name="buffers">The binding list to modify</param>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        private void RecordStorageAlignment(BuffersPerStage buffers, int index, ulong gpuVa)
        {
            bool unaligned = (gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1)) != 0;

            if (unaligned || HasUnalignedStorageBuffers)
            {
                // Check if the alignment changed for this binding.

                ref bool currentUnaligned = ref buffers.Unaligned[index];

                if (currentUnaligned != unaligned)
                {
                    currentUnaligned = unaligned;
                    _unalignedStorageBuffers += unaligned ? 1 : -1;
                }
            }
        }

        /// <summary>
        /// Sets a storage buffer on the compute pipeline.
        /// Storage buffers can be read and written to on shaders.
        /// </summary>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public void SetComputeStorageBuffer(int index, ulong gpuVa, ulong size, BufferUsageFlags flags)
        {
            size += gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1);

            RecordStorageAlignment(_cpStorageBuffers, index, gpuVa);

            gpuVa = BitUtils.AlignDown(gpuVa, (ulong)_context.Capabilities.StorageBufferOffsetAlignment);

            MultiRange range = _channel.MemoryManager.Physical.BufferCache.TranslateAndCreateMultiBuffers(_channel.MemoryManager, gpuVa, size, BufferStageUtils.ComputeStorage(flags));

            _cpStorageBuffers.SetBounds(index, range, flags);
        }

        /// <summary>
        /// Sets a storage buffer on the graphics pipeline.
        /// Storage buffers can be read and written to on shaders.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the storage buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <param name="flags">Buffer usage flags</param>
        public void SetGraphicsStorageBuffer(int stage, int index, ulong gpuVa, ulong size, BufferUsageFlags flags)
        {
            size += gpuVa & ((ulong)_context.Capabilities.StorageBufferOffsetAlignment - 1);

            BuffersPerStage buffers = _gpStorageBuffers[stage];

            RecordStorageAlignment(buffers, index, gpuVa);

            gpuVa = BitUtils.AlignDown(gpuVa, (ulong)_context.Capabilities.StorageBufferOffsetAlignment);

            MultiRange range = _channel.MemoryManager.Physical.BufferCache.TranslateAndCreateMultiBuffers(_channel.MemoryManager, gpuVa, size, BufferStageUtils.GraphicsStorage(stage, flags));

            if (!buffers.Buffers[index].Range.Equals(range))
            {
                _gpStorageBuffersDirty = true;
            }

            buffers.SetBounds(index, range, flags);
        }

        /// <summary>
        /// Sets a uniform buffer on the compute pipeline.
        /// Uniform buffers are read-only from shaders, and have a small capacity.
        /// </summary>
        /// <param name="index">Index of the uniform buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        public void SetComputeUniformBuffer(int index, ulong gpuVa, ulong size)
        {
            MultiRange range = _channel.MemoryManager.Physical.BufferCache.TranslateAndCreateBuffer(_channel.MemoryManager, gpuVa, size, BufferStage.Compute);

            _cpUniformBuffers.SetBounds(index, range);
        }

        /// <summary>
        /// Sets a uniform buffer on the graphics pipeline.
        /// Uniform buffers are read-only from shaders, and have a small capacity.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the storage buffer</param>
        /// <summary>BOTW-PC: physical range of a bound graphics uniform buffer.</summary>
        public MultiRange GetGraphicsUniformBufferRange(int stage, int index) => _gpUniformBuffers[stage].Buffers[index].Range;

        public void SetGraphicsUniformBuffer(int stage, int index, ulong gpuVa, ulong size)
        {
            MemoryManager memoryManager = MemoMemoryManager();
            ref BindingMemo memo = ref _uniformMemo[stage * Constants.TotalGpUniformBuffers + index];

            if (MemoMatches(memo, gpuVa, size, contained: false, memoryManager))
            {
                BindingChanged(false);
                return;
            }

            int mappingSequence = memoryManager.MappingSequence;
            MultiRange range = memoryManager.Physical.BufferCache.TranslateAndCreateBuffer(memoryManager, gpuVa, size, BufferStageUtils.FromShaderStage(stage));

            BuffersPerStage buffers = _gpUniformBuffers[stage];

            if (BindingChanged(!buffers.Buffers[index].Range.Equals(range)))
            {
                _gpUniformBuffersDirty = true;
            }

            buffers.SetBounds(index, range);
            memo = NewMemo(gpuVa, size, mappingSequence, memoryManager);
        }

        /// <summary>
        /// Sets the number of vertices per instance on a instanced draw. Used for transform feedback emulation.
        /// </summary>
        /// <param name="vertexCount">Vertex count per instance</param>
        public void SetInstancedDrawVertexCount(int vertexCount)
        {
            if (!_context.Capabilities.SupportsTransformFeedback && HasTransformFeedbackOutputs)
            {
                _context.SupportBufferUpdater.SetTfeVertexCount(vertexCount);
                _context.SupportBufferUpdater.Commit();
            }
        }

        /// <summary>
        /// Forces transform feedback and storage buffers to be updated on the next draw.
        /// </summary>
        public void ForceTransformFeedbackAndStorageBuffersDirty()
        {
            _transformFeedbackBuffersDirty = true;
            _gpStorageBuffersDirty = true;
        }

        /// <summary>
        /// Sets the binding points for the storage buffers bound on the compute pipeline.
        /// </summary>
        /// <param name="bindings">Bindings for the active shader</param>
        public void SetComputeBufferBindings(CachedShaderBindings bindings)
        {
            _cpStorageBuffers.SetBindings(bindings.StorageBufferBindings[0]);
            _cpUniformBuffers.SetBindings(bindings.ConstantBufferBindings[0]);
        }

        /// <summary>
        /// Sets the binding points for the storage buffers bound on the graphics pipeline.
        /// </summary>
        /// <param name="bindings">Bindings for the active shader</param>
        public void SetGraphicsBufferBindings(CachedShaderBindings bindings)
        {
            for (int i = 0; i < Constants.ShaderStages; i++)
            {
                _gpStorageBuffers[i].SetBindings(bindings.StorageBufferBindings[i]);
                _gpUniformBuffers[i].SetBindings(bindings.ConstantBufferBindings[i]);
            }

            _gpStorageBuffersDirty = true;
            _gpUniformBuffersDirty = true;
        }

        /// <summary>
        /// Gets a bit mask indicating which compute uniform buffers are currently bound.
        /// </summary>
        /// <returns>Mask where each bit set indicates a bound constant buffer</returns>
        public uint GetComputeUniformBufferUseMask()
        {
            uint mask = 0;

            for (int i = 0; i < _cpUniformBuffers.Buffers.Length; i++)
            {
                if (!_cpUniformBuffers.Buffers[i].IsUnmapped)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }

        /// <summary>
        /// Gets a bit mask indicating which graphics uniform buffers are currently bound.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <returns>Mask where each bit set indicates a bound constant buffer</returns>
        public uint GetGraphicsUniformBufferUseMask(int stage)
        {
            uint mask = 0;

            for (int i = 0; i < _gpUniformBuffers[stage].Buffers.Length; i++)
            {
                if (!_gpUniformBuffers[stage].Buffers[i].IsUnmapped)
                {
                    mask |= 1u << i;
                }
            }

            return mask;
        }

        /// <summary>
        /// Gets the address of the compute uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer address, or an undefined value if the buffer is not currently bound</returns>
        public ulong GetComputeUniformBufferAddress(int index)
        {
            return _cpUniformBuffers.Buffers[index].Range.GetSubRange(0).Address;
        }

        /// <summary>
        /// Gets the size of the compute uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer size, or an undefined value if the buffer is not currently bound</returns>
        public int GetComputeUniformBufferSize(int index)
        {
            return (int)_cpUniformBuffers.Buffers[index].Range.GetSubRange(0).Size;
        }

        /// <summary>
        /// Gets the address of the graphics uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer address, or an undefined value if the buffer is not currently bound</returns>
        public ulong GetGraphicsUniformBufferAddress(int stage, int index)
        {
            return _gpUniformBuffers[stage].Buffers[index].Range.GetSubRange(0).Address;
        }

        /// <summary>
        /// Gets the size of the graphics uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="stage">Index of the shader stage</param>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer size, or an undefined value if the buffer is not currently bound</returns>
        public int GetGraphicsUniformBufferSize(int stage, int index)
        {
            return (int)_gpUniformBuffers[stage].Buffers[index].Range.GetSubRange(0).Size;
        }

        /// <summary>
        /// Gets the bounds of the uniform buffer currently bound at the given index.
        /// </summary>
        /// <param name="isCompute">Indicates whenever the uniform is requested by the 3D or compute engine</param>
        /// <param name="stage">Index of the shader stage, if the uniform is for the 3D engine</param>
        /// <param name="index">Index of the uniform buffer binding</param>
        /// <returns>The uniform buffer bounds, or an undefined value if the buffer is not currently bound</returns>
        public ref BufferBounds GetUniformBufferBounds(bool isCompute, int stage, int index)
        {
            if (isCompute)
            {
                return ref _cpUniformBuffers.Buffers[index];
            }
            else
            {
                return ref _gpUniformBuffers[stage].Buffers[index];
            }
        }

        /// <summary>
        /// Ensures that the compute engine bindings are visible to the host GPU.
        /// Note: this actually performs the binding using the host graphics API.
        /// </summary>
        public void CommitComputeBindings()
        {
            BufferCache bufferCache = _channel.MemoryManager.Physical.BufferCache;

            BindBuffers(bufferCache, _cpStorageBuffers, isStorage: true);
            BindBuffers(bufferCache, _cpUniformBuffers, isStorage: false);

            CommitBufferTextureBindings(bufferCache);

            // Force rebind after doing compute work.
            Rebind();

            _context.SupportBufferUpdater.Commit();
        }

        /// <summary>
        /// Commit any queued buffer texture bindings.
        /// </summary>
        /// <param name="bufferCache">Buffer cache</param>
        private void CommitBufferTextureBindings(BufferCache bufferCache)
        {
            if (_bufferTextures.Count > 0)
            {
                foreach (BufferTextureBinding binding in _bufferTextures)
                {
                    bool isStore = binding.BindingInfo.Flags.HasFlag(TextureUsageFlags.ImageStore);
                    BufferRange range = bufferCache.GetBufferRange(binding.Range, BufferStageUtils.TextureBuffer(binding.Stage, binding.BindingInfo.Flags), isStore);
                    binding.Texture.SetStorage(range);

                    // The texture must be rebound to use the new storage if it was updated.

                    if (binding.IsImage)
                    {
                        _context.Renderer.Pipeline.SetImage(binding.Stage, binding.BindingInfo.Binding, binding.Texture);
                    }
                    else
                    {
                        _context.Renderer.Pipeline.SetTextureAndSampler(binding.Stage, binding.BindingInfo.Binding, binding.Texture, null);
                    }
                }

                _bufferTextures.Clear();
            }

            if (_bufferTextureArrays.Count > 0 || _bufferImageArrays.Count > 0)
            {
                ITexture[] textureArray = new ITexture[1];

                foreach (BufferTextureArrayBinding<ITextureArray> binding in _bufferTextureArrays)
                {
                    BufferRange range = bufferCache.GetBufferRange(binding.Range, BufferStage.None);
                    binding.Texture.SetStorage(range);

                    textureArray[0] = binding.Texture;
                    binding.Array.SetTextures(binding.Index, textureArray);
                }

                foreach (BufferTextureArrayBinding<IImageArray> binding in _bufferImageArrays)
                {
                    bool isStore = binding.BindingInfo.Flags.HasFlag(TextureUsageFlags.ImageStore);
                    BufferRange range = bufferCache.GetBufferRange(binding.Range, BufferStage.None, isStore);
                    binding.Texture.SetStorage(range);

                    textureArray[0] = binding.Texture;
                    binding.Array.SetImages(binding.Index, textureArray);
                }

                _bufferTextureArrays.Clear();
                _bufferImageArrays.Clear();
            }
        }

        /// <summary>
        /// Ensures that the graphics engine bindings are visible to the host GPU.
        /// Note: this actually performs the binding using the host graphics API.
        /// </summary>
        /// <param name="indexed">True if the index buffer is in use</param>
        public void CommitGraphicsBindings(bool indexed)
        {
            if (Engine.FrontendStats.Enabled)
            {
                Engine.FrontendStats.E(Engine.FrontendStats.Ev.BufCommits);
            }

            BufferCache bufferCache = _channel.MemoryManager.Physical.BufferCache;

            if (indexed)
            {
                if (_indexBufferDirty || _rebind)
                {
                    _indexBufferDirty = false;

                    if (!_indexBuffer.Range.IsUnmapped)
                    {
                        if (Engine.FrontendStats.Enabled)
                        {
                            Engine.FrontendStats.E(Engine.FrontendStats.Ev.IndexBind);
                        }

                        BufferRange buffer = bufferCache.GetBufferRange(_indexBuffer.Range, BufferStage.IndexBuffer);

                        _context.Renderer.Pipeline.SetIndexBuffer(buffer, _indexBuffer.Type);
                    }
                }
                else if (!_indexBuffer.Range.IsUnmapped)
                {
                    if (Engine.FrontendStats.Enabled)
                    {
                        Engine.FrontendStats.E(Engine.FrontendStats.Ev.IndexSync);
                    }

                    bufferCache.SynchronizeBufferRange(_indexBuffer.Range);
                }
            }
            else if (_rebind)
            {
                _indexBufferDirty = true;
            }

            uint vbEnableMask = _vertexBuffersEnableMask;

            if (_vertexBuffersDirty || _rebind)
            {
                _vertexBuffersDirty = false;

                Span<VertexBufferDescriptor> vertexBuffers = stackalloc VertexBufferDescriptor[Constants.TotalVertexBuffers];

                for (int index = 0; (vbEnableMask >> index) != 0; index++)
                {
                    VertexBuffer vb = _vertexBuffers[index];

                    if (vb.Range.IsUnmapped)
                    {
                        continue;
                    }

                    if (Engine.FrontendStats.Enabled)
                    {
                        Engine.FrontendStats.E(Engine.FrontendStats.Ev.VertexBind);
                    }

                    BufferRange buffer = bufferCache.GetBufferRange(vb.Range, BufferStage.VertexBuffer);

                    vertexBuffers[index] = new VertexBufferDescriptor(buffer, vb.Stride, vb.Divisor);
                }

                _context.Renderer.Pipeline.SetVertexBuffers(vertexBuffers);
            }
            else
            {
                for (int index = 0; (vbEnableMask >> index) != 0; index++)
                {
                    VertexBuffer vb = _vertexBuffers[index];

                    if (vb.Range.IsUnmapped)
                    {
                        continue;
                    }

                    if (Engine.FrontendStats.Enabled)
                    {
                        Engine.FrontendStats.E(Engine.FrontendStats.Ev.VertexSync);
                    }

                    bufferCache.SynchronizeBufferRange(vb.Range);
                }
            }

            if (_transformFeedbackBuffersDirty || _rebind)
            {
                _transformFeedbackBuffersDirty = false;

                if (_context.Capabilities.SupportsTransformFeedback)
                {
                    Span<BufferRange> tfbs = stackalloc BufferRange[Constants.TotalTransformFeedbackBuffers];

                    for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                    {
                        BufferBounds tfb = _transformFeedbackBuffers[index];

                        if (tfb.IsUnmapped)
                        {
                            tfbs[index] = BufferRange.Empty;
                            continue;
                        }

                        tfbs[index] = bufferCache.GetBufferRange(tfb.Range, BufferStage.TransformFeedback, write: true);
                    }

                    _context.Renderer.Pipeline.SetTransformFeedbackBuffers(tfbs);
                }
                else if (HasTransformFeedbackOutputs)
                {
                    Span<BufferAssignment> buffers = stackalloc BufferAssignment[Constants.TotalTransformFeedbackBuffers];

                    int alignment = _context.Capabilities.StorageBufferOffsetAlignment;

                    for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                    {
                        BufferBounds tfb = _transformFeedbackBuffers[index];

                        if (tfb.IsUnmapped)
                        {
                            buffers[index] = new BufferAssignment(index, BufferRange.Empty);
                        }
                        else
                        {
                            MultiRange range = tfb.Range;
                            ulong address0 = range.GetSubRange(0).Address;
                            ulong address = BitUtils.AlignDown(address0, (ulong)alignment);

                            if (range.Count == 1)
                            {
                                range = new MultiRange(address, range.GetSubRange(0).Size + (address0 - address));
                            }
                            else
                            {
                                MemoryRange[] subRanges = new MemoryRange[range.Count];

                                subRanges[0] = new MemoryRange(address, range.GetSubRange(0).Size + (address0 - address));

                                for (int i = 1; i < range.Count; i++)
                                {
                                    subRanges[i] = range.GetSubRange(i);
                                }

                                range = new MultiRange(subRanges);
                            }

                            int tfeOffset = ((int)address0 & (alignment - 1)) / 4;

                            _context.SupportBufferUpdater.SetTfeOffset(index, tfeOffset);

                            buffers[index] = new BufferAssignment(index, bufferCache.GetBufferRange(range, BufferStage.TransformFeedback, write: true));
                        }
                    }

                    _context.Renderer.Pipeline.SetStorageBuffers(buffers);
                }
            }
            else
            {
                for (int index = 0; index < Constants.TotalTransformFeedbackBuffers; index++)
                {
                    BufferBounds tfb = _transformFeedbackBuffers[index];

                    if (tfb.IsUnmapped)
                    {
                        continue;
                    }

                    bufferCache.SynchronizeBufferRange(tfb.Range);
                }
            }

            if (_gpStorageBuffersDirty || _rebind)
            {
                _gpStorageBuffersDirty = false;

                BindBuffers(bufferCache, _gpStorageBuffers, isStorage: true);
            }
            else
            {
                UpdateBuffers(_gpStorageBuffers);
            }

            if (_gpUniformBuffersDirty || _rebind)
            {
                _gpUniformBuffersDirty = false;

                BindBuffers(bufferCache, _gpUniformBuffers, isStorage: false);
            }
            else
            {
                UpdateBuffers(_gpUniformBuffers);
            }

            CommitBufferTextureBindings(bufferCache);

            _rebind = false;

            _context.SupportBufferUpdater.Commit();
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="bufferCache">Buffer cache holding the buffers for the specified ranges</param>
        /// <param name="bindings">Buffer memory ranges to bind</param>
        /// <param name="isStorage">True to bind as storage buffer, false to bind as uniform buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BindBuffers(BufferCache bufferCache, BuffersPerStage[] bindings, bool isStorage)
        {
            int rangesCount = 0;

            Span<BufferAssignment> ranges = _ranges;

            for (ShaderStage stage = ShaderStage.Vertex; stage <= ShaderStage.Fragment; stage++)
            {
                ref BuffersPerStage buffers = ref bindings[(int)stage - 1];
                BufferStage bufferStage = BufferStageUtils.FromShaderStage(stage);

                for (int index = 0; index < buffers.Count; index++)
                {
                    ref BufferDescriptor bindingInfo = ref buffers.Bindings[index];

                    BufferBounds bounds = buffers.Buffers[bindingInfo.Slot];

                    if (!bounds.IsUnmapped)
                    {
                        if (Engine.FrontendStats.Enabled)
                        {
                            Engine.FrontendStats.E(isStorage ? Engine.FrontendStats.Ev.StorageBind : Engine.FrontendStats.Ev.UniformBind);
                        }

                        bool isWrite = (bounds.Flags & BufferUsageFlags.Write) == BufferUsageFlags.Write;
                        BufferRange range = isStorage
                            ? bufferCache.GetBufferRangeAligned(bounds.Range, bufferStage | BufferStageUtils.FromUsage(bounds.Flags), isWrite)
                            : bufferCache.GetBufferRange(ClampUniform(bounds.Range, bindingInfo), bufferStage);

                        ranges[rangesCount++] = new BufferAssignment(bindingInfo.Binding, range);
                    }
                }
            }

            if (rangesCount != 0)
            {
                SetHostBuffers(ranges, rangesCount, isStorage);
            }
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="bufferCache">Buffer cache holding the buffers for the specified ranges</param>
        /// <param name="buffers">Buffer memory ranges to bind</param>
        /// <param name="isStorage">True to bind as storage buffer, false to bind as uniform buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BindBuffers(BufferCache bufferCache, BuffersPerStage buffers, bool isStorage)
        {
            int rangesCount = 0;

            Span<BufferAssignment> ranges = _ranges;

            for (int index = 0; index < buffers.Count; index++)
            {
                ref BufferDescriptor bindingInfo = ref buffers.Bindings[index];

                BufferBounds bounds = buffers.Buffers[bindingInfo.Slot];

                if (!bounds.IsUnmapped)
                {
                    bool isWrite = (bounds.Flags & BufferUsageFlags.Write) == BufferUsageFlags.Write;
                    BufferRange range = isStorage
                        ? bufferCache.GetBufferRangeAligned(bounds.Range, BufferStageUtils.ComputeStorage(bounds.Flags), isWrite)
                        : bufferCache.GetBufferRange(isStorage ? bounds.Range : ClampUniform(bounds.Range, bindingInfo), BufferStage.Compute);

                    ranges[rangesCount++] = new BufferAssignment(bindingInfo.Binding, range);
                }
            }

            if (rangesCount != 0)
            {
                SetHostBuffers(ranges, rangesCount, isStorage);
            }
        }

        /// <summary>
        /// Bind respective buffer bindings on the host API.
        /// </summary>
        /// <param name="ranges">Host buffers to bind, with their offsets and sizes</param>
        /// <param name="count">Number of bindings</param>
        /// <param name="isStorage">Indicates if the buffers are storage or uniform buffers</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetHostBuffers(ReadOnlySpan<BufferAssignment> ranges, int count, bool isStorage)
        {
            if (isStorage)
            {
                _context.Renderer.Pipeline.SetStorageBuffers(ranges[..count]);
            }
            else
            {
                _context.Renderer.Pipeline.SetUniformBuffers(ranges[..count]);
            }
        }

        /// <summary>
        /// Updates data for the already bound buffer bindings.
        /// </summary>
        /// <param name="bindings">Bindings to update</param>
        private void UpdateBuffers(BuffersPerStage[] bindings)
        {
            for (ShaderStage stage = ShaderStage.Vertex; stage <= ShaderStage.Fragment; stage++)
            {
                ref BuffersPerStage buffers = ref bindings[(int)stage - 1];

                for (int index = 0; index < buffers.Count; index++)
                {
                    ref BufferDescriptor binding = ref buffers.Bindings[index];

                    BufferBounds bounds = buffers.Buffers[binding.Slot];

                    if (bounds.IsUnmapped)
                    {
                        continue;
                    }

                    if (Engine.FrontendStats.Enabled)
                    {
                        Engine.FrontendStats.E(ReferenceEquals(bindings, _gpStorageBuffers) ? Engine.FrontendStats.Ev.StorageSync : Engine.FrontendStats.Ev.UniformSync);
                    }

                    _channel.MemoryManager.Physical.BufferCache.SynchronizeBufferRange(
                        ReferenceEquals(bindings, _gpUniformBuffers) ? ClampUniform(bounds.Range, binding) : bounds.Range);
                }
            }
        }

        // Uniform buffer binding range clamped to what the bound shader can read (BufferDescriptor.UsedSize): smaller
        // descriptor ranges, buffer synchronization and uniform mirrors. Experimental, off unless BOTWPC_UB_CLAMP=1 or
        // compared in-run through BOTWPC_AB_SWITCHES.
        private static readonly Ryujinx.Common.BotwPc.AbPhase.Switch _uniformClamp = new("BOTWPC_UB_CLAMP", experimental: true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static MultiRange ClampUniform(MultiRange range, in BufferDescriptor bindingInfo)
        {
            int usedSize = bindingInfo.UsedSize;

            if (usedSize > 0 && range.Count == 1 &&(ulong)usedSize < range.GetSubRange(0).Size && _uniformClamp.IsOn)
            {
                return range.Slice(0, (ulong)usedSize);
            }

            return range;
        }

        /// <summary>
        /// Sets the buffer storage of a buffer texture. This will be bound when the buffer manager commits bindings.
        /// </summary>
        /// <param name="stage">Shader stage accessing the texture</param>
        /// <param name="texture">Buffer texture</param>
        /// <param name="range">Physical ranges of memory where the buffer texture data is located</param>
        /// <param name="bindingInfo">Binding info for the buffer texture</param>
        /// <param name="isImage">Whether the binding is for an image or a sampler</param>
        public void SetBufferTextureStorage(
            ShaderStage stage,
            ITexture texture,
            MultiRange range,
            TextureBindingInfo bindingInfo,
            bool isImage)
        {
            _channel.MemoryManager.Physical.BufferCache.CreateBuffer(range, BufferStageUtils.TextureBuffer(stage, bindingInfo.Flags));

            _bufferTextures.Add(new BufferTextureBinding(stage, texture, range, bindingInfo, isImage));
        }

        /// <summary>
        /// Sets the buffer storage of a buffer texture array element. This will be bound when the buffer manager commits bindings.
        /// </summary>
        /// <param name="stage">Shader stage accessing the texture</param>
        /// <param name="array">Texture array where the element will be inserted</param>
        /// <param name="texture">Buffer texture</param>
        /// <param name="range">Physical ranges of memory where the buffer texture data is located</param>
        /// <param name="bindingInfo">Binding info for the buffer texture</param>
        /// <param name="index">Index of the binding on the array</param>
        public void SetBufferTextureStorage(
            ShaderStage stage,
            ITextureArray array,
            ITexture texture,
            MultiRange range,
            TextureBindingInfo bindingInfo,
            int index)
        {
            _channel.MemoryManager.Physical.BufferCache.CreateBuffer(range, BufferStageUtils.TextureBuffer(stage, bindingInfo.Flags));

            _bufferTextureArrays.Add(new BufferTextureArrayBinding<ITextureArray>(array, texture, range, bindingInfo, index));
        }

        /// <summary>
        /// Sets the buffer storage of a buffer image array element. This will be bound when the buffer manager commits bindings.
        /// </summary>
        /// <param name="stage">Shader stage accessing the texture</param>
        /// <param name="array">Image array where the element will be inserted</param>
        /// <param name="texture">Buffer texture</param>
        /// <param name="range">Physical ranges of memory where the buffer texture data is located</param>
        /// <param name="bindingInfo">Binding info for the buffer texture</param>
        /// <param name="index">Index of the binding on the array</param>
        public void SetBufferTextureStorage(
            ShaderStage stage,
            IImageArray array,
            ITexture texture,
            MultiRange range,
            TextureBindingInfo bindingInfo,
            int index)
        {
            _channel.MemoryManager.Physical.BufferCache.CreateBuffer(range, BufferStageUtils.TextureBuffer(stage, bindingInfo.Flags));

            _bufferImageArrays.Add(new BufferTextureArrayBinding<IImageArray>(array, texture, range, bindingInfo, index));
        }

        /// <summary>
        /// Force all bound textures and images to be rebound the next time CommitBindings is called.
        /// </summary>
        public void Rebind()
        {
            _rebind = true;
        }
    }
}
