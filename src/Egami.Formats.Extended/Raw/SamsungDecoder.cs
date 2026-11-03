using System.Buffers.Binary;

namespace Egami.Formats.Extended.Raw;

/// <summary>The uncompressed SRW layouts (SRW spec, section 3).</summary>
internal enum SamsungLayout
{
    /// <summary>16-bit little-endian words, one sample each.</summary>
    Words16,

    /// <summary>12-bit samples two in three bytes, most significant bits first.</summary>
    Packed12Msb,

    /// <summary>12-bit samples two in three bytes, least significant bits first (the NX20, NX200, NX210, NX1000 and NX1100).</summary>
    Packed12Lsb,

    /// <summary>12-bit packed data under compression 32769, whose packing no sample has shown yet (spec, open point 1).</summary>
    Packed12Unknown,
}

/// <summary>Where a V0 file's table of row offsets lies: raw IFD tag 0xA010 (SRW spec, section 4.1).</summary>
internal sealed record SamsungRows(long Table);

/// <summary>
/// Samsung's SRW sensor data, written from the clean-room specification a study-only session wrote: the uncompressed layouts,
/// V0's per-row bitstreams of 16-sample groups with a swap of each cell's greens, V1's single bitstream of prefix-coded
/// differences, and V2's 16-byte-aligned rows of blocks predicted from the rows above. All three codecs predict from rows
/// already decoded, so they run row by row on one thread.
/// </summary>
internal static class SamsungDecoder
{
    /// <summary>V1's prefix codes (spec, section 5.2): code bits, code length, difference length.</summary>
    private static readonly (int Code, int Length, int Bits)[] V1Codes =
    [
        (0b000, 3, 4), (0b001, 3, 7), (0b01, 2, 6), (0b10, 2, 5), (0b1100, 4, 3), (0b110100, 6, 0), (0b1101010, 7, 9),
        (0b11010110, 8, 10), (0b110101110, 9, 11), (0b1101011110, 10, 12), (0b1101011111, 10, 13), (0b11011, 5, 1),
        (0b1110, 4, 8), (0b1111, 4, 2),
    ];

    /// <summary>A 10-bit peek's (code length, difference length), packed as length × 16 + bits.</summary>
    private static readonly byte[] V1Table = BuildV1Table();

    private static readonly int[] V2Offsets = [-4, -2, -2, 0, 0, 2, 4];

    private static byte[] BuildV1Table()
    {
        byte[] table = new byte[1024];
        foreach ((int code, int length, int bits) in V1Codes)
        {
            int shift = 10 - length;
            for (int j = 0; j < 1 << shift; j++)
            {
                table[(code << shift) | j] = (byte)((length << 4) | bits);
            }
        }

        return table;
    }

