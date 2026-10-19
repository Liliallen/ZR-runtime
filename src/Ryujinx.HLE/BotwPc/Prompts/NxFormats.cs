using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Ryujinx.HLE.BotwPc.Prompts
{
    /// <summary>Yaz0 (SZS) compression as used by BotW's .s* files.</summary>
    public static class Yaz0
    {
        public static bool IsCompressed(ReadOnlySpan<byte> data) => data.Length >= 16 && data[..4].SequenceEqual("Yaz0"u8);

        public static byte[] Decompress(ReadOnlySpan<byte> src)
        {
            if (!IsCompressed(src))
            {
                throw new InvalidDataException("not Yaz0 data");
            }

            byte[] dst = new byte[BinaryPrimitives.ReadUInt32BigEndian(src[4..])];
            int s = 16, d = 0;

            while (d < dst.Length)
            {
                byte code = src[s++];

                for (int bit = 0; bit < 8 && d < dst.Length; bit++, code <<= 1)
                {
                    if ((code & 0x80) != 0)
                    {
                        dst[d++] = src[s++];
                        continue;
                    }

                    int b1 = src[s], b2 = src[s + 1];
                    s += 2;
                    int dist = ((b1 & 0xF) << 8 | b2) + 1;
                    int length = b1 >> 4;
                    length = length == 0 ? src[s++] + 0x12 : length + 2;

                    if (dist > d || d + length > dst.Length)
                    {
                        throw new InvalidDataException("corrupt Yaz0 data");
                    }

                    for (int i = 0; i < length; i++, d++)
                    {
                        dst[d] = dst[d - dist];
                    }
                }
            }

            return dst;
        }

        /// <summary>Greedy LZ with hash chains over the 4 KiB Yaz0 window.</summary>
        public static byte[] Compress(ReadOnlySpan<byte> src)
        {
            const int Window = 0x1000, MaxLength = 0x111, MaxChain = 48, HashBits = 15;
            int[] head = new int[1 << HashBits];
            int[] prev = new int[Window];
            Array.Fill(head, -1);

            using MemoryStream output = new(src.Length / 2 + 64);
            Span<byte> header = stackalloc byte[16];
            "Yaz0"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)src.Length);
            output.Write(header);

            Span<byte> group = stackalloc byte[1 + 8 * 3];
            int pos = 0;

            static int Hash(ReadOnlySpan<byte> s, int p) => (s[p] << 10 ^ s[p + 1] << 5 ^ s[p + 2]) & ((1 << HashBits) - 1);

            void Insert(ReadOnlySpan<byte> s, int p)
            {
                if (p + 2 < s.Length)
                {
                    int h = Hash(s, p);
                    prev[p & (Window - 1)] = head[h];
                    head[h] = p;
                }
            }

            while (pos < src.Length)
            {
                int groupLength = 1;
                group[0] = 0;

                for (int bit = 0; bit < 8 && pos < src.Length; bit++)
                {
                    int bestLength = 0, bestDist = 0;

                    if (pos + 2 < src.Length)
                    {
                        int maxLength = Math.Min(MaxLength, src.Length - pos);
                        int candidate = head[Hash(src, pos)];

                        for (int chain = 0; candidate >= 0 && pos - candidate <= Window && chain < MaxChain; chain++)
                        {
                            if (src[candidate + bestLength] == src[pos + bestLength])
                            {
                                int length = 0;

                                while (length < maxLength && src[candidate + length] == src[pos + length])
                                {
                                    length++;
                                }

                                if (length > bestLength)
                                {
                                    bestLength = length;
                                    bestDist = pos - candidate;

                                    if (length == maxLength)
                                    {
                                        break;
                                    }
                                }
                            }

                            int next = prev[candidate & (Window - 1)];

                            if (next >= candidate)
                            {
                                break; // Slot was overwritten by a newer position: chain left the window.
                            }

                            candidate = next;
                        }
                    }

                    if (bestLength >= 3)
                    {
                        int dist = bestDist - 1;

                        if (bestLength >= 0x12)
                        {
                            group[groupLength++] = (byte)(dist >> 8);
                            group[groupLength++] = (byte)dist;
                            group[groupLength++] = (byte)(bestLength - 0x12);
                        }
                        else
                        {
                            group[groupLength++] = (byte)((bestLength - 2) << 4 | dist >> 8);
                            group[groupLength++] = (byte)dist;
                        }

                        for (int i = 0; i < bestLength; i++)
                        {
                            Insert(src, pos++);
                        }
                    }
                    else
                    {
                        group[0] |= (byte)(0x80 >> bit);
                        group[groupLength++] = src[pos];
                        Insert(src, pos++);
                    }
                }

                output.Write(group[..groupLength]);
            }

            return output.ToArray();
        }
    }

    /// <summary>Minimal SARC reader/patcher (BotW packs and archives).</summary>
    public sealed class Sarc
    {
        public byte[] Data { get; private set; }
        private readonly bool _bigEndian;
        private readonly int _nodeTable, _nodeCount, _nameTable;

        public Sarc(byte[] data)
        {
            if (data.Length < 0x20 || !data.AsSpan(0, 4).SequenceEqual("SARC"u8))
            {
                throw new InvalidDataException("not a SARC archive");
            }

            Data = data;
            _bigEndian = data[6] == 0xFE;
            int headerSize = U16(4);

            if (!data.AsSpan(headerSize, 4).SequenceEqual("SFAT"u8))
            {
                throw new InvalidDataException("SARC without SFAT");
            }

            _nodeCount = U16(headerSize + 6);
            _nodeTable = headerSize + 12;
            int sfnt = _nodeTable + 16 * _nodeCount;

            if (!data.AsSpan(sfnt, 4).SequenceEqual("SFNT"u8))
            {
                throw new InvalidDataException("SARC without SFNT");
            }

            _nameTable = sfnt + 8;
        }

        private int DataOffset => (int)U32(0xC);
        private ushort U16(int o) => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(o));
        private uint U32(int o) => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(o));

        private void W32(int o, uint v)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(Data.AsSpan(o), v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(o), v);
            }
        }

        private string NodeName(int node)
        {
            uint attr = U32(_nodeTable + 16 * node + 4);

            if (attr >> 24 == 0)
            {
                return null;
            }

            int start = _nameTable + (int)(attr & 0xFFFF) * 4;
            int end = Array.IndexOf(Data, (byte)0, start);

            return Encoding.UTF8.GetString(Data, start, end - start);
        }

        private int FindNode(string name)
        {
            for (int i = 0; i < _nodeCount; i++)
            {
                if (NodeName(i) == name)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Absolute offset and length of a file in <see cref="Data"/>.</summary>
        public bool TryGetFile(string name, out int offset, out int length)
        {
            int node = FindNode(name);
            offset = length = 0;

            if (node < 0)
            {
                return false;
            }

            uint begin = U32(_nodeTable + 16 * node + 8), end = U32(_nodeTable + 16 * node + 12);
            offset = DataOffset + (int)begin;
            length = (int)(end - begin);

            return true;
        }

        public byte[] GetFile(string name) =>
            TryGetFile(name, out int offset, out int length) ? Data.AsSpan(offset, length).ToArray() : throw new FileNotFoundException(name);

        /// <summary>
        /// Replaces a file. A shorter or equal file is written in place; a longer one moves every later file by a
        /// multiple of 0x2000 bytes, so their alignments are preserved.
        /// </summary>
        public void ReplaceFile(string name, ReadOnlySpan<byte> contents)
        {
            int node = FindNode(name);

            if (node < 0)
            {
                throw new FileNotFoundException(name);
            }

            int entry = _nodeTable + 16 * node;
            uint begin = U32(entry + 8), end = U32(entry + 12);
            int oldLength = (int)(end - begin);
            int start = DataOffset + (int)begin;

            if (contents.Length <= oldLength)
            {
                contents.CopyTo(Data.AsSpan(start));
                Data.AsSpan(start + contents.Length, oldLength - contents.Length).Clear();
                W32(entry + 12, begin + (uint)contents.Length);

                return;
            }

            int shift = (contents.Length - oldLength + 0x1FFF) & ~0x1FFF;
            byte[] grown = new byte[Data.Length + shift];
            Data.AsSpan(0, start).CopyTo(grown);
            contents.CopyTo(grown.AsSpan(start));
            Data.AsSpan(start + oldLength).CopyTo(grown.AsSpan(start + oldLength + shift));
            Data = grown;

            for (int i = 0; i < _nodeCount; i++)
            {
                int e = _nodeTable + 16 * i;

                if (i != node && U32(e + 8) >= end)
                {
                    W32(e + 8, U32(e + 8) + (uint)shift);
                    W32(e + 12, U32(e + 12) + (uint)shift);
                }
            }

            W32(entry + 12, begin + (uint)contents.Length);
            W32(8, (uint)Data.Length);
        }
    }

    /// <summary>A texture inside a BNTX container (offsets relative to the BNTX start).</summary>
    public sealed record BntxTexture(string Name, int Format, int Width, int Height, int BlockHeightLog2, int MipCount, int DataOffset, int DataSize);

    public static class Bntx
    {
        public const int FormatR8Unorm = 0x0201, FormatBc4Unorm = 0x1D01;

        public static BntxTexture[] Parse(ReadOnlySpan<byte> b)
        {
            if (b.Length < 0x30 || !b[..4].SequenceEqual("BNTX"u8) || !b.Slice(0x20, 4).SequenceEqual("NX  "u8))
            {
                throw new InvalidDataException("not a Switch BNTX");
            }

            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(b[0x24..]);
            int array = (int)BinaryPrimitives.ReadUInt64LittleEndian(b[0x28..]);
            BntxTexture[] textures = new BntxTexture[count];

            for (int i = 0; i < count; i++)
            {
                int o = (int)BinaryPrimitives.ReadUInt64LittleEndian(b[(array + 8 * i)..]);

                if (!b.Slice(o, 4).SequenceEqual("BRTI"u8))
                {
                    throw new InvalidDataException("bad BRTI");
                }

                int nameOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(b[(o + 0x60)..]);
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(b[nameOffset..]);
                int mipArray = (int)BinaryPrimitives.ReadUInt64LittleEndian(b[(o + 0x70)..]);

                textures[i] = new BntxTexture(
                    Encoding.UTF8.GetString(b.Slice(nameOffset + 2, nameLength)),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 0x1C)..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 0x24)..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 0x28)..]),
                    (int)(BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 0x34)..]) & 7),
                    BinaryPrimitives.ReadUInt16LittleEndian(b[(o + 0x16)..]),
                    (int)BinaryPrimitives.ReadUInt64LittleEndian(b[mipArray..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(o + 0x50)..]));
            }

            return textures;
        }

        /// <summary>Decodes mip 0 of an R8 or BC4 texture to one byte per pixel (row-major, top row first as stored).</summary>
        public static byte[] DecodeAlpha(ReadOnlySpan<byte> bntx, BntxTexture t)
        {
            ReadOnlySpan<byte> data = bntx.Slice(t.DataOffset, t.DataSize);

            return t.Format switch
            {
                FormatR8Unorm => BlockLinear.Deswizzle(data, t.Width, t.Height, 1, t.BlockHeightLog2),
                FormatBc4Unorm => Bc4.Decode(BlockLinear.Deswizzle(data, (t.Width + 3) / 4, (t.Height + 3) / 4, 8, t.BlockHeightLog2), t.Width, t.Height),
                _ => throw new NotSupportedException($"texture format 0x{t.Format:X4}"),
            };
        }

        /// <summary>
        /// Encodes <paramref name="pixels"/> into mip 0 of the texture in place. For BC4, only blocks whose decoded pixels
        /// differ from <paramref name="original"/> are re-encoded, so untouched areas stay bit-identical.
        /// </summary>
        public static void EncodeAlpha(Span<byte> bntx, BntxTexture t, byte[] pixels, byte[] original)
        {
            Span<byte> data = bntx.Slice(t.DataOffset, t.DataSize);

            switch (t.Format)
            {
                case FormatR8Unorm:
                    BlockLinear.Swizzle(pixels, data, t.Width, t.Height, 1, t.BlockHeightLog2);
                    break;
                case FormatBc4Unorm:
                    int wb = (t.Width + 3) / 4, hb = (t.Height + 3) / 4;
                    byte[] blocks = BlockLinear.Deswizzle(data, wb, hb, 8, t.BlockHeightLog2);
                    Bc4.EncodeChanged(blocks, pixels, original, t.Width, t.Height);
                    BlockLinear.Swizzle(blocks, data, wb, hb, 8, t.BlockHeightLog2);
                    break;
                default:
                    throw new NotSupportedException($"texture format 0x{t.Format:X4}");
            }
        }
    }

    /// <summary>Tegra X1 block-linear (GOB) layout.</summary>
    public static class BlockLinear
    {
        public static int Address(int xBytes, int y, int widthBytes, int blockHeightLog2)
        {
            int blockHeight = 1 << blockHeightLog2;
            int gobsPerRow = (widthBytes + 63) / 64;
            int address = ((y / (8 * blockHeight)) * gobsPerRow + xBytes / 64) * 512 * blockHeight + (y % (8 * blockHeight)) / 8 * 512;
            int x = xBytes % 64, yy = y % 8;

            return address + (x / 32) * 256 + (yy / 2) * 64 + (x % 32 / 16) * 32 + (yy % 2) * 16 + (x % 16);
        }

        /// <param name="width">Width in elements (pixels or compressed blocks).</param>
        /// <param name="height">Height in element rows.</param>
        /// <param name="bpe">Bytes per element.</param>
        public static byte[] Deswizzle(ReadOnlySpan<byte> src, int width, int height, int bpe, int blockHeightLog2)
        {
            byte[] dst = new byte[width * height * bpe];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    src.Slice(Address(x * bpe, y, width * bpe, blockHeightLog2), bpe).CopyTo(dst.AsSpan((y * width + x) * bpe));
                }
            }

            return dst;
        }

        public static void Swizzle(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int bpe, int blockHeightLog2)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    src.Slice((y * width + x) * bpe, bpe).CopyTo(dst[Address(x * bpe, y, width * bpe, blockHeightLog2)..]);
                }
            }
        }
    }

    /// <summary>BC4 (single-channel) block codec.</summary>
    public static class Bc4
    {
        private static void Palette(byte r0, byte r1, Span<int> p)
        {
            p[0] = r0;
            p[1] = r1;

            if (r0 > r1)
            {
                for (int i = 1; i < 7; i++)
                {
                    p[i + 1] = ((7 - i) * r0 + i * r1) / 7;
                }
            }
            else
            {
                for (int i = 1; i < 5; i++)
                {
                    p[i + 1] = ((5 - i) * r0 + i * r1) / 5;
                }

                p[6] = 0;
                p[7] = 255;
            }
        }

        public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> pixels16)
        {
            Span<int> p = stackalloc int[8];
            Palette(block[0], block[1], p);
            ulong bits = 0;

            for (int i = 0; i < 6; i++)
            {
                bits |= (ulong)block[2 + i] << (8 * i);
            }

            for (int i = 0; i < 16; i++)
            {
                pixels16[i] = (byte)p[(int)(bits >> (3 * i)) & 7];
            }
        }

        public static byte[] Decode(ReadOnlySpan<byte> blocks, int width, int height)
        {
            int wb = (width + 3) / 4, hb = (height + 3) / 4;
            byte[] image = new byte[width * height];
            Span<byte> px = stackalloc byte[16];

            for (int by = 0; by < hb; by++)
            {
                for (int bx = 0; bx < wb; bx++)
                {
                    DecodeBlock(blocks.Slice((by * wb + bx) * 8, 8), px);

                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + i % 4, y = by * 4 + i / 4;

                        if (x < width && y < height)
                        {
                            image[y * width + x] = px[i];
                        }
                    }
                }
            }

            return image;
        }

        private static int Encode(ReadOnlySpan<byte> px, byte r0, byte r1, Span<byte> block)
        {
            Span<int> p = stackalloc int[8];
            Palette(r0, r1, p);
            ulong bits = 0;
            int error = 0;

            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestError = int.MaxValue;

                for (int k = 0; k < 8; k++)
                {
                    int e = Math.Abs(p[k] - px[i]);

                    if (e < bestError)
                    {
                        bestError = e;
                        best = k;
                    }
                }

                error += bestError * bestError;
                bits |= (ulong)best << (3 * i);
            }

            block[0] = r0;
            block[1] = r1;

            for (int i = 0; i < 6; i++)
            {
                block[2 + i] = (byte)(bits >> (8 * i));
            }

            return error;
        }

        /// <summary>Encodes one block, choosing the better of the 8-level and the 6-level (+0/255) modes.</summary>
        public static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> block)
        {
            byte min = 255, max = 0, innerMin = 255, innerMax = 0;

            foreach (byte v in px)
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);

                if (v != 0 && v != 255)
                {
                    innerMin = Math.Min(innerMin, v);
                    innerMax = Math.Max(innerMax, v);
                }
            }

            Span<byte> a = stackalloc byte[8];
            int errorA = Encode(px, max, min, a);

            if (innerMin > innerMax)
            {
                innerMin = innerMax = 0; // only 0/255 values: the 6-level mode reproduces them exactly
            }

            int errorB = Encode(px, innerMin, innerMax, block);

            if (errorA < errorB)
            {
                a.CopyTo(block);
            }
        }

        /// <summary>Re-encodes the blocks (linear block order) whose pixels differ between <paramref name="original"/> and <paramref name="image"/>.</summary>
        public static void EncodeChanged(Span<byte> blocks, byte[] image, byte[] original, int width, int height)
        {
            int wb = (width + 3) / 4, hb = (height + 3) / 4;
            Span<byte> px = stackalloc byte[16];

            for (int by = 0; by < hb; by++)
            {
                for (int bx = 0; bx < wb; bx++)
                {
                    bool changed = false;

                    for (int i = 0; i < 16; i++)
                    {
                        int x = Math.Min(bx * 4 + i % 4, width - 1), y = Math.Min(by * 4 + i / 4, height - 1);
                        px[i] = image[y * width + x];
                        changed |= image[y * width + x] != original[y * width + x];
                    }

                    if (changed)
                    {
                        EncodeBlock(px, blocks.Slice((by * wb + bx) * 8, 8));
                    }
                }
            }
        }
    }
}
