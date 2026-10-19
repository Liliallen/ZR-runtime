using Ryujinx.HLE.Loaders.Mods;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Ryujinx.HLE.BotwPc.Mods
{
    /// <summary>
    /// Recognises the layout of an imported BotW mod and checks it against the supported game version.
    /// Only Switch LayeredFS layouts are installable; Wii U, Cemu, BCML and unmerged UKMM packages are refused with a reason.
    /// </summary>
    public static class ModInspector
    {
        public const ulong GameTitleId = BotwProfile.TitleId;

        public sealed class Report
        {
            /// <summary>Title id → directory holding that title's romfs/exefs/cheats.</summary>
            public Dictionary<ulong, string> Parts { get; } = [];
            public List<string> Errors { get; } = [];
            public List<string> Warnings { get; } = [];
            public List<string> RomfsFiles { get; } = [];
            public string Category { get; set; } = "gameplay";
            public bool Installable => Errors.Count == 0 && Parts.Count > 0;
        }

        private static readonly string[] _partDirs = ["romfs", "exefs", "cheats"];

        /// <summary>BotW DLC (add-on content) title ids share the game's prefix: 01007EF00011F001, ...</summary>
        public static bool IsBotwTitle(ulong titleId) => titleId == GameTitleId || (titleId & ~0xFFFUL) == (GameTitleId | 0x1000);

        public static Report Inspect(string root)
        {
            Report report = new();
            string dir = Descend(root);

            if (File.Exists(Path.Combine(dir, "meta.yml")) || File.Exists(Path.Combine(dir, "meta.yaml")))
            {
                report.Errors.Add("This is an unmerged UKMM mod package. Install it in UKMM (Switch mode, BotW 1.6.0), deploy, and import the deploy output folder.");
                return report;
            }

            if (File.Exists(Path.Combine(dir, "rules.txt")))
            {
                report.Errors.Add("This is a Cemu graphic pack (rules.txt). Cemu graphic packs are for the Wii U version and cannot be loaded.");
                return report;
            }

            if (File.Exists(Path.Combine(dir, "info.json")) && (Directory.Exists(Path.Combine(dir, "logs")) || Directory.Exists(Path.Combine(dir, "options"))))
            {
                report.Errors.Add("This is an extracted BCML package. Merge it with BCML or UKMM and import the merged Switch export.");
                return report;
            }

            CollectParts(dir, report);

            if (report.Parts.Count == 0)
            {
                if (Directory.Exists(Path.Combine(dir, "content")) || Directory.Exists(Path.Combine(dir, "aoc")))
                {
                    report.Errors.Add("This is a Wii U (content/aoc) mod. Wii U files are not loadable on the Switch version; use a Switch build of the mod.");
                }
                else
                {
                    report.Errors.Add("No Switch mod layout found (expected romfs/ or exefs/, optionally under <title id>/, contents/<title id>/ or atmosphere/contents/<title id>/).");
                }

                return report;
            }

            foreach ((ulong titleId, string partDir) in report.Parts)
            {
                CheckRomfs(titleId, Path.Combine(partDir, "romfs"), report);
                CheckExefs(Path.Combine(partDir, "exefs"), report);
            }

            report.Category = GuessCategory(report);

            return report;
        }

        // Archives often wrap everything in one folder; descend while there is exactly one directory and no files.
        private static string Descend(string dir)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_partDirs.Any(p => Directory.Exists(Path.Combine(dir, p))) || ParseTitleDir(Path.GetFileName(dir)) != null)
                {
                    return dir;
                }

                string[] dirs = Directory.GetDirectories(dir);

                string only = dirs.Length == 1 ? Path.GetFileName(dirs[0]) : null;

                if (only == null || Directory.GetFiles(dir).Length != 0 || new[] { "contents", "atmosphere", "content", "aoc" }.Any(n => StrEq(only, n)))
                {
                    return dir;
                }

                dir = dirs[0];
            }

            return dir;
        }

        private static void CollectParts(string dir, Report report)
        {
            if (_partDirs.Any(p => Directory.Exists(Path.Combine(dir, p))))
            {
                ulong? own = ParseTitleDir(Path.GetFileName(dir));
                AddPart(report, own ?? GameTitleId, dir);

                return;
            }

            foreach (string candidate in new[] { dir, Path.Combine(dir, "contents"), Path.Combine(dir, "atmosphere", "contents") })
            {
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                foreach (string sub in Directory.GetDirectories(candidate))
                {
                    if (ParseTitleDir(Path.GetFileName(sub)) is ulong titleId && _partDirs.Any(p => Directory.Exists(Path.Combine(sub, p))))
                    {
                        AddPart(report, titleId, sub);
                    }
                }
            }
        }

        private static void AddPart(Report report, ulong titleId, string dir)
        {
            if (!IsBotwTitle(titleId))
            {
                report.Errors.Add($"Contains files for title {titleId:X16}, which is not Breath of the Wild.");
                return;
            }

            report.Parts[titleId] = dir;
        }

        private static ulong? ParseTitleDir(string name)
            => name?.Length == 16 && ulong.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) ? id : null;

        private static void CheckRomfs(ulong titleId, string romfs, Report report)
        {
            if (!Directory.Exists(romfs))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(romfs, "*", SearchOption.AllDirectories))
            {
                report.RomfsFiles.Add($"{titleId:X16}:/{Path.GetRelativePath(romfs, file).Replace('\\', '/')}");
            }

            if (Directory.Exists(Path.Combine(romfs, "content")) || Directory.Exists(Path.Combine(romfs, "aoc")))
            {
                report.Errors.Add("romfs/ contains Wii U content/ or aoc/ folders; this is a Wii U mod repackaged incorrectly.");
            }
        }

        private static void CheckExefs(string exefs, Report report)
        {
            if (!Directory.Exists(exefs))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(exefs))
            {
                string name = Path.GetFileName(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (ext is ".ips" or ".pchtxt")
                {
                    CheckPatch(file, report);
                }
                else if (name is "main" or "rtld" or "sdk" or "main.npdm" || name.StartsWith("subsdk", StringComparison.Ordinal))
                {
                    report.Warnings.Add($"Replaces the game executable '{name}'. BotW-PC only enables its high-FPS timing for the original 1.6.0 executable, so timing correction will be off.");
                }
            }
        }

        private static void CheckPatch(string file, Report report)
        {
            MemPatch patch = new();
            string buildId;

            try
            {
                if (file.EndsWith(".ips", StringComparison.OrdinalIgnoreCase))
                {
                    buildId = Path.GetFileNameWithoutExtension(file).Split('.')[0].TrimEnd('0').ToUpperInvariant();
                    using FileStream fs = File.OpenRead(file);
                    new IpsPatcher(new BinaryReader(fs)).AddPatches(patch);
                }
                else
                {
                    using StreamReader reader = new(file);
                    IPSwitchPatcher patcher = new(reader);
                    buildId = patcher.BuildId?.ToUpperInvariant();

                    if (buildId == null)
                    {
                        report.Errors.Add($"'{Path.GetFileName(file)}' is not a valid IPSwitch patch.");
                        return;
                    }

                    patcher.AddPatches(patch);
                }
            }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException or EndOfStreamException)
            {
                report.Errors.Add($"'{Path.GetFileName(file)}' could not be read: {e.Message}");
                return;
            }

            if (!BotwProfile.ModuleBuildIds.TryGetValue(buildId, out string module))
            {
                report.Errors.Add($"'{Path.GetFileName(file)}' targets build {buildId}, which is not BotW 1.6.0 (wrong game version).");
                return;
            }

            if (module.EndsWith(" main", StringComparison.Ordinal) && BotwProfile.Versions.TryGetValue(buildId, out BotwProfile.Version version))
            {
                // NSO patch offsets include the 0x100-byte NSO header.
                foreach ((uint offset, int length) in patch.Ranges)
                {
                    ulong start = offset - 0x100UL;

                    if (offset >= 0x100 && start < version.TimingCode.End && start + (ulong)length > version.TimingCode.Start)
                    {
                        report.Errors.Add($"'{Path.GetFileName(file)}' patches the frame-timing code (main+0x{start:X}) that BotW-PC's timing correction relies on; refused. Use BotW-PC's own frame-rate modes instead of FPS patches.");
                        return;
                    }
                }

                report.Warnings.Add($"'{Path.GetFileName(file)}' patches the 1.6.0 main executable; if it changes frame pacing, BotW-PC's timing check disables its correction.");
            }
        }

        private static string GuessCategory(Report report)
        {
            if (report.RomfsFiles.Count == 0)
            {
                return "patch";
            }

            static string Top(string f) => f[(f.IndexOf(":/", StringComparison.Ordinal) + 2)..].Split('/')[0];
            HashSet<string> tops = report.RomfsFiles.Select(Top).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tops.All(t => t is "Layout" or "Font" or "Movie"))
            {
                return "ui";
            }

            if (tops.All(t => t is "Sound" or "Voice"))
            {
                return "audio";
            }

            if (tops.All(t => t is "Model" or "Effect" or "Terrain" or "Shader" or "Env"))
            {
                return "visual";
            }

            return "gameplay";
        }

        private static bool StrEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
