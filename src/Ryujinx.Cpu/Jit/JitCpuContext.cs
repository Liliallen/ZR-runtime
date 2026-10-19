using ARMeilleure.Common;
using ARMeilleure.Memory;
using ARMeilleure.Translation;
using Ryujinx.Cpu.Signal;

namespace Ryujinx.Cpu.Jit
{
    class JitCpuContext : ICpuContext
    {
        private readonly ITickSource _tickSource;
        private readonly Translator _translator;
        private readonly AddressTable<ulong> _functionTable;

        static JitCpuContext()
        {
            // BOTW-PC Runtime V2: select the high-quality tier's code generator before anything is translated.
            ARMeilleure.CpuV2.Features = SelectCpuV2Features();
            Ryujinx.Common.BotwPc.RuntimeV2.LogSelection();
        }

        internal static ARMeilleure.CpuV2Features SelectCpuV2Features()
        {
            if (Ryujinx.Common.BotwPc.RuntimeV2.CpuV2FeatureOverride >= 0)
            {
                return (ARMeilleure.CpuV2Features)Ryujinx.Common.BotwPc.RuntimeV2.CpuV2FeatureOverride;
            }

            if (!Ryujinx.Common.BotwPc.RuntimeV2.CpuV2)
            {
                return ARMeilleure.CpuV2Features.None;
            }

            return ARMeilleure.CpuV2Features.Exact | Ryujinx.Common.BotwPc.RuntimeV2.CpuV2Abi switch
            {
                Ryujinx.Common.BotwPc.CpuV2AbiMode.Flags => ARMeilleure.CpuV2Features.AbiFlagLiveness,
                Ryujinx.Common.BotwPc.CpuV2AbiMode.Temporaries => ARMeilleure.CpuV2Features.AbiCallLiveness,
                _ => ARMeilleure.CpuV2Features.None,
            };
        }

        public JitCpuContext(ITickSource tickSource, IMemoryManager memory, bool for64Bit)
        {
            _tickSource = tickSource;
            _functionTable = AddressTable<ulong>.CreateForArm(for64Bit, memory.Type);
            _translator = new Translator(new JitMemoryAllocator(forJit: true), memory, _functionTable);

            if (memory.Type.IsHostMappedOrTracked())
            {
                NativeSignalHandler.InitializeSignalHandler();
            }

            memory.UnmapEvent += UnmapHandler;
        }

        private void UnmapHandler(ulong address, ulong size)
        {
            _translator.InvalidateJitCacheRegion(address, size);
        }

        /// <inheritdoc/>
        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            return new JitExecutionContext(new JitMemoryAllocator(), _tickSource, exceptionCallbacks);
        }

        /// <inheritdoc/>
        public void Execute(IExecutionContext context, ulong address)
        {
            _translator.Execute(((JitExecutionContext)context).Impl, address);
        }

        /// <inheritdoc/>
        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            _translator.InvalidateJitCacheRegion(address, size);
        }

        /// <inheritdoc/>
        public IDiskCacheLoadState LoadDiskCache(string titleIdText, string displayVersion, bool enabled, string cacheSelector)
        {
            return new JitDiskCacheLoadState(_translator.LoadDiskCache(titleIdText, displayVersion, enabled, cacheSelector));
        }

        /// <inheritdoc/>
        public void PrepareCodeRange(ulong address, ulong size)
        {
            _functionTable.SignalCodeRange(address, size);
            _translator.PrepareCodeRange(address, size);
        }

        public void Dispose()
        {
        }
    }
}
