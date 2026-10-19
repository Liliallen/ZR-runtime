using Ryujinx.Common.Configuration;
using Ryujinx.Common.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryujinx.HLE.BotwPc.Mods
{
    /// <summary>Per-mod record written next to the installed files (ignored by ModLoader).</summary>
    public sealed class ModManifest
    {
        public string Name { get; set; }
        public string Version { get; set; } = "";
        public string GameVersion { get; set; } = "1.6.0";
        public string Category { get; set; } = "gameplay";
        public List<string> Dependencies { get; set; } = [];
        public List<string> Titles { get; set; } = [];
        public List<string> Files { get; set; } = [];
        public string Source { get; set; } = "";
        public DateTime InstalledUtc { get; set; }
        public List<string> Warnings { get; set; } = [];
    }

    /// <summary>A saved mod set: names in priority order (first wins) with their enabled state.</summary>
    public sealed class ModProfile
    {
        public string Name { get; set; }
        public List<ModProfileEntry> Mods { get; set; } = [];
    }

    public sealed class ModProfileEntry
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(ModManifest))]
    [JsonSerializable(typeof(ModProfile))]
    internal partial class ModStoreJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Installs and manages BotW mods in the directories Ryujinx's ModLoader already reads:
    /// &lt;mods&gt;/contents/&lt;title&gt;/&lt;mod&gt;/{romfs,exefs,cheats}. Enabled state and priority live in the per-title
    /// mods.json that ModLoader and the mod manager window use; list order = priority (first entry wins a file conflict).
    /// </summary>
    public sealed class ModStore
    {
        public const string ManifestName = "botwpc-mod.json";
        public static readonly string[] BuiltInProfiles = ["Vanilla", "Performance", "Visual", "Custom"];

        private static readonly ModMetadataJsonSerializerContext _metadataContext = new(JsonHelper.GetDefaultSerializerOptions());

        // BotW resources that several mods commonly edit and that need a semantic merge (UKMM/BCML), not file priority.
        private static readonly string[] _mergeSensitive =
        [
            "System/Resource/ResourceSizeTable.product.srsizetable",
            "Actor/ActorInfo.product.sbyml",
            "Pack/Bootup.pack",
            "Pack/TitleBG.pack",
            "Event/EventInfo.product.sbyml",
            "GameData/gamedata.ssarc",
            "GameData/savedataformat.ssarc",
            "Map/MainField/Static.smubin",
            "Map/CDungeon/Static.smubin",
        ];

        public string ContentsRoot { get; }

        /// <summary>Other contents roots ModLoader reads (sdcard/atmosphere/contents); listed and managed, never installed into.</summary>
        public IReadOnlyList<string> ExtraContentsRoots { get; }
        public string GamesRoot { get; }
        public string WorkRoot { get; }

        public sealed record InstalledMod(string Name, bool Enabled, int Priority, ModManifest Manifest, IReadOnlyDictionary<ulong, string> Parts);

        public sealed record Conflict(string File, IReadOnlyList<string> Mods, bool NeedsMerge)
        {
            public string Winner => Mods[0];
        }

        /// <param name="contentsRoot">&lt;mods&gt;/contents</param>
        /// <param name="gamesRoot">AppDataManager.GamesDirPath (mods.json location)</param>
        /// <param name="workRoot">&lt;data&gt;/botw (staging, backups, profiles)</param>
        public ModStore(string contentsRoot, string gamesRoot, string workRoot, params string[] extraContentsRoots)
        {
            ContentsRoot = contentsRoot;
            ExtraContentsRoots = extraContentsRoots;
            GamesRoot = gamesRoot;
            WorkRoot = workRoot;
        }

        public static ModStore ForCurrentUser()
            => new(Path.Combine(AppDataManager.GetModsPath(), "contents"), AppDataManager.GamesDirPath, Path.Combine(AppDataManager.BaseDirPath, "botw"),
                Path.Combine(AppDataManager.GetSdModsPath(), "contents"));

        // ---- Listing ----

        private IEnumerable<ulong> TitleDirs() => TitleDirs(ContentsRoot);

        private static IEnumerable<ulong> TitleDirs(string root)
        {
            if (!Directory.Exists(root))
            {
                yield break;
            }

            foreach (string dir in Directory.GetDirectories(root))
            {
                if (ulong.TryParse(Path.GetFileName(dir), System.Globalization.NumberStyles.HexNumber, null, out ulong id) && ModInspector.IsBotwTitle(id))
                {
                    yield return id;
                }
            }
        }

        // ModLoader finds the title folder case-insensitively and uses the first match; reuse an existing one.
        private string TitleDir(ulong titleId)
            => (Directory.Exists(ContentsRoot) ? Directory.GetDirectories(ContentsRoot).FirstOrDefault(d => string.Equals(Path.GetFileName(d), $"{titleId:x16}", StringComparison.OrdinalIgnoreCase)) : null)
               ?? Path.Combine(ContentsRoot, $"{titleId:x16}");

        public List<InstalledMod> List()
        {
            Dictionary<string, Dictionary<ulong, string>> parts = new(StringComparer.Ordinal);

            foreach (string root in ExtraContentsRoots.Prepend(ContentsRoot))
            {
                foreach (ulong titleId in TitleDirs(root))
                {
                    // Title folders may differ in case (ModLoader matches them case-insensitively).
                    string titleDir = Directory.GetDirectories(root).First(d => string.Equals(Path.GetFileName(d), $"{titleId:x16}", StringComparison.OrdinalIgnoreCase));

                    foreach (string modDir in Directory.GetDirectories(titleDir))
                    {
                        string name = Path.GetFileName(modDir);

                        if (!parts.TryGetValue(name, out Dictionary<ulong, string> p))
                        {
                            parts[name] = p = [];
                        }

                        p.TryAdd(titleId, modDir);
                    }
                }
            }

            Dictionary<ulong, List<Mod>> metadataByTitle = [];
            List<InstalledMod> result = [];

            foreach ((string name, Dictionary<ulong, string> p) in parts)
            {
                // State lives in the mods.json of the title the mod is listed under: the game's, or the DLC's for a
                // DLC-only mod (Save writes the same order and state into every title's mods.json).
                // shortcut: a DLC-only mod's priority is its index in the DLC list, so it can sort slightly differently
                // relative to game mods than in the saved order; harmless today (no file can conflict across titles).
                ulong title = p.ContainsKey(ModInspector.GameTitleId) ? ModInspector.GameTitleId : p.Keys.First();
                string gamePath = p[title];

                if (!metadataByTitle.TryGetValue(title, out List<Mod> metadata))
                {
                    metadataByTitle[title] = metadata = LoadMetadata(title).Mods;
                }

                int index = metadata.FindIndex(m => PathEq(m.Path, gamePath));
                bool enabled = index < 0 || metadata[index].Enabled;

                result.Add(new InstalledMod(name, enabled, index < 0 ? int.MaxValue : index, ReadManifest(p.Values), p));
            }

            // Unlisted mods (copied in by hand) go last, by name, so the order is always deterministic.
            result.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.Name, b.Name));

            return result.Select((m, i) => m with { Priority = i }).ToList();
        }

        private static ModManifest ReadManifest(IEnumerable<string> dirs)
        {
            foreach (string dir in dirs)
            {
                string path = Path.Combine(dir, ManifestName);

                if (File.Exists(path))
                {
                    try
                    {
                        return JsonSerializer.Deserialize(File.ReadAllText(path), ModStoreJsonContext.Default.ModManifest);
                    }
                    catch (JsonException)
                    {
                        // A damaged manifest only loses the details; the mod itself still lists and loads.
                    }
                }
            }

            return null;
        }

        // ---- mods.json (shared with ModLoader / the mod manager window) ----

        private string MetadataPath(ulong titleId) => Path.Combine(GamesRoot, $"{titleId:x16}", "mods.json");

        private ModMetadata LoadMetadata(ulong titleId)
        {
            string path = MetadataPath(titleId);

            try
            {
                return File.Exists(path) ? JsonHelper.DeserializeFromFile(path, _metadataContext.ModMetadata) : new ModMetadata();
            }
            catch (JsonException)
            {
                return new ModMetadata();
            }
        }

        /// <summary>
        /// Writes enabled state and priority order for every BotW title. ModLoader reads the game's and each DLC's
        /// own mods.json; both get the same order. Entries for mods that no longer exist are dropped.
        /// </summary>
        public void Save(IReadOnlyList<(string Name, bool Enabled)> order)
        {
            List<InstalledMod> installed = List();

            foreach (ulong titleId in installed.SelectMany(m => m.Parts.Keys).Append(ModInspector.GameTitleId).Distinct())
            {
                ModMetadata metadata = new();

                foreach ((string name, bool enabled) in order)
                {
                    InstalledMod mod = installed.Find(m => m.Name == name);

                    if (mod != null && mod.Parts.TryGetValue(titleId, out string dir))
                    {
                        metadata.Mods.Add(new Mod { Name = name, Path = Path.GetFullPath(dir), Enabled = enabled });
                    }
                }

                string path = MetadataPath(titleId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                JsonHelper.SerializeToFile(path, metadata, _metadataContext.ModMetadata);
            }
        }

        public void SetEnabled(string name, bool enabled)
            => Save(List().Select(m => (m.Name, m.Name == name ? enabled : m.Enabled)).ToList());

        /// <summary>Moves a mod by <paramref name="delta"/> places in priority order (negative = higher priority).</summary>
        public void Move(string name, int delta)
        {
            List<(string Name, bool Enabled)> order = List().Select(m => (m.Name, m.Enabled)).ToList();
            int from = order.FindIndex(m => m.Name == name);

            if (from < 0)
            {
                return;
            }

            (string, bool) item = order[from];
            order.RemoveAt(from);
            order.Insert(Math.Clamp(from + delta, 0, order.Count), item);
            Save(order);
        }

        // ---- Install / update / uninstall / rollback ----

        public sealed class InstallResult
        {
            public bool Success { get; init; }
            public string Name { get; init; }
            public IReadOnlyList<string> Errors { get; init; } = [];
            public IReadOnlyList<string> Warnings { get; init; } = [];
        }

        public static string SanitizeName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string clean = new string(name.Select(c => invalid.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().Trim('.');

            return clean.Length == 0 ? "mod" : clean[..Math.Min(clean.Length, 96)];
        }

        /// <summary>
        /// Imports, validates and installs a mod. With <paramref name="replace"/> an installed mod of the same name is
        /// updated (its previous files are kept as a backup for <see cref="Rollback"/>). New mods get the highest priority.
        /// </summary>
        public InstallResult Install(string source, string name = null, bool replace = false, string version = "")
        {
            name = SanitizeName(name ?? Path.GetFileNameWithoutExtension(source.TrimEnd('/', '\\')));
            string staging = Path.Combine(WorkRoot, "staging", Guid.NewGuid().ToString("N"));

            try
            {
                try
                {
                    ModArchive.Import(source, staging);
                }
                catch (ModArchive.ImportException e)
                {
                    return new InstallResult { Name = name, Errors = [e.Message] };
                }

                ModInspector.Report report = ModInspector.Inspect(staging);

                if (!report.Installable)
                {
                    return new InstallResult { Name = name, Errors = report.Errors, Warnings = report.Warnings };
                }

                bool exists = List().Any(m => m.Name == name);

                if (exists && !replace)
                {
                    return new InstallResult { Name = name, Errors = [$"A mod named '{name}' is already installed. Choose update to replace it."], Warnings = report.Warnings };
                }

                ModManifest manifest = new()
                {
                    Name = name,
                    Version = version,
                    Category = report.Category,
                    Titles = report.Parts.Keys.Select(t => $"{t:X16}").ToList(),
                    Files = report.RomfsFiles,
                    Source = Path.GetFileName(source.TrimEnd('/', '\\')),
                    InstalledUtc = DateTime.UtcNow,
                    Warnings = report.Warnings,
                };

                List<(string Name, bool Enabled)> order = List().Select(m => (m.Name, m.Enabled)).ToList();
                bool wasEnabled = order.Find(m => m.Name == name) is { Name: not null } previous ? previous.Enabled : true;

                MoveIntoPlace(name, report, manifest);

                order.RemoveAll(m => m.Name == name);
                order.Insert(0, (name, wasEnabled));
                Save(order);

                return new InstallResult { Success = true, Name = name, Warnings = report.Warnings };
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
            }
        }

        private string BackupRoot(string name) => Path.Combine(WorkRoot, "backup", name);

        private void MoveIntoPlace(string name, ModInspector.Report report, ModManifest manifest)
        {
            string backup = Path.Combine(BackupRoot(name), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            List<(string Target, string Backup)> movedOut = [];
            List<string> movedIn = [];

            try
            {
                // Move every existing part of this mod (all titles) out first, so an update never mixes versions.
                foreach (ulong titleId in TitleDirs().ToList())
                {
                    string target = Path.Combine(TitleDir(titleId), name);

                    if (Directory.Exists(target))
                    {
                        string saved = Path.Combine(backup, $"{titleId:x16}");
                        Directory.CreateDirectory(backup);
                        Directory.Move(target, saved);
                        movedOut.Add((target, saved));
                    }
                }

                foreach ((ulong titleId, string partDir) in report.Parts)
                {
                    string target = Path.Combine(TitleDir(titleId), name);
                    Directory.CreateDirectory(TitleDir(titleId));
                    // shortcut: Directory.Move needs staging and mods on one filesystem (both under the data dir); a custom mods path elsewhere fails cleanly and rolls back.
                    Directory.Move(partDir, target);
                    movedIn.Add(target);
                }

                ulong manifestTitle = report.Parts.ContainsKey(ModInspector.GameTitleId) ? ModInspector.GameTitleId : report.Parts.Keys.First();
                string manifestDir = Path.Combine(TitleDir(manifestTitle), name);
                File.WriteAllText(Path.Combine(manifestDir, ManifestName), JsonSerializer.Serialize(manifest, ModStoreJsonContext.Default.ModManifest));
                PruneBackups(name, keep: 2);
            }
            catch
            {
                // Roll back: remove what was moved in, restore what was moved out.
                foreach (string target in movedIn)
                {
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, true);
                    }
                }

                foreach ((string target, string saved) in movedOut)
                {
                    Directory.Move(saved, target);
                }

                throw;
            }
        }

        /// <summary>Removes a mod; its files are moved to the backup area so <see cref="Rollback"/> can restore them.</summary>
        public bool Uninstall(string name)
        {
            InstalledMod mod = List().Find(m => m.Name == name);

            if (mod == null)
            {
                return false;
            }

            string backup = Path.Combine(BackupRoot(name), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backup);

            foreach ((ulong titleId, string dir) in mod.Parts)
            {
                Directory.Move(dir, Path.Combine(backup, $"{titleId:x16}"));
            }

            Save(List().Select(m => (m.Name, m.Enabled)).ToList());
            PruneBackups(name, keep: 2);

            return true;
        }

        /// <summary>Restores the most recent backup of <paramref name="name"/> (undoes the last update or uninstall).</summary>
        public bool Rollback(string name)
        {
            string root = BackupRoot(name);
            string latest = Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault() : null;

            if (latest == null)
            {
                return false;
            }

            List<(string Name, bool Enabled)> order = List().Select(m => (m.Name, m.Enabled)).ToList();
            InstalledMod current = List().Find(m => m.Name == name);

            if (current != null)
            {
                foreach (string dir in current.Parts.Values)
                {
                    Directory.Delete(dir, true);
                }
            }

            foreach (string titleDir in Directory.GetDirectories(latest))
            {
                ulong titleId = Convert.ToUInt64(Path.GetFileName(titleDir), 16);
                Directory.CreateDirectory(TitleDir(titleId));
                Directory.Move(titleDir, Path.Combine(TitleDir(titleId), name));
            }

            Directory.Delete(latest, true);

            if (!order.Exists(m => m.Name == name))
            {
                order.Insert(0, (name, true));
            }

            Save(order);

            return true;
        }

        /// <summary>Mods that have a backup to restore.</summary>
        public List<string> BackupNames()
        {
            string root = Path.Combine(WorkRoot, "backup");

            return Directory.Exists(root)
                ? Directory.GetDirectories(root).Where(d => Directory.GetDirectories(d).Length > 0).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()
                : [];
        }

        private void PruneBackups(string name, int keep)
        {
            string root = BackupRoot(name);

            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string old in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(keep))
            {
                Directory.Delete(old, true);
            }
        }

        // ---- Conflicts ----

        /// <summary>RomFS files provided by more than one enabled mod, with the winning mod first.</summary>
        public List<Conflict> Conflicts()
        {
            Dictionary<string, List<string>> owners = new(StringComparer.Ordinal);

            foreach (InstalledMod mod in List().Where(m => m.Enabled))
            {
                foreach ((ulong titleId, string dir) in mod.Parts)
                {
                    string romfs = Path.Combine(dir, "romfs");

                    if (!Directory.Exists(romfs))
                    {
                        continue;
                    }

                    foreach (string file in Directory.EnumerateFiles(romfs, "*", SearchOption.AllDirectories))
                    {
                        string key = $"{titleId:X16}:/{Path.GetRelativePath(romfs, file).Replace('\\', '/')}";

                        if (!owners.TryGetValue(key, out List<string> list))
                        {
                            owners[key] = list = [];
                        }

                        list.Add(mod.Name);
                    }
                }
            }

            return owners.Where(o => o.Value.Count > 1)
                .Select(o => new Conflict(o.Key, o.Value, _mergeSensitive.Any(s => o.Key.EndsWith(":/" + s, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(c => c.File, StringComparer.Ordinal)
                .ToList();
        }

        // ---- Profiles ----

        private string ProfilePath(string profile) => Path.Combine(WorkRoot, "profiles", SanitizeName(profile) + ".json");

        public void SaveProfile(string profile)
        {
            ModProfile p = new() { Name = profile, Mods = List().Select(m => new ModProfileEntry { Name = m.Name, Enabled = m.Enabled }).ToList() };
            Directory.CreateDirectory(Path.Combine(WorkRoot, "profiles"));
            File.WriteAllText(ProfilePath(profile), JsonSerializer.Serialize(p, ModStoreJsonContext.Default.ModProfile));
        }

        /// <summary>
        /// Applies a profile. Vanilla disables everything; Performance enables only "patch"/"performance" mods; Visual only
        /// "visual"/"ui" mods; Custom (and any user profile) restores its saved set. Takes effect at the next game start.
        /// </summary>
        public void ApplyProfile(string profile)
        {
            List<InstalledMod> mods = List();
            List<(string Name, bool Enabled)> order;

            static bool Is(InstalledMod m, params string[] categories) => categories.Contains(m.Manifest?.Category ?? "", StringComparer.OrdinalIgnoreCase);

            switch (profile)
            {
                case "Vanilla":
                    order = mods.Select(m => (m.Name, false)).ToList();
                    break;
                case "Performance":
                    order = mods.Select(m => (m.Name, Is(m, "patch", "performance"))).ToList();
                    break;
                case "Visual":
                    order = mods.Select(m => (m.Name, Is(m, "visual", "ui"))).ToList();
                    break;
                default:
                    string path = ProfilePath(profile);
                    ModProfile saved = File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), ModStoreJsonContext.Default.ModProfile) : null;

                    if (saved == null)
                    {
                        // An unsaved Custom profile is the current state.
                        return;
                    }

                    order = saved.Mods.Where(e => mods.Exists(m => m.Name == e.Name)).Select(e => (e.Name, e.Enabled)).ToList();
                    order.AddRange(mods.Where(m => !order.Exists(o => o.Name == m.Name)).Select(m => (m.Name, false)));
                    break;
            }

            Save(order);
        }

        private static bool PathEq(string a, string b)
            => a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);
    }
}
