using Ryujinx.Common.BotwPc;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.HLE.BotwPc.Prompts
{
    /// <summary>
    /// Generates RomFS replacement files that make BotW's button prompts show the physical Xbox buttons of the active
    /// mapping. Sources are the user's own game files (after RomFS mods); output is cached under &lt;data&gt;/botw/prompts
    /// and never contains anything but the patched copies of those files.
    ///
    /// Where BotW draws button prompts (v1.6.0, verified by decoding the files):
    /// - Text (dialogue, tips, tutorials, menus, context actions): private-use glyphs U+E040..E08F of
    ///   Font/Font_*.sbfarc → External_00.bffnt (one 128x1024 BC4 sheet, stored bottom-up).
    /// - HUD action icons and the D-pad shortcut: textures in Pack/Bootup.pack → Layout/Common.sblarc → timg/__Combined.bntx.
    /// </summary>
    public static class PromptOverlay
    {
        /// <summary>Bump when the art or the patching changes, so cached overlays are rebuilt.</summary>
        public const int GeneratorVersion = 2;

        public const string BootupPack = "/Pack/Bootup.pack";
        private const string CommonArchive = "Layout/Common.sblarc";
        private const string CombinedTextures = "timg/__Combined.bntx";
        private const string ExternalFont = "External_00.bffnt";

        public static readonly string[] FontArchives =
            ["/Font/Font_US.sbfarc", "/Font/Font_EU.sbfarc", "/Font/Font_JP.sbfarc", "/Font/Font_CN.sbfarc", "/Font/Font_KR.sbfarc", "/Font/Font_TW.sbfarc"];

        /// <summary>A file the overlay replaces and where its source came from.</summary>
        public sealed record Source(string RomfsPath, byte[] Data, string Origin, bool IsBase);

        public sealed class Result
        {
            /// <summary>RomFS path → generated file on the host.</summary>
            public Dictionary<string, string> Files { get; } = [];
            public List<string> Notes { get; } = [];

            /// <summary>Directory holding the generated files at their RomFS paths.</summary>
            public string RomfsDirectory { get; set; }
        }

        /// <param name="readEffective">Returns the file as the game would see it after RomFS mods (null if absent).</param>
        /// <param name="readBase">Returns the unmodified base game file (null if absent).</param>
        public static Result Build(PromptMapping mapping, PromptPriority priority, Func<string, Source> readEffective, Func<string, byte[]> readBase, string cacheRoot)
        {
            Result result = new();
            List<Source> sources = [.. FontArchives.Append(BootupPack).Select(readEffective).Where(s => s != null)];

            string key = CacheKey(mapping, priority, sources);
            string dir = Path.Combine(cacheRoot, key);
            result.RomfsDirectory = Path.Combine(dir, "romfs");

            if (File.Exists(Path.Combine(dir, "overlay.txt")))
            {
                foreach (string line in File.ReadAllLines(Path.Combine(dir, "overlay.txt")))
                {
                    if (line.StartsWith('/'))
                    {
                        result.Files[line] = Path.Combine(dir, "romfs" + line);
                    }
                    else
                    {
                        result.Notes.Add(line);
                    }
                }

                result.Notes.Insert(0, $"cached overlay {key}");

                return result;
            }

            string temp = dir + ".tmp";

            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }

            bool failed = false;

            foreach (Source source in sources)
            {
                byte[] patched = null;

                try
                {
                    patched = source.RomfsPath == BootupPack
                        ? PatchBootupPack(source, mapping, priority, readBase, result.Notes)
                        : PatchFontArchive(source, mapping, priority, readBase, result.Notes);
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException or FileNotFoundException or IndexOutOfRangeException or ArgumentException)
                {
                    result.Notes.Add($"{source.RomfsPath} ({source.Origin}): not patched, unexpected format: {e.Message}");
                    failed = true;
                }

                if (patched != null)
                {
                    string target = Path.Combine(temp, "romfs" + source.RomfsPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, patched);
                    result.Files[source.RomfsPath] = Path.Combine(dir, "romfs" + source.RomfsPath);
                }
            }

            Directory.CreateDirectory(temp);

            // overlay.txt marks a complete cache entry; a partial result is used this time but rebuilt next start.
            if (!failed)
            {
                File.WriteAllLines(Path.Combine(temp, "overlay.txt"), result.Notes.Concat(result.Files.Keys));
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.Move(temp, dir);
            Prune(cacheRoot, dir);

            return result;
        }

        private static string CacheKey(PromptMapping mapping, PromptPriority priority, List<Source> sources)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"v{GeneratorVersion};{priority};{mapping.Key}"));

            foreach (Source s in sources)
            {
                hash.AppendData(Encoding.UTF8.GetBytes($";{s.RomfsPath};{s.IsBase};"));
                hash.AppendData(SHA256.HashData(s.Data));
            }

            return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
        }

        /// <summary>
        /// Marks every cached overlay incomplete so the next game start regenerates it. Only the marker is removed: a
        /// running game keeps reading its files. Returns the number of entries invalidated.
        /// </summary>
        public static int Invalidate(string cacheRoot)
        {
            int count = 0;

            if (Directory.Exists(cacheRoot))
            {
                foreach (string marker in Directory.GetFiles(cacheRoot, "overlay.txt", SearchOption.AllDirectories))
                {
                    File.Delete(marker);
                    count++;
                }
            }

            return count;
        }

        /// <summary>Keeps the three most recent overlays.</summary>
        private static void Prune(string cacheRoot, string keep)
        {
            try
            {
                foreach (DirectoryInfo old in new DirectoryInfo(cacheRoot).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(3))
                {
                    if (old.FullName != keep)
                    {
                        old.Delete(true);
                    }
                }
            }
            catch (IOException)
            {
                // Cache cleanup is best effort.
            }
        }

        /// <summary>True when a mod's copy of a texture/glyph area differs from the base game's (the mod draws its own prompts).</summary>
        private static bool ModOwns(Source source, PromptPriority priority, bool differs, string what, List<string> notes)
        {
            if (source.IsBase || !differs)
            {
                return false;
            }

            if (priority == PromptPriority.Mod)
            {
                notes.Add($"conflict: {source.Origin} replaces {what}; kept the mod's version (prompt priority: Mod)");
                return true;
            }

            notes.Add($"conflict: {source.Origin} replaces {what}; drawn over by BOTW-PC prompts (prompt priority: BuiltIn)");
            return false;
        }

        // ---------------------------------------------------------------- fonts

        private static byte[] PatchFontArchive(Source source, PromptMapping mapping, PromptPriority priority, Func<string, byte[]> readBase, List<string> notes)
        {
            Sarc archive = new(Yaz0.Decompress(source.Data));

            if (!archive.TryGetFile(ExternalFont, out int offset, out int length))
            {
                return null;
            }

            byte[] baseFont = null;

            if (!source.IsBase && readBase(source.RomfsPath) is byte[] baseData)
            {
                Sarc baseArchive = new(Yaz0.Decompress(baseData));
                baseFont = baseArchive.TryGetFile(ExternalFont, out _, out _) ? baseArchive.GetFile(ExternalFont) : null;
            }

            byte[] font = archive.Data.AsSpan(offset, length).ToArray();
            bool differs = baseFont != null && !baseFont.AsSpan().SequenceEqual(font);

            if (ModOwns(source, priority, differs, $"{source.RomfsPath}/{ExternalFont} (button glyph font)", notes))
            {
                return null;
            }

            int changed = PatchExternalFont(font, mapping);
            archive.ReplaceFile(ExternalFont, font);
            notes.Add($"{source.RomfsPath} ({source.Origin}): {changed} prompt glyphs redrawn");

            return Yaz0.Compress(archive.Data);
        }

        /// <summary>BotW External_00 glyph code points (identified from the v1.6.0 font sheet).</summary>
        public static class Glyphs
        {
            public const int A = 0xE040, B = 0xE041, X = 0xE042, Y = 0xE043, L = 0xE044, R = 0xE045, ZL = 0xE046, ZR = 0xE047;
            public const int Dpad = 0xE049, Plus = 0xE04B, Minus = 0xE04C, LeftStickPress = 0xE056, RightStickPress = 0xE057;
            public const int DpadUp = 0xE060, DpadDown = 0xE061, DpadLeft = 0xE062, DpadRight = 0xE063, DpadUpDown = 0xE064, DpadLeftRight = 0xE065;
        }

        /// <summary>What to draw for each prompt code point under <paramref name="mapping"/>; null keeps the game's glyph.</summary>
        public static Dictionary<int, Func<int, int, byte[]>> FontPlan(PromptMapping mapping)
        {
            Func<int, int, byte[]> Button(SwitchButton b) => (w, h) => GlyphArt.Draw(mapping.Get(b), w, h);

            bool dpadIdentity = mapping.Get(SwitchButton.DpadUp) == GamepadInputId.DpadUp && mapping.Get(SwitchButton.DpadDown) == GamepadInputId.DpadDown &&
                                mapping.Get(SwitchButton.DpadLeft) == GamepadInputId.DpadLeft && mapping.Get(SwitchButton.DpadRight) == GamepadInputId.DpadRight;

            Func<int, int, byte[]> Dpad(GlyphArt.Dpad variant) =>
                dpadIdentity ? (w, h) => GlyphArt.DrawDpad(variant, w, h) : (w, h) => GlyphArt.Draw(GamepadInputId.Unbound, w, h);

            Dictionary<int, Func<int, int, byte[]>> plan = new()
            {
                [Glyphs.A] = Button(SwitchButton.A),
                [Glyphs.B] = Button(SwitchButton.B),
                [Glyphs.X] = Button(SwitchButton.X),
                [Glyphs.Y] = Button(SwitchButton.Y),
                [Glyphs.L] = Button(SwitchButton.L),
                [Glyphs.R] = Button(SwitchButton.R),
                [Glyphs.ZL] = Button(SwitchButton.ZL),
                [Glyphs.ZR] = Button(SwitchButton.ZR),
                [Glyphs.Plus] = Button(SwitchButton.Plus),
                [Glyphs.Minus] = Button(SwitchButton.Minus),
                [Glyphs.DpadUp] = Button(SwitchButton.DpadUp),
                [Glyphs.DpadDown] = Button(SwitchButton.DpadDown),
                [Glyphs.DpadLeft] = Button(SwitchButton.DpadLeft),
                [Glyphs.DpadRight] = Button(SwitchButton.DpadRight),
                [Glyphs.Dpad] = Dpad(GlyphArt.Dpad.All),
                [Glyphs.DpadUpDown] = Dpad(GlyphArt.Dpad.UpDown),
                [Glyphs.DpadLeftRight] = Dpad(GlyphArt.Dpad.LeftRight),
            };

            // The game's stick-press glyphs already read as "press the left/right stick"; only redraw them when remapped.
            if (mapping.Get(SwitchButton.LeftStick) != GamepadInputId.LeftStick)
            {
                plan[Glyphs.LeftStickPress] = Button(SwitchButton.LeftStick);
            }

            if (mapping.Get(SwitchButton.RightStick) != GamepadInputId.RightStick)
            {
                plan[Glyphs.RightStickPress] = Button(SwitchButton.RightStick);
            }

            return plan;
        }

        /// <summary>Redraws the prompt glyphs of an External_00.bffnt in place; returns the number of glyphs changed.</summary>
        public static int PatchExternalFont(byte[] font, PromptMapping mapping)
        {
            FontSheet sheet = FontSheet.Open(font);
            int changed = 0;

            foreach ((int codePoint, Func<int, int, byte[]> draw) in FontPlan(mapping))
            {
                if (sheet.Redraw(codePoint, draw))
                {
                    changed++;
                }
            }

            sheet.Commit();

            return changed;
        }

        /// <summary>The glyph sheet of a Switch BFFNT (v4, single BC4 sheet embedded as BNTX).</summary>
        public sealed class FontSheet
        {
            private readonly byte[] _font;
            private readonly int _bntxOffset;
            private readonly BntxTexture _texture;
            private readonly int _cellWidth, _cellHeight, _cellsPerRow;
            private readonly Dictionary<int, int> _glyphIndex;
            private readonly byte[] _original;

            /// <summary>Decoded sheet, one byte per pixel, rows as stored (bottom-up: row 0 is the bottom of the sheet).</summary>
            public byte[] Pixels { get; }
            public int Width => _texture.Width;
            public int Height => _texture.Height;

            private FontSheet(byte[] font, int bntxOffset, BntxTexture texture, int cellWidth, int cellHeight, int cellsPerRow, Dictionary<int, int> glyphIndex)
            {
                _font = font;
                _bntxOffset = bntxOffset;
                _texture = texture;
                _cellWidth = cellWidth;
                _cellHeight = cellHeight;
                _cellsPerRow = cellsPerRow;
                _glyphIndex = glyphIndex;
                Pixels = Bntx.DecodeAlpha(font.AsSpan(bntxOffset), texture);
                _original = (byte[])Pixels.Clone();
            }

            public static FontSheet Open(byte[] font)
            {
                ReadOnlySpan<byte> f = font;

                if (!f[..4].SequenceEqual("FFNT"u8) || f[4] != 0xFF || !f.Slice(0x14, 4).SequenceEqual("FINF"u8))
                {
                    throw new InvalidDataException("not a little-endian BFFNT");
                }

                // FINF: ... +0x14 TGLP, +0x18 CWDH, +0x1C CMAP offsets (each points 8 bytes into its section).
                int tglp = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[0x28..]) - 8;
                int cmap = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[0x30..]) - 8;

                if (!f.Slice(tglp, 4).SequenceEqual("TGLP"u8))
                {
                    throw new InvalidDataException("BFFNT without TGLP");
                }

                int cellWidth = f[tglp + 8], cellHeight = f[tglp + 9], sheetCount = f[tglp + 10];
                int cellsPerRow = BinaryPrimitives.ReadUInt16LittleEndian(f[(tglp + 0x14)..]);
                int sheetData = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[(tglp + 0x1C)..]);

                if (sheetCount != 1)
                {
                    throw new NotSupportedException($"{sheetCount} glyph sheets");
                }

                BntxTexture[] textures = Bntx.Parse(f[sheetData..]);

                if (textures.Length != 1 || textures[0].Format != Bntx.FormatBc4Unorm)
                {
                    throw new NotSupportedException("glyph sheet is not a single BC4 texture");
                }

                return new FontSheet(font, sheetData, textures[0], cellWidth, cellHeight, cellsPerRow, ReadCmap(f, cmap));
            }

            private static Dictionary<int, int> ReadCmap(ReadOnlySpan<byte> f, int cmap)
            {
                Dictionary<int, int> map = [];

                while (cmap > 0)
                {
                    if (!f.Slice(cmap, 4).SequenceEqual("CMAP"u8))
                    {
                        throw new InvalidDataException("bad CMAP chain");
                    }

                    int begin = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[(cmap + 8)..]);
                    int end = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[(cmap + 12)..]);
                    int method = BinaryPrimitives.ReadUInt16LittleEndian(f[(cmap + 16)..]);
                    int next = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[(cmap + 20)..]);
                    ReadOnlySpan<byte> d = f[(cmap + 24)..];

                    switch (method)
                    {
                        case 0:
                            int first = BinaryPrimitives.ReadUInt16LittleEndian(d);
                            for (int c = begin; c <= end; c++)
                            {
                                map[c] = first + c - begin;
                            }
                            break;
                        case 1:
                            for (int c = begin; c <= end; c++)
                            {
                                int index = BinaryPrimitives.ReadUInt16LittleEndian(d[(2 * (c - begin))..]);
                                if (index != 0xFFFF)
                                {
                                    map[c] = index;
                                }
                            }
                            break;
                        case 2:
                            int count = BinaryPrimitives.ReadUInt16LittleEndian(d);
                            for (int i = 0; i < count; i++)
                            {
                                map[(int)BinaryPrimitives.ReadUInt32LittleEndian(d[(4 + 8 * i)..])] = BinaryPrimitives.ReadUInt16LittleEndian(d[(8 + 8 * i)..]);
                            }
                            break;
                        default:
                            throw new NotSupportedException($"CMAP method {method}");
                    }

                    cmap = next > 0 ? next - 8 : 0;
                }

                return map;
            }

            /// <summary>Cell of a glyph in top-down sheet coordinates, including the 1-pixel gutter on its right/bottom.</summary>
            public bool TryGetCell(int codePoint, out int x, out int y, out int width, out int height)
            {
                x = y = width = height = 0;

                if (!_glyphIndex.TryGetValue(codePoint, out int index))
                {
                    return false;
                }

                x = index % _cellsPerRow * (_cellWidth + 1);
                y = index / _cellsPerRow * (_cellHeight + 1);
                width = _cellWidth + 1;
                height = _cellHeight + 1;

                return x + width <= Width && y + height <= Height;
            }

            /// <summary>Top-down pixel accessors (the sheet is stored bottom-up).</summary>
            public byte Get(int x, int y) => Pixels[(Height - 1 - y) * Width + x];
            private void Set(int x, int y, byte v) => Pixels[(Height - 1 - y) * Width + x] = v;

            /// <summary>Ink bounding box of a glyph (top-down coordinates).</summary>
            public bool TryGetInk(int codePoint, out int x0, out int y0, out int x1, out int y1)
            {
                x0 = y0 = int.MaxValue;
                x1 = y1 = -1;

                if (!TryGetCell(codePoint, out int cx, out int cy, out int cw, out int ch))
                {
                    return false;
                }

                for (int y = cy; y < cy + ch; y++)
                {
                    for (int x = cx; x < cx + cw; x++)
                    {
                        if (Get(x, y) > 32)
                        {
                            x0 = Math.Min(x0, x);
                            y0 = Math.Min(y0, y);
                            x1 = Math.Max(x1, x);
                            y1 = Math.Max(y1, y);
                        }
                    }
                }

                return x1 >= 0;
            }

            /// <summary>Clears the glyph's cell and draws <paramref name="draw"/> over the original ink box.</summary>
            public bool Redraw(int codePoint, Func<int, int, byte[]> draw)
            {
                if (!TryGetInk(codePoint, out int x0, out int y0, out int x1, out int y1) || !TryGetCell(codePoint, out int cx, out int cy, out int cw, out int ch))
                {
                    return false;
                }

                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                byte[] art = draw(w, h);

                for (int y = cy; y < cy + ch; y++)
                {
                    for (int x = cx; x < cx + cw; x++)
                    {
                        bool inArt = x >= x0 && x <= x1 && y >= y0 && y <= y1;
                        Set(x, y, inArt ? art[(y - y0) * w + (x - x0)] : (byte)0);
                    }
                }

                return true;
            }

            public void Commit() => Bntx.EncodeAlpha(_font.AsSpan(_bntxOffset), _texture, Pixels, _original);
        }

        // ---------------------------------------------------------------- layout textures

        /// <summary>HUD textures in Common.sblarc and what they show (null = keep).</summary>
        public static Dictionary<string, Func<int, int, byte[]>> TexturePlan(PromptMapping mapping)
        {
            Func<int, int, byte[]> Button(SwitchButton b) => (w, h) => GlyphArt.Draw(mapping.Get(b), w, h);

            bool dpadIdentity = mapping.Get(SwitchButton.DpadUp) == GamepadInputId.DpadUp && mapping.Get(SwitchButton.DpadDown) == GamepadInputId.DpadDown &&
                                mapping.Get(SwitchButton.DpadLeft) == GamepadInputId.DpadLeft && mapping.Get(SwitchButton.DpadRight) == GamepadInputId.DpadRight;

            Func<int, int, byte[]> dpad = dpadIdentity
                ? (w, h) => GlyphArt.DrawDpad(GlyphArt.Dpad.All, w, h)
                : (w, h) => GlyphArt.Draw(GamepadInputId.Unbound, w, h);

            return new Dictionary<string, Func<int, int, byte[]>>
            {
                // HUD "do command" icons (PaDoCommandA/B/X/Y/L and the NN key variants).
                ["Nt_KeyTexA_00^d"] = Button(SwitchButton.A),
                ["Nt_KeyTexB_00^d"] = Button(SwitchButton.B),
                ["Nt_KeyTexX_00^d"] = Button(SwitchButton.X),
                ["Nt_KeyTexY_00^d"] = Button(SwitchButton.Y),
                ["Nt_KeyTexL_00^d"] = Button(SwitchButton.L),
                ["Nt_KeyTexZL_00^d"] = Button(SwitchButton.ZL),
                // D-pad shortcut icon (PaShortCutIconCrossNN) and its soft shadow/glow.
                ["ButtonIconL46NN_00^d"] = dpad,
                ["ButtonIconL46NN_01^s"] = dpad,
                ["ButtonIconL46ShCu_00^s"] = dpad,
                // +/- on the loading-tip controller pictures (PaMessageTipsDrcImageNN).
                ["Nt_ControllerNNPlus_00^s"] = Button(SwitchButton.Plus),
                ["Nt_ControllerNNMinus_00^s"] = Button(SwitchButton.Minus),
            };
        }

        private static byte[] PatchBootupPack(Source source, PromptMapping mapping, PromptPriority priority, Func<string, byte[]> readBase, List<string> notes)
        {
            Sarc pack = new(source.Data);
            Sarc common = new(Yaz0.Decompress(pack.GetFile(CommonArchive)));
            int offset = common.TryGetFile(CombinedTextures, out int o, out _) ? o : throw new FileNotFoundException(CombinedTextures);
            Span<byte> bntx = common.Data.AsSpan(offset);
            Dictionary<string, BntxTexture> textures = Bntx.Parse(bntx).ToDictionary(t => t.Name);

            byte[] baseBntx = null;
            Sarc baseCommon = null;

            if (!source.IsBase && readBase(BootupPack) is byte[] baseData)
            {
                baseCommon = new(Yaz0.Decompress(new Sarc(baseData).GetFile(CommonArchive)));
                baseBntx = baseCommon.GetFile(CombinedTextures);
            }

            Dictionary<string, BntxTexture> baseTextures = baseBntx == null ? null : Bntx.Parse(baseBntx).ToDictionary(t => t.Name);
            int changed = 0;

            foreach ((string name, Func<int, int, byte[]> draw) in TexturePlan(mapping))
            {
                if (!textures.TryGetValue(name, out BntxTexture t) || t.Format is not (Bntx.FormatR8Unorm or Bntx.FormatBc4Unorm))
                {
                    notes.Add($"{CommonArchive}: texture {name} missing or in an unexpected format; left unchanged");
                    continue;
                }

                bool differs = baseTextures != null && (!baseTextures.TryGetValue(name, out BntxTexture bt) ||
                    !baseBntx.AsSpan(bt.DataOffset, bt.DataSize).SequenceEqual(bntx.Slice(t.DataOffset, t.DataSize)));

                if (ModOwns(source, priority, differs, $"{CommonArchive}/{name}", notes))
                {
                    continue;
                }

                byte[] original = Bntx.DecodeAlpha(bntx, t);
                Bntx.EncodeAlpha(bntx, t, RedrawTexture(original, t.Width, t.Height, draw), original);
                changed++;
            }

            foreach (char letter in "ABXY")
            {
                string layout = $"blyt/PaDoCommand{letter}_00.bflyt";

                if (!common.TryGetFile(layout, out int lo, out int ll))
                {
                    continue;
                }

                Span<byte> data = common.Data.AsSpan(lo, ll);
                byte[] baseLayout = baseCommon != null && baseCommon.TryGetFile(layout, out _, out _) ? baseCommon.GetFile(layout) : null;

                if (!ModOwns(source, priority, baseLayout != null && !data.SequenceEqual(baseLayout), $"{CommonArchive}/{layout}", notes))
                {
                    changed += PatchDiamondLabels(data, mapping);
                }
            }

            if (changed == 0)
            {
                return null;
            }

            pack.ReplaceFile(CommonArchive, Yaz0.Compress(common.Data));
            notes.Add($"{BootupPack} ({source.Origin}): {changed} HUD prompt textures and labels redrawn");

            return pack.Data;
        }

        /// <summary>
        /// The HUD action diamond (PaDoCommandA/B/X/Y_00.bflyt) names the three idle face buttons with one-letter text
        /// panes T_&lt;letter&gt;_0n in NormalS_00.bffnt. Rewrites each letter, in place, to the Xbox name of the input bound
        /// to that Switch button ("?" when that name is not one letter: the text buffer holds one character).
        /// Returns the number of panes changed.
        /// </summary>
        public static int PatchDiamondLabels(Span<byte> layout, PromptMapping mapping)
        {
            if (!layout[..4].SequenceEqual("FLYT"u8) || layout[4] != 0xFF)
            {
                throw new InvalidDataException("not a little-endian BFLYT");
            }

            int changed = 0;
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(layout[0x10..]);

            for (int i = 0, o = BinaryPrimitives.ReadUInt16LittleEndian(layout[0x06..]); i < sections; i++)
            {
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(layout[(o + 4)..]);
                Span<byte> section = layout.Slice(o, size);
                o += size;

                // txt1: pane name at 0x0C, text byte count at 0x56, text offset at 0x64 (UTF-16 with terminator).
                if (!section[..4].SequenceEqual("txt1"u8) || section[0x0C] != 'T' || section[0x0D] != '_' || section[0x0F] != '_' ||
                    BinaryPrimitives.ReadUInt16LittleEndian(section[0x56..]) != 4)
                {
                    continue;
                }

                char letter = (char)section[0x0E];
                Span<byte> text = section[(int)BinaryPrimitives.ReadUInt32LittleEndian(section[0x64..])..];

                if ("ABXY".IndexOf(letter) < 0 || BinaryPrimitives.ReadUInt16LittleEndian(text) != letter)
                {
                    continue;
                }

                string label = ButtonPrompts.XboxLabel(mapping.Get(Enum.Parse<SwitchButton>(letter.ToString())));
                BinaryPrimitives.WriteUInt16LittleEndian(text, label.Length == 1 ? label[0] : '?');
                changed++;
            }

            return changed;
        }

        /// <summary>
        /// Redraws a layout texture (stored top-down): new art fills the original ink box; soft textures (shadows/glows,
        /// peak below 250) get a blurred copy of the art scaled to the original peak.
        /// </summary>
        public static byte[] RedrawTexture(byte[] original, int width, int height, Func<int, int, byte[]> draw)
        {
            int x0 = width, y0 = height, x1 = -1, y1 = -1, peak = 0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int v = original[y * width + x];
                    peak = Math.Max(peak, v);
                }
            }

            int threshold = Math.Max(8, peak / 4);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (original[y * width + x] > threshold)
                    {
                        x0 = Math.Min(x0, x);
                        y0 = Math.Min(y0, y);
                        x1 = Math.Max(x1, x);
                        y1 = Math.Max(y1, y);
                    }
                }
            }

            byte[] image = new byte[width * height];

            if (x1 < 0)
            {
                return original;
            }

            bool soft = peak < 250;

            if (soft)
            {
                // Glow/shadow: draw the shape solid (a filled silhouette reads better than the cut-out label when blurred).
                int inset = Math.Max(1, (x1 - x0) / 8);
                x0 += inset;
                y0 += inset;
                x1 -= inset;
                y1 -= inset;
            }

            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            byte[] art = draw(w, h);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    image[(y0 + y) * width + x0 + x] = art[y * w + x];
                }
            }

            if (soft)
            {
                image = BoxBlur(BoxBlur(image, width, height, Math.Max(1, w / 10)), width, height, Math.Max(1, w / 10));
                int max = image.Max();

                for (int i = 0; i < image.Length && max > 0; i++)
                {
                    image[i] = (byte)(image[i] * peak / max);
                }
            }

            return image;
        }

        private static byte[] BoxBlur(byte[] src, int width, int height, int radius)
        {
            byte[] dst = new byte[src.Length];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sum = 0, n = 0;

                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int sx = x + dx, sy = y + dy;

                            if (sx >= 0 && sy >= 0 && sx < width && sy < height)
                            {
                                sum += src[sy * width + sx];
                            }

                            n++;
                        }
                    }

                    dst[y * width + x] = (byte)(sum / n);
                }
            }

            return dst;
        }

        /// <summary>Logs the outcome in one place (used by the mod loader).</summary>
        public static void Log(Result result, PromptMapping mapping)
        {
            Logger.Info?.Print(LogClass.ModLoader, $"BOTW-PC prompts: {ButtonPrompts.Describe(mapping)}");

            foreach (string note in result.Notes)
            {
                (note.StartsWith("conflict") ? Logger.Warning : Logger.Info)?.Print(LogClass.ModLoader, $"BOTW-PC prompts: {note}");
            }
        }
    }
}
