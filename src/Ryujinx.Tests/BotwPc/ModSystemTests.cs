using NUnit.Framework;
using Ryujinx.HLE.BotwPc.Mods;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Ryujinx.Tests.BotwPc
{
    public class ModSystemTests
    {
        private const string Game = "01007ef00011e000";
        private const string Dlc = "01007ef00011f001";
        private string _root;
        private ModStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "botwpc-mods-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _store = new ModStore(Path.Combine(_root, "mods", "contents"), Path.Combine(_root, "games"), Path.Combine(_root, "botw"));
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private string MakeMod(string name, params (string Path, string Content)[] files)
        {
            string dir = Path.Combine(_root, "src", name);

            foreach ((string path, string content) in files)
            {
                string full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, content);
            }

            return dir;
        }

        private string MakeZip(string name, Action<ZipArchive> fill)
        {
            string path = Path.Combine(_root, name);
            using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
            fill(zip);
            return path;
        }

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            using Stream s = zip.CreateEntry(entryName).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }

        // ---- Archive safety ----

        [Test]
        public void RejectsZipPathTraversalAndAbsolutePaths()
        {
            foreach (string bad in new[] { "../evil.txt", "romfs/../../evil.txt", "/etc/evil", "C:/evil" })
            {
                string zip = MakeZip($"bad{Math.Abs(bad.GetHashCode())}.zip", z => AddText(z, bad, "x"));
                ModStore.InstallResult r = _store.Install(zip, "bad");

                Assert.That(r.Success, Is.False, bad);
                Assert.That(r.Errors[0], Does.Contain("Unsafe path"), bad);
            }

            Assert.That(File.Exists(Path.Combine(_root, "evil.txt")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_root, "botw", "staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "botw", "staging")).Any(), Is.False, "staging cleaned up");
        }

        [Test]
        public void RejectsZipSymlinkEntries()
        {
            string zip = MakeZip("link.zip", z =>
            {
                ZipArchiveEntry e = z.CreateEntry("romfs/link");
                e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using Stream s = e.Open();
                s.Write("/etc/passwd"u8);
            });

            Assert.That(_store.Install(zip, "link").Errors[0], Does.Contain("symbolic link"));
        }

        [Test]
        public void RejectsFolderWithSymlink()
        {
            string dir = MakeMod("withlink", ("romfs/Pack/a.pack", "a"));
            File.CreateSymbolicLink(Path.Combine(dir, "romfs", "escape"), "/etc");

            Assert.That(_store.Install(dir, "withlink").Errors[0], Does.Contain("symbolic link"));
        }

        [Test]
        public void EnforcesSizeLimits()
        {
            string dir = MakeMod("big", ("romfs/a.bin", new string('x', 4096)));

            Assert.Throws<ModArchive.ImportException>(() => ModArchive.Import(dir, Path.Combine(_root, "stage-limit"), new ModArchive.Limits(1024, 1 << 20, 10, 100)));
        }

        [Test]
        public void RejectsUnsupportedArchiveTypes()
        {
            string bnp = Path.Combine(_root, "x.bnp");
            File.WriteAllText(bnp, "7z");

            Assert.That(_store.Install(bnp).Errors[0], Does.Contain("BCML"));
        }

        // ---- Layout detection ----

        [Test]
        public void DetectsSwitchLayouts()
        {
            Assert.That(_store.Install(MakeMod("plain", ("romfs/Pack/A.pack", "1"))).Success, Is.True);
            Assert.That(_store.Install(MakeMod("wrapped", ("Wrapper/romfs/Pack/B.pack", "1"))).Success, Is.True);
            Assert.That(_store.Install(MakeMod("tid", ($"{Game.ToUpper()}/romfs/Pack/C.pack", "1"))).Success, Is.True);

            ModStore.InstallResult ukmmDeploy = _store.Install(MakeMod("deploy",
                ($"atmosphere/contents/{Game.ToUpper()}/romfs/Pack/D.pack", "1"),
                ($"atmosphere/contents/{Dlc.ToUpper()}/romfs/Map/E.smubin", "1")));
            Assert.That(ukmmDeploy.Success, Is.True, string.Join("; ", ukmmDeploy.Errors));

            Assert.That(File.Exists(Path.Combine(_root, "mods", "contents", Game, "deploy", "romfs", "Pack", "D.pack")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "mods", "contents", Dlc, "deploy", "romfs", "Map", "E.smubin")), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "mods", "contents", Game, "deploy", ModStore.ManifestName)), Is.True);
        }

        [Test]
        public void RefusesWiiUAndPackages()
        {
            Assert.That(_store.Install(MakeMod("wiiu", ("content/Pack/A.pack", "1"))).Errors[0], Does.Contain("Wii U"));
            Assert.That(_store.Install(MakeMod("cemu", ("rules.txt", "[Definition]"))).Errors[0], Does.Contain("Cemu"));
            Assert.That(_store.Install(MakeMod("ukmm", ("meta.yml", "name: x"), ("romfs/a", "1"))).Errors[0], Does.Contain("UKMM"));
            Assert.That(_store.Install(MakeMod("other", ("0100000000010000/romfs/a", "1"))).Errors[0], Does.Contain("not Breath of the Wild"));
            Assert.That(_store.Install(MakeMod("empty", ("readme.txt", "hi"))).Success, Is.False);
            Assert.That(_store.List(), Is.Empty);
        }

        [Test]
        public void ChecksExefsPatchVersionAndTimingCode()
        {
            const string Header = "@nsobid-8E9978D50BDD20B4C8395A106C27FFDE\n@enabled\n";

            // 0x1C01380 - 0x100 header = main+0x1C01280, inside VFR::updateInterval.
            ModStore.InstallResult fps = _store.Install(MakeMod("fps", ("exefs/fps.pchtxt", Header + "01C01380 1F2003D5\n@stop\n")));
            Assert.That(fps.Success, Is.False);
            Assert.That(fps.Errors[0], Does.Contain("frame-timing code"));

            ModStore.InstallResult other = _store.Install(MakeMod("other", ("exefs/x.pchtxt", Header + "00100100 1F2003D5\n@stop\n")));
            Assert.That(other.Success, Is.True);
            Assert.That(other.Warnings, Has.Some.Contains("1.6.0 main"));

            ModStore.InstallResult old = _store.Install(MakeMod("old", ("exefs/y.pchtxt", "@nsobid-0123456789ABCDEF0123456789ABCDEF\n@enabled\n00100100 00\n@stop\n")));
            Assert.That(old.Errors[0], Does.Contain("not BotW 1.6.0"));

            ModStore.InstallResult lower = _store.Install(MakeMod("lower", ("exefs/z.pchtxt", Header.ToLowerInvariant() + "00100100 00\n@stop\n")));
            Assert.That(lower.Success, Is.True, "build ids are matched case-insensitively");
        }

        // ---- Store: order, conflicts, enable, update, rollback, profiles ----

        [Test]
        public void ConflictsFollowPriorityAndFlagMergeFiles()
        {
            _store.Install(MakeMod("A", ("romfs/Pack/Shared.pack", "A"), ("romfs/System/Resource/ResourceSizeTable.product.srsizetable", "A")));
            _store.Install(MakeMod("B", ("romfs/Pack/Shared.pack", "B"), ("romfs/System/Resource/ResourceSizeTable.product.srsizetable", "B"), ("romfs/Pack/OnlyB.pack", "B")));

            // Newest install has the highest priority.
            Assert.That(_store.List().Select(m => m.Name), Is.EqualTo(new[] { "B", "A" }));

            var conflicts = _store.Conflicts();
            Assert.That(conflicts.Select(c => c.File), Is.EquivalentTo(new[] { "01007EF00011E000:/Pack/Shared.pack", "01007EF00011E000:/System/Resource/ResourceSizeTable.product.srsizetable" }));
            Assert.That(conflicts.All(c => c.Winner == "B"));
            Assert.That(conflicts.Single(c => c.File.EndsWith("srsizetable")).NeedsMerge, Is.True);
            Assert.That(conflicts.Single(c => c.File.EndsWith("Shared.pack")).NeedsMerge, Is.False);

            _store.Move("A", -1);
            Assert.That(_store.List().Select(m => m.Name), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(_store.Conflicts().All(c => c.Winner == "A"));

            _store.SetEnabled("B", false);
            Assert.That(_store.Conflicts(), Is.Empty);

            // mods.json is what ModLoader reads: order and enabled state must be there.
            string json = File.ReadAllText(Path.Combine(_root, "games", Game, "mods.json"));
            Assert.That(json.IndexOf("\"A\"", StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"B\"", StringComparison.Ordinal)));
            Assert.That(json, Does.Contain("\"enabled\": false"));
        }

        [Test]
        public void UpdateUninstallAndRollback()
        {
            _store.Install(MakeMod("v1", ("romfs/Pack/X.pack", "one")), "Mod");
            Assert.That(_store.Install(MakeMod("v2", ("romfs/Pack/X.pack", "two")), "Mod").Success, Is.False, "same name needs update");

            Assert.That(_store.Install(MakeMod("v2b", ("romfs/Pack/X.pack", "two")), "Mod", replace: true).Success, Is.True);
            string file = Path.Combine(_root, "mods", "contents", Game, "Mod", "romfs", "Pack", "X.pack");
            Assert.That(File.ReadAllText(file), Is.EqualTo("two"));

            Assert.That(_store.Rollback("Mod"), Is.True);
            Assert.That(File.ReadAllText(file), Is.EqualTo("one"));

            Assert.That(_store.Uninstall("Mod"), Is.True);
            Assert.That(File.Exists(file), Is.False);
            Assert.That(_store.List(), Is.Empty);

            Assert.That(_store.Rollback("Mod"), Is.True, "uninstall is undoable");
            Assert.That(File.ReadAllText(file), Is.EqualTo("one"));
        }

        [Test]
        public void FailedUpdateKeepsInstalledVersion()
        {
            _store.Install(MakeMod("good", ("romfs/Pack/X.pack", "good")), "Mod");
            ModStore.InstallResult r = _store.Install(MakeMod("bad", ("content/Pack/X.pack", "wiiu")), "Mod", replace: true);

            Assert.That(r.Success, Is.False);
            Assert.That(File.ReadAllText(Path.Combine(_root, "mods", "contents", Game, "Mod", "romfs", "Pack", "X.pack")), Is.EqualTo("good"));
        }

        [Test]
        public void ProfilesSwitchEnabledSets()
        {
            _store.Install(MakeMod("ui", ("romfs/Layout/Common.sblarc", "1")), "XboxPrompts");
            _store.Install(MakeMod("game", ("romfs/Actor/Pack/X.sbactorpack", "1")), "Gameplay");
            _store.Install(MakeMod("patch", ("exefs/p.pchtxt", "@nsobid-8E9978D50BDD20B4C8395A106C27FFDE\n@enabled\n00100100 00\n@stop\n")), "Patch");

            _store.Install(MakeMod("dlc", ($"{Dlc}/romfs/Pack/AocMainField.pack", "1")), "DlcOnly");

            _store.ApplyProfile("Vanilla");
            Assert.That(_store.List().All(m => !m.Enabled));

            // A DLC-only mod keeps its state when another mod changes (it used to read as enabled and be re-enabled).
            _store.SetEnabled("Gameplay", true);
            Assert.That(_store.List().Single(m => m.Name == "DlcOnly").Enabled, Is.False);
            _store.SetEnabled("Gameplay", false);

            _store.ApplyProfile("Visual");
            Assert.That(_store.List().Where(m => m.Enabled).Select(m => m.Name), Is.EquivalentTo(new[] { "XboxPrompts" }));

            _store.ApplyProfile("Performance");
            Assert.That(_store.List().Where(m => m.Enabled).Select(m => m.Name), Is.EquivalentTo(new[] { "Patch" }));

            _store.SetEnabled("Gameplay", true);
            _store.SaveProfile("Custom");
            _store.ApplyProfile("Vanilla");
            _store.ApplyProfile("Custom");
            Assert.That(_store.List().Where(m => m.Enabled).Select(m => m.Name), Is.EquivalentTo(new[] { "Patch", "Gameplay" }));
        }
    }
}
