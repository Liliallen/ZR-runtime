using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration;
using System;
using System.Linq;

namespace Ryujinx.HLE.BotwPc.Mods
{
    /// <summary>
    /// Command-line front end of <see cref="ModStore"/>: <c>Ryujinx --botw-mods [-r data-dir] &lt;command&gt; ...</c>.
    /// Same code path as the mod manager window; useful for scripting and for testing without the UI.
    /// </summary>
    public static class ModCli
    {
        private const string Usage = """
            usage: Ryujinx --botw-mods -r <data dir> <command>
              list                         mods in priority order (first wins)
              install <zip|folder> [name]  validate and install (new mods get the highest priority)
              update <zip|folder> <name>   replace an installed mod (previous version kept as backup)
              uninstall <name>             remove (moved to backup)
              rollback <name>              restore the latest backup
              enable|disable <name>
              up|down <name>               change priority
              conflicts                    RomFS files provided by several enabled mods
              profile <Vanilla|Performance|Visual|Custom|name> | save-profile <name>
              safe-mode on|off
            """;

        /// <summary>
        /// <c>Ryujinx --botw-dlc -r &lt;data dir&gt; [--verify]</c>: BotW add-on content registered in games/&lt;id&gt;/dlc.json
        /// (Ryujinx's DLC manager), with version, required game version and optional full hash verification.
        /// </summary>
        public static int RunDlc(string[] args)
        {
            int r = Array.IndexOf(args, "-r");
            string baseDir = r >= 0 && r + 1 < args.Length ? args[r + 1] : null;

            if (baseDir == null || !System.IO.Directory.Exists(baseDir))
            {
                Console.Error.WriteLine("usage: Ryujinx --botw-dlc -r <data dir> [--verify]");
                return 2;
            }

            AppDataManager.Initialize(baseDir);
            using Ryujinx.HLE.FileSystem.VirtualFileSystem vfs = Ryujinx.HLE.FileSystem.VirtualFileSystem.CreateInstance();
            vfs.ReloadKeySet();

            string gameDir = System.IO.Path.Combine(AppDataManager.GamesDirPath, $"{BotwProfile.TitleId:x16}");
            string updatePath = null;
            var updates = System.Text.Json.JsonDocument.Parse(System.IO.File.Exists(System.IO.Path.Combine(gameDir, "updates.json")) ? System.IO.File.ReadAllText(System.IO.Path.Combine(gameDir, "updates.json")) : "{}");

            if (updates.RootElement.TryGetProperty("selected", out var selected))
            {
                updatePath = selected.GetString();
            }

            uint gameVersion = BotwDlc.ReadGameVersion(updatePath, vfs);
            Console.WriteLine($"game update: {BotwDlc.FormatVersion(gameVersion)}");

            string dlcJson = System.IO.Path.Combine(gameDir, "dlc.json");
            var containers = System.Text.Json.JsonDocument.Parse(System.IO.File.Exists(dlcJson) ? System.IO.File.ReadAllText(dlcJson) : "[]").RootElement;
            int failures = 0;

            foreach (var container in containers.EnumerateArray())
            {
                string path = container.GetProperty("path").GetString();
                bool enabled = container.GetProperty("dlc_nca_list").EnumerateArray().Any(n => n.GetProperty("is_enabled").GetBoolean());

                foreach (BotwDlc.Package package in BotwDlc.Read(path, vfs))
                {
                    Console.WriteLine($"{(enabled ? "[x]" : "[ ]")} {package.TitleId:X16} {package.Name} {BotwDlc.FormatVersion(package.Version)}: {BotwDlc.Compatibility(package, gameVersion)}");

                    if (package.Error != null)
                    {
                        failures++;
                    }
                    else if (Array.IndexOf(args, "--verify") >= 0)
                    {
                        try
                        {
                            (int files, long bytes) = BotwDlc.Verify(path, package.NcaPath, vfs);
                            Console.WriteLine($"    verified: {files} files, {bytes / 1048576.0:F1} MiB, all hashes valid");
                        }
                        catch (Exception e) when (e is not OutOfMemoryException)
                        {
                            Console.WriteLine($"    VERIFY FAILED: {e.Message}");
                            failures++;
                        }
                    }
                }
            }

            return failures == 0 ? 0 : 1;
        }

        public static int Run(string[] args)
        {
            int r = Array.IndexOf(args, "-r");
            string baseDir = r >= 0 && r + 1 < args.Length ? args[r + 1] : null;
            string[] rest = args.Where((a, i) => a != "--botw-mods" && i != r && i != r + 1).ToArray();

            // Required: never fall back to a default data directory from a script.
            if (baseDir == null || !System.IO.Directory.Exists(baseDir) || rest.Length == 0)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            AppDataManager.Initialize(baseDir);
            ModStore store = ModStore.ForCurrentUser();

            if (rest.Length == 0)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            string Arg(int i) => i < rest.Length ? rest[i] : throw new ArgumentException(Usage);

            try
            {
                switch (rest[0])
                {
                    case "list":
                        foreach (ModStore.InstalledMod m in store.List())
                        {
                            Console.WriteLine($"{m.Priority,3} [{(m.Enabled ? "x" : " ")}] {m.Name}  ({m.Manifest?.Category ?? "unmanaged"}; {string.Join(",", m.Parts.Keys.Select(t => $"{t:X16}"))})");
                        }

                        return 0;
                    case "install":
                    case "update":
                        ModStore.InstallResult result = store.Install(Arg(1), rest.Length > 2 ? rest[2] : null, replace: rest[0] == "update");
                        result.Errors.ToList().ForEach(e => Console.WriteLine("error: " + e));
                        result.Warnings.ToList().ForEach(w => Console.WriteLine("warning: " + w));
                        Console.WriteLine(result.Success ? $"installed '{result.Name}'" : "not installed");

                        return result.Success ? 0 : 1;
                    case "uninstall":
                        return store.Uninstall(Arg(1)) ? 0 : 1;
                    case "rollback":
                        return store.Rollback(Arg(1)) ? 0 : 1;
                    case "enable":
                    case "disable":
                        store.SetEnabled(Arg(1), rest[0] == "enable");
                        return 0;
                    case "up":
                    case "down":
                        store.Move(Arg(1), rest[0] == "up" ? -1 : 1);
                        return 0;
                    case "conflicts":
                        foreach (ModStore.Conflict c in store.Conflicts())
                        {
                            Console.WriteLine($"{c.File}: {string.Join(" > ", c.Mods)}{(c.NeedsMerge ? "  [needs merge]" : "")}");
                        }

                        return 0;
                    case "profile":
                        store.ApplyProfile(Arg(1));
                        return 0;
                    case "save-profile":
                        store.SaveProfile(Arg(1));
                        return 0;
                    case "safe-mode":
                        BotwPcSettings settings = BotwPcSettings.Instance;
                        settings.Mods.SafeMode = Arg(1) == "on";
                        settings.Save();
                        return 0;
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
        }
    }
}
