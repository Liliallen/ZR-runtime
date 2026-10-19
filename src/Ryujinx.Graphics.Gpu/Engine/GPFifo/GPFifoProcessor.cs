using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using Ryujinx.Graphics.Gpu.Engine.Dma;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Twod;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Represents a GPU General Purpose FIFO command processor.
    /// </summary>
    class GPFifoProcessor : IDisposable
    {
        private const int MacrosCount = 0x80;
        private const int MacroIndexMask = MacrosCount - 1;

        private const int LoadInlineDataMethodOffset = 0x6d;
        private const int UniformBufferUpdateDataMethodOffset = 0x8e4;

        private readonly GpuChannel _channel;

        /// <summary>
        /// Channel memory manager.
        /// </summary>
        public MemoryManager MemoryManager => _channel.MemoryManager;

        /// <summary>
        /// Channel texture manager.
        /// </summary>
        public TextureManager TextureManager => _channel.TextureManager;

        /// <summary>
        /// 3D Engine.
        /// </summary>
        public ThreedClass ThreedClass => _3dClass;

        /// <summary>
        /// Internal GPFIFO state.
        /// </summary>
        private struct DmaState
        {
            public int Method;
            public int SubChannel;
            public int MethodCount;
            public bool NonIncrementing;
            public bool IncrementOnce;
        }

        private DmaState _state;

        private readonly ThreedClass _3dClass;
        private readonly ComputeClass _computeClass;
        private readonly InlineToMemoryClass _i2mClass;
        private readonly TwodClass _2dClass;
        private readonly DmaClass _dmaClass;

        private readonly GPFifoClass _fifoClass;

        /// <summary>
        /// Creates a new instance of the GPU General Purpose FIFO command processor.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">Channel that the GPFIFO processor belongs to</param>
        public GPFifoProcessor(GpuContext context, GpuChannel channel)
        {
            _channel = channel;

            _fifoClass = new GPFifoClass(context, this);
            _3dClass = new ThreedClass(context, channel, _fifoClass);
            _computeClass = new ComputeClass(context, channel, _3dClass);
            _i2mClass = new InlineToMemoryClass(context, channel);
            _2dClass = new TwodClass(channel);
            _dmaClass = new DmaClass(context, channel, _3dClass);
        }

        /// <summary>
        /// Processes a command buffer.
        /// </summary>
        /// <param name="baseGpuVa">Base GPU virtual address of the command buffer</param>
        /// <param name="commandBuffer">Command buffer</param>
        public void Process(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer)
        {
            if (Ryujinx.Common.BotwPc.ScopeProfile.Enabled)
            {
                Ryujinx.Common.BotwPc.GpuCounters.Add(Ryujinx.Common.BotwPc.GpuCounters.Id.Methods, commandBuffer.Length);

                if (_parseProbe)
                {
                    Ryujinx.Common.BotwPc.ScopeProfile.Enter(Ryujinx.Common.BotwPc.ScopeProfile.Scope.ParseProbe);
                    _parseProbeSink += ParseOnly(commandBuffer);
                    Ryujinx.Common.BotwPc.ScopeProfile.Exit(Ryujinx.Common.BotwPc.ScopeProfile.Scope.ParseProbe);
                }

                Ryujinx.Common.BotwPc.ScopeProfile.Enter(Ryujinx.Common.BotwPc.ScopeProfile.Scope.Process);
                ProcessImpl(baseGpuVa, commandBuffer);
                Ryujinx.Common.BotwPc.ScopeProfile.Exit(Ryujinx.Common.BotwPc.ScopeProfile.Scope.Process);
            }
            else
            {
                ProcessImpl(baseGpuVa, commandBuffer);
            }
        }

        /// <summary>
        /// BOTW-PC diagnostics (BOTWPC_PROFILE=1 and BOTWPC_PARSE_PROBE=1): decodes the command words a second time without
        /// executing them, to measure how much of <see cref="Process"/> is pure decoding (what a separate decode stage could
        /// take off this thread) and how much is register writes and method handlers.
        /// </summary>
        private static readonly bool _parseProbe = Environment.GetEnvironmentVariable("BOTWPC_PARSE_PROBE") == "1";
        private static long _parseProbeSink;

        private int ParseOnly(ReadOnlySpan<int> commandBuffer)
        {
            DmaState state = _state;
            int sink = 0;

            for (int index = 0; index < commandBuffer.Length; index++)
            {
                int command = commandBuffer[index];

                if (state.MethodCount != 0)
                {
                    if (state.Method == LoadInlineDataMethodOffset && state.NonIncrementing && state.SubChannel <= 2)
                    {
                        int consumeCount = Math.Min(state.MethodCount, commandBuffer.Length - index);
                        index += consumeCount - 1;
                        state.MethodCount -= consumeCount;
                        continue;
                    }

                    sink += (state.Method * 31) ^ command ^ state.SubChannel;

                    if (!state.NonIncrementing)
                    {
                        state.Method++;
                    }

                    if (state.IncrementOnce)
                    {
                        state.NonIncrementing = true;
                    }

                    state.MethodCount--;
                }
                else
                {
                    CompressedMethod meth = Unsafe.As<int, CompressedMethod>(ref command);

                    if (meth.MethodAddress == UniformBufferUpdateDataMethodOffset && meth.MethodCount < commandBuffer.Length - index &&
                        meth.SecOp == SecOp.NonIncMethod)
                    {
                        index += meth.MethodCount;
                        continue;
                    }

                    switch (meth.SecOp)
                    {
                        case SecOp.IncMethod:
                        case SecOp.NonIncMethod:
                        case SecOp.OneInc:
                            state.Method = meth.MethodAddress;
                            state.SubChannel = meth.MethodSubchannel;
                            state.MethodCount = meth.MethodCount;
                            state.IncrementOnce = meth.SecOp == SecOp.OneInc;
                            state.NonIncrementing = meth.SecOp == SecOp.NonIncMethod;
                            break;
                        case SecOp.ImmdDataMethod:
                            sink += (meth.MethodAddress * 31) ^ meth.ImmdData;
                            break;
                    }
                }
            }

            return sink;
        }

        private void ProcessImpl(ulong baseGpuVa, ReadOnlySpan<int> commandBuffer)
        {
            // BOTW-PC optimized front end: the two most frequent kinds of method go straight to their handler instead of
            // through Send() (a large non-inlined method that re-derives the destination for every word).
            bool fastDispatch = GpuFrontend.FastDispatch;

            for (int index = 0; index < commandBuffer.Length; index++)
            {
                int command = commandBuffer[index];

                ulong gpuVa = baseGpuVa + (ulong)index * 4;

                if (_state.MethodCount != 0)
                {
                    if (FrontendStats.Enabled && _state.Method == LoadInlineDataMethodOffset && _state.NonIncrementing && _state.SubChannel <= 2)
                    {
                        FrontendStats.W(FrontendStats.Word.InlineData, Math.Min(_state.MethodCount, commandBuffer.Length - index));
                    }

                    if (TryFastI2mBufferUpdate(commandBuffer, ref index))
                    {
                        continue;
                    }

                    if (fastDispatch && _state.SubChannel == 0)
                    {
                        SendThreed(gpuVa, _state.Method, command, _state.MethodCount <= 1);
                    }
                    else
                    {
                        Send(gpuVa, _state.Method, command, _state.SubChannel, _state.MethodCount <= 1);
                    }

                    if (!_state.NonIncrementing)
                    {
                        _state.Method++;
                    }

                    if (_state.IncrementOnce)
                    {
                        _state.NonIncrementing = true;
                    }

                    _state.MethodCount--;
                }
                else
                {
                    CompressedMethod meth = Unsafe.As<int, CompressedMethod>(ref command);

                    if (FrontendStats.Enabled)
                    {
                        FrontendStats.W(FrontendStats.Word.Header);
                    }

                    if (TryFastUniformBufferUpdate(meth, commandBuffer, index))
                    {
                        if (FrontendStats.Enabled)
                        {
                            FrontendStats.W(FrontendStats.Word.UboData, meth.MethodCount);
                        }

                        index += meth.MethodCount;
                        continue;
                    }

                    switch (meth.SecOp)
                    {
                        case SecOp.IncMethod:
                        case SecOp.NonIncMethod:
                        case SecOp.OneInc:
                            _state.Method = meth.MethodAddress;
                            _state.SubChannel = meth.MethodSubchannel;
                            _state.MethodCount = meth.MethodCount;
                            _state.IncrementOnce = meth.SecOp == SecOp.OneInc;
                            _state.NonIncrementing = meth.SecOp == SecOp.NonIncMethod;
                            break;
                        case SecOp.ImmdDataMethod:
                            if (fastDispatch && meth.MethodSubchannel == 0)
                            {
                                SendThreed(gpuVa, meth.MethodAddress, meth.ImmdData, true);
                            }
                            else
                            {
                                Send(gpuVa, meth.MethodAddress, meth.ImmdData, meth.MethodSubchannel, true);
                            }

                            break;
                    }
                }
            }

            _3dClass.FlushUboDirty();
        }

        /// <summary>
        /// Tries to perform a fast Inline-to-Memory data update.
        /// If successful, all data will be copied at once, and <see cref="DmaState.MethodCount"/>
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="commandBuffer">Command buffer where the data is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where the data is located, auto-incremented on success</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastI2mBufferUpdate(ReadOnlySpan<int> commandBuffer, ref int offset)
        {
            if (_state.Method == LoadInlineDataMethodOffset && _state.NonIncrementing && _state.SubChannel <= 2)
            {
                int availableCount = commandBuffer.Length - offset;
                int consumeCount = Math.Min(_state.MethodCount, availableCount);

                ReadOnlySpan<int> data = commandBuffer.Slice(offset, consumeCount);

                if (_state.SubChannel == 0)
                {
                    _3dClass.LoadInlineData(data);
                }
                else if (_state.SubChannel == 1)
                {
                    _computeClass.LoadInlineData(data);
                }
                else /* if (_state.SubChannel == 2) */
                {
                    _i2mClass.LoadInlineData(data);
                }

                offset += consumeCount - 1;
                _state.MethodCount -= consumeCount;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to perform a fast constant buffer data update.
        /// If successful, all data will be copied at once, and <see cref="CompressedMethod.MethodCount"/> + 1
        /// command buffer entries will be consumed.
        /// </summary>
        /// <param name="meth">Compressed method to be checked</param>
        /// <param name="commandBuffer">Command buffer where <paramref name="meth"/> is contained</param>
        /// <param name="offset">Offset at <paramref name="commandBuffer"/> where <paramref name="meth"/> is located</param>
        /// <returns>True if the fast copy was successful, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFastUniformBufferUpdate(CompressedMethod meth, ReadOnlySpan<int> commandBuffer, int offset)
        {
            int availableCount = commandBuffer.Length - offset;

            if (meth.MethodAddress == UniformBufferUpdateDataMethodOffset &&
                meth.MethodCount < availableCount &&
                meth.SecOp == SecOp.NonIncMethod)
            {
                _3dClass.ConstantBufferUpdate(commandBuffer.Slice(offset + 1, meth.MethodCount));

                return true;
            }

            return false;
        }

        /// <summary>
        /// Sends a uncompressed method for processing by the graphics pipeline.
        /// </summary>
        /// <param name="gpuVa">GPU virtual address where the command word is located</param>
        /// <param name="meth">Method to be processed</param>
        private void Send(ulong gpuVa, int offset, int argument, int subChannel, bool isLastCall)
        {
            if (FrontendStats.Enabled)
            {
                FrontendStats.W(offset < 0x60 ? FrontendStats.Word.Fifo : offset >= 0xe00 ? ((offset & 1) != 0 ? FrontendStats.Word.MacroArg : FrontendStats.Word.MacroStart) : subChannel switch
                {
                    0 => _3dClass.HasWriteCallback(offset * 4) ? FrontendStats.Word.ThreedCallbackReg : FrontendStats.Word.ThreedReg,
                    1 => FrontendStats.Word.Compute,
                    2 => FrontendStats.Word.I2m,
                    3 => FrontendStats.Word.Twod,
                    _ => FrontendStats.Word.Dma,
                });
            }

            if (offset < 0x60)
            {
                _fifoClass.Write(offset * 4, argument);
            }
            else if (offset < 0xe00)
            {
                offset *= 4;

                switch (subChannel)
                {
                    case 0:
                        if (FrontendTrace.Enabled)
                        {
                            FrontendTrace.Record(offset, argument);
                        }

                        if (ThreedClass.SplitProbe)
                        {
                            _3dClass.ProbeStageA = true;
                            _3dClass.Write(offset, argument);
                            _3dClass.ProbeStageA = false;
                        }
                        else
                        {
                            _3dClass.Write(offset, argument);
                        }

                        break;
                    case 1:
                        _computeClass.Write(offset, argument);
                        break;
                    case 2:
                        _i2mClass.Write(offset, argument);
                        break;
                    case 3:
                        _2dClass.Write(offset, argument);
                        break;
                    case 4:
                        _dmaClass.Write(offset, argument);
                        break;
                }
            }
            else
            {
                IDeviceState state = subChannel switch
                {
                    0 => _3dClass,
                    3 => _2dClass,
                    _ => null,
                };

                if (state != null)
                {
                    int macroIndex = (offset >> 1) & MacroIndexMask;

                    if ((offset & 1) != 0)
                    {
                        _fifoClass.MmePushArgument(macroIndex, gpuVa, argument);
                    }
                    else
                    {
                        _fifoClass.MmeStart(macroIndex, argument);
                    }

                    if (isLastCall)
                    {
                        if (Ryujinx.Common.BotwPc.ScopeProfile.Enabled)
                        {
                            Ryujinx.Common.BotwPc.ScopeProfile.Enter(Ryujinx.Common.BotwPc.ScopeProfile.Scope.Macro);
                        }

                        _fifoClass.CallMme(macroIndex, state);

                        _3dClass.PerformDeferredDraws();

                        if (Ryujinx.Common.BotwPc.ScopeProfile.Enabled)
                        {
                            Ryujinx.Common.BotwPc.ScopeProfile.Exit(Ryujinx.Common.BotwPc.ScopeProfile.Scope.Macro);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// BOTW-PC optimized front end: <see cref="Send"/> for sub-channel 0 (3D), the destination of almost every
        /// method BotW sends. Same handlers in the same order: 3D registers through
        /// <see cref="ThreedClass.WriteFast"/> (equivalent to ThreedClass.Write), macro starts, arguments and calls as
        /// in Send, GPFIFO class methods through Send.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SendThreed(ulong gpuVa, int offset, int argument, bool isLastCall)
        {
            if ((uint)(offset - 0x60) < 0xe00 - 0x60 && !ThreedClass.SplitProbe)
            {
                if (FrontendTrace.Enabled)
                {
                    FrontendTrace.Record(offset * 4, argument);
                }

                if (FrontendStats.Enabled)
                {
                    FrontendStats.W(_3dClass.HasWriteCallback(offset * 4) ? FrontendStats.Word.ThreedCallbackReg : FrontendStats.Word.ThreedReg);
                }

                _3dClass.WriteFast(offset * 4, argument);
            }
            else if (offset >= 0xe00)
            {
                SendMacro(gpuVa, offset, argument, isLastCall);
            }
            else
            {
                Send(gpuVa, offset, argument, 0, isLastCall);
            }
        }

        /// <summary>Macro method on the 3D sub-channel (the macro part of <see cref="Send"/>).</summary>
        private void SendMacro(ulong gpuVa, int offset, int argument, bool isLastCall)
        {
            int macroIndex = (offset >> 1) & MacroIndexMask;

            if (FrontendStats.Enabled)
            {
                FrontendStats.W((offset & 1) != 0 ? FrontendStats.Word.MacroArg : FrontendStats.Word.MacroStart);
            }

            if ((offset & 1) != 0)
            {
                _fifoClass.MmePushArgument(macroIndex, gpuVa, argument);
            }
            else
            {
                _fifoClass.MmeStart(macroIndex, argument);
            }

            if (isLastCall)
            {
                if (Ryujinx.Common.BotwPc.ScopeProfile.Enabled)
                {
                    Ryujinx.Common.BotwPc.ScopeProfile.Enter(Ryujinx.Common.BotwPc.ScopeProfile.Scope.Macro);
                }

                _fifoClass.CallMme(macroIndex, _3dClass);
                _3dClass.PerformDeferredDraws();

                if (Ryujinx.Common.BotwPc.ScopeProfile.Enabled)
                {
                    Ryujinx.Common.BotwPc.ScopeProfile.Exit(Ryujinx.Common.BotwPc.ScopeProfile.Scope.Macro);
                }
            }
        }

        /// <summary>
        /// Writes data directly to the state of the specified class.
        /// </summary>
        /// <param name="classId">ID of the class to write the data into</param>
        /// <param name="offset">State offset in bytes</param>
        /// <param name="value">Value to be written</param>
        public void Write(ClassId classId, int offset, int value)
        {
            switch (classId)
            {
                case ClassId.Threed:
                    _3dClass.Write(offset, value);
                    break;
                case ClassId.Compute:
                    _computeClass.Write(offset, value);
                    break;
                case ClassId.InlineToMemory:
                    _i2mClass.Write(offset, value);
                    break;
                case ClassId.Twod:
                    _2dClass.Write(offset, value);
                    break;
                case ClassId.Dma:
                    _dmaClass.Write(offset, value);
                    break;
                case ClassId.GPFifo:
                    _fifoClass.Write(offset, value);
                    break;
            }
        }

        /// <summary>
        /// Sets the shadow ram control value of all sub-channels.
        /// </summary>
        /// <param name="control">New shadow ram control value</param>
        public void SetShadowRamControl(int control)
        {
            _3dClass.SetShadowRamControl(control);
        }

        /// <summary>
        /// Forces a full host state update by marking all state as modified,
        /// and also requests all GPU resources in use to be rebound.
        /// </summary>
        public void ForceAllDirty()
        {
            _3dClass.ForceStateDirty();
            _channel.BufferManager.Rebind();
            _channel.TextureManager.Rebind();
        }

        /// <summary>
        /// Perform any deferred draws.
        /// </summary>
        public void PerformDeferredDraws()
        {
            _3dClass.PerformDeferredDraws();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _3dClass.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
