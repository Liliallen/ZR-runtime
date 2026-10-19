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

        public static readonly bool CpuV2 = Resolve("BOTWPC_CPU_CORE", _settings.CpuCore);

        public static readonly CpuV2AbiMode CpuV2Abi = ResolveAbi();

        public static readonly bool MemoryV2 = Resolve("BOTWPC_MEMORY_TRACKING", _settings.MemoryTracking);

        public static readonly bool HleV2 = Resolve("BOTWPC_HLE_SCHEDULER", _settings.HleScheduler);

        /// <summary>
        /// BOTWPC_CPU_V2_FEATURES (hex): an explicit ARMeilleure CPU V2 feature mask for experiments; -1 when unset.
        /// </summary>
        public static readonly int CpuV2FeatureOverride =
            int.TryParse(Environment.GetEnvironmentVariable("BOTWPC_CPU_V2_FEATURES"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mask) ? mask : -1;

        private static bool Resolve(string variable, RuntimeCoreMode setting)
        {
            string value = Environment.GetEnvironmentVariable(variable);

            return string.IsNullOrEmpty(value) ? setting == RuntimeCoreMode.V2 : string.Equals(value, "v2", StringComparison.OrdinalIgnoreCase);
        }

        private static CpuV2AbiMode ResolveAbi()
        {
            string value = Environment.GetEnvironmentVariable("BOTWPC_CPU_V2_ABI");

            return !string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out CpuV2AbiMode mode) ? mode : _settings.CpuV2Abi;
        }

        public static string Describe()
        {
            return $"CPU {(CpuV2 ? $"V2 ({CpuV2Abi})" : "legacy")}, memory tracking {(MemoryV2 ? "V2" : "legacy")}, HLE scheduler {(HleV2 ? "V2" : "legacy")}";
        }

        public static void LogSelection()
        {
            Logger.Info?.Print(LogClass.Application, $"BOTW-PC Runtime: {Describe()}");
        }
    }
}
