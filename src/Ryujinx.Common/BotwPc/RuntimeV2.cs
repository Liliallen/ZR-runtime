using Ryujinx.Common.Logging;
using System;
using System.Globalization;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// BOTW-PC Runtime V2 component selection, resolved once per process from the environment (benchmarks) or
    /// &lt;data&gt;/botw/settings.json ("runtime" section). Components are chosen at startup only, never while guest code or
    /// GPU commands are in flight (RUNTIME_V2_ARCHITECTURE.md).
    /// </summary>
    public static class RuntimeV2
    {
        private static readonly RuntimeSettings _settings = BotwPcSettings.Instance.Runtime;

        public static readonly RuntimeCoreMode CpuLevel = Resolve("BOTWPC_CPU_CORE", _settings.CpuCore);

        public static readonly bool CpuV2 = CpuLevel >= RuntimeCoreMode.V2;

        /// <summary>CPU V3: CPU V2 plus the V3 code generator features (CPU_V3_JIT.md).</summary>
        public static readonly bool CpuV3 = CpuLevel >= RuntimeCoreMode.V3;

        public static readonly CpuV2AbiMode CpuV2Abi = ResolveAbi();

        public static readonly RuntimeCoreMode MemoryLevel = Resolve("BOTWPC_MEMORY_TRACKING", _settings.MemoryTracking);

        public static readonly bool MemoryV2 = MemoryLevel >= RuntimeCoreMode.V2;

        public static readonly bool MemoryV3 = MemoryLevel >= RuntimeCoreMode.V3;

        public static readonly RuntimeCoreMode HleLevel = Resolve("BOTWPC_HLE_SCHEDULER", _settings.HleScheduler);

        public static readonly bool HleV2 = HleLevel >= RuntimeCoreMode.V2;

        public static readonly bool HleV3 = HleLevel >= RuntimeCoreMode.V3;

        /// <summary>
        /// GPU V3 level (graphics.gpu_frontend = V3, BOTWPC_GPU_FRONTEND=v3), for components outside the GPU front end
        /// project (Vulkan backend: eager submission, GPU_FRONTEND_V3.md).
        /// </summary>
        public static readonly bool GpuV3 = ResolveGpuV3();

        private static bool ResolveGpuV3()
        {
            string value = Environment.GetEnvironmentVariable("BOTWPC_GPU_FRONTEND");

            return string.IsNullOrEmpty(value)
                ? BotwPcSettings.Instance.Graphics.GpuFrontend == GpuFrontendMode.V3
                : value.Equals("v3", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// BOTWPC_CPU_V2_FEATURES (hex): an explicit ARMeilleure CPU V2 feature mask for experiments; -1 when unset.
        /// </summary>
        public static readonly int CpuV2FeatureOverride =
            int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_CPU_V2_FEATURES"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mask) ? mask : -1;

        private static RuntimeCoreMode Resolve(string variable, RuntimeCoreMode setting)
        {
            string value = Environment.GetEnvironmentVariable(variable);

            if (string.IsNullOrEmpty(value))
            {
                return setting;
            }

            return Enum.TryParse(value, ignoreCase: true, out RuntimeCoreMode mode) ? mode : RuntimeCoreMode.Legacy;
        }

        private static CpuV2AbiMode ResolveAbi()
        {
            string value = Environment.GetEnvironmentVariable("BOTWPC_CPU_V2_ABI");

            return !string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out CpuV2AbiMode mode) ? mode : _settings.CpuV2Abi;
        }

        public static string Describe()
        {
            return $"CPU {(CpuV2 ? $"{CpuLevel} ({CpuV2Abi})" : "legacy")}, memory tracking {MemoryLevel}, HLE scheduler {HleLevel}";
        }

        public static void LogSelection()
        {
            Logger.Info?.Print(LogClass.Application, $"BOTW-PC Runtime: {Describe()}");
        }
    }
}