    /// <summary>Uncompressed rows of <c>count / height</c> bytes (spec, section 3).</summary>
    public static void Uncompressed(byte[] file, int offset, int count, RawPixels pixels, SamsungLayout layout, ParallelOptions parallel)
    {
        if (layout == SamsungLayout.Packed12Unknown)
        {
            throw new NotSupportedException("Egami does not read 12-bit packed SRW data under compression 32769 yet: no sample has shown its packing.");
        }

        int width = pixels.Width, height = pixels.Height;
        int stride = count / height, need = layout == SamsungLayout.Words16 ? width * 2 : (width * 3) + 1 >> 1;
        if (stride < need)
        {
            throw new InvalidDataException("The SRW strip is smaller than its rows.");
        }

        RawParallel.For(0, height, parallel, y =>
        {
            ReadOnlySpan<byte> src = file.AsSpan(offset + (y * stride), need);
            Span<ushort> row = pixels.U16Row(y);
            switch (layout)
            {
                case SamsungLayout.Words16:
                    // The words are little-endian in big-endian files too. Values past the bit depth are kept, not refused: the EX1
                    // and WB2000 write them in their margins and Adobe DNG Converter keeps them (measured).
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = BinaryPrimitives.ReadUInt16LittleEndian(src[(x * 2)..]);
                    }

                    break;
                case SamsungLayout.Packed12Msb:
                    for (int x = 0, p = 0; x < width; x += 2, p += 3)
                    {
                        row[x] = (ushort)((src[p] << 4) | (src[p + 1] >> 4));
                        if (x + 1 < width)
                        {
                            row[x + 1] = (ushort)(((src[p + 1] & 0xF) << 8) | src[p + 2]);
                        }
                    }

                    break;
                default:
                    for (int x = 0, p = 0; x < width; x += 2, p += 3)
                    {
                        row[x] = (ushort)(src[p] | ((src[p + 1] & 0xF) << 8));
                        if (x + 1 < width)
                        {
                            row[x + 1] = (ushort)((src[p + 1] >> 4) | (src[p + 2] << 4));
                        }
                    }

                    break;
            }
        });
    }

    /// <summary>The V0 codec (spec, section 4): independent rows from a table of offsets, then a swap in every 2 × 2 cell.</summary>
    public static void V0(byte[] file, long offset, long count, RawPixels pixels, SamsungRows rows, long fileLength, ParallelOptions parallel)
    {
        int width = pixels.Width, height = pixels.Height;
        if (rows.Table < 0 || rows.Table + (4L * height) > fileLength)
        {
            throw new InvalidDataException("The SRW row table lies outside the file.");
        }

        ReadOnlySpan<byte> table = file.AsSpan((int)rows.Table, 4 * height);
        Span<int> len = stackalloc int[4];
        Span<int> group = stackalloc int[16];
        for (int y = 0; y < height; y++)
        {
            long start = BinaryPrimitives.ReadUInt32LittleEndian(table[(4 * y)..]);
            long end = y + 1 < height ? BinaryPrimitives.ReadUInt32LittleEndian(table[(4 * (y + 1))..]) : count;
            if (start >= end || end > count)
            {
                throw new InvalidDataException($"The SRW row table is out of order at row {y}.");
            }

            Le32Bits bits = new(file.AsSpan((int)(offset + start), (int)(end - start)));
            Span<ushort> row = pixels.U16Row(y);
            ReadOnlySpan<ushort> up1 = y >= 1 ? pixels.U16Row(y - 1) : default, up2 = y >= 2 ? pixels.U16Row(y - 2) : default;
            len.Fill(y < 2 ? 7 : 4);
            for (int g = 0; g < width; g += 16)
            {
                bool vertical = bits.Read(1) == 1;
                uint ops = bits.Read(8);
                for (int i = 0; i < 4; i++)
                {
                    uint op = (ops >> (6 - (2 * i))) & 3;
                    len[i] = op == 3 ? (int)bits.Read(4) : op == 2 ? len[i] - 1 : op == 1 ? len[i] + 1 : len[i];
                    if ((uint)len[i] > 16)
                    {
                        throw new InvalidDataException($"The SRW V0 data is damaged at row {y}.");
                    }
                }

                if (vertical && y < 2)
                {
                    throw new InvalidDataException($"The SRW V0 data predicts from above row 0 at row {y}.");
                }

                int even = g == 0 ? 128 : row[g - 2], odd = g == 0 ? 128 : row[g - 1];
                for (int k = 0; k < 16; k++)
                {
                    // The eight even columns first, then the eight odd ones.
                    int c = k < 8 ? 2 * k : (2 * (k - 8)) + 1;
                    int n = len[k < 8 ? k >> 2 : 2 + ((k - 8) >> 2)];
                    int diff = Signed(bits.Read(n), n);
                    int x = g + c;
                    int prediction = !vertical ? ((c & 1) == 0 ? even : odd)
                        : x >= width ? 0 : (c & 1) == 0 ? up1[x] : up2[x];
                    group[c] = prediction + diff;
                }

                for (int c = 0; c < 16 && g + c < width; c++)
                {
                    row[g + c] = (ushort)group[c];
                }
            }

            if (bits.Overrun)
            {
                throw new InvalidDataException($"The SRW V0 data ends early at row {y}.");
            }
        }

        RawParallel.For(0, height / 2, parallel, h =>
        {
            Span<ushort> top = pixels.U16Row(2 * h), bottom = pixels.U16Row((2 * h) + 1);
            for (int x = 0; x + 1 < width; x += 2)
            {
                (top[x + 1], bottom[x]) = (bottom[x], top[x + 1]);
            }
        });
    }

    /// <summary>The V1 codec (spec, section 5): one bitstream of prefix-coded differences, two predictors per row.</summary>
    public static void V1(ReadOnlySpan<byte> data, RawPixels pixels, int bits)
    {
        int width = pixels.Width, height = pixels.Height, max = 1 << bits;
        MakerDecoders.MsbBits reader = new(data);
        for (int y = 0; y < height; y++)
        {
            Span<ushort> row = pixels.U16Row(y);
            int even = y >= 2 ? pixels.U16Row(y - 2)[0] : 0, odd = y >= 2 && width > 1 ? pixels.U16Row(y - 2)[1] : 0;
            for (int x = 0; x < width; x++)
            {
                int entry = V1Table[reader.Peek(10)];
                reader.Skip(entry >> 4);
                int n = entry & 15;
                int diff = MakerDecoders.Extend(reader.Read(n), n);
                int v = (x & 1) == 0 ? even += diff : odd += diff;
                if ((uint)v >= (uint)max)
                {
                    throw new InvalidDataException($"The SRW V1 data decodes outside the sample range at row {y}.");
                }

                row[x] = (ushort)v;
            }

            if (reader.Overrun)
            {
                throw new InvalidDataException($"The SRW V1 data ends early at row {y}.");
            }
        }
    }

    /// <summary>The V2 codec (spec, section 6): a 16-byte header, then 16-byte-aligned rows of 16-column blocks.</summary>
    public static void V2(ReadOnlySpan<byte> data, RawPixels pixels, int bits)
    {
        int width = pixels.Width, height = pixels.Height;
        if (data.Length < 16)
        {
            throw new InvalidDataException("The SRW V2 header is missing.");
        }

        Le32Bits header = new(data[..16]);
        header.Read(16);
        header.Read(4);
        int depth = (int)header.Read(4) + 1;
        header.Read(8);
        int w = (int)header.Read(16), h = (int)header.Read(16);
        header.Read(16);
        header.Read(4);
        int flags = (int)header.Read(4);
        header.Read(26);
        int initial = (int)header.Read(14);
        if (depth != bits || w != width || h != height)
        {
            throw new InvalidDataException("The SRW V2 header does not match the raw IFD.");
        }

        if (width % 16 != 0 || width > 6496 || height > 4336 || width < 646 || height < 436)
        {
            throw new NotSupportedException($"Egami reads SRW V2 data up to 6496 × 4336 pixels with a width a multiple of 16, not {width} × {height}.");
        }

        int max = (1 << bits) - 1;
        Span<int> prev2 = stackalloc int[3], prev1 = stackalloc int[3], len = stackalloc int[4], baseline = stackalloc int[16];
        long at = 16;
        for (int y = 0; y < height; y++)
        {
            if (at >= data.Length)
            {
                throw new InvalidDataException($"The SRW V2 data ends early at row {y}.");
            }

            Le32Bits reader = new(data[(int)at..]);
            Span<ushort> row = pixels.U16Row(y);
            ReadOnlySpan<ushort> up1 = y >= 1 ? pixels.U16Row(y - 1) : default, up2 = y >= 2 ? pixels.U16Row(y - 2) : default;
            int motion = 7, scale = 0;
            prev2.Fill(y < 2 ? 7 : 4);
            prev1.Fill(y < 2 ? 7 : 4);
            bool oddRow = (y & 1) == 1;
            for (int g = 0; g < width; g += 16)
            {
                if ((flags & 4) == 0 && g % 64 == 0)
                {
                    uint i = reader.Read(2);
                    scale = i == 3 ? (int)reader.Read(12) : scale + (i == 1 ? -2 : i == 2 ? 2 : 0);
                }

                if ((flags & 2) != 0)
                {
                    motion = reader.Read(1) == 1 ? 3 : 7;
                }
                else if (reader.Read(1) == 0)
                {
                    motion = (int)reader.Read(3);
                }

                if (y < 2 && motion != 7)
                {
                    throw new InvalidDataException($"The SRW V2 data predicts from above row 0 at row {y}.");
                }

                if (motion == 7)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        baseline[i] = g == 0 ? initial : row[g - 2 + (i & 1)];
                    }
                }
                else
                {
                    int shift = V2Offsets[motion];
                    bool average = motion is 2 or 4;
                    for (int i = 0; i < 16; i++)
                    {
                        bool fromTwoUp = ((y + i) & 1) == 1;
                        int c = g + i + shift + (fromTwoUp ? 0 : (i & 1) == 0 ? 1 : -1);
                        ReadOnlySpan<ushort> refRow = fromTwoUp ? up2 : up1;
                        if (c < 0 || c > (average ? width - 3 : width - 1))
                        {
                            throw new InvalidDataException($"The SRW V2 data predicts from outside the row at row {y}.");
                        }

                        baseline[i] = average ? (refRow[c] + refRow[c + 2] + 1) >> 1 : refRow[c];
                    }
                }

                if ((flags & 1) == 0 && reader.Read(1) == 1)
                {
                    len.Clear();
                }
                else
                {
                    uint codes = reader.Read(8);
                    for (int j = 0; j < 4; j++)
                    {
                        int t = oddRow ? j >> 1 : ((j >> 1) + 2) % 3;
                        uint f = (codes >> (6 - (2 * j))) & 3;
                        if (f == 2 && prev2[t] == 0)
                        {
                            throw new InvalidDataException($"The SRW V2 data has a negative length at row {y}.");
                        }

                        len[j] = f == 0 ? prev2[t] : f == 1 ? prev2[t] + 1 : f == 2 ? prev2[t] - 1 : (int)reader.Read(4);
                        prev2[t] = prev1[t];
                        prev1[t] = len[j];
                        if (len[j] > bits + 1)
                        {
                            throw new InvalidDataException($"The SRW V2 data has a length past the bit depth at row {y}.");
                        }
                    }
                }

                for (int k = 0; k < 16; k++)
                {
                    int n = len[k >> 2];
                    int diff = (Signed(reader.Read(n), n) * ((2 * scale) + 1)) + scale;
                    int p = oddRow ? (2 * (k & 7)) + 1 - (k >> 3) : (2 * (k & 7)) + (k >> 3);
                    row[g + p] = (ushort)Math.Clamp(baseline[p] + diff, 0, max);
                }
            }

            if (reader.Overrun)
            {
                throw new InvalidDataException($"The SRW V2 data ends early at row {y}.");
            }

            // The next row starts at the next 16-byte boundary after this row's last 32-bit word.
            at += ((reader.BitsRead + 31) / 32 * 4 + 15) & ~15L;
        }
    }

    /// <summary>Reads <paramref name="n"/> bits as a two's-complement number.</summary>
    private static int Signed(uint v, int n) => n == 0 ? 0 : (int)(v << (32 - n)) >> (32 - n);

    /// <summary>A reader of 32-bit little-endian words, each from its most significant bit (spec, section 2); past the end it reads zeros.</summary>
    internal ref struct Le32Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;
        private ulong _buffer;
        private int _count;

        public readonly bool Overrun => _pos > _data.Length + 8;

        /// <summary>Gets the bits read so far.</summary>
        public readonly long BitsRead => (8L * _pos) - _count;

        public uint Read(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            if (_count < n)
            {
                Fill();
            }

            uint v = (uint)(_buffer >> (64 - n));
            _buffer <<= n;
            _count -= n;
            return v;
        }

        private void Fill()
        {
            while (_count <= 32)
            {
                uint word;
                if (_pos + 4 <= _data.Length)
                {
                    word = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
                }
                else
                {
                    word = 0;
                    for (int i = 0; _pos + i < _data.Length; i++)
                    {
                        word |= (uint)_data[_pos + i] << (8 * i);
                    }
                }

                _pos += 4;
                _buffer |= (ulong)word << (32 - _count);
                _count += 32;
            }
        }
    }
}
