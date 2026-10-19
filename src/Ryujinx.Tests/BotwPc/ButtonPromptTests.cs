using NUnit.Framework;
using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.HLE.BotwPc.Prompts;
using Ryujinx.Input.HLE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ryujinx.Tests.BotwPc
{
    public class ButtonPromptTests
    {
        private static PromptMapping Prompts(ControllerSettings settings, string name = "Xbox Series X Controller") =>
            ButtonPrompts.FromConfig(AutoController.Build("0-xbox", name, settings, hasMotion: false), settings.Prompts, out _);

        [Test]
        public void PositionLayoutShowsThePhysicalXboxButton()
        {
            PromptMapping m = Prompts(new ControllerSettings { Layout = ButtonLayout.Nintendo });

            // The game asks for Switch B (bottom); the player presses Xbox A (bottom), so Xbox A is shown.
            Assert.That(m.Get(SwitchButton.B), Is.EqualTo(GamepadInputId.A));
            Assert.That(m.Get(SwitchButton.A), Is.EqualTo(GamepadInputId.B));
            Assert.That(m.Get(SwitchButton.Y), Is.EqualTo(GamepadInputId.X));
            Assert.That(m.Get(SwitchButton.X), Is.EqualTo(GamepadInputId.Y));
            Assert.That(m.Get(SwitchButton.L), Is.EqualTo(GamepadInputId.LeftShoulder));
            Assert.That(m.Get(SwitchButton.ZR), Is.EqualTo(GamepadInputId.RightTrigger));
            Assert.That(m.Get(SwitchButton.Plus), Is.EqualTo(GamepadInputId.Plus));
            Assert.That(m.Get(SwitchButton.Minus), Is.EqualTo(GamepadInputId.Minus));
            Assert.That(m.Get(SwitchButton.LeftStick), Is.EqualTo(GamepadInputId.LeftStick));
        }

        [Test]
        public void LabelLayoutIsIdentityWithoutDoubleSwap()
        {
            PromptMapping m = Prompts(new ControllerSettings { Layout = ButtonLayout.Xbox });

            foreach ((SwitchButton logical, GamepadInputId physical) in new[]
                     {
                         (SwitchButton.A, GamepadInputId.A), (SwitchButton.B, GamepadInputId.B),
                         (SwitchButton.X, GamepadInputId.X), (SwitchButton.Y, GamepadInputId.Y),
                     })
            {
                Assert.That(m.Get(logical), Is.EqualTo(physical), logical.ToString());
            }
        }

        [Test]
        public void PromptsFollowTheInputConfigExactly()
        {
            // Whatever produced the config (layout, custom bindings), the glyph of each logical button is the input bound to it.
            ControllerSettings settings = new() { Bindings = new() { ["X"] = "A", ["B"] = "X", ["ZL"] = "LeftShoulder", ["L"] = "Paddle1" } };
            StandardControllerInputConfig config = AutoController.Build("0-xbox", "Xbox Elite Wireless Controller", settings, hasMotion: false);
            PromptMapping m = ButtonPrompts.FromConfig(config, PromptStyle.Auto, out _);

            Assert.That(config.RightJoycon.ButtonX, Is.EqualTo(GamepadInputId.A));
            Assert.That(m.Get(SwitchButton.X), Is.EqualTo(GamepadInputId.A));
            Assert.That(m.Get(SwitchButton.B), Is.EqualTo(GamepadInputId.X));
            Assert.That(m.Get(SwitchButton.ZL), Is.EqualTo(GamepadInputId.LeftShoulder));
            Assert.That(m.Get(SwitchButton.L), Is.EqualTo(GamepadInputId.Paddle1));
            Assert.That(m.Get(SwitchButton.A), Is.EqualTo(GamepadInputId.B), "unchanged bindings keep the layout");
        }

        [Test]
        public void BadBindingsAreIgnored()
        {
            ControllerSettings settings = new() { Bindings = new() { ["Jump"] = "A", ["A"] = "NoSuchButton", ["B"] = "Count" } };
            StandardControllerInputConfig config = AutoController.Build("0-xbox", "Xbox One Controller", settings, hasMotion: false);

            Assert.That(config.RightJoycon.ButtonA, Is.EqualTo(GamepadInputId.B));
            Assert.That(config.RightJoycon.ButtonB, Is.EqualTo(GamepadInputId.A));
        }

        [Test]
        public void OriginalGlyphsForKeyboardNintendoAndPlayStation()
        {
            Assert.That(ButtonPrompts.FromConfig(new StandardKeyboardInputConfig(), PromptStyle.Auto, out _), Is.Null);
            Assert.That(ButtonPrompts.FromConfig(null, PromptStyle.Auto, out _), Is.Null);
            Assert.That(Prompts(new ControllerSettings(), "Nintendo Switch Pro Controller"), Is.Null);
            Assert.That(Prompts(new ControllerSettings(), "PS5 Controller"), Is.Null);
            Assert.That(Prompts(new ControllerSettings { Prompts = PromptStyle.Original }), Is.Null);
            Assert.That(Prompts(new ControllerSettings { Prompts = PromptStyle.Xbox }, "PS5 Controller"), Is.Not.Null);
            Assert.That(Prompts(new ControllerSettings(), "8BitDo Pro 2"), Is.Not.Null);
        }

        [Test]
        public void FontPlanDrawsTheBoundInput()
        {
            PromptMapping m = Prompts(new ControllerSettings());
            Dictionary<int, Func<int, int, byte[]>> plan = PromptOverlay.FontPlan(m);

            Assert.That(plan[PromptOverlay.Glyphs.B](30, 30), Is.EqualTo(GlyphArt.Draw(GamepadInputId.A, 30, 30)));
            Assert.That(plan[PromptOverlay.Glyphs.A](30, 30), Is.EqualTo(GlyphArt.Draw(GamepadInputId.B, 30, 30)));
            Assert.That(plan[PromptOverlay.Glyphs.ZL](34, 30), Is.EqualTo(GlyphArt.Draw(GamepadInputId.LeftTrigger, 34, 30)));
            Assert.That(plan[PromptOverlay.Glyphs.Minus](30, 30), Is.EqualTo(GlyphArt.Draw(GamepadInputId.Minus, 30, 30)));
            Assert.That(plan[PromptOverlay.Glyphs.Dpad](30, 30), Is.EqualTo(GlyphArt.DrawDpad(GlyphArt.Dpad.All, 30, 30)));
            Assert.That(plan.ContainsKey(PromptOverlay.Glyphs.LeftStickPress), Is.False, "unremapped stick press keeps the game glyph");

            // A logical button with no binding shows the "?" glyph, never another button's glyph.
            Dictionary<SwitchButton, GamepadInputId> physical = m.Physical.ToDictionary();
            physical[SwitchButton.Y] = GamepadInputId.Unbound;
            physical[SwitchButton.LeftStick] = GamepadInputId.Paddle2;
            Dictionary<int, Func<int, int, byte[]>> remapped = PromptOverlay.FontPlan(new PromptMapping("pad", physical));
            Assert.That(remapped[PromptOverlay.Glyphs.Y](30, 30), Is.EqualTo(GlyphArt.Draw(GamepadInputId.Unbound, 30, 30)));
            Assert.That(remapped[PromptOverlay.Glyphs.LeftStickPress](30, 30), Is.EqualTo(GlyphArt.Draw(GamepadInputId.Paddle2, 30, 30)));
        }

        [Test]
        public void DiamondLabelsFollowTheMapping()
        {
            // Minimal BFLYT: header + txt1 panes T_B_00 ("B"), T_X_00 ("X") and T_GuideA_00 (not a diamond label).
            static byte[] Txt1(string name, string text)
            {
                byte[] t = new byte[0x70 + 2 * text.Length + 2];
                "txt1"u8.CopyTo(t);
                BitConverter.GetBytes(t.Length).CopyTo(t, 4);
                Encoding.ASCII.GetBytes(name).CopyTo(t, 0x0C);
                BitConverter.GetBytes((ushort)(2 * text.Length + 2)).CopyTo(t, 0x56);
                BitConverter.GetBytes(0x70).CopyTo(t, 0x64);
                Encoding.Unicode.GetBytes(text).CopyTo(t, 0x70);
                return t;
            }

            byte[][] panes = [Txt1("T_B_00", "B"), Txt1("T_X_00", "X"), Txt1("T_GuideA_00", "AGuide")];
            byte[] header = new byte[0x14];
            "FLYT"u8.CopyTo(header);
            header[4] = 0xFF;
            header[5] = 0xFE;
            header[0x06] = 0x14;
            header[0x10] = (byte)panes.Length;
            byte[] layout = [.. header, .. panes.SelectMany(p => p)];
            BitConverter.GetBytes(layout.Length).CopyTo(layout, 0x0C);
            int b = 0x14 + 0x70, x = 0x14 + panes[0].Length + 0x70, guide = 0x14 + panes[0].Length + panes[1].Length + 0x70;

            byte[] position = (byte[])layout.Clone();
            Assert.That(PromptOverlay.PatchDiamondLabels(position, Prompts(new ControllerSettings())), Is.EqualTo(2));
            Assert.That(Encoding.Unicode.GetString(position, b, 2), Is.EqualTo("A"), "Switch B is the bottom button: Xbox A");
            Assert.That(Encoding.Unicode.GetString(position, x, 2), Is.EqualTo("Y"));
            Assert.That(Encoding.Unicode.GetString(position, guide, 12), Is.EqualTo("AGuide"));

            byte[] custom = (byte[])layout.Clone();
            PromptOverlay.PatchDiamondLabels(custom, Prompts(new ControllerSettings { Bindings = new() { ["X"] = "LeftShoulder" } }));
            Assert.That(Encoding.Unicode.GetString(custom, x, 2), Is.EqualTo("?"), "a name longer than one letter does not fit");
        }

        [Test]
        public void FailedOverlayIsNotCached()
        {
            string cache = Path.Combine(Path.GetTempPath(), "botwpc-prompt-cache-" + Guid.NewGuid().ToString("N"));
            PromptMapping m = Prompts(new ControllerSettings());
            PromptOverlay.Source Broken(string path) =>
                path == PromptOverlay.BootupPack ? new PromptOverlay.Source(path, Encoding.ASCII.GetBytes("not a SARC archive"), "base game", true) : null;

            try
            {
                PromptOverlay.Result first = PromptOverlay.Build(m, PromptPriority.BuiltIn, Broken, _ => null, cache);
                PromptOverlay.Result second = PromptOverlay.Build(m, PromptPriority.BuiltIn, Broken, _ => null, cache);

                Assert.That(first.Notes, Has.Some.Contains("not patched"));
                Assert.That(second.Notes, Has.None.StartsWith("cached overlay"), "a failed build must be regenerated");

                // A complete build is cached until invalidated (Settings > BotW-PC > Rebuild prompt glyphs).
                PromptOverlay.Source Empty(string path) => null;
                PromptOverlay.Build(m, PromptPriority.BuiltIn, Empty, _ => null, cache);
                Assert.That(PromptOverlay.Build(m, PromptPriority.BuiltIn, Empty, _ => null, cache).Notes, Has.Some.StartsWith("cached overlay"));
                Assert.That(PromptOverlay.Invalidate(cache), Is.EqualTo(1));
                Assert.That(PromptOverlay.Build(m, PromptPriority.BuiltIn, Empty, _ => null, cache).Notes, Has.None.StartsWith("cached overlay"));
            }
            finally
            {
                Directory.Delete(cache, true);
            }
        }

        [Test]
        public void GlyphsAreDistinct()
        {
            GamepadInputId[] inputs =
            [
                GamepadInputId.A, GamepadInputId.B, GamepadInputId.X, GamepadInputId.Y, GamepadInputId.LeftShoulder, GamepadInputId.RightShoulder,
                GamepadInputId.LeftTrigger, GamepadInputId.RightTrigger, GamepadInputId.Minus, GamepadInputId.Plus, GamepadInputId.LeftStick,
                GamepadInputId.RightStick, GamepadInputId.DpadUp, GamepadInputId.DpadLeft, GamepadInputId.Misc1, GamepadInputId.Paddle1, GamepadInputId.Unbound,
            ];

            List<string> seen = [];

            foreach (GamepadInputId id in inputs)
            {
                byte[] glyph = GlyphArt.Draw(id, 40, 36);
                Assert.That(glyph.Count(v => v > 128), Is.GreaterThan(200), $"{id} has ink");
                Assert.That(glyph.Count(v => v < 128), Is.GreaterThan(100), $"{id} has a cut-out label");
                seen.Add(Convert.ToBase64String(glyph));
            }

            Assert.That(seen.Distinct().Count(), Is.EqualTo(inputs.Length));
        }

        [Test]
        public void Yaz0RoundTrip()
        {
            Random random = new(1);
            byte[] noise = new byte[5000];
            random.NextBytes(noise);
            byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BOTW-PC button prompts ", 4000)));

            foreach (byte[] data in new[] { noise, text, new byte[70000], Array.Empty<byte>(), new byte[] { 1, 2 } })
            {
                byte[] packed = Yaz0.Compress(data);
                Assert.That(Yaz0.Decompress(packed), Is.EqualTo(data));
            }

            Assert.That(Yaz0.Compress(text).Length, Is.LessThan(text.Length / 20));
        }

        private static byte[] BuildSarc(params (string Name, byte[] Data, int Align)[] files)
        {
            // Little-endian SARC as BotW for Switch uses (hashes are not checked by the reader).
            MemoryStream names = new();
            List<(int NameOffset, int Begin, int End)> nodes = [];
            MemoryStream data = new();

            foreach ((string name, byte[] contents, int align) in files)
            {
                int nameOffset = (int)names.Length / 4;
                names.Write(Encoding.ASCII.GetBytes(name + "\0"));

                while (names.Length % 4 != 0)
                {
                    names.WriteByte(0);
                }

                while (data.Length % align != 0)
                {
                    data.WriteByte(0);
                }

                nodes.Add((nameOffset, (int)data.Length, (int)data.Length + contents.Length));
                data.Write(contents);
            }

            int dataOffset = (0x14 + 12 + 16 * files.Length + 8 + (int)names.Length + 0xFF) & ~0xFF;
            using MemoryStream s = new();
            using BinaryWriter w = new(s);
            w.Write("SARC"u8.ToArray());
            w.Write((ushort)0x14);
            w.Write((ushort)0xFEFF);
            w.Write(0);
            w.Write(dataOffset);
            w.Write(0x0100);
            w.Write("SFAT"u8.ToArray());
            w.Write((ushort)0xC);
            w.Write((ushort)files.Length);
            w.Write(0x65);

            foreach ((int nameOffset, int begin, int end) in nodes)
            {
                w.Write(0);
                w.Write(0x01000000 | nameOffset);
                w.Write(begin);
                w.Write(end);
            }

            w.Write("SFNT"u8.ToArray());
            w.Write((ushort)8);
            w.Write((ushort)0);
            w.Write(names.ToArray());
            w.Write(new byte[dataOffset - (int)s.Length]);
            w.Write(data.ToArray());
            byte[] result = s.ToArray();
            BitConverter.GetBytes(result.Length).CopyTo(result, 8);

            return result;
        }

        [Test]
        public void SarcReplaceKeepsOtherFilesAndAlignment()
        {
            byte[] a = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
            byte[] b = [.. Enumerable.Repeat((byte)7, 100)];
            byte[] c = [.. Enumerable.Repeat((byte)9, 50)];
            Sarc sarc = new(BuildSarc(("Pack/A.bin", a, 4), ("Layout/B.sblarc", b, 4), ("C.bntx", c, 0x1000)));
            sarc.TryGetFile("C.bntx", out int cOffset, out _);

            sarc.ReplaceFile("Layout/B.sblarc", [1, 2, 3]);
            Assert.That(sarc.GetFile("Layout/B.sblarc"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(sarc.GetFile("Pack/A.bin"), Is.EqualTo(a));
            Assert.That(sarc.GetFile("C.bntx"), Is.EqualTo(c));

            byte[] big = new byte[10000];
            big[9999] = 42;
            sarc.ReplaceFile("Layout/B.sblarc", big);
            Assert.That(sarc.GetFile("Layout/B.sblarc"), Is.EqualTo(big));
            Assert.That(sarc.GetFile("Pack/A.bin"), Is.EqualTo(a));
            Assert.That(sarc.GetFile("C.bntx"), Is.EqualTo(c));
            sarc.TryGetFile("C.bntx", out int cMoved, out _);
            Assert.That((cMoved - cOffset) % 0x2000, Is.Zero, "later files move by whole 0x2000 steps");
            Assert.That(BitConverter.ToInt32(sarc.Data, 8), Is.EqualTo(sarc.Data.Length));

            Sarc reread = new(sarc.Data);
            Assert.That(reread.GetFile("C.bntx"), Is.EqualTo(c));
        }

        [Test]
        public void BlockLinearRoundTrip()
        {
            Random random = new(2);

            foreach ((int w, int h, int bpe, int bhLog2) in new[] { (48, 48, 1, 3), (32, 256, 8, 4), (12, 12, 8, 1), (5, 5, 8, 0) })
            {
                byte[] linear = new byte[w * h * bpe];
                random.NextBytes(linear);
                int gobRows = 8 << bhLog2;
                byte[] swizzled = new byte[((w * bpe + 63) / 64) * 64 * ((h + gobRows - 1) / gobRows * gobRows)];
                BlockLinear.Swizzle(linear, swizzled, w, h, bpe, bhLog2);
                Assert.That(BlockLinear.Deswizzle(swizzled, w, h, bpe, bhLog2), Is.EqualTo(linear));
            }

            // GOB layout spot check (Tegra X1 TRM): byte (x=16, y=1) of the first GOB is at offset 0x30.
            Assert.That(BlockLinear.Address(16, 1, 64, 0), Is.EqualTo(0x30));
            Assert.That(BlockLinear.Address(32, 0, 64, 0), Is.EqualTo(0x100));
        }

        [Test]
        public void Bc4EncodesMasksAccurately()
        {
            byte[] image = new byte[16 * 16];

            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    image[y * 16 + x] = (byte)(x < 6 ? 0 : x > 9 ? 255 : (x - 5) * 50);
                }
            }

            byte[] blocks = new byte[16 * 8];
            Bc4.EncodeChanged(blocks, image, new byte[image.Length], 16, 16);
            byte[] decoded = Bc4.Decode(blocks, 16, 16);

            for (int i = 0; i < image.Length; i++)
            {
                Assert.That(Math.Abs(decoded[i] - image[i]), Is.LessThanOrEqualTo(12), $"pixel {i}");

                if (image[i] is 0 or 255)
                {
                    Assert.That(decoded[i], Is.EqualTo(image[i]), "pure black/white must stay exact");
                }
            }

            // Unchanged blocks are not touched.
            byte[] marker = [.. Enumerable.Repeat((byte)0xAB, 16 * 8)];
            Bc4.EncodeChanged(marker, image, image, 16, 16);
            Assert.That(marker.All(v => v == 0xAB));
        }
    }
}
