using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ryujinx.HLE.BotwPc
{
    /// <summary>
    /// First-run setup of the standalone BOTW-PC data directory:
    /// <c>Ryujinx --botw-setup [-r dir] [--import ryujinx-data-dir [--import-saves]] [--game file] [--update file] [--dlc file]...</c>.
    /// Imports only copy (never move or modify) the source; existing destination files are never overwritten.
    /// </summary>
    public static class BotwSetup
    {
        public static string DefaultDataDir => Path.Combine(
            OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support") :
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "BOTW-PC");

        private const string Usage = """
            usage: Ryujinx --botw-setup [-r <data dir>] [options]      (default data dir: ~/.local/share/BOTW-PC; macOS: ~/Library/Application Support/BOTW-PC)
              --import <ryujinx data dir>  copy keys, firmware and user profiles from an existing Ryujinx data dir
              --import-saves               also copy save data (only if the BOTW-PC data dir has none)
              --game <nsp|xci>             base game file used by botw-pc / Ryujinx --botw
              --update <nsp>               register the game update
              --dlc <nsp>                  register an add-on content package (repeatable; enabled)
            Without options it prints the setup status.
            """;

        public static int Run(string[] args)
        {
            string Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

            if (Array.IndexOf(args, "--help") >= 0)
            {
                Console.WriteLine(Usage);
                return 0;
            }

            string dataDir = Path.GetFullPath(Arg("-r") ?? DefaultDataDir);
            Directory.CreateDirectory(dataDir);
            AppDataManager.Initialize(dataDir);
            Console.WriteLine($"BOTW-PC data directory: {dataDir}");

            if (Arg("--import") is string source)
            {
                if (!Directory.Exists(source))
                {
                    Console.Error.WriteLine($"not a directory: {source}");
                    return 2;
                }

                if (Path.GetFullPath(source).TrimEnd('/') == dataDir.TrimEnd('/'))
                {
                    Console.Error.WriteLine("source and destination are the same directory");
                    return 2;
                }

                CopyMissing(Path.Combine(source, "system"), Path.Combine(dataDir, "system"), f => f.EndsWith(".keys") || f.EndsWith("Profiles.json"));
                CopyMissing(Path.Combine(source, "bis", "system", "Contents"), Path.Combine(dataDir, "bis", "system", "Contents"), _ => true);

                if (Array.IndexOf(args, "--import-saves") >= 0)
                {
                    foreach (string save in new[] { Path.Combine("bis", "user", "save"), Path.Combine("bis", "system", "save") })
                    {
                        string from = Path.Combine(source, save), to = Path.Combine(dataDir, save);

                        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
                        {
                            Console.WriteLine($"  {save}: kept (BOTW-PC already has save data; nothing overwritten)");
                        }
                        else
                        {
                            CopyMissing(from, to, _ => true);
                        }
                    }
                }
            }

            BotwPcSettings settings = BotwPcSettings.Load(BotwPcSettings.FilePath);

            if (Arg("--game") is string game)
            {
                if (!File.Exists(game) && !Directory.Exists(game))
                {
                    Console.Error.WriteLine($"not found: {game}");
                    return 2;
                }

                settings.Game.Path = Path.GetFullPath(game);
                settings.Save(BotwPcSettings.FilePath);
            }

            string gameDir = Path.Combine(AppDataManager.GamesDirPath, $"{BotwProfile.TitleId:x16}");
            Directory.CreateDirectory(gameDir);
            using VirtualFileSystem vfs = VirtualFileSystem.CreateInstance();
            bool keys = File.Exists(Path.Combine(AppDataManager.KeysDirPath, "prod.keys"));

            if (keys)
            {
                vfs.ReloadKeySet();
            }

            if (Arg("--update") is string update)
            {
                string full = Path.GetFullPath(update);
                File.WriteAllText(Path.Combine(gameDir, "updates.json"),
                    new JsonObject { ["selected"] = full, ["paths"] = new JsonArray(full) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            List<string> dlcFiles = [.. args.Select((a, i) => (a, i)).Where(x => x.a == "--dlc" && x.i + 1 < args.Length).Select(x => Path.GetFullPath(args[x.i + 1]))];

            if (dlcFiles.Count > 0)
            {
                if (!keys)
                {
                    Console.Error.WriteLine("prod.keys is needed to read add-on content; import or copy keys first");
                    return 2;
                }

                string dlcJson = Path.Combine(gameDir, "dlc.json");
                JsonArray list = File.Exists(dlcJson) ? JsonNode.Parse(File.ReadAllText(dlcJson)) as JsonArray ?? [] : [];

                foreach (string file in dlcFiles)
                {
                    BotwDlc.Package[] packages = [.. BotwDlc.Read(file, vfs).Where(p => p.Error == null)];

                    if (packages.Length == 0)
                    {
                        Console.Error.WriteLine($"no usable add-on content in {file}");
                        continue;
                    }

                    JsonArray ncas = [.. packages.Select(p => (JsonNode)new JsonObject { ["path"] = p.NcaPath, ["title_id"] = p.TitleId, ["is_enabled"] = true })];

                    foreach (JsonNode existing in list.Where(n => n?["path"]?.GetValue<string>() == file).ToList())
                    {
                        list.Remove(existing);
                    }

                    list.Add(new JsonObject { ["path"] = file, ["dlc_nca_list"] = ncas });
                }

                File.WriteAllText(dlcJson, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            // Status.
            string firmware = Path.Combine(dataDir, "bis", "system", "Contents", "registered");
            Console.WriteLine($"  keys (system/prod.keys):   {(keys ? "present" : "MISSING — use --import or copy prod.keys")}");
            Console.WriteLine($"  firmware:                  {(Directory.Exists(firmware) && Directory.EnumerateFileSystemEntries(firmware).Any() ? "installed" : "MISSING — use --import or Tools > Install Firmware")}");
            Console.WriteLine($"  game file:                 {(string.IsNullOrEmpty(settings.Game.Path) ? "not set (found in the game folders on first start, or use --game)" : settings.Game.Path)}");

            string updatesJson = Path.Combine(gameDir, "updates.json");
            string selected = File.Exists(updatesJson) ? JsonNode.Parse(File.ReadAllText(updatesJson))?["selected"]?.GetValue<string>() : null;
            Console.WriteLine($"  update:                    {(string.IsNullOrEmpty(selected) ? "none" : $"{selected} ({(keys ? BotwDlc.FormatVersion(BotwDlc.ReadGameVersion(selected, vfs)) : "?")})")}");

            string dlcPath = Path.Combine(gameDir, "dlc.json");
            int dlcCount = File.Exists(dlcPath) ? (JsonNode.Parse(File.ReadAllText(dlcPath)) as JsonArray)?.Count ?? 0 : 0;
            Console.WriteLine($"  add-on content packages:   {dlcCount}");
            Console.WriteLine("Start the game with: botw-pc   (or: Ryujinx --botw)");

            return keys ? 0 : 1;
        }

        /// <summary>Copies files that do not exist at the destination yet; the source is only read.</summary>
        private static void CopyMissing(string from, string to, Func<string, bool> include)
        {
            if (!Directory.Exists(from))
            {
                Console.WriteLine($"  {from}: not found, skipped");
                return;
            }

            int copied = 0, kept = 0;

            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(include))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));

                if (File.Exists(target))
                {
                    kept++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
                copied++;
            }

            Console.WriteLine($"  {from} → {to}: {copied} copied, {kept} already present (kept)");
        }
    }
}
