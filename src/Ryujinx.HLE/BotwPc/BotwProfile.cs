using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// Identifies The Legend of Zelda: Breath of the Wild (Switch) and the exact executable version.
    /// BotW-specific behaviour is only enabled for builds listed in <see cref="Versions"/>; any other
    /// title or build keeps stock emulator behaviour.
    /// </summary>
    public static class BotwProfile
    {
        public const ulong TitleId = 0x01007EF00011E000;

        /// <summary>Addresses are offsets from the main NSO load base, found by analysing that exact build.</summary>
        /// <param name="TimingCode">Main-relative [start, end) of code whose behaviour the timing correction relies on;
        /// ExeFS patches touching it are refused by the mod installer.</param>
        public sealed record Version(string Name, ulong VfrInstanceOffset, (ulong Start, ulong End) TimingCode);

        /// <summary>Main NSO build id (16 significant bytes, hex) to version data.</summary>
        public static readonly IReadOnlyDictionary<string, Version> Versions = new Dictionary<string, Version>
        {
            // v1.6.0: ksys::VFR::sInstance, loaded by both callers of VFR::updateInterval (main+0x1c01180).
            ["8E9978D50BDD20B4C8395A106C27FFDE"] = new("1.6.0", 0x2D1E170, (0x1C01180, 0x1C01398)),
        };

        /// <summary>Build ids (trailing zeros trimmed, as ModLoader matches them) of every module of each supported version.</summary>
        public static readonly IReadOnlyDictionary<string, string> ModuleBuildIds = new Dictionary<string, string>
        {
            ["8E9978D50BDD20B4C8395A106C27FFDE"] = "1.6.0 main",
            ["39E10C07096696C9C4E228496C4BC44D6B9E0041"] = "1.6.0 rtld",
            ["93FF8252D975ADF07230CB86C83301C53F2804DF"] = "1.6.0 subsdk0",
            ["FCF93A41B08C5193A1410D262602DC621617C56F"] = "1.6.0 sdk",
        };

        /// <summary>
        /// Returns the supported version and the main image base, or null with a human-readable reason.
        /// </summary>
        public static Version Detect(ulong programId, IEnumerable<string> buildIds, IEnumerable<ulong> codeAddresses, out ulong mainBase, out string reason)
        {
            mainBase = 0;

            if (programId != TitleId)
            {
                reason = "not Breath of the Wild";
                return null;
            }

            foreach ((string buildId, ulong address) in buildIds.Zip(codeAddresses))
            {
                string key = buildId.Length >= 32 ? buildId[..32].ToUpperInvariant() : buildId.ToUpperInvariant();

                if (Versions.TryGetValue(key, out Version version))
                {
                    mainBase = address;
                    reason = $"BotW {version.Name} (main build {key})";
                    return version;
                }
            }

            reason = $"unsupported BotW build ({string.Join(", ", buildIds.Select(b => b.TrimEnd('0')))}); supported: {string.Join(", ", Versions.Select(v => $"{v.Value.Name} {v.Key}"))}";
            return null;
        }
    }
}
