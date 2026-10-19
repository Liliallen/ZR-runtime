using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Cpu.AppleHv;
using Ryujinx.Cpu.Jit;
using Ryujinx.Cpu.LightningJit;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Memory;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS
{
    class ArmProcessContextFactory : IProcessContextFactory
    {
        private readonly ITickSource _tickSource;
        private readonly GpuContext _gpu;
        private readonly string _titleIdText;
        private readonly string _displayVersion;
        private readonly bool _diskCacheEnabled;
        private readonly string _diskCacheSelector;
        private readonly ulong _codeAddress;
        private readonly ulong _codeSize;

        public IDiskCacheLoadState DiskCacheLoadState { get; private set; }

        public ArmProcessContextFactory(
            ITickSource tickSource,
            GpuContext gpu,
            string titleIdText,
            string displayVersion,
            bool diskCacheEnabled,
            string diskCacheSelector,
            ulong codeAddress,
            ulong codeSize)
        {
            _tickSource = tickSource;
            _gpu = gpu;
            _titleIdText = titleIdText;
            _displayVersion = displayVersion;
            _diskCacheEnabled = diskCacheEnabled;
            _diskCacheSelector = diskCacheSelector;
            _codeAddress = codeAddress;
            _codeSize = codeSize;
        }

        public IProcessContext Create(KernelContext context, ulong pid, ulong addressSpaceSize, InvalidAccessHandler invalidAccessHandler, bool for64Bit)
        {
            IArmProcessContext processContext;

            bool isArm64Host = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

            // BOTW-PC: BotW corrupts guest pointers within minutes under the Apple Hypervisor (MACOS_PORT.md); the JIT is
            // used for it unless BOTWPC_HYPERVISOR=1. Other titles keep the configured behaviour.
            bool useHypervisor = context.Device.Configuration.UseHypervisor;

            if (useHypervisor && OperatingSystem.IsMacOS() && string.Equals(_titleIdText, $"{BotwPc.BotwProfile.TitleId:x16}", StringComparison.OrdinalIgnoreCase) &&
                Environment.GetEnvironmentVariable("BOTWPC_HYPERVISOR") != "1")
            {
                Logger.Warning?.Print(LogClass.Cpu, "BOTW-PC: Apple Hypervisor is unstable with BotW; using the JIT (BOTWPC_HYPERVISOR=1 overrides)");
                useHypervisor = false;
            }

            if (OperatingSystem.IsMacOS() && isArm64Host && for64Bit && useHypervisor)
            {
                HvEngine cpuEngine = new(_tickSource);
                HvMemoryManager memoryManager = new(context.Memory, addressSpaceSize, invalidAccessHandler);
                processContext = new ArmProcessContext<HvMemoryManager>(pid, cpuEngine, _gpu, memoryManager, addressSpaceSize, for64Bit);
                Logger.Info?.Print(LogClass.Cpu, "BOTW-PC CPU engine: Apple Hypervisor (guest code runs natively)");
            }
            else
            {
                MemoryManagerMode mode = context.Device.Configuration.MemoryManagerMode;

                if (!MemoryBlock.SupportsFlags(MemoryAllocationFlags.ViewCompatible))
                {
                    Logger.Warning?.Print(LogClass.Cpu, "Host system doesn't support views, falling back to software page table");

                    mode = MemoryManagerMode.SoftwarePageTable;
                }

                // BOTW-PC: CPU V2 is an ARMeilleure code generator. On ARM64 hosts upstream runs LightningJit; ARMeilleure
                // there is opt-in (BOTWPC_ARM64_ARMEILLEURE=1) because BotW crashes or hangs under it on macOS, in the
                // unmodified upstream code too (MACOS_PORT.md), so CPU V2 falls back to LightningJit by default.
                bool armeilleureOnArm64 = Ryujinx.Common.BotwPc.RuntimeV2.CpuV2 && Environment.GetEnvironmentVariable("BOTWPC_ARM64_ARMEILLEURE") == "1";

                if (isArm64Host && Ryujinx.Common.BotwPc.RuntimeV2.CpuV2 && !armeilleureOnArm64)
                {
                    Logger.Info?.Print(LogClass.Cpu, "BOTW-PC CPU V2 needs ARMeilleure, which is not stable on ARM64 hosts; using LightningJit (BOTWPC_ARM64_ARMEILLEURE=1 overrides)");
                }

                ICpuEngine cpuEngine = isArm64Host && (mode == MemoryManagerMode.HostMapped || mode == MemoryManagerMode.HostMappedUnsafe) && !context.Device.Configuration.EnableGdbStub
                    && !armeilleureOnArm64
                    ? new LightningJitEngine(_tickSource)
                    : new JitEngine(_tickSource);

                Logger.Info?.Print(LogClass.Cpu, $"BOTW-PC CPU engine: {(cpuEngine is LightningJitEngine ? "LightningJit" : "ARMeilleure")}");

                AddressSpace addressSpace = null;

                // We want to use host tracked mode if the host page size is > 4KB.
                if ((mode == MemoryManagerMode.HostMapped || mode == MemoryManagerMode.HostMappedUnsafe) && MemoryBlock.GetPageSize() <= 0x1000)
                {
                    if (!AddressSpace.TryCreate(context.Memory, addressSpaceSize, out addressSpace))
                    {
                        Logger.Warning?.Print(LogClass.Cpu, "Address space creation failed, falling back to software page table");

                        mode = MemoryManagerMode.SoftwarePageTable;
                    }
                }

                switch (mode)
                {
                    case MemoryManagerMode.SoftwarePageTable:
                        MemoryManager memoryManager = new(context.Memory, addressSpaceSize, invalidAccessHandler);
                        processContext = new ArmProcessContext<MemoryManager>(pid, cpuEngine, _gpu, memoryManager, addressSpaceSize, for64Bit);
                        break;

                    case MemoryManagerMode.HostMapped:
                    case MemoryManagerMode.HostMappedUnsafe:
                        if (addressSpace == null)
                        {
                            MemoryManagerHostTracked memoryManagerHostTracked = new(context.Memory, addressSpaceSize, mode == MemoryManagerMode.HostMappedUnsafe, invalidAccessHandler);
                            processContext = new ArmProcessContext<MemoryManagerHostTracked>(pid, cpuEngine, _gpu, memoryManagerHostTracked, addressSpaceSize, for64Bit);
                        }
                        else
                        {
                            if (addressSpaceSize != addressSpace.AddressSpaceSize)
                            {
                                Logger.Warning?.Print(LogClass.Emulation, $"Allocated address space (0x{addressSpace.AddressSpaceSize:X}) is smaller than guest application requirements (0x{addressSpaceSize:X})");
                            }

                            MemoryManagerHostMapped memoryManagerHostMapped = new(addressSpace, mode == MemoryManagerMode.HostMappedUnsafe, invalidAccessHandler);
                            processContext = new ArmProcessContext<MemoryManagerHostMapped>(pid, cpuEngine, _gpu, memoryManagerHostMapped, addressSpace.AddressSpaceSize, for64Bit);
                        }

                        break;

                    default:
                        throw new InvalidOperationException($"{nameof(mode)} contains an invalid value: {mode}");
                }
            }

            DiskCacheLoadState = processContext.Initialize(_titleIdText, _displayVersion, _diskCacheEnabled, _codeAddress, _codeSize, _diskCacheSelector ?? "default");

            return processContext;
        }
    }
}
